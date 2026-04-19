using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Playwright;

if (args.Length == 0)
{
    PrintUsage();
    return;
}

if (!Uri.TryCreate(args[0], UriKind.Absolute, out var startUri))
{
    Console.WriteLine("Invalid URL. Example: https://example.com");
    return;
}

var outputRoot = args.Length >= 2 ? args[1] : "mirror-output";
var waitMs = 4_000;
if (args.Length >= 3 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWaitMs) && parsedWaitMs > 0)
{
    waitMs = parsedWaitMs;
}

Directory.CreateDirectory(outputRoot);

var runStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var siteFolderName = $"{SanitizeFileName(startUri.Host)}-{runStamp}";
var siteOutputPath = Path.GetFullPath(Path.Combine(outputRoot, siteFolderName));
Directory.CreateDirectory(siteOutputPath);

Console.WriteLine($"Mirror source: {startUri}");
Console.WriteLine($"Output folder: {siteOutputPath}");
Console.WriteLine("Installing Playwright browser if needed...");
Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true
});

var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    IgnoreHTTPSErrors = true
});

var page = await context.NewPageAsync();

var mirror = new MirrorState(startUri, siteOutputPath);

page.Response += async (_, response) =>
{
    try
    {
        if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var responseUri))
        {
            return;
        }

        if (!IsHttpOrHttps(responseUri))
        {
            return;
        }

        var body = await response.BodyAsync();
        await mirror.SaveResponseAsync(responseUri, body, response.Headers, response.Status);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[warn] Failed to persist response {response.Url}: {ex.Message}");
    }
};

Console.WriteLine("Loading page and waiting for full render...");
await page.GotoAsync(startUri.ToString(), new PageGotoOptions
{
    WaitUntil = WaitUntilState.NetworkIdle,
    Timeout = 120_000
});

await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
await page.WaitForTimeoutAsync(waitMs);

var renderedHtml = await page.ContentAsync();
var finalPageUrl = page.Url;
if (!Uri.TryCreate(finalPageUrl, UriKind.Absolute, out var finalUri))
{
    finalUri = startUri;
}

await mirror.SaveRenderedDocumentAsync(finalUri, renderedHtml);
await mirror.DownloadLinkedResourcesAsync();
await mirror.RewriteHtmlDocumentsAsync();

var entryFilePath = mirror.GetEntryFile(finalUri);
Console.WriteLine();
Console.WriteLine("Mirror completed.");
Console.WriteLine($"Files saved: {mirror.TotalFilesWritten}");
Console.WriteLine($"Entry file: {entryFilePath}");
Console.WriteLine();
Console.WriteLine("Preview locally with:");
Console.WriteLine($"  dotnet run --project SiteMirror -- \"{startUri}\"");
Console.WriteLine($"  python3 -m http.server 8080 --directory \"{siteOutputPath}\"");
Console.WriteLine($"  open http://localhost:8080/{Path.GetRelativePath(siteOutputPath, entryFilePath).Replace('\\', '/')}");

static bool IsHttpOrHttps(Uri uri) =>
    string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
    string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

static string SanitizeFileName(string value)
{
    var invalid = Path.GetInvalidFileNameChars();
    var builder = new StringBuilder(value.Length);
    foreach (var c in value)
    {
        builder.Append(invalid.Contains(c) ? '-' : c);
    }

    return builder.ToString();
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project SiteMirror -- <url> [output-folder] [extra-wait-ms]");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet run --project SiteMirror -- https://example.com ./mirror-output 4000");
}

