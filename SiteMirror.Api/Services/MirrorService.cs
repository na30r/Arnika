using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public sealed class MirrorService : ISiteMirrorService
{
    private readonly MirrorSettings _settings;

    public MirrorService(IOptions<MirrorSettings> options)
    {
        _settings = options.Value;
    }

    public async Task<MirrorResult> MirrorAsync(MirrorRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var startUri))
        {
            throw new ArgumentException("Invalid URL.", nameof(request.Url));
        }

        var waitMs = request.ExtraWaitMs <= 0 ? 4_000 : request.ExtraWaitMs;
        var outputRoot = string.IsNullOrWhiteSpace(_settings.OutputFolder) ? "mirror-output" : _settings.OutputFolder;
        var chromiumExecutablePath = string.IsNullOrWhiteSpace(_settings.ChromiumExecutablePath)
            ? null
            : Path.GetFullPath(_settings.ChromiumExecutablePath);

        if (!string.IsNullOrWhiteSpace(chromiumExecutablePath) && !File.Exists(chromiumExecutablePath))
        {
            throw new FileNotFoundException($"Chromium executable not found: {chromiumExecutablePath}", chromiumExecutablePath);
        }

        Directory.CreateDirectory(outputRoot);

        var runStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var siteFolderName = $"{SanitizeFileName(startUri.Host)}-{runStamp}";
        var siteOutputPath = Path.GetFullPath(Path.Combine(outputRoot, siteFolderName));
        Directory.CreateDirectory(siteOutputPath);

        if (string.IsNullOrWhiteSpace(chromiumExecutablePath))
        {
            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = chromiumExecutablePath
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });

        var page = await context.NewPageAsync();
        var mirror = new MirrorState(startUri, siteOutputPath);
        var responseTasks = new ConcurrentBag<Task>();

        page.Response += (_, response) =>
        {
            responseTasks.Add(PersistResponseAsync(response));
        };

        await page.GotoAsync(startUri.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 120_000
        });

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForTimeoutAsync(waitMs);

        var renderedHtml = await page.ContentAsync();
        await WaitForPendingResponseSavesAsync(responseTasks, cancellationToken);
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var finalUri))
        {
            finalUri = startUri;
        }

        await mirror.SaveRenderedDocumentAsync(finalUri, renderedHtml);
        await mirror.DownloadLinkedResourcesAsync(cancellationToken);
        await mirror.RewriteHtmlDocumentsAsync(cancellationToken);

        var entryFilePath = mirror.GetEntryFile(finalUri);
        var relativeEntry = Path.GetRelativePath(siteOutputPath, entryFilePath).Replace('\\', '/');

        return new MirrorResult
        {
            SourceUrl = startUri.ToString(),
            FinalUrl = finalUri.ToString(),
            OutputFolder = siteOutputPath,
            EntryFilePath = entryFilePath,
            EntryFileRelativePath = relativeEntry,
            FilesSaved = mirror.TotalFilesWritten,
            UsedChromiumExecutablePath = chromiumExecutablePath,
            WaitMs = waitMs
        };

        async Task PersistResponseAsync(IResponse response)
        {
            try
            {
                if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var responseUri) || !IsHttpOrHttps(responseUri))
                {
                    return;
                }

                Uri? requestUri = null;
                if (Uri.TryCreate(response.Request.Url, UriKind.Absolute, out var parsedRequestUri) && IsHttpOrHttps(parsedRequestUri))
                {
                    requestUri = parsedRequestUri;
                }

                var body = await response.BodyAsync();
                await mirror.SaveResponseAsync(responseUri, body, response.Headers, response.Status, requestUri);
            }
            catch
            {
                // Continue mirroring on individual response failures.
            }
        }
    }

    private static async Task WaitForPendingResponseSavesAsync(ConcurrentBag<Task> responseTasks, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = responseTasks.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending);
        }
    }

    private static bool IsHttpOrHttps(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) ? '-' : c);
        }

        return builder.ToString();
    }

    private sealed class MirrorState(Uri rootUri, string outputDir)
    {
        private static readonly Regex CssUrlRegex = new(@"url\((?<quote>['""]?)(?<value>[^)'""]+)\k<quote>\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CssImportRegex = new(
            @"@import\s+(?:url\((?<quote1>['""]?)(?<url1>[^)'""]+)\k<quote1>\)|(?<quote2>['""])(?<url2>[^'""]+)\k<quote2>)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "href", "src", "data", "action", "formaction", "poster", "background", "xlink:href", "imagesrc", "longdesc", "cite"
        };
        private static readonly HashSet<string> SrcSetAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "srcset", "imagesrcset"
        };
        private readonly ConcurrentDictionary<string, string> _urlToRelativePath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Uri> _htmlSourceByRelativePath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _downloadQueue = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _htmlDocuments = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public int TotalFilesWritten => _urlToRelativePath.Count;

        public async Task SaveResponseAsync(Uri resourceUri, byte[] body, IReadOnlyDictionary<string, string> headers, int statusCode, Uri? requestUri = null)
        {
            if (statusCode is < 200 or >= 400 || body.Length == 0)
            {
                return;
            }

            var mediaType = ParseMediaType(headers);
            var relativePath = MapUriToRelativePath(resourceUri, mediaType);
            var localPath = Path.Combine(outputDir, relativePath);
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
            var relativePath = MapUriToRelativePath(pageUri, "text/html");
            var localPath = Path.Combine(outputDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllTextAsync(localPath, html, Encoding.UTF8);
            RegisterMapping(pageUri, relativePath);

            lock (_lock)
            {
                _htmlDocuments.Add(relativePath);
                _htmlSourceByRelativePath[relativePath] = pageUri;
            }

            EnqueueResourcesFromHtml(pageUri, html);
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

                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsHttpOrHttps(uri))
                    {
                        continue;
                    }

                    if (_urlToRelativePath.ContainsKey(NormalizeUri(uri)))
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
                        var relativePath = MapUriToRelativePath(uri, mediaType);
                        var localPath = Path.Combine(outputDir, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
                        RegisterMapping(uri, relativePath);
                        if (res.RequestMessage?.RequestUri is { } finalUri && IsHttpOrHttps(finalUri))
                        {
                            RegisterMapping(finalUri, relativePath);
                        }

                        if (mediaType.StartsWith("text/css", StringComparison.OrdinalIgnoreCase))
                        {
                            var css = Encoding.UTF8.GetString(bytes);
                            EnqueueResourcesFromCss(uri, css);
                        }

                        if (mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                        {
                            var html = Encoding.UTF8.GetString(bytes);
                            lock (_lock)
                            {
                                _htmlDocuments.Add(relativePath);
                                _htmlSourceByRelativePath[relativePath] = uri;
                            }

                            EnqueueResourcesFromHtml(uri, html);
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
                var fullPath = Path.Combine(outputDir, relativePath);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var html = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
                var parser = new HtmlParser();
                var document = await parser.ParseDocumentAsync(html, cancellationToken);
                var docUri = ResolveFileUri(relativePath);

                RewriteAttributeUrls(document, docUri);
                RewriteCssBlocks(document, docUri);

                var updated = document.DocumentElement?.OuterHtml ?? html;
                await File.WriteAllTextAsync(fullPath, updated, Encoding.UTF8, cancellationToken);
            }
        }

        public string GetEntryFile(Uri pageUri)
        {
            var relative = MapUriToRelativePath(pageUri, "text/html");
            return Path.Combine(outputDir, relative);
        }

        private void EnqueueResourcesFromHtml(Uri baseUri, string html)
        {
            var parser = new HtmlParser();
            var document = parser.ParseDocument(html);

            foreach (var element in document.All)
            {
                foreach (var attribute in element.Attributes)
                {
                    var value = attribute.Value;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (SrcSetAttributes.Contains(attribute.Name))
                    {
                        EnqueueResourcesFromSrcSet(baseUri, value);
                        continue;
                    }

                    if (UrlAttributes.Contains(attribute.Name))
                    {
                        // Avoid crawling full navigation trees via anchors.
                        if (string.Equals(element.LocalName, "a", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(attribute.Name, "href", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        AddToQueue(baseUri, value);
                        continue;
                    }

                    if (attribute.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase) && LooksLikeUrl(value))
                    {
                        AddToQueue(baseUri, value);
                    }
                }
            }

            foreach (var element in document.All.Where(e => e.HasAttribute("style")))
            {
                var inlineStyle = element.GetAttribute("style");
                if (!string.IsNullOrWhiteSpace(inlineStyle))
                {
                    EnqueueResourcesFromCss(baseUri, inlineStyle);
                }
            }

            foreach (var styleNode in document.QuerySelectorAll("style"))
            {
                EnqueueResourcesFromCss(baseUri, styleNode.TextContent);
            }
        }

        private void EnqueueResourcesFromCss(Uri baseUri, string css)
        {
            foreach (Match match in CssUrlRegex.Matches(css))
            {
                var value = match.Groups["value"].Value.Trim();
                AddToQueue(baseUri, value);
            }

            foreach (Match match in CssImportRegex.Matches(css))
            {
                var value = match.Groups["url1"].Success
                    ? match.Groups["url1"].Value.Trim()
                    : match.Groups["url2"].Value.Trim();
                AddToQueue(baseUri, value);
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

            if (Uri.TryCreate(baseUri, rawUrl, out var resolved) && IsHttpOrHttps(resolved))
            {
                _downloadQueue.TryAdd(NormalizeUri(resolved), 0);
            }
        }

        private void RewriteAttributeUrls(IDocument document, Uri docUri)
        {
            foreach (var element in document.All)
            {
                foreach (var attribute in element.Attributes.ToArray())
                {
                    var value = attribute.Value;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (SrcSetAttributes.Contains(attribute.Name))
                    {
                        element.SetAttribute(attribute.Name, RewriteSrcSet(docUri, value));
                        continue;
                    }

                    if (UrlAttributes.Contains(attribute.Name) ||
                        (attribute.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase) && LooksLikeUrl(value)))
                    {
                        var rewritten = RewriteUrl(docUri, value);
                        if (rewritten is not null)
                        {
                            element.SetAttribute(attribute.Name, rewritten);
                        }
                    }
                }
            }
        }

        private void RewriteCssBlocks(IDocument document, Uri docUri)
        {
            foreach (var styleNode in document.QuerySelectorAll("style"))
            {
                styleNode.TextContent = RewriteCss(docUri, styleNode.TextContent);
            }

            foreach (var element in document.All.Where(e => e.HasAttribute("style")))
            {
                var inline = element.GetAttribute("style");
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    element.SetAttribute("style", RewriteCss(docUri, inline));
                }
            }
        }

        private string RewriteCss(Uri docUri, string css)
        {
            var rewritten = CssUrlRegex.Replace(css, match =>
            {
                var raw = match.Groups["value"].Value.Trim();
                var rewritten = RewriteUrl(docUri, raw);
                return rewritten is null ? match.Value : $"url(\"{rewritten}\")";
            });

            return CssImportRegex.Replace(rewritten, match =>
            {
                var raw = match.Groups["url1"].Success
                    ? match.Groups["url1"].Value.Trim()
                    : match.Groups["url2"].Value.Trim();
                var rewrittenImport = RewriteUrl(docUri, raw);
                if (rewrittenImport is null)
                {
                    return match.Value;
                }

                return $"@import url(\"{rewrittenImport}\")";
            });
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

            if (!Uri.TryCreate(baseUri, originalUrl, out var absolute) || !IsHttpOrHttps(absolute))
            {
                return null;
            }

            var currentRelativePath = ResolveCurrentDocumentRelativePath(baseUri);
            var key = NormalizeUri(absolute);
            if (!_urlToRelativePath.TryGetValue(key, out var targetRelativePath))
            {
                var fallbackPath = MapUriToRelativePath(absolute, "text/html");
                if (!File.Exists(Path.Combine(outputDir, fallbackPath)))
                {
                    return null;
                }

                targetRelativePath = fallbackPath;
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
            var normalized = NormalizeUri(baseUri);
            if (_urlToRelativePath.TryGetValue(normalized, out var mapped))
            {
                return mapped;
            }

            var fallback = MapUriToRelativePath(baseUri, "text/html");
            if (File.Exists(Path.Combine(outputDir, fallback)))
            {
                return fallback;
            }

            return MapUriToRelativePath(rootUri, "text/html");
        }

        private void EnqueueResourcesFromSrcSet(Uri baseUri, string? srcSet)
        {
            if (string.IsNullOrWhiteSpace(srcSet))
            {
                return;
            }

            var candidates = srcSet.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var candidate in candidates)
            {
                var url = ExtractSrcSetUrl(candidate);
                AddToQueue(baseUri, url);
            }
        }

        private string RewriteSrcSet(Uri baseUri, string srcSet)
        {
            var candidates = srcSet.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var rewrittenCandidates = new List<string>(candidates.Length);
            foreach (var candidate in candidates)
            {
                var trimmed = candidate.Trim();
                var url = ExtractSrcSetUrl(trimmed);
                if (string.IsNullOrWhiteSpace(url))
                {
                    rewrittenCandidates.Add(trimmed);
                    continue;
                }

                var descriptor = trimmed[url.Length..].TrimStart();
                var rewrittenUrl = RewriteUrl(baseUri, url);
                if (string.IsNullOrWhiteSpace(rewrittenUrl))
                {
                    rewrittenCandidates.Add(trimmed);
                    continue;
                }

                rewrittenCandidates.Add(string.IsNullOrWhiteSpace(descriptor)
                    ? rewrittenUrl
                    : $"{rewrittenUrl} {descriptor}");
            }

            return string.Join(", ", rewrittenCandidates);
        }

        private static string ExtractSrcSetUrl(string candidate)
        {
            var trimmed = candidate.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            var separatorIndex = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
            return separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];
        }

        private static bool LooksLikeUrl(string value)
        {
            var trimmed = value.Trim();
            return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith("/", StringComparison.Ordinal) ||
                   trimmed.StartsWith("./", StringComparison.Ordinal) ||
                   trimmed.StartsWith("../", StringComparison.Ordinal) ||
                   trimmed.StartsWith('#') ||
                   trimmed.StartsWith("?", StringComparison.Ordinal);
        }

        private Uri ResolveFileUri(string relativePath)
        {
            if (_htmlSourceByRelativePath.TryGetValue(relativePath, out var sourceUri))
            {
                return sourceUri;
            }

            var withSlashes = relativePath.Replace('\\', '/');
            var hostPrefix = $"{SanitizePathSegment(rootUri.Host)}/";
            if (withSlashes.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var uriPath = "/" + withSlashes[hostPrefix.Length..];
                return new Uri($"{rootUri.Scheme}://{rootUri.Host}{uriPath}", UriKind.Absolute);
            }

            return rootUri;
        }

        private string MapUriToRelativePath(Uri resourceUri, string? mediaType)
        {
            var normalizedPath = resourceUri.AbsolutePath;
            if (string.IsNullOrWhiteSpace(normalizedPath) || normalizedPath.EndsWith('/'))
            {
                normalizedPath += "index.html";
            }

            var fileName = Path.GetFileName(normalizedPath);
            if (!Path.HasExtension(fileName))
            {
                normalizedPath += GuessExtensionFromMediaType(mediaType, ".html");
            }

            var extension = Path.GetExtension(normalizedPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = GuessExtensionFromMediaType(mediaType, ".bin");
                normalizedPath += extension;
            }

            var query = resourceUri.Query;
            if (!string.IsNullOrWhiteSpace(query))
            {
                var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(query))).ToLowerInvariant()[..8];
                var dir = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? string.Empty;
                var baseName = Path.GetFileNameWithoutExtension(normalizedPath);
                var ext = Path.GetExtension(normalizedPath);
                var merged = $"{baseName}-{hash}{ext}";
                normalizedPath = string.IsNullOrWhiteSpace(dir) ? merged : $"{dir}/{merged}";
            }

            var sanitized = normalizedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var hostSegment = SanitizePathSegment(resourceUri.Host);
            return Path.Combine(hostSegment, sanitized);
        }

        private static string SanitizePathSegment(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                builder.Append(invalid.Contains(c) ? '-' : c);
            }

            return builder.ToString();
        }

        private static string GuessExtensionFromMediaType(string? mediaType, string defaultExtension)
        {
            return mediaType?.ToLowerInvariant() switch
            {
                var m when m is not null && m.Contains("text/html", StringComparison.Ordinal) => ".html",
                var m when m is not null && m.Contains("text/css", StringComparison.Ordinal) => ".css",
                var m when m is not null && m.Contains("javascript", StringComparison.Ordinal) => ".js",
                var m when m is not null && m.Contains("application/json", StringComparison.Ordinal) => ".json",
                var m when m is not null && m.Contains("image/png", StringComparison.Ordinal) => ".png",
                var m when m is not null && m.Contains("image/jpeg", StringComparison.Ordinal) => ".jpg",
                var m when m is not null && m.Contains("image/webp", StringComparison.Ordinal) => ".webp",
                var m when m is not null && m.Contains("image/svg+xml", StringComparison.Ordinal) => ".svg",
                var m when m is not null && m.Contains("font/woff2", StringComparison.Ordinal) => ".woff2",
                var m when m is not null && m.Contains("font/woff", StringComparison.Ordinal) => ".woff",
                var m when m is not null && m.Contains("font/ttf", StringComparison.Ordinal) => ".ttf",
                _ => defaultExtension
            };
        }

        private static string ParseMediaType(IReadOnlyDictionary<string, string> headers)
        {
            if (!headers.TryGetValue("content-type", out var value))
            {
                var pair = headers.FirstOrDefault(kv => string.Equals(kv.Key, "content-type", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    return string.Empty;
                }

                value = pair.Value;
            }

            var semicolonIndex = value.IndexOf(';');
            return semicolonIndex < 0 ? value : value[..semicolonIndex];
        }

        private static string NormalizeUri(Uri uri)
        {
            var builder = new UriBuilder(uri)
            {
                Fragment = string.Empty
            };
            return builder.Uri.ToString();
        }

        private void RegisterMapping(Uri uri, string relativePath)
        {
            var key = NormalizeUri(uri);
            _urlToRelativePath[key] = relativePath;
        }

        private static bool IsHttpOrHttps(Uri uri) =>
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
