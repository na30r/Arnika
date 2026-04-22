using System.Collections.Concurrent;
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
    private const string MirrorRuntimeFileName = "_mirror-runtime.js";
    private static readonly string[] FallbackLocalizationLanguages = ["en"];
    private readonly MirrorSettings _settings;
    private readonly ILogger<MirrorService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LocalizationGenerator _localizationGenerator;
    private readonly string _contentRootPath;

    public MirrorService(
        IOptions<MirrorSettings> options,
        ILogger<MirrorService> logger,
        ILoggerFactory loggerFactory,
        IWebHostEnvironment hostEnvironment)
    {
        _settings = options.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _localizationGenerator = new LocalizationGenerator(_loggerFactory.CreateLogger<LocalizationGenerator>());
        _contentRootPath = hostEnvironment.ContentRootPath;
    }

    public async Task<MirrorResult> MirrorAsync(MirrorRequest request, CancellationToken cancellationToken)
    {
        var normalizedRequestUrl = NormalizeRequestedUrl(request.Url);
        if (!Uri.TryCreate(normalizedRequestUrl, UriKind.Absolute, out var startUri) || !IsHttpOrHttps(startUri))
        {
            throw new ArgumentException("Invalid URL. Use an absolute HTTP or HTTPS URL.", nameof(request.Url));
        }
        var normalizedVersion = NormalizeVersion(request.Version);
        var siteHost = MirrorPathHelper.SanitizePathSegment(startUri.Host);

        var waitMs = request.ExtraWaitMs <= 0 ? 4_000 : request.ExtraWaitMs;
        var outputRoot = ResolveOutputRoot(_settings.OutputFolder);
        var chromiumExecutablePath = string.IsNullOrWhiteSpace(_settings.ChromiumExecutablePath)
            ? null
            : Path.GetFullPath(_settings.ChromiumExecutablePath);

        if (!string.IsNullOrWhiteSpace(chromiumExecutablePath) && !File.Exists(chromiumExecutablePath))
        {
            throw new FileNotFoundException($"Chromium executable not found: {chromiumExecutablePath}", chromiumExecutablePath);
        }

        Directory.CreateDirectory(outputRoot);

        var siteOutputPath = Path.Combine(outputRoot, siteHost, normalizedVersion);
        Directory.CreateDirectory(siteOutputPath);
        await EnsureRuntimeScriptAvailableAsync(outputRoot, cancellationToken);

        _logger.LogInformation(
            "Mirror started for {SourceUrl}. Output: {OutputPath}, WaitMs: {WaitMs}, AutoScroll: {AutoScroll}, Version: {Version}",
            startUri, siteOutputPath, waitMs, request.AutoScroll, normalizedVersion);

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

            if (request.AutoScroll)
            {
                await AutoScrollPageAsync(page, request, cancellationToken);
                await WaitForNetworkIdleOrContinueAsync(page, 10_000);
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

        var requestedLanguages = (request.Languages is { Length: > 0 })
            ? request.Languages
            : FallbackLocalizationLanguages;
        _logger.LogInformation("Stage generate-localizations: generating localized copies for languages {Languages}",
            string.Join(", ", requestedLanguages));
        var localizationResult = await _localizationGenerator.GenerateLocalizedCopiesAsync(
            siteOutputPath,
            requestedLanguages,
            cancellationToken);
        _logger.LogInformation("Stage generate-localizations: complete");

        var entryFilePath = mirror.GetEntryFile(finalUri);
        var relativeEntry = Path.GetRelativePath(siteOutputPath, entryFilePath).Replace('\\', '/');
        var frontendPreviewPath = BuildFrontendPreviewPath(siteHost, normalizedVersion, localizationResult.DefaultLanguage, relativeEntry);
        _logger.LogInformation(
            "Mirror completed for {SourceUrl}. FilesSaved: {FilesSaved}, Entry: {EntryFile}",
            startUri, mirror.TotalFilesWritten, entryFilePath);

        return new MirrorResult
        {
            SourceUrl = startUri.ToString(),
            SiteHost = siteHost,
            Version = normalizedVersion,
            DefaultLanguage = localizationResult.DefaultLanguage,
            AvailableLanguages = localizationResult.AvailableLanguages,
            FinalUrl = finalUri.ToString(),
            OutputFolder = siteOutputPath,
            EntryFilePath = entryFilePath,
            EntryFileRelativePath = relativeEntry,
            FrontendPreviewPath = frontendPreviewPath,
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
        var htmlDirectory = new DirectoryInfo(Path.GetDirectoryName(htmlFilePath)!);
        var directory = htmlDirectory;
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

        if (hostDirectory.FullName == htmlDirectory.FullName)
        {
            return hostDirectory.FullName;
        }

        var relativeInsideHost = Path.GetRelativePath(hostDirectory.FullName, htmlDirectory.FullName);
        var firstSegment = relativeInsideHost
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault(segment => !string.IsNullOrWhiteSpace(segment));
        if (!string.IsNullOrWhiteSpace(firstSegment))
        {
            return Path.Combine(hostDirectory.FullName, firstSegment);
        }

        return hostDirectory.FullName;
    }

    private static bool LooksLikeHostSegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment) &&
        segment.Contains('.') &&
        !segment.Contains(Path.DirectorySeparatorChar) &&
        !segment.Contains(Path.AltDirectorySeparatorChar);

    private static bool IsHttpOrHttps(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private async Task AutoScrollPageAsync(IPage page, MirrorRequest request, CancellationToken cancellationToken)
    {
        var stepPx = request.ScrollStepPx <= 0 ? 1_200 : request.ScrollStepPx;
        var delayMs = request.ScrollDelayMs <= 0 ? 150 : request.ScrollDelayMs;
        var maxRounds = request.MaxScrollRounds <= 0 ? 20 : request.MaxScrollRounds;

        _logger.LogDebug(
            "Auto-scroll enabled. StepPx={StepPx}, DelayMs={DelayMs}, MaxRounds={MaxRounds}",
            stepPx, delayMs, maxRounds);

        for (var round = 0; round < maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reachedBottom = await page.EvaluateAsync<bool>(
                """
                ({ stepPx }) => {
                    const before = window.scrollY;
                    window.scrollBy(0, stepPx);
                    const atBottom = window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 2;
                    return atBottom || before === window.scrollY;
                }
                """,
                new { stepPx });
            await page.WaitForTimeoutAsync((float)delayMs);
            if (reachedBottom)
            {
                break;
            }
        }

        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
    }

    private async Task WaitForNetworkIdleOrContinueAsync(IPage page, float timeoutMs)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = timeoutMs
            });
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("Network idle wait timed out after auto-scroll; continuing.");
        }
    }

    private static string NormalizeRequestedUrl(string rawUrl)
    {
        var trimmed = rawUrl?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"https://{trimmed}";
    }

    private static string NormalizeVersion(string rawVersion)
    {
        var candidate = string.IsNullOrWhiteSpace(rawVersion) ? "latest" : rawVersion.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(candidate.Select(ch => invalidChars.Contains(ch) ? '-' : ch).ToArray())
            .Replace('/', '-')
            .Replace('\\', '-');
        if (string.IsNullOrWhiteSpace(sanitized) ||
            string.Equals(sanitized, ".", StringComparison.Ordinal) ||
            string.Equals(sanitized, "..", StringComparison.Ordinal))
        {
            return "latest";
        }

        return sanitized;
    }

    private static string BuildFrontendPreviewPath(string siteHost, string version, string language, string relativeEntry)
    {
        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
        return $"/mirror/{siteHost}/{version}/{LocalizationGenerator.LocalizedRootFolderName}/{normalizedLanguage}/{relativeEntry}";
    }

    private string ResolveOutputRoot(string? configuredOutputFolder)
    {
        var configured = string.IsNullOrWhiteSpace(configuredOutputFolder)
            ? "../frontend/public/mirror"
            : configuredOutputFolder.Trim();

        if (Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.GetFullPath(Path.Combine(_contentRootPath, configured));
    }

    private async Task EnsureRuntimeScriptAvailableAsync(string outputRoot, CancellationToken cancellationToken)
    {
        var runtimeTargetPath = Path.Combine(outputRoot, MirrorRuntimeFileName);
        if (File.Exists(runtimeTargetPath))
        {
            return;
        }

        var runtimeSourcePath = Path.GetFullPath(Path.Combine(_contentRootPath, "..", "frontend", "public", "mirror", MirrorRuntimeFileName));
        if (!File.Exists(runtimeSourcePath))
        {
            _logger.LogWarning(
                "Mirror runtime script not found at {RuntimeSourcePath}. Interactive controls may fail in mirrored pages.",
                runtimeSourcePath);
            return;
        }

        var runtimeContent = await File.ReadAllTextAsync(runtimeSourcePath, cancellationToken);
        await File.WriteAllTextAsync(runtimeTargetPath, runtimeContent, cancellationToken);
    }
}
