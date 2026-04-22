using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services.Mirroring;

namespace SiteMirror.Api.Services;

/// <summary>
/// Coordinates end-to-end mirroring workflow:
/// browser navigation, response capture, resource download, and final HTML rewrite.
/// </summary>
public sealed class MirrorService : ISiteMirrorService
{
    private readonly MirrorSettings _settings;
    private readonly ILogger<MirrorService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public MirrorService(
        IOptions<MirrorSettings> options,
        ILogger<MirrorService> logger,
        ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;
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

        _logger.LogInformation(
            "Mirror started for {SourceUrl}. Output: {OutputPath}, WaitMs: {WaitMs}",
            startUri, siteOutputPath, waitMs);

        if (string.IsNullOrWhiteSpace(chromiumExecutablePath))
        {
            _logger.LogInformation("Chromium path not configured; running Playwright install.");
            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }
        else
        {
            _logger.LogInformation("Using configured Chromium executable: {ChromiumPath}", chromiumExecutablePath);
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
        var mirror = new MirrorState(startUri, siteOutputPath, _loggerFactory.CreateLogger<MirrorState>());
        var responseTasks = new ConcurrentBag<Task>();

        EventHandler<IResponse> responseHandler = (_, response) =>
        {
            responseTasks.Add(PersistResponseAsync(response, mirror));
        };
        page.Response += responseHandler;

        string renderedHtml;
        try
        {
            await page.GotoAsync(startUri.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 120_000
            });

            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
                {
                    Timeout = 15_000
                });
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Network idle wait timed out for {Url}; continuing with rendered content.", startUri);
            }

