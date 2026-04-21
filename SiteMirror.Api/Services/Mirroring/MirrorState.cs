using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AngleSharp.Html.Parser;
using Microsoft.Playwright;

namespace SiteMirror.Api.Services.Mirroring;

internal sealed class MirrorState
{
    private readonly Uri _rootUri;
    private readonly string _outputDir;
    private readonly MirrorPathHelper _pathHelper;
    private readonly HtmlRewriter _htmlRewriter;

    private readonly ConcurrentDictionary<string, string> _urlToRelativePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _htmlSourceByRelativePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _downloadQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _htmlDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public MirrorState(Uri rootUri, string outputDir)
    {
        _rootUri = rootUri;
        _outputDir = outputDir;
        _pathHelper = new MirrorPathHelper(rootUri);
        _htmlRewriter = new HtmlRewriter();
    }

    public int TotalFilesWritten => _urlToRelativePath.Count;

    public async Task SaveResponseAsync(Uri resourceUri, byte[] body, IReadOnlyDictionary<string, string> headers, int statusCode, Uri? requestUri = null)
    {
        if (statusCode is < 200 or >= 400 || body.Length == 0)
        {
            return;
        }

        var mediaType = MirrorPathHelper.ParseMediaType(headers);
        var relativePath = _pathHelper.MapUriToRelativePath(resourceUri, mediaType);
        var localPath = Path.Combine(_outputDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllBytesAsync(localPath, body);

        RegisterMapping(resourceUri, relativePath);
        if (requestUri is not null)
        {
            RegisterMapping(requestUri, relativePath);
        }

        if (mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            lock (_lock)
            {
                _htmlDocuments.Add(relativePath);
                _htmlSourceByRelativePath[relativePath] = resourceUri;
            }
        }
    }

    public async Task SaveRenderedDocumentAsync(Uri pageUri, string html)
    {
        var relativePath = _pathHelper.MapUriToRelativePath(pageUri, "text/html");
        var localPath = Path.Combine(_outputDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllTextAsync(localPath, html, Encoding.UTF8);
        RegisterMapping(pageUri, relativePath);

        lock (_lock)
        {
            _htmlDocuments.Add(relativePath);
            _htmlSourceByRelativePath[relativePath] = pageUri;
        }

        _htmlRewriter.EnqueueResourcesFromHtml(pageUri, html, AddToQueue);
    }

    public async Task DownloadLinkedResourcesAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        });

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SiteMirrorBot/1.0");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pending = _downloadQueue.Keys.ToList();
            if (pending.Count == 0)
            {
                break;
            }

            foreach (var url in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _downloadQueue.TryRemove(url, out _);

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !MirrorPathHelper.IsHttpOrHttps(uri))
                {
                    continue;
                }