file sealed class MirrorState(Uri rootUri, string outputDir)
{
    private static readonly Regex CssUrlRegex = new(@"url\((?<quote>['""]?)(?<value>[^)'""]+)\k<quote>\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, string> _urlToRelativePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _htmlSourceByRelativePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _downloadQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _htmlDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int TotalFilesWritten => _urlToRelativePath.Count;

    public async Task SaveResponseAsync(Uri resourceUri, byte[] body, IReadOnlyDictionary<string, string> headers, int statusCode)
    {
        if (statusCode < 200 || statusCode >= 400 || body.Length == 0)
        {
            return;
        }

        var mediaType = ParseMediaType(headers);
        var relativePath = MapUriToRelativePath(resourceUri, mediaType);
        var localPath = Path.Combine(outputDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllBytesAsync(localPath, body);

        RegisterMapping(resourceUri, relativePath);

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

    public async Task DownloadLinkedResourcesAsync()
    {
        using var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        });

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SiteMirrorBot/1.0");

        while (true)
        {
            var pending = _downloadQueue.Keys.ToList();
            if (pending.Count == 0)
            {
                break;
            }

            foreach (var url in pending)
            {
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
                    var res = await client.SendAsync(req);
                    if (!res.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var bytes = await res.Content.ReadAsByteArrayAsync();
                    if (bytes.Length == 0)
                    {
                        continue;
                    }

                    var mediaType = res.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    var relativePath = MapUriToRelativePath(uri, mediaType);
                    var localPath = Path.Combine(outputDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    await File.WriteAllBytesAsync(localPath, bytes);
                    RegisterMapping(uri, relativePath);

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
                    // Keep mirroring resilient; continue with other resources.
                }
            }
        }
    }

    public async Task RewriteHtmlDocumentsAsync()
    {
        List<string> htmlFiles;
        lock (_lock)
        {
            htmlFiles = _htmlDocuments.ToList();
        }

        foreach (var relativePath in htmlFiles)
        {
            var fullPath = Path.Combine(outputDir, relativePath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var html = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);
            var docUri = ResolveFileUri(relativePath);

            RewriteAttributeUrls(document, docUri);
            RewriteCssBlocks(document, docUri);

            var updated = document.DocumentElement?.OuterHtml ?? html;
            await File.WriteAllTextAsync(fullPath, updated, Encoding.UTF8);
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

        var selectors = new (string selector, string attribute)[]
        {
            ("link[href]", "href"),
            ("script[src]", "src"),
            ("img[src]", "src"),
            ("source[src]", "src"),
            ("video[src]", "src"),
            ("audio[src]", "src"),
            ("iframe[src]", "src"),
            ("embed[src]", "src"),
            ("object[data]", "data"),
            ("input[src]", "src"),
            ("track[src]", "src"),
            ("image[href]", "href"),
            ("image[xlink\\:href]", "xlink:href"),
            ("use[href]", "href"),
            ("use[xlink\\:href]", "xlink:href"),
            ("a[href]", "href")
        };

        foreach (var (selector, attribute) in selectors)
        {
            foreach (var element in document.QuerySelectorAll(selector))
            {
                var value = element.GetAttribute(attribute);
                AddToQueue(baseUri, value);
            }
        }

        foreach (var element in document.All.Where(e => e.HasAttribute("style")))
        {
            var inlineStyle = element.GetAttribute("style");
            if (string.IsNullOrWhiteSpace(inlineStyle))
            {
                continue;
            }

            EnqueueResourcesFromCss(baseUri, inlineStyle);
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
    }

    private void AddToQueue(Uri baseUri, string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return;
        }

        if (rawUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
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
        var mappings = new (string selector, string attribute)[]
        {
            ("link[href]", "href"),
            ("script[src]", "src"),
            ("img[src]", "src"),
            ("source[src]", "src"),
            ("video[src]", "src"),
            ("audio[src]", "src"),
            ("iframe[src]", "src"),
            ("embed[src]", "src"),
            ("object[data]", "data"),
            ("input[src]", "src"),
            ("track[src]", "src"),
            ("a[href]", "href"),
            ("image[href]", "href"),
            ("image[xlink\\:href]", "xlink:href"),
            ("use[href]", "href"),
            ("use[xlink\\:href]", "xlink:href")
        };

        foreach (var (selector, attribute) in mappings)
        {
            foreach (var element in document.QuerySelectorAll(selector))
            {
                var original = element.GetAttribute(attribute);
                var rewritten = RewriteUrl(docUri, original);
                if (rewritten is not null)
                {
                    element.SetAttribute(attribute, rewritten);
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
            if (string.IsNullOrWhiteSpace(inline))
            {
                continue;
            }

            element.SetAttribute("style", RewriteCss(docUri, inline));
        }
    }

    private string RewriteCss(Uri docUri, string css)
    {
        return CssUrlRegex.Replace(css, match =>
        {
            var raw = match.Groups["value"].Value.Trim();
            var rewritten = RewriteUrl(docUri, raw);
            if (rewritten is null)
            {
                return match.Value;
            }

            return $"url(\"{rewritten}\")";
        });
    }

    private string? RewriteUrl(Uri baseUri, string? originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl))
        {
            return null;
        }

        if (originalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            originalUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            originalUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUri, originalUrl, out var absolute) || !IsHttpOrHttps(absolute))
        {
            return null;
        }

        var key = NormalizeUri(absolute);
        if (!_urlToRelativePath.TryGetValue(key, out var targetRelativePath))
        {
            return null;
        }

        var currentRelativePath = MapUriToRelativePath(baseUri, "text/html");
        var fromDirectory = Path.GetDirectoryName(currentRelativePath) ?? ".";
        var relative = Path.GetRelativePath(fromDirectory, targetRelativePath).Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(absolute.Fragment))
        {
            relative += absolute.Fragment;
        }

        return relative;
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
            normalizedPath = normalizedPath + "index.html";
        }

        var fileName = Path.GetFileName(normalizedPath);
        if (!Path.HasExtension(fileName))
        {
            normalizedPath += GuessExtensionFromMediaType(mediaType, defaultExtension: ".html");
        }

        var extension = Path.GetExtension(normalizedPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = GuessExtensionFromMediaType(mediaType, defaultExtension: ".bin");
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
