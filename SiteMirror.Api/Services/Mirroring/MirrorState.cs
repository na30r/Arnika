using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SiteMirror.Api.Services;

namespace SiteMirror.Api.Services.Mirroring;

/// <summary>
/// Maintains mirror lifecycle state: persisted resource map, discovered URLs, and HTML rewrite passes.
/// </summary>
internal sealed class MirrorState
{
    private static readonly Regex HostLikeSegmentRegex = new(
        "^[a-z0-9-]+(\\.[a-z0-9-]+)*\\.[a-z]{2,}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches <c>/en/docs</c> → group 2 is <c>/docs</c> (trailing path after locale).</summary>
    private static readonly Regex LeadingLocalePathSegmentRegex = new(
        @"^/([a-z]{2}(?:-[a-z]{2})?)(/.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PathStartsWithLocaleSegmentRegex = new(
        @"^/[a-z]{2}(?:-[a-z]{2})?/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Uri _rootUri;
    private readonly string _outputDir;
    private readonly MirrorPathHelper _pathHelper;
    private readonly HtmlRewriter _htmlRewriter;
    private readonly ILogger<MirrorState> _logger;
    private readonly IMirrorContentAddressRegistry? _contentRegistry;
    private readonly string? _siteHost;
    private readonly string? _version;
    private readonly bool _useContentAddressing;

    private readonly ConcurrentDictionary<string, string> _urlToRelativePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _htmlSourceByRelativePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _downloadQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _htmlDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public MirrorState(
        Uri rootUri,
        string outputDir,
        ILogger<MirrorState> logger,
        IMirrorContentAddressRegistry? contentRegistry = null,
        string? siteHost = null,
        string? version = null)
    {
        _rootUri = rootUri;
        _outputDir = outputDir;
        _pathHelper = new MirrorPathHelper(rootUri);
        _htmlRewriter = new HtmlRewriter();
        _logger = logger;
        _contentRegistry = contentRegistry;
        _siteHost = siteHost;
        _version = version;
        _useContentAddressing =
            contentRegistry is { IsEnabled: true }
            && !string.IsNullOrWhiteSpace(siteHost)
            && !string.IsNullOrWhiteSpace(version);
    }

    public int TotalFilesWritten => _urlToRelativePath.Count;
    public int PendingQueueCount => _downloadQueue.Count;
    public int HtmlDocumentCount
    {
        get
        {
            lock (_lock)
            {
                return _htmlDocuments.Count;
            }
        }
    }

    /// <summary>
    /// HTML must stay on URL-derived paths so <c>_i18n/blocks</c> and <c>_i18n/pages</c> stay editable and aligned with page URLs.
    /// </summary>
    private static bool IsUrlKeyedHtmlResponse(string mediaType, byte[] body)
    {
        if (mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return BodyLooksLikeHtml(body);
        }

        return false;
    }

    private static bool BodyLooksLikeHtml(ReadOnlySpan<byte> body)
    {
        var i = 0;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            i = 3;
        }

        while (i < body.Length && body[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        if (i >= body.Length || body[i] != (byte)'<')
        {
            return false;
        }

        var take = Math.Min(body.Length - i, 64);
        var prefix = Encoding.ASCII.GetString(body.Slice(i, take));
        return prefix.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> PersistRawResponseAsync(
        Uri resourceUri,
        byte[] body,
        string mediaType,
        Uri? requestUri,
        CancellationToken cancellationToken)
    {
        var useContentAddressingForThisFile =
            _useContentAddressing && !IsUrlKeyedHtmlResponse(mediaType, body);

        if (!_useContentAddressing || !useContentAddressingForThisFile)
        {
            var relativePath = _pathHelper.MapUriToRelativePath(resourceUri, mediaType);
            var localPath = Path.Combine(_outputDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, body, cancellationToken);
            RegisterMapping(resourceUri, relativePath);
            if (requestUri is not null)
            {
                RegisterMapping(requestUri, relativePath);
            }

            return relativePath;
        }

        // Content-addressed storage (non-HTML assets only).
        var urlKey = _pathHelper.NormalizeUri(resourceUri);
        var cas = await _contentRegistry!.RegisterOrGetContentAsync(
            _siteHost!,
            _version!,
            _outputDir,
            urlKey,
            body,
            string.IsNullOrWhiteSpace(mediaType) ? null : mediaType,
            cancellationToken);

        if (cas.CallerMustWriteFile)
        {
            var localPath = Path.Combine(_outputDir, cas.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, body, cancellationToken);
        }

        RegisterMapping(resourceUri, cas.RelativePath);
        if (requestUri is not null)
        {
            RegisterMapping(requestUri, cas.RelativePath);
            var altKey = _pathHelper.NormalizeUri(requestUri);
            if (!string.Equals(altKey, urlKey, StringComparison.OrdinalIgnoreCase))
            {
                _ = await _contentRegistry.RegisterOrGetContentAsync(
                    _siteHost!,
                    _version!,
                    _outputDir,
                    altKey,
                    body,
                    string.IsNullOrWhiteSpace(mediaType) ? null : mediaType,
                    cancellationToken);
            }
        }

        return cas.RelativePath;
    }

    /// <summary>
    /// Persists a browser/network response and registers URL-to-local-file mappings for rewrite.
    /// </summary>
    public async Task SaveResponseAsync(Uri resourceUri, byte[] body, IReadOnlyDictionary<string, string> headers, int statusCode, Uri? requestUri = null, CancellationToken cancellationToken = default)
    {
        if (statusCode is < 200 or >= 400 || body.Length == 0)
        {
            return;
        }

        var mediaType = MirrorPathHelper.ParseMediaType(headers);
        var relativePath = await PersistRawResponseAsync(resourceUri, body, mediaType, requestUri, cancellationToken);
        _logger.LogDebug("Persisted response -> {RelativePath} ({Bytes} bytes)", relativePath, body.Length);

        if (mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var html = Encoding.UTF8.GetString(body);
            lock (_lock)
            {
                if (_htmlDocuments.Add(relativePath))
                {
                    _htmlSourceByRelativePath[relativePath] = resourceUri;
                    _htmlRewriter.EnqueueResourcesFromHtml(resourceUri, html, AddToQueue);
                }
            }
        }
    }

    /// <summary>
    /// Saves the final rendered HTML for the entry page and discovers linked resources.
    /// </summary>
    public async Task SaveRenderedDocumentAsync(Uri pageUri, string html, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        var relativePath = await PersistRawResponseAsync(pageUri, bytes, "text/html", null, cancellationToken);

        lock (_lock)
        {
            _htmlDocuments.Add(relativePath);
            _htmlSourceByRelativePath[relativePath] = pageUri;
        }

        _htmlRewriter.EnqueueResourcesFromHtml(pageUri, html, AddToQueue);
        _logger.LogDebug("Saved rendered HTML for {PageUri} -> {RelativePath}", pageUri, relativePath);
    }

    /// <summary>
    /// Drains the discovered URL queue and downloads linked assets that were not captured by Playwright events.
    /// </summary>
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

            _logger.LogDebug("Downloading {Count} queued resources", pending.Count);

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
                    var relativePath = await PersistRawResponseAsync(uri, bytes, mediaType, res.RequestMessage?.RequestUri, cancellationToken);

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
                            if (_htmlDocuments.Add(relativePath))
                            {
                                _htmlSourceByRelativePath[relativePath] = uri;
                                _htmlRewriter.EnqueueResourcesFromHtml(uri, html, AddToQueue);
                            }
                        }
                    }
                }
                catch
                {
                    _logger.LogDebug("Failed to download linked resource: {Url}", url);
                }
            }
        }
    }

    /// <summary>
    /// Rewrites all discovered HTML documents to local relative references.
    /// </summary>
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

        _logger.LogInformation("Rewrote {Count} HTML documents to local paths", htmlFiles.Count);
    }

    public async Task<int> RewriteSingleHtmlFileAsync(string htmlFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(htmlFilePath))
        {
            return 0;
        }

        var html = await File.ReadAllTextAsync(htmlFilePath, Encoding.UTF8, cancellationToken);
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html, cancellationToken);

        var relativePath = Path.GetRelativePath(_outputDir, htmlFilePath);
        var normalizedRelativePath = relativePath.Replace('\\', '/');
        var docUri = ResolveFileUri(normalizedRelativePath);

        _htmlRewriter.RewriteHtmlDocument(document, docUri, RewriteUrl);
        var updated = document.DocumentElement?.OuterHtml ?? html;
        await File.WriteAllTextAsync(htmlFilePath, updated, Encoding.UTF8, cancellationToken);

        return 1;
    }

    public Task<int> SeedMappingsFromExistingFilesAsync(CancellationToken cancellationToken)
    {
        var htmlCount = 0;
        foreach (var file in Directory.EnumerateFiles(_outputDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(_outputDir, file);
            var normalizedRelativePath = relativePath.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalizedRelativePath))
            {
                continue;
            }

            var uri = BuildUriFromRelativePath(normalizedRelativePath);
            RegisterMapping(uri, normalizedRelativePath);

            if (string.Equals(Path.GetExtension(file), ".html", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock)
                {
                    _htmlDocuments.Add(normalizedRelativePath);
                    _htmlSourceByRelativePath[normalizedRelativePath] = uri;
                }

                htmlCount++;
            }
        }

        return Task.FromResult(htmlCount);
    }

    public string GetEntryFile(Uri pageUri)
    {
        var key = _pathHelper.NormalizeUri(pageUri);
        if (_urlToRelativePath.TryGetValue(key, out var relative))
        {
            return Path.Combine(_outputDir, relative);
        }

        var fallback = _pathHelper.MapUriToRelativePath(pageUri, "text/html");
        return Path.Combine(_outputDir, fallback);
    }

    private Uri BuildUriFromRelativePath(string relativePath)
    {
        var withSlashes = relativePath.Replace('\\', '/').TrimStart('/');
        return new Uri($"{_rootUri.Scheme}://{_rootUri.Host}/{withSlashes}", UriKind.Absolute);
    }

    /// <summary>
    /// Handles Playwright response events and forwards successfully read bodies to persistence.
    /// </summary>
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
            await SaveResponseAsync(responseUri, body, response.Headers, response.Status, requestUri, CancellationToken.None);
        }
        catch
        {
            // Intentionally continue so one failed asset does not abort full mirroring.
        }
    }

    /// <summary>
    /// Adds resolved HTTP(S) URLs into the pending download queue.
    /// </summary>
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

        var withSlashes = relativePath.Replace('\\', '/').TrimStart('/');
        return new Uri($"{_rootUri.Scheme}://{_rootUri.Host}/{withSlashes}", UriKind.Absolute);
    }

    private void RegisterMapping(Uri uri, string relativePath)
    {
        _urlToRelativePath[_pathHelper.NormalizeUri(uri)] = relativePath;
        _logger.LogTrace("Registered URL mapping {Url} -> {RelativePath}", uri, relativePath);
    }

    /// <summary>
    /// Resolves a same-site URL to a mirrored relative file path when the crawl registered a variant
    /// (e.g. link <c>/en/docs/foo</c> but the page was saved as <c>/docs/foo</c>).
    /// </summary>
    private bool TryGetMappedRelativePath(Uri absolute, out string targetRelativePath)
    {
        var triedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        targetRelativePath = string.Empty;

        foreach (var candidate in EnumerateMappingUriCandidates(absolute))
        {
            var k = _pathHelper.NormalizeUri(candidate);
            if (!triedKeys.Add(k))
            {
                continue;
            }

            if (_urlToRelativePath.TryGetValue(k, out var mapped))
            {
                targetRelativePath = mapped;
                return true;
            }
        }

        targetRelativePath = string.Empty;
        return false;
    }

    private static IEnumerable<Uri> EnumerateMappingUriCandidates(Uri absolute)
    {
        yield return absolute;

        var path = absolute.AbsolutePath;
        if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
        {
            path = path.TrimEnd('/');
        }

        var stripMatch = LeadingLocalePathSegmentRegex.Match(path);
        if (stripMatch.Success)
        {
            yield return new UriBuilder(absolute) { Path = stripMatch.Groups[2].Value }.Uri;
        }

        // Common i18n routing (e.g. Next.js): HTML links use /en/... while the mirrored URL may omit the locale.
        if (path.Length > 1 && !PathStartsWithLocaleSegmentRegex.IsMatch(path))
        {
            yield return new UriBuilder(absolute) { Path = "/en" + path }.Uri;
        }
    }

    /// <summary>
    /// Rewrites an absolute/relative URL to a local relative path if possible.
    /// </summary>
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

        var candidateUrl = originalUrl;
        if (TryRepairSyntheticHostReference(originalUrl, out var repairedAbsoluteUrl))
        {
            candidateUrl = repairedAbsoluteUrl;
        }

        if (!Uri.TryCreate(baseUri, candidateUrl, out var absolute) || !MirrorPathHelper.IsHttpOrHttps(absolute))
        {
            return null;
        }

        var currentRelativePath = ResolveCurrentDocumentRelativePath(baseUri);
        if (!TryGetMappedRelativePath(absolute, out var targetRelativePath))
        {
            // Always normalize same-site links to their mirrored path shape, even if that target
            // page/resource has not been crawled yet. This keeps navigation consistent (relative hrefs).
            if (IsSameSiteHost(absolute))
            {
                targetRelativePath = _pathHelper.MapUriToRelativePath(absolute, mediaType: null);
            }
            else
            {
                // Keep off-site links unchanged.
                _logger.LogDebug("No local mapping found for {Url}; keeping original reference.", absolute);
                return candidateUrl;
            }
        }

        // GetDirectoryName returns "" (not null) for root-level files like "index.html"; GetRelativePath then throws.
        var fromDir = Path.GetDirectoryName(currentRelativePath);
        var fromDirectory = string.IsNullOrEmpty(fromDir) ? "." : fromDir;
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

    private bool IsSameSiteHost(Uri uri)
    {
        var rootHost = _rootUri.Host.Trim().ToLowerInvariant();
        var candidateHost = uri.Host.Trim().ToLowerInvariant();
        if (rootHost == candidateHost)
        {
            return true;
        }

        return rootHost == $"www.{candidateHost}" || candidateHost == $"www.{rootHost}";
    }

    /// <summary>
    /// Repairs previously broken synthetic relative references like "../../../cdn.example.com/index.html"
    /// back to absolute URLs so they no longer point to wrong local paths.
    /// </summary>
    private static bool TryRepairSyntheticHostReference(string originalUrl, out string repairedAbsoluteUrl)
    {
        repairedAbsoluteUrl = string.Empty;

        var trimmed = originalUrl.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith("#", StringComparison.Ordinal) ||
            trimmed.StartsWith("?", StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = trimmed;
        while (remainder.StartsWith("../", StringComparison.Ordinal) || remainder.StartsWith("./", StringComparison.Ordinal))
        {
            remainder = remainder.StartsWith("../", StringComparison.Ordinal)
                ? remainder[3..]
                : remainder[2..];
        }

        if (string.IsNullOrWhiteSpace(remainder))
        {
            return false;
        }

        var slashIndex = remainder.IndexOf('/');
        var firstSegment = slashIndex < 0 ? remainder : remainder[..slashIndex];
        if (!HostLikeSegmentRegex.IsMatch(firstSegment))
        {
            return false;
        }

        var trailing = slashIndex < 0 ? string.Empty : remainder[(slashIndex + 1)..];
        repairedAbsoluteUrl = string.IsNullOrWhiteSpace(trailing)
            ? $"https://{firstSegment}/"
            : $"https://{firstSegment}/{trailing}";
        return true;
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

    public async Task NormalizeUnmappedHttpUrlsAsync(string htmlFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(htmlFilePath))
        {
            return;
        }

        var html = await File.ReadAllTextAsync(htmlFilePath, Encoding.UTF8, cancellationToken);
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html, cancellationToken);

        foreach (var element in document.All)
        {
            foreach (var attribute in element.Attributes.ToArray())
            {
                var value = attribute.Value?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (!LooksLikeRelativeHostPath(value))
                {
                    continue;
                }

                var host = value.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(host) || !host.Contains('.'))
                {
                    continue;
                }

                var asAbsolute = $"https://{value.TrimStart('/')}";
                if (Uri.TryCreate(asAbsolute, UriKind.Absolute, out var absolute) &&
                    _urlToRelativePath.ContainsKey(_pathHelper.NormalizeUri(absolute)))
                {
                    continue;
                }

                element.SetAttribute(attribute.Name, asAbsolute);
            }
        }

        var updated = document.DocumentElement?.OuterHtml ?? html;
        await File.WriteAllTextAsync(htmlFilePath, updated, Encoding.UTF8, cancellationToken);
    }

    private static bool LooksLikeRelativeHostPath(string value)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.StartsWith("#", StringComparison.Ordinal) ||
            value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Contains('.') && value.Contains('/');
    }
}