                if (_urlToRelativePath.ContainsKey(_pathHelper.NormalizeUri(uri)))
                {
                    continue;
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                    var res = await client.SendAsync(req, cancellationToken);
                    if (!res.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var bytes = await res.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length == 0)
                    {
                        continue;
                    }

                    var mediaType = res.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    var relativePath = _pathHelper.MapUriToRelativePath(uri, mediaType);
                    var localPath = Path.Combine(_outputDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
                    RegisterMapping(uri, relativePath);
                        var finalUri = res.RequestMessage?.RequestUri;
                        if (finalUri is not null && MirrorPathHelper.IsHttpOrHttps(finalUri))
                    {
                        RegisterMapping(finalUri, relativePath);
                    }

                    if (mediaType.StartsWith("text/css", StringComparison.OrdinalIgnoreCase))
                    {
                        var css = Encoding.UTF8.GetString(bytes);
                        _htmlRewriter.EnqueueResourcesFromCss(uri, css, AddToQueue);
                    }

                    if (mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                    {
                        var html = Encoding.UTF8.GetString(bytes);
                        lock (_lock)
                        {
                            _htmlDocuments.Add(relativePath);
                            _htmlSourceByRelativePath[relativePath] = uri;
                        }

                        _htmlRewriter.EnqueueResourcesFromHtml(uri, html, AddToQueue);
                    }
                }
                catch
                {
                    // Keep mirroring resilient.
                }
            }
        }
    }

    public async Task RewriteHtmlDocumentsAsync(CancellationToken cancellationToken)
    {
        List<string> htmlFiles;
        lock (_lock)
        {
            htmlFiles = _htmlDocuments.ToList();
        }

        foreach (var relativePath in htmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(_outputDir, relativePath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var html = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html, cancellationToken);
            var docUri = ResolveFileUri(relativePath);

            _htmlRewriter.RewriteHtmlDocument(document, docUri, RewriteUrl);

            var updated = document.DocumentElement?.OuterHtml ?? html;
            await File.WriteAllTextAsync(fullPath, updated, Encoding.UTF8, cancellationToken);
        }
    }

    public string GetEntryFile(Uri pageUri)
    {
        var relative = _pathHelper.MapUriToRelativePath(pageUri, "text/html");
        return Path.Combine(_outputDir, relative);
    }

    public async Task PersistResponseAsync(IResponse response)
    {
        try
        {
            if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var responseUri) || !MirrorPathHelper.IsHttpOrHttps(responseUri))
            {
                return;
            }

            Uri? requestUri = null;
            if (Uri.TryCreate(response.Request.Url, UriKind.Absolute, out var parsedRequestUri) && MirrorPathHelper.IsHttpOrHttps(parsedRequestUri))
            {
                requestUri = parsedRequestUri;
            }

            var bodyTask = response.BodyAsync();
            var completed = await Task.WhenAny(bodyTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != bodyTask)
            {
                return;
            }

            var body = await bodyTask;
            await SaveResponseAsync(responseUri, body, response.Headers, response.Status, requestUri);
        }
        catch
        {
            // Continue mirroring on individual response failures.
        }
    }

    private void AddToQueue(Uri baseUri, string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            rawUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            rawUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            rawUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            rawUrl.StartsWith('#'))
        {
            return;
        }

        if (Uri.TryCreate(baseUri, rawUrl, out var resolved) && MirrorPathHelper.IsHttpOrHttps(resolved))
        {
            _downloadQueue.TryAdd(_pathHelper.NormalizeUri(resolved), 0);
        }
    }

    private Uri ResolveFileUri(string relativePath)
    {
        if (_htmlSourceByRelativePath.TryGetValue(relativePath, out var sourceUri))
        {
            return sourceUri;
        }

        var withSlashes = relativePath.Replace('\\', '/');
        var hostPrefix = $"{MirrorPathHelper.SanitizePathSegment(_rootUri.Host)}/";
        if (withSlashes.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var uriPath = "/" + withSlashes[hostPrefix.Length..];
            return new Uri($"{_rootUri.Scheme}://{_rootUri.Host}{uriPath}", UriKind.Absolute);
        }

        return _rootUri;
    }

    private void RegisterMapping(Uri uri, string relativePath)
    {
        _urlToRelativePath[_pathHelper.NormalizeUri(uri)] = relativePath;
    }

    private string? RewriteUrl(Uri baseUri, string? originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl) ||
            originalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            originalUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            originalUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (originalUrl.StartsWith('#'))
        {
            return originalUrl;
        }

        if (!Uri.TryCreate(baseUri, originalUrl, out var absolute) || !MirrorPathHelper.IsHttpOrHttps(absolute))
        {
            return null;
        }

        var currentRelativePath = ResolveCurrentDocumentRelativePath(baseUri);
        var key = _pathHelper.NormalizeUri(absolute);
        if (!_urlToRelativePath.TryGetValue(key, out var targetRelativePath))
        {
            targetRelativePath = _pathHelper.MapUriToRelativePath(absolute, mediaType: null);
        }

        var fromDirectory = Path.GetDirectoryName(currentRelativePath) ?? ".";
        var relative = Path.GetRelativePath(fromDirectory, targetRelativePath).Replace('\\', '/');
        if (string.Equals(currentRelativePath, targetRelativePath, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(absolute.Fragment))
        {
            return absolute.Fragment;
        }

        if (!string.IsNullOrWhiteSpace(absolute.Fragment))
        {
            relative += absolute.Fragment;
        }

        return relative;
    }

    private string ResolveCurrentDocumentRelativePath(Uri baseUri)
    {
        var normalized = _pathHelper.NormalizeUri(baseUri);
        if (_urlToRelativePath.TryGetValue(normalized, out var mapped))
        {
            return mapped;
        }

        var fallback = _pathHelper.MapUriToRelativePath(baseUri, "text/html");
        if (File.Exists(Path.Combine(_outputDir, fallback)))
        {
            return fallback;
        }

        return _pathHelper.MapUriToRelativePath(_rootUri, "text/html");
    }
}