            await page.WaitForTimeoutAsync(waitMs);
            renderedHtml = await page.ContentAsync();
            _logger.LogInformation("Initial page load completed for {CurrentUrl}", page.Url);
        }
        finally
        {
            page.Response -= responseHandler;
        }

        await WaitForPendingResponseSavesAsync(responseTasks, cancellationToken);
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var finalUri))
        {
            finalUri = startUri;
        }

        _logger.LogInformation("Stage save-rendered-document: saving rendered HTML for {FinalUrl}", finalUri);
        await mirror.SaveRenderedDocumentAsync(finalUri, renderedHtml);

        _logger.LogInformation(
            "Stage download-linked-resources: downloading queued resources. QueueCount={QueueCount}",
            mirror.PendingQueueCount);
        await mirror.DownloadLinkedResourcesAsync(cancellationToken);
        _logger.LogInformation("Stage download-linked-resources: complete. FilesSaved={FilesSaved}", mirror.TotalFilesWritten);

        _logger.LogInformation(
            "Stage rewrite-html-documents: rewriting HTML documents. HtmlCount={HtmlCount}",
            mirror.HtmlDocumentCount);
        await mirror.RewriteHtmlDocumentsAsync(cancellationToken);
        _logger.LogInformation("Stage rewrite-html-documents: complete");

        var entryFilePath = mirror.GetEntryFile(finalUri);
        var relativeEntry = Path.GetRelativePath(siteOutputPath, entryFilePath).Replace('\\', '/');
        _logger.LogInformation(
            "Mirror completed for {SourceUrl}. FilesSaved: {FilesSaved}, Entry: {EntryFile}",
            startUri, mirror.TotalFilesWritten, entryFilePath);

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
    }

    /// <summary>
    /// Rewrite mode: takes an existing mirrored html path, rebuilds file mappings from disk,
    /// and rewrites links to local relative paths.
    /// </summary>
    public async Task<RewriteLinksResult> RewriteLinksAsync(RewriteLinksRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.HtmlFilePath))
        {
            throw new ArgumentException("HtmlFilePath is required.", nameof(request.HtmlFilePath));
        }

        var htmlFilePath = Path.GetFullPath(request.HtmlFilePath);
        if (!File.Exists(htmlFilePath))
        {
            throw new FileNotFoundException($"HTML file not found: {htmlFilePath}", htmlFilePath);
        }

        string inferredHost = string.Empty;
        var mirrorRoot = string.IsNullOrWhiteSpace(request.MirrorRootFolder)
            ? InferMirrorRootFromHtmlPath(htmlFilePath, out inferredHost)
            : Path.GetFullPath(request.MirrorRootFolder);

        if (!Directory.Exists(mirrorRoot))
        {
            throw new DirectoryNotFoundException($"Mirror root folder not found: {mirrorRoot}");
        }

        var rootUrl = request.RootUrl;
        if (string.IsNullOrWhiteSpace(rootUrl))
        {
            rootUrl = $"https://{inferredHost}/";
        }

        if (!Uri.TryCreate(rootUrl, UriKind.Absolute, out var rootUri))
        {
            throw new ArgumentException("RootUrl must be a valid absolute URL.", nameof(request.RootUrl));
        }

        _logger.LogInformation(
            "Rewrite-links started. HtmlFilePath={HtmlFilePath}, MirrorRoot={MirrorRoot}, RootUrl={RootUrl}",
            htmlFilePath, mirrorRoot, rootUri);

        var mirror = new MirrorState(rootUri, mirrorRoot, _loggerFactory.CreateLogger<MirrorState>());
        var htmlDiscovered = await mirror.SeedMappingsFromExistingFilesAsync(cancellationToken);

        int htmlRewritten;
        if (request.RewriteAllHtmlFiles)
        {
            await mirror.RewriteHtmlDocumentsAsync(cancellationToken);
            htmlRewritten = mirror.HtmlDocumentCount;
        }
        else
        {
            htmlRewritten = await mirror.RewriteSingleHtmlFileAsync(htmlFilePath, cancellationToken);
        }

        _logger.LogInformation(
            "Rewrite-links completed. HtmlDiscovered={HtmlDiscovered}, HtmlRewritten={HtmlRewritten}",
            htmlDiscovered, htmlRewritten);

        return new RewriteLinksResult
        {
            StartHtmlPath = htmlFilePath,
            MirrorRootFolder = mirrorRoot,
            HtmlFilesDiscovered = htmlDiscovered,
            HtmlFilesRewritten = htmlRewritten
        };
    }

    private async Task PersistResponseAsync(IResponse response, MirrorState mirror)
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

            var bodyTask = response.BodyAsync();
            var completed = await Task.WhenAny(bodyTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != bodyTask)
            {
                _logger.LogDebug("Skipped response body due to timeout for {ResponseUrl}", response.Url);
                return;
            }

            var body = await bodyTask;
            await mirror.SaveResponseAsync(responseUri, body, response.Headers, response.Status, requestUri);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist response {ResponseUrl}", response.Url);
        }
    }

    private async Task WaitForPendingResponseSavesAsync(ConcurrentBag<Task> responseTasks, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = responseTasks.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _logger.LogWarning("Stopped waiting for response persistence tasks due to deadline.");
                return;
            }

            var allPending = Task.WhenAll(pending);
            var completed = await Task.WhenAny(allPending, Task.Delay(remaining, cancellationToken));
            if (completed != allPending)
            {
                _logger.LogWarning("Timed out while waiting for response persistence tasks.");
                return;
            }
        }
    }

    private static string InferMirrorRootFromHtmlPath(string htmlFilePath, out string hostSegment)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(htmlFilePath)!);
        DirectoryInfo? hostDirectory = null;
        while (directory is not null)
        {
            if (LooksLikeHostSegment(directory.Name))
            {
                hostDirectory = directory;
                break;
            }

            directory = directory.Parent;
        }

        if (hostDirectory is null || hostDirectory.Parent is null)
        {
            throw new ArgumentException(
                "Could not infer mirror root from HtmlFilePath. Provide MirrorRootFolder and RootUrl explicitly.");
        }

        hostSegment = hostDirectory.Name;
        return hostDirectory.Parent.FullName;
    }

    private static bool LooksLikeHostSegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment) &&
        segment.Contains('.') &&
        !segment.Contains(Path.DirectorySeparatorChar) &&
        !segment.Contains(Path.AltDirectorySeparatorChar);

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
}
