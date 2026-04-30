using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services.Mirroring;
using AngleSharp.Dom;

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
    private readonly ICrawlRepository _crawlRepository;
    private readonly string _contentRootPath;
    private readonly string _dbConnectionString;
    private static readonly System.Text.RegularExpressions.Regex LocalePathSegmentRegex = new(
        "^[a-z]{2}(?:-[a-z]{2})?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        WriteIndented = true
    };

    public MirrorService(
        IOptions<MirrorSettings> options,
        IOptions<DatabaseSettings> dbOptions,
        ILogger<MirrorService> logger,
        ILoggerFactory loggerFactory,
        ICrawlRepository crawlRepository,
        IWebHostEnvironment hostEnvironment)
    {
        _settings = options.Value;
        _dbConnectionString = dbOptions.Value.ConnectionString ?? string.Empty;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _localizationGenerator = new LocalizationGenerator(_loggerFactory.CreateLogger<LocalizationGenerator>());
        _crawlRepository = crawlRepository;
        _contentRootPath = hostEnvironment.ContentRootPath;
    }

    public async Task<MirrorResult> MirrorAsync(MirrorRequest request, Guid? actingUserId, CancellationToken cancellationToken = default)
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

        var crawlId = $"crawl-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N", null)[..8]}";
        var linkDrillCount = Math.Max(0, request.LinkDrillCount);
        var requestedLanguages = (request.Languages is { Length: > 0 })
            ? request.Languages
            : FallbackLocalizationLanguages;
        var crawlStart = DateTimeOffset.UtcNow;

        await PersistCrawlSafeAsync(
            new CrawlRecord
            {
                CrawlId = crawlId,
                UserId = actingUserId,
                SourceUrl = startUri.ToString(),
                SiteHost = siteHost,
                Version = normalizedVersion,
                Status = "running",
                RequestedLinkLimit = linkDrillCount,
                ProcessedPages = 0,
                TotalFilesSaved = 0,
                DefaultLanguage = "en",
                AvailableLanguages = requestedLanguages,
                CreatedAtUtc = crawlStart,
                UpdatedAtUtc = crawlStart,
                ErrorMessage = null
            },
            []);

        var crawledPages = new List<PageExecutionResult>();
        var nextQueueIndex = 1; // first drill page (entry URL is queue 0)
        var runningFilesTotal = 0;
        var skippedPages = 0;

        try
        {
            _logger.LogInformation(
                "Mirror crawl {CrawlId} started for {SourceUrl}. Output: {OutputPath}, LinkDrillCount: {LinkDrillCount}, Version: {Version}",
                crawlId, startUri, siteOutputPath, linkDrillCount, normalizedVersion);

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
            await using var browserContext = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true
            });

            async Task PersistPageRowAsync(int queue, PageExecutionResult page)
            {
                var processedCount = string.Equals(page.PageStatus, "failed", StringComparison.Ordinal)
                    ? Math.Max(0, queue)
                    : queue + 1;
                await PersistCrawlSafeAsync(
                    new CrawlRecord
                    {
                        CrawlId = crawlId,
                        UserId = actingUserId,
                        SourceUrl = startUri.ToString(),
                        SiteHost = siteHost,
                        Version = normalizedVersion,
                        Status = "running",
                        RequestedLinkLimit = linkDrillCount,
                        ProcessedPages = processedCount,
                        TotalFilesSaved = runningFilesTotal,
                        DefaultLanguage = "en",
                        AvailableLanguages = requestedLanguages,
                        CreatedAtUtc = crawlStart,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        ErrorMessage = null
                    },
                    [BuildPageRecord(crawlId, queue, page, siteHost, normalizedVersion)]);
            }

            async Task<PageExecutionResult> EntryPageOrSkipAsync()
            {
                var key = CrawlKeyHelper.NormalizeUriKey(startUri);
                var prior = await _crawlRepository.TryGetCompletedPageAsync(siteHost, normalizedVersion, key, actingUserId, cancellationToken);
                if (linkDrillCount > 0 && prior is null)
                {
                    return await RunFirstPageWithFailureBoundaryAsync();
                }

                if (prior is not null)
                {
                    var skipped = TryBuildSkippedPageResult(
                        prior, startUri.ToString(), key, siteOutputPath, siteHost, normalizedVersion);
                    if (skipped is not null)
                    {
                        if (linkDrillCount > 0 &&
                            !string.IsNullOrWhiteSpace(skipped.EntryFilePath) &&
                            File.Exists(skipped.EntryFilePath) &&
                            Uri.TryCreate(skipped.FinalUrl, UriKind.Absolute, out var docUri))
                        {
                            var html = await File.ReadAllTextAsync(skipped.EntryFilePath, cancellationToken);
                            return skipped with
                            {
                                DiscoveredLinks = ExtractCandidateLinks(docUri, html, startUri)
                            };
                        }

                        return skipped;
                    }
                }

                return await RunFirstPageWithFailureBoundaryAsync();
            }

            var firstPage = await EntryPageOrSkipAsync();
            if (firstPage.SkippedFromStore)
            {
                skippedPages++;
            }

            async Task<PageExecutionResult> RunFirstPageWithFailureBoundaryAsync()
            {
                try
                {
                    return await MirrorPageCoreAsync(
                        startUri, startUri, siteOutputPath, siteHost, normalizedVersion, waitMs, request, browserContext,
                        collectLinks: true, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var key = CrawlKeyHelper.NormalizeUriKey(startUri);
                    var failed = BuildFailedPageStub(startUri.ToString(), ex, key);
                    await PersistPageRowAsync(0, failed);
                    throw;
                }
            }

            crawledPages.Add(firstPage);
            runningFilesTotal += firstPage.SkippedFromStore ? 0 : firstPage.FilesSaved;

            await PersistPageRowAsync(0, firstPage);

            var queuedLinks = BuildNonRecursiveQueue(startUri, firstPage.DiscoveredLinks, linkDrillCount);
            foreach (var queuedUri in queuedLinks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var qKey = CrawlKeyHelper.NormalizeUriKey(queuedUri);
                var priorQueued = await _crawlRepository.TryGetCompletedPageAsync(siteHost, normalizedVersion, qKey, actingUserId, cancellationToken);
                var skippedQueued = priorQueued is not null
                    ? TryBuildSkippedPageResult(priorQueued, queuedUri.ToString(), qKey, siteOutputPath, siteHost, normalizedVersion)
                    : null;
                PageExecutionResult pageResult;
                if (skippedQueued is not null)
                {
                    pageResult = skippedQueued;
                    skippedPages++;
                }
                else
                {
                    try
                    {
                        pageResult = await MirrorPageCoreAsync(
                            queuedUri, startUri, siteOutputPath, siteHost, normalizedVersion, waitMs, request, browserContext,
                            collectLinks: false, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var failed = BuildFailedPageStub(queuedUri.ToString(), ex, qKey);
                        await PersistPageRowAsync(nextQueueIndex, failed);
                        throw;
                    }
                }

                crawledPages.Add(pageResult);
                runningFilesTotal += pageResult.SkippedFromStore ? 0 : pageResult.FilesSaved;
                await PersistPageRowAsync(nextQueueIndex, pageResult);
                nextQueueIndex++;
            }

            _logger.LogInformation("Stage generate-localizations: generating localized copies for languages {Languages}",
                string.Join(", ", requestedLanguages));
            var localizationResult = await _localizationGenerator.GenerateLocalizedCopiesAsync(
                siteOutputPath,
                requestedLanguages,
                request.DoNotTranslateTexts,
                request.GeneralTranslationClasses,
                cancellationToken);
            _logger.LogInformation("Stage generate-localizations: complete");

            var pageInfos = crawledPages
                .Select(page => new CrawlPageInfo
                {
                    Url = page.RequestedUrl,
                    FinalUrl = page.FinalUrl,
                    FrontendPreviewPath = BuildFrontendPreviewPath(siteHost, normalizedVersion, localizationResult.DefaultLanguage, page.EntryFileRelativePath),
                    EntryFileRelativePath = page.EntryFileRelativePath,
                    FilesSaved = page.FilesSaved,
                    PageStatus = page.PageStatus
                })
                .ToList();
            var totalFilesSaved = pageInfos.Where(p => p.PageStatus != "skipped").Sum(p => p.FilesSaved);

            await PersistCrawlSafeAsync(
                new CrawlRecord
                {
                    CrawlId = crawlId,
                    UserId = actingUserId,
                    SourceUrl = startUri.ToString(),
                    SiteHost = siteHost,
                    Version = normalizedVersion,
                    Status = "completed",
                    RequestedLinkLimit = linkDrillCount,
                    ProcessedPages = pageInfos.Count,
                    TotalFilesSaved = totalFilesSaved,
                    DefaultLanguage = localizationResult.DefaultLanguage,
                    AvailableLanguages = localizationResult.AvailableLanguages,
                    CreatedAtUtc = crawlStart,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    ErrorMessage = null
                },
                []);

            var firstPageInfo = pageInfos[0];
            var firstExecution = crawledPages[0];
            return new MirrorResult
            {
                CrawlId = crawlId,
                SourceUrl = startUri.ToString(),
                SiteHost = siteHost,
                Version = normalizedVersion,
                DefaultLanguage = localizationResult.DefaultLanguage,
                AvailableLanguages = localizationResult.AvailableLanguages,
                FinalUrl = firstPageInfo.FinalUrl,
                OutputFolder = siteOutputPath,
                EntryFilePath = firstExecution.EntryFilePath,
                EntryFileRelativePath = firstPageInfo.EntryFileRelativePath,
                FrontendPreviewPath = firstPageInfo.FrontendPreviewPath,
                RequestedLinkLimit = linkDrillCount,
                ProcessedPages = pageInfos.Count,
                SkippedPages = skippedPages,
                Pages = pageInfos,
                FilesSaved = totalFilesSaved,
                UsedChromiumExecutablePath = chromiumExecutablePath,
                WaitMs = waitMs
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errTotal = 0;
            for (var i = 0; i < crawledPages.Count; i++)
            {
                if (!crawledPages[i].SkippedFromStore)
                {
                    errTotal += crawledPages[i].FilesSaved;
                }
            }

            await PersistCrawlSafeAsync(
                new CrawlRecord
                {
                    CrawlId = crawlId,
                    UserId = actingUserId,
                    SourceUrl = startUri.ToString(),
                    SiteHost = siteHost,
                    Version = normalizedVersion,
                    Status = "failed",
                    RequestedLinkLimit = linkDrillCount,
                    ProcessedPages = crawledPages.Count,
                    TotalFilesSaved = errTotal,
                    DefaultLanguage = "en",
                    AvailableLanguages = requestedLanguages,
                    CreatedAtUtc = crawlStart,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    ErrorMessage = ex.Message
                },
                []);
            throw;
        }
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

    public async Task<CrawlStatusResult?> GetCrawlStatusAsync(string crawlId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(crawlId))
        {
            throw new ArgumentException("crawlId is required.", nameof(crawlId));
        }

        return await _crawlRepository.GetCrawlAsync(crawlId, cancellationToken);
    }

    public async Task<UpdateTranslationsResult> UpdateTranslationsAsync(
        UpdateTranslationsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SiteHost))
        {
            throw new ArgumentException("SiteHost is required.", nameof(request.SiteHost));
        }

        var language = request.Language?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(language))
        {
            throw new ArgumentException("Language is required.", nameof(request.Language));
        }

        if (language.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-'))
        {
            throw new ArgumentException("Language must contain only letters, numbers, and '-'.", nameof(request.Language));
        }

        var normalizedHost = MirrorPathHelper.SanitizePathSegment(request.SiteHost.Trim());
        var normalizedVersion = NormalizeVersion(request.Version);
        var outputRoot = ResolveOutputRoot(_settings.OutputFolder);
        var siteOutputPath = Path.Combine(outputRoot, normalizedHost, normalizedVersion);
        if (!Directory.Exists(siteOutputPath))
        {
            throw new DirectoryNotFoundException($"Mirror folder not found: {siteOutputPath}");
        }

        var i18nFolder = Path.Combine(siteOutputPath, LocalizationGenerator.CatalogRootFolderName);
        Directory.CreateDirectory(i18nFolder);
        var catalogPath = Path.Combine(i18nFolder, $"{language}.json");

        var mergedEntries = await LoadLanguageEntriesAsync(catalogPath, cancellationToken);
        var incomingEntries = request.Entries ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in incomingEntries)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            mergedEntries[key.Trim()] = value ?? string.Empty;
        }

        var payload = new TranslationCatalogPayload
        {
            Language = language,
            Entries = mergedEntries
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        };
        var catalogJson = JsonSerializer.Serialize(payload, CatalogJsonOptions);
        await File.WriteAllTextAsync(catalogPath, catalogJson, Encoding.UTF8, cancellationToken);

        var rebuiltPages = await _localizationGenerator.RegenerateLocalizedPagesAsync(
            siteOutputPath,
            language,
            request.TargetPages,
            request.DoNotTranslateTexts,
            cancellationToken);

        return new UpdateTranslationsResult
        {
            SiteHost = normalizedHost,
            Version = normalizedVersion,
            Language = language,
            CatalogPath = catalogPath,
            LocalizedOutputPath = Path.Combine(siteOutputPath, LocalizationGenerator.LocalizedRootFolderName, language),
            UpdatedEntryCount = incomingEntries.Count,
            RebuiltPageCount = rebuiltPages.Count,
            RebuiltPages = rebuiltPages
        };
    }

    public async Task<FixPageLinksResult> FixPageLinksAsync(
        FixPageLinksRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SiteHost))
        {
            throw new ArgumentException("SiteHost is required.", nameof(request.SiteHost));
        }

        var language = request.Language?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(language))
        {
            throw new ArgumentException("Language is required.", nameof(request.Language));
        }

        var siteHost = MirrorPathHelper.SanitizePathSegment(request.SiteHost.Trim());
        var version = NormalizeVersion(request.Version);
        var outputRoot = ResolveOutputRoot(_settings.OutputFolder);
        var localizedRoot = Path.Combine(
            outputRoot,
            siteHost,
            version,
            LocalizationGenerator.LocalizedRootFolderName,
            language);

        if (!Directory.Exists(localizedRoot))
        {
            throw new DirectoryNotFoundException($"Localized folder not found: {localizedRoot}");
        }

        var files = ResolveLocalizedPages(localizedRoot, request.PagePath);
        var parser = new HtmlParser();
        var processed = new List<string>(files.Count);
        var linksFixed = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var html = await File.ReadAllTextAsync(file, cancellationToken);
            var document = await parser.ParseDocumentAsync(html, cancellationToken);
            linksFixed += FixDocumentLinks(document, siteHost, version, language);
            var updated = document.DocumentElement?.OuterHtml ?? html;
            await File.WriteAllTextAsync(file, updated, cancellationToken);
            processed.Add(Path.GetRelativePath(localizedRoot, file).Replace('\\', '/'));
        }

        return new FixPageLinksResult
        {
            SiteHost = siteHost,
            Version = version,
            Language = language,
            PagesProcessed = processed.Count,
            LinksFixed = linksFixed,
            ProcessedPages = processed
        };
    }

    public async Task<UpdateBlockTranslationsResult> UpdateBlockTranslationsAsync(
        UpdateBlockTranslationsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SiteHost))
        {
            throw new ArgumentException("SiteHost is required.", nameof(request.SiteHost));
        }

        if (string.IsNullOrWhiteSpace(request.PagePath))
        {
            throw new ArgumentException("PagePath is required.", nameof(request.PagePath));
        }

        var language = request.Language?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(language))
        {
            throw new ArgumentException("Language is required.", nameof(request.Language));
        }

        var siteHost = MirrorPathHelper.SanitizePathSegment(request.SiteHost.Trim());
        var version = NormalizeVersion(request.Version);
        var outputRoot = ResolveOutputRoot(_settings.OutputFolder);
        var siteOutputPath = Path.Combine(outputRoot, siteHost, version);
        if (!Directory.Exists(siteOutputPath))
        {
            throw new DirectoryNotFoundException($"Mirror folder not found: {siteOutputPath}");
        }

        var normalizedPagePath = NormalizeBlockPagePath(request.PagePath);
        var i18nFolder = Path.Combine(siteOutputPath, LocalizationGenerator.CatalogRootFolderName);
        var blockPath = Path.Combine(
            i18nFolder,
            LocalizationGenerator.PerPageBlocksFolderName,
            normalizedPagePath.Replace('/', Path.DirectorySeparatorChar) + ".json");
        if (!File.Exists(blockPath))
        {
            throw new FileNotFoundException($"Block page not found: {normalizedPagePath}.json", blockPath);
        }

        var raw = await File.ReadAllTextAsync(blockPath, Encoding.UTF8, cancellationToken);
        BlockPagePayload blockDoc;
        try
        {
            blockDoc = JsonSerializer.Deserialize<BlockPagePayload>(raw) ?? new BlockPagePayload();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid block page JSON at {blockPath}.", ex);
        }

        var incoming = request.Entries ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (incoming.Count > 0)
        {
            var incomingById = new Dictionary<string, string>(incoming, StringComparer.Ordinal);
            for (var i = 0; i < blockDoc.Blocks.Count; i++)
            {
                var block = blockDoc.Blocks[i];
                if (string.IsNullOrWhiteSpace(block.Id))
                {
                    continue;
                }

                if (incomingById.TryGetValue(block.Id, out var translated))
                {
                    blockDoc.Blocks[i] = block with { Translated = translated ?? string.Empty };
                }
            }

            for (var g = 0; g < blockDoc.Groups.Count; g++)
            {
                var group = blockDoc.Groups[g];
                if (group.Blocks.Count == 0)
                {
                    continue;
                }

                var updatedBlocks = new List<BlockItemPayload>(group.Blocks.Count);
                foreach (var block in group.Blocks)
                {
                    if (string.IsNullOrWhiteSpace(block.Id))
                    {
                        updatedBlocks.Add(block);
                        continue;
                    }

                    if (incomingById.TryGetValue(block.Id, out var translated))
                    {
                        updatedBlocks.Add(block with { Translated = translated ?? string.Empty });
                    }
                    else
                    {
                        updatedBlocks.Add(block);
                    }
                }

                blockDoc.Groups[g] = group with { Blocks = updatedBlocks };
            }
        }

        var updatedJson = JsonSerializer.Serialize(blockDoc, CatalogJsonOptions);
        await File.WriteAllTextAsync(blockPath, updatedJson, Encoding.UTF8, cancellationToken);

        // Merge translated block content into language catalog by source text,
        // then rebuild only the requested page for the selected language.
        var languageCatalogPath = Path.Combine(i18nFolder, $"{language}.json");
        var mergedEntries = await LoadLanguageEntriesAsync(languageCatalogPath, cancellationToken);
        var flattenedBlocks = FlattenBlockItems(blockDoc);
        foreach (var block in flattenedBlocks)
        {
            var source = block.Original?.Trim() ?? string.Empty;
            var translated = block.Translated?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }

            if (string.Equals(source, translated, StringComparison.Ordinal))
            {
                continue;
            }

            mergedEntries[source] = translated;
        }

        var languagePayload = new TranslationCatalogPayload
        {
            Language = language,
            Entries = mergedEntries
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        };
        var languageJson = JsonSerializer.Serialize(languagePayload, CatalogJsonOptions);
        await File.WriteAllTextAsync(languageCatalogPath, languageJson, Encoding.UTF8, cancellationToken);

        var rebuiltPages = await _localizationGenerator.RegenerateLocalizedPagesAsync(
            siteOutputPath,
            language,
            [$"{normalizedPagePath}.html"],
            doNotTranslateTexts: null,
            cancellationToken);

        return new UpdateBlockTranslationsResult
        {
            SiteHost = siteHost,
            Version = version,
            Language = language,
            PagePath = normalizedPagePath,
            BlockFilePath = blockPath,
            UpdatedEntryCount = incoming.Count,
            RebuiltPageCount = rebuiltPages.Count,
            RebuiltPages = rebuiltPages
        };
    }

    public async Task<ApplyCommonBlockTranslationsResult> ApplyCommonBlockTranslationsAsync(
        string siteHost,
        string version,
        string language,
        Stream fileStream,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteHost))
        {
            throw new ArgumentException("SiteHost is required.", nameof(siteHost));
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            throw new ArgumentException("Language is required.", nameof(language));
        }

        var normalizedHost = MirrorPathHelper.SanitizePathSegment(siteHost.Trim());
        var normalizedVersion = NormalizeVersion(version);
        var normalizedLanguage = language.Trim().ToLowerInvariant();
        var outputRoot = ResolveOutputRoot(_settings.OutputFolder);
        var siteOutputPath = Path.Combine(outputRoot, normalizedHost, normalizedVersion);
        if (!Directory.Exists(siteOutputPath))
        {
            throw new DirectoryNotFoundException($"Mirror folder not found: {siteOutputPath}");
        }

        var i18nFolder = Path.Combine(siteOutputPath, LocalizationGenerator.CatalogRootFolderName);
        var sourceCatalogPath = Path.Combine(i18nFolder, "source.json");
        var sourceEntries = await LoadLanguageEntriesAsync(sourceCatalogPath, cancellationToken);
        var incoming = await ParseCommonBlockEntriesAsync(fileStream, sourceEntries, cancellationToken);
        var blocksFolder = Path.Combine(i18nFolder, LocalizationGenerator.PerPageBlocksFolderName);
        Directory.CreateDirectory(blocksFolder);
        var commonPath = Path.Combine(blocksFolder, LocalizationGenerator.CommonBlockFileName);

        var existing = await LoadCommonCatalogAsync(commonPath, cancellationToken);
        var merged = new Dictionary<string, CommonBlockEntryPayload>(existing, StringComparer.Ordinal);
        foreach (var entry in incoming)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            if (merged.TryGetValue(entry.Key, out var existingEntry))
            {
                merged[entry.Key] = existingEntry with
                {
                    Original = string.IsNullOrWhiteSpace(entry.Original) ? existingEntry.Original : entry.Original,
                    Translated = string.IsNullOrWhiteSpace(entry.Translated) ? existingEntry.Translated : entry.Translated
                };
            }
            else
            {
                merged[entry.Key] = entry;
            }
        }

        var commonPayload = new CommonBlockCatalogPayload
        {
            Language = normalizedLanguage,
            Entries = merged.Values
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToList()
        };
        var commonJson = JsonSerializer.Serialize(commonPayload, CatalogJsonOptions);
        await File.WriteAllTextAsync(commonPath, commonJson, Encoding.UTF8, cancellationToken);

        var languageCatalogPath = Path.Combine(i18nFolder, $"{normalizedLanguage}.json");
        var mergedLanguageEntries = await LoadLanguageEntriesAsync(languageCatalogPath, cancellationToken);
        foreach (var entry in merged.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.Original) ||
                string.IsNullOrWhiteSpace(entry.Translated) ||
                string.Equals(entry.Original, entry.Translated, StringComparison.Ordinal))
            {
                continue;
            }

            mergedLanguageEntries[entry.Original] = entry.Translated;
        }

        var languagePayload = new TranslationCatalogPayload
        {
            Language = normalizedLanguage,
            Entries = mergedLanguageEntries
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        };
        var languageJson = JsonSerializer.Serialize(languagePayload, CatalogJsonOptions);
        await File.WriteAllTextAsync(languageCatalogPath, languageJson, Encoding.UTF8, cancellationToken);

        var rebuiltPages = await _localizationGenerator.RegenerateLocalizedPagesAsync(
            siteOutputPath,
            normalizedLanguage,
            targetPages: null,
            doNotTranslateTexts: null,
            cancellationToken);

        return new ApplyCommonBlockTranslationsResult
        {
            SiteHost = normalizedHost,
            Version = normalizedVersion,
            Language = normalizedLanguage,
            CommonFilePath = commonPath,
            UpdatedCommonCount = merged.Count,
            RebuiltPageCount = rebuiltPages.Count,
            RebuiltPages = rebuiltPages
        };
    }

    public async Task<ApplyCommonBlockTranslationsResult> UpdateCommonBlockTranslationsAsync(
        UpdateCommonBlockTranslationsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SiteHost))
        {
            throw new ArgumentException("SiteHost is required.", nameof(request.SiteHost));
        }

        if (string.IsNullOrWhiteSpace(request.Language))
        {
            throw new ArgumentException("Language is required.", nameof(request.Language));
        }

        var normalizedHost = MirrorPathHelper.SanitizePathSegment(request.SiteHost.Trim());
        var normalizedVersion = NormalizeVersion(request.Version);
        var normalizedLanguage = request.Language.Trim().ToLowerInvariant();
        var outputRoot = ResolveOutputRoot(_settings.OutputFolder);
        var siteOutputPath = Path.Combine(outputRoot, normalizedHost, normalizedVersion);
        if (!Directory.Exists(siteOutputPath))
        {
            throw new DirectoryNotFoundException($"Mirror folder not found: {siteOutputPath}");
        }

        var i18nFolder = Path.Combine(siteOutputPath, LocalizationGenerator.CatalogRootFolderName);
        var sourceCatalogPath = Path.Combine(i18nFolder, "source.json");
        var sourceEntries = await LoadLanguageEntriesAsync(sourceCatalogPath, cancellationToken);
        var sourceByTextKey = sourceEntries
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => LocalizationGenerator.BuildGlobalTextKey(x.Value), x => x.Value, StringComparer.Ordinal);

        var blocksFolder = Path.Combine(i18nFolder, LocalizationGenerator.PerPageBlocksFolderName);
        Directory.CreateDirectory(blocksFolder);
        var commonPath = Path.Combine(blocksFolder, LocalizationGenerator.CommonBlockFileName);
        var existing = await LoadCommonCatalogAsync(commonPath, cancellationToken);
        var merged = new Dictionary<string, CommonBlockEntryPayload>(existing, StringComparer.Ordinal);

        foreach (var pair in request.Entries ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            var rawKey = pair.Key?.Trim() ?? string.Empty;
            var translated = pair.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                continue;
            }

            string key;
            string original;
            if (rawKey.StartsWith("k_", StringComparison.Ordinal))
            {
                key = rawKey;
                merged.TryGetValue(key, out var existingEntry);
                if (!sourceEntries.TryGetValue(key, out original!) && existingEntry is null)
                {
                    continue;
                }
                original = sourceEntries.TryGetValue(key, out var sourceOriginal)
                    ? sourceOriginal
                    : existingEntry.Original;
            }
            else
            {
                original = rawKey;
                key = LocalizationGenerator.BuildGlobalTextKey(original);
                if (sourceByTextKey.TryGetValue(key, out var sourceOriginal))
                {
                    original = sourceOriginal;
                }
            }

            merged[key] = new CommonBlockEntryPayload
            {
                Key = key,
                Original = original,
                Translated = translated
            };
        }

        var commonPayload = new CommonBlockCatalogPayload
        {
            Language = normalizedLanguage,
            Entries = merged.Values
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToList()
        };
        var commonJson = JsonSerializer.Serialize(commonPayload, CatalogJsonOptions);
        await File.WriteAllTextAsync(commonPath, commonJson, Encoding.UTF8, cancellationToken);

        var languageCatalogPath = Path.Combine(i18nFolder, $"{normalizedLanguage}.json");
        var mergedLanguageEntries = await LoadLanguageEntriesAsync(languageCatalogPath, cancellationToken);
        foreach (var entry in merged.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.Original) ||
                string.IsNullOrWhiteSpace(entry.Translated) ||
                string.Equals(entry.Original, entry.Translated, StringComparison.Ordinal))
            {
                continue;
            }
            mergedLanguageEntries[entry.Original] = entry.Translated;
        }

        var languagePayload = new TranslationCatalogPayload
        {
            Language = normalizedLanguage,
            Entries = mergedLanguageEntries
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        };
        var languageJson = JsonSerializer.Serialize(languagePayload, CatalogJsonOptions);
        await File.WriteAllTextAsync(languageCatalogPath, languageJson, Encoding.UTF8, cancellationToken);

        var rebuiltPages = await _localizationGenerator.RegenerateLocalizedPagesAsync(
            siteOutputPath,
            normalizedLanguage,
            targetPages: null,
            doNotTranslateTexts: null,
            cancellationToken);

        return new ApplyCommonBlockTranslationsResult
        {
            SiteHost = normalizedHost,
            Version = normalizedVersion,
            Language = normalizedLanguage,
            CommonFilePath = commonPath,
            UpdatedCommonCount = merged.Count,
            RebuiltPageCount = rebuiltPages.Count,
            RebuiltPages = rebuiltPages
        };
    }

    public async Task<CreateInjectionAssetResult> CreateInjectionAssetAsync(
        CreateInjectionAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SiteHost))
        {
            throw new ArgumentException("SiteHost is required.", nameof(request.SiteHost));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required.", nameof(request.Name));
        }

        var assetType = request.AssetType?.Trim().ToLowerInvariant();
        if (assetType is not ("css" or "js"))
        {
            throw new ArgumentException("AssetType must be css or js.", nameof(request.AssetType));
        }

        var siteHost = MirrorPathHelper.SanitizePathSegment(request.SiteHost.Trim());
        var version = NormalizeVersion(request.Version);
        var outputRoot = ResolveOutputRoot(_settings.OutputFolder);
        var siteOutputPath = Path.Combine(outputRoot, siteHost, version);
        if (!Directory.Exists(siteOutputPath))
        {
            throw new DirectoryNotFoundException($"Mirror folder not found: {siteOutputPath}");
        }

        var safeName = MirrorPathHelper.SanitizePathSegment(request.Name.Trim().ToLowerInvariant());
        var assetId = $"inj_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..30];
        var fileName = $"{safeName}-{assetId}.{assetType}";
        var relativeFilePath = Path.Combine("_injections", fileName).Replace('\\', '/');
        var absoluteFilePath = Path.Combine(siteOutputPath, "_injections", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteFilePath)!);
        await File.WriteAllTextAsync(absoluteFilePath, request.Content ?? string.Empty, Encoding.UTF8, cancellationToken);

        var targets = ResolveInjectionTargets(siteOutputPath, request.TargetPages, request.ApplyToAllPages);
        var publicAssetPath = $"/mirror/{siteHost}/{version}/{relativeFilePath}";
        var parser = new HtmlParser();
        var injected = new List<string>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await File.ReadAllTextAsync(target, Encoding.UTF8, cancellationToken);
            var document = await parser.ParseDocumentAsync(raw, cancellationToken);
            var changed = assetType == "css"
                ? InjectCss(document, publicAssetPath)
                : InjectJs(document, publicAssetPath);
            if (!changed)
            {
                continue;
            }

            var html = document.DocumentElement?.OuterHtml ?? raw;
            await File.WriteAllTextAsync(target, html, Encoding.UTF8, cancellationToken);
            injected.Add(Path.GetRelativePath(siteOutputPath, target).Replace('\\', '/'));
        }

        await SaveInjectionMetadataAsync(
            assetId,
            siteHost,
            version,
            assetType,
            request.Name.Trim(),
            request.Description ?? string.Empty,
            relativeFilePath,
            injected,
            cancellationToken);

        return new CreateInjectionAssetResult
        {
            SiteHost = siteHost,
            Version = version,
            AssetId = assetId,
            AssetType = assetType,
            Name = request.Name.Trim(),
            Description = request.Description ?? string.Empty,
            RelativeFilePath = relativeFilePath,
            PagesInjected = injected.Count,
            InjectedPages = injected
        };
    }

    public async Task<IReadOnlyList<InjectionAssetDto>> GetInjectionAssetsAsync(
        string siteHost,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_dbConnectionString))
        {
            return [];
        }

        var normalizedHost = MirrorPathHelper.SanitizePathSegment(siteHost.Trim());
        var normalizedVersion = NormalizeVersion(version);
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_dbConnectionString);
        await connection.OpenAsync(cancellationToken);

        const string q = """
            SELECT AssetId, SiteHost, Version, AssetType, Name, Description, RelativeFilePath, CreatedAtUtc
            FROM dbo.InjectionAssets
            WHERE SiteHost = @SiteHost AND Version = @Version
            ORDER BY CreatedAtUtc DESC;
            """;
        var list = new List<InjectionAssetDto>();
        await using (var cmd = new Microsoft.Data.SqlClient.SqlCommand(q, connection))
        {
            cmd.Parameters.AddWithValue("@SiteHost", normalizedHost);
            cmd.Parameters.AddWithValue("@Version", normalizedVersion);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new InjectionAssetDto
                {
                    AssetId = reader.GetString(0),
                    SiteHost = reader.GetString(1),
                    Version = reader.GetString(2),
                    AssetType = reader.GetString(3),
                    Name = reader.GetString(4),
                    Description = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    RelativeFilePath = reader.GetString(6),
                    CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(7),
                    TargetPages = []
                });
            }
        }

        for (var i = 0; i < list.Count; i++)
        {
            var targets = await LoadInjectionTargetsAsync(connection, list[i].AssetId, cancellationToken);
            var row = list[i];
            list[i] = new InjectionAssetDto
            {
                AssetId = row.AssetId,
                SiteHost = row.SiteHost,
                Version = row.Version,
                AssetType = row.AssetType,
                Name = row.Name,
                Description = row.Description,
                RelativeFilePath = row.RelativeFilePath,
                CreatedAtUtc = row.CreatedAtUtc,
                TargetPages = targets
            };
        }

        return list;
    }

    public async Task<InjectionAssetDto?> GetInjectionAssetAsync(string assetId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_dbConnectionString) || string.IsNullOrWhiteSpace(assetId))
        {
            return null;
        }

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_dbConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string q = """
            SELECT AssetId, SiteHost, Version, AssetType, Name, Description, RelativeFilePath, CreatedAtUtc
            FROM dbo.InjectionAssets
            WHERE AssetId = @AssetId;
            """;
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(q, connection);
        cmd.Parameters.AddWithValue("@AssetId", assetId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var dto = new InjectionAssetDto
        {
            AssetId = reader.GetString(0),
            SiteHost = reader.GetString(1),
            Version = reader.GetString(2),
            AssetType = reader.GetString(3),
            Name = reader.GetString(4),
            Description = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            RelativeFilePath = reader.GetString(6),
            CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(7),
            TargetPages = []
        };
        await reader.CloseAsync();
        var targets = await LoadInjectionTargetsAsync(connection, assetId, cancellationToken);
        return new InjectionAssetDto
        {
            AssetId = dto.AssetId,
            SiteHost = dto.SiteHost,
            Version = dto.Version,
            AssetType = dto.AssetType,
            Name = dto.Name,
            Description = dto.Description,
            RelativeFilePath = dto.RelativeFilePath,
            CreatedAtUtc = dto.CreatedAtUtc,
            TargetPages = targets
        };
    }

    public async Task<InjectionAssetDto> UpdateInjectionAssetAsync(
        string assetId,
        UpdateInjectionAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        var asset = await GetInjectionAssetAsync(assetId, cancellationToken)
                    ?? throw new FileNotFoundException($"Injection asset not found: {assetId}");

        if (string.IsNullOrWhiteSpace(_dbConnectionString))
        {
            throw new InvalidOperationException("Database connection is not configured.");
        }

        var outputRoot = ResolveOutputRoot(_settings.OutputFolder);
        var siteRoot = Path.Combine(outputRoot, asset.SiteHost, asset.Version);
        var absoluteFilePath = Path.Combine(siteRoot, asset.RelativeFilePath.Replace('/', Path.DirectorySeparatorChar));
        if (request.Content is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absoluteFilePath)!);
            await File.WriteAllTextAsync(absoluteFilePath, request.Content, Encoding.UTF8, cancellationToken);
        }

        var updatedName = string.IsNullOrWhiteSpace(request.Name) ? asset.Name : request.Name.Trim();
        var updatedDescription = request.Description ?? asset.Description;

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_dbConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string q = """
            UPDATE dbo.InjectionAssets
            SET Name = @Name, Description = @Description
            WHERE AssetId = @AssetId;
            """;
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(q, connection);
        cmd.Parameters.AddWithValue("@Name", updatedName);
        cmd.Parameters.AddWithValue("@Description", updatedDescription);
        cmd.Parameters.AddWithValue("@AssetId", assetId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return (await GetInjectionAssetAsync(assetId, cancellationToken))!;
    }

    public async Task DeleteInjectionAssetAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var asset = await GetInjectionAssetAsync(assetId, cancellationToken);
        if (asset is null || string.IsNullOrWhiteSpace(_dbConnectionString))
        {
            return;
        }

        var outputRoot = ResolveOutputRoot(_settings.OutputFolder);
        var siteRoot = Path.Combine(outputRoot, asset.SiteHost, asset.Version);
        var absoluteFilePath = Path.Combine(siteRoot, asset.RelativeFilePath.Replace('/', Path.DirectorySeparatorChar));
        var publicPath = $"/mirror/{asset.SiteHost}/{asset.Version}/{asset.RelativeFilePath}";

        var parser = new HtmlParser();
        foreach (var rel in asset.TargetPages)
        {
            var full = Path.Combine(siteRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }

            var raw = await File.ReadAllTextAsync(full, Encoding.UTF8, cancellationToken);
            var doc = await parser.ParseDocumentAsync(raw, cancellationToken);
            var removed = false;
            foreach (var link in doc.QuerySelectorAll($"link[data-site-mirror-injection][href='{publicPath}']").ToArray())
            {
                link.Remove();
                removed = true;
            }
            foreach (var script in doc.QuerySelectorAll($"script[data-site-mirror-injection][src='{publicPath}']").ToArray())
            {
                script.Remove();
                removed = true;
            }
            if (removed)
            {
                var html = doc.DocumentElement?.OuterHtml ?? raw;
                await File.WriteAllTextAsync(full, html, Encoding.UTF8, cancellationToken);
            }
        }

        if (File.Exists(absoluteFilePath))
        {
            File.Delete(absoluteFilePath);
        }

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_dbConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var delTargets = new Microsoft.Data.SqlClient.SqlCommand("DELETE FROM dbo.InjectionAssetTargets WHERE AssetId=@AssetId;", connection, (Microsoft.Data.SqlClient.SqlTransaction)transaction))
            {
                delTargets.Parameters.AddWithValue("@AssetId", assetId);
                await delTargets.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var delAsset = new Microsoft.Data.SqlClient.SqlCommand("DELETE FROM dbo.InjectionAssets WHERE AssetId=@AssetId;", connection, (Microsoft.Data.SqlClient.SqlTransaction)transaction))
            {
                delAsset.Parameters.AddWithValue("@AssetId", assetId);
                await delAsset.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<PageExecutionResult> MirrorPageCoreAsync(
        Uri requestedUri,
        Uri rootUri,
        string siteOutputPath,
        string siteHost,
        string version,
        int waitMs,
        MirrorRequest request,
        IBrowserContext browserContext,
        bool collectLinks,
        CancellationToken cancellationToken)
    {
        var page = await browserContext.NewPageAsync();
        var mirror = new MirrorState(rootUri, siteOutputPath, _loggerFactory.CreateLogger<MirrorState>());
        var responseTasks = new ConcurrentBag<Task>();

        EventHandler<IResponse> responseHandler = (_, response) =>
        {
            responseTasks.Add(PersistResponseAsync(response, mirror));
        };
        page.Response += responseHandler;

        string renderedHtml;
        try
        {
            await page.GotoAsync(requestedUri.ToString(), new PageGotoOptions
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
                _logger.LogWarning("Network idle wait timed out for {Url}; continuing with rendered content.", requestedUri);
            }

            if (request.AutoScroll)
            {
                await AutoScrollPageAsync(page, request, cancellationToken);
                await WaitForNetworkIdleOrContinueAsync(page, 10_000);
            }

            await page.WaitForTimeoutAsync(waitMs);
            renderedHtml = await page.ContentAsync();
            _logger.LogInformation("Page load completed for {CurrentUrl}", page.Url);
        }
        finally
        {
            page.Response -= responseHandler;
        }

        await WaitForPendingResponseSavesAsync(responseTasks, cancellationToken);
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var finalUri))
        {
            finalUri = requestedUri;
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
        var discoveredLinks = collectLinks
            ? ExtractCandidateLinks(finalUri, renderedHtml, rootUri)
            : [];

        await page.CloseAsync();
        return new PageExecutionResult
        {
            RequestedUrl = requestedUri.ToString(),
            RequestedUrlKey = CrawlKeyHelper.NormalizeUriKey(requestedUri),
            FinalUrl = finalUri.ToString(),
            EntryFilePath = entryFilePath,
            EntryFileRelativePath = relativeEntry,
            FrontendPreviewPath = BuildFrontendPreviewPath(siteHost, version, "en", relativeEntry),
            FilesSaved = mirror.TotalFilesWritten,
            DiscoveredLinks = discoveredLinks,
            PageStatus = "completed",
            PageError = null,
            SkippedFromStore = false
        };
    }

    private static List<Uri> BuildNonRecursiveQueue(Uri startUri, IReadOnlyList<Uri> discoveredLinks, int linkDrillCount)
    {
        if (linkDrillCount <= 0 || discoveredLinks.Count == 0)
        {
            return [];
        }

        var queue = new List<Uri>(linkDrillCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeUriForQueue(startUri)
        };

        foreach (var link in discoveredLinks)
        {
            var normalized = NormalizeUriForQueue(link);
            if (!seen.Add(normalized))
            {
                continue;
            }

            queue.Add(link);
            if (queue.Count >= linkDrillCount)
            {
                break;
            }
        }

        return queue;
    }

    private static string NormalizeUriForQueue(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };
        var normalizedPath = builder.Path;
        if (normalizedPath.Length > 1 && normalizedPath.EndsWith("/", StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath.TrimEnd('/');
        }

        builder.Path = normalizedPath;
        return builder.Uri.ToString();
    }

    private static List<Uri> ExtractCandidateLinks(Uri pageUri, string html, Uri rootUri)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        var links = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href) ||
                href.StartsWith('#') ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(pageUri, href, out var absolute) || !IsHttpOrHttps(absolute))
            {
                continue;
            }

            if (!IsSameSite(rootUri, absolute))
            {
                continue;
            }

            var normalized = NormalizeUriForQueue(absolute);
            if (!seen.Add(normalized))
            {
                continue;
            }

            links.Add(absolute);
        }

        return links;
    }

    private async Task RunLocalizationPassAsync(
        string siteOutputPath,
        IReadOnlyList<string> requestedLanguages,
        IReadOnlyList<string>? doNotTranslateTexts,
        IReadOnlyList<string>? generalTranslationClasses,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stage generate-localizations: generating localized copies for languages {Languages}",
            string.Join(", ", requestedLanguages));
        await _localizationGenerator.GenerateLocalizedCopiesAsync(
            siteOutputPath,
            requestedLanguages,
            doNotTranslateTexts,
            generalTranslationClasses,
            cancellationToken);
        _logger.LogInformation("Stage generate-localizations: complete");
    }

    private static bool IsSameSite(Uri referenceUri, Uri candidateUri)
    {
        var hostA = referenceUri.Host.Trim().ToLowerInvariant();
        var hostB = candidateUri.Host.Trim().ToLowerInvariant();
        if (hostA == hostB)
        {
            return true;
        }

        return hostA == $"www.{hostB}" || hostB == $"www.{hostA}";
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

    private static CrawlPageRecord BuildPageRecord(
        string crawlId,
        int queueOrder,
        PageExecutionResult page,
        string siteHost,
        string version) =>
        new()
        {
            CrawlId = crawlId,
            SiteHost = siteHost,
            Version = version,
            QueueOrder = queueOrder,
            RequestedUrl = page.RequestedUrl,
            RequestedUrlKey = string.IsNullOrEmpty(page.RequestedUrlKey) ? page.RequestedUrl : page.RequestedUrlKey,
            FinalUrl = page.FinalUrl,
            FrontendPreviewPath = page.FrontendPreviewPath,
            EntryFileRelativePath = page.EntryFileRelativePath,
            FilesSaved = page.SkippedFromStore ? 0 : page.FilesSaved,
            PageStatus = page.PageStatus,
            ErrorMessage = page.PageError,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

    private static PageExecutionResult BuildFailedPageStub(
        string requestedUrl,
        Exception ex,
        string urlKey) =>
        new()
        {
            RequestedUrl = requestedUrl,
            RequestedUrlKey = urlKey,
            FinalUrl = string.Empty,
            EntryFilePath = string.Empty,
            EntryFileRelativePath = string.Empty,
            FrontendPreviewPath = string.Empty,
            FilesSaved = 0,
            DiscoveredLinks = [],
            PageStatus = "failed",
            PageError = ex.Message,
            SkippedFromStore = false
        };

    private static PageExecutionResult? TryBuildSkippedPageResult(
        CompletedPageSnapshot prior,
        string requestedUrl,
        string requestedUrlKey,
        string siteOutputPath,
        string siteHost,
        string version)
    {
        var rel = prior.EntryFileRelativePath.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(siteOutputPath, rel));
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var key = !string.IsNullOrWhiteSpace(prior.FinalUrl) && Uri.TryCreate(prior.FinalUrl, UriKind.Absolute, out var fu)
            ? CrawlKeyHelper.NormalizeUriKey(fu)
            : requestedUrlKey;

        return new PageExecutionResult
        {
            RequestedUrl = requestedUrl,
            RequestedUrlKey = key,
            FinalUrl = string.IsNullOrWhiteSpace(prior.FinalUrl) ? requestedUrl : prior.FinalUrl,
            EntryFilePath = fullPath,
            EntryFileRelativePath = rel,
            FrontendPreviewPath = BuildFrontendPreviewPathStatic(siteHost, version, "en", rel),
            FilesSaved = prior.FilesSaved,
            DiscoveredLinks = [],
            PageStatus = "skipped",
            PageError = null,
            SkippedFromStore = true
        };
    }

    private static string BuildFrontendPreviewPathStatic(string siteHost, string version, string language, string relativeEntry) =>
        $"/mirror/{siteHost}/{version}/{LocalizationGenerator.LocalizedRootFolderName}/{(string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant())}/{relativeEntry}";

    private static async Task<Dictionary<string, string>> LoadLanguageEntriesAsync(string catalogPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(catalogPath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var raw = await File.ReadAllTextAsync(catalogPath, Encoding.UTF8, cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            if (doc.RootElement.TryGetProperty("Entries", out var entriesNode) &&
                entriesNode.ValueKind == JsonValueKind.Object)
            {
                return ReadEntries(entriesNode);
            }

            if (doc.RootElement.TryGetProperty("entries", out var entriesNodeCamel) &&
                entriesNodeCamel.ValueKind == JsonValueKind.Object)
            {
                return ReadEntries(entriesNodeCamel);
            }
        }
        catch
        {
            // If parsing fails we rebuild from incoming request entries.
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ReadEntries(JsonElement entriesNode)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in entriesNode.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return result;
    }

    private static async Task<Dictionary<string, CommonBlockEntryPayload>> LoadCommonCatalogAsync(
        string commonPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(commonPath))
        {
            return new Dictionary<string, CommonBlockEntryPayload>(StringComparer.Ordinal);
        }

        try
        {
            var raw = await File.ReadAllTextAsync(commonPath, Encoding.UTF8, cancellationToken);
            var payload = JsonSerializer.Deserialize<CommonBlockCatalogPayload>(raw);
            var result = new Dictionary<string, CommonBlockEntryPayload>(StringComparer.Ordinal);
            foreach (var item in payload?.Entries ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Key))
                {
                    continue;
                }

                result[item.Key] = item;
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, CommonBlockEntryPayload>(StringComparer.Ordinal);
        }
    }

    private static async Task<List<CommonBlockEntryPayload>> ParseCommonBlockEntriesAsync(
        Stream stream,
        IReadOnlyDictionary<string, string> sourceEntries,
        CancellationToken cancellationToken)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Root JSON must be an object.");
        }

        var result = new Dictionary<string, CommonBlockEntryPayload>(StringComparer.Ordinal);
        static void Add(
            Dictionary<string, CommonBlockEntryPayload> map,
            IReadOnlyDictionary<string, string> sources,
            string? keyOrId,
            string? original,
            string? translated)
        {
            var explicitKey = (keyOrId ?? string.Empty).Trim();
            var normalizedOriginal = (original ?? string.Empty).Trim();
            var normalizedTranslated = (translated ?? string.Empty).Trim();
            if (explicitKey.StartsWith("k_", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(normalizedOriginal) &&
                sources.TryGetValue(explicitKey, out var sourceText))
            {
                normalizedOriginal = sourceText?.Trim() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(normalizedOriginal) && !explicitKey.StartsWith("k_", StringComparison.Ordinal))
            {
                return;
            }

            var key = explicitKey.StartsWith("k_", StringComparison.Ordinal)
                ? explicitKey
                : LocalizationGenerator.BuildGlobalTextKey(normalizedOriginal);

            if (string.IsNullOrWhiteSpace(normalizedOriginal) &&
                sources.TryGetValue(key, out var sourceByKey))
            {
                normalizedOriginal = sourceByKey?.Trim() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(normalizedOriginal))
            {
                return;
            }

            map[key] = new CommonBlockEntryPayload
            {
                Key = key,
                Original = normalizedOriginal,
                Translated = normalizedTranslated
            };
        }

        void ParseBlockArray(JsonElement blocksNode)
        {
            foreach (var item in blocksNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var original = (item.TryGetProperty("original", out var o1) || item.TryGetProperty("Original", out o1)) &&
                               o1.ValueKind == JsonValueKind.String
                    ? o1.GetString()
                    : null;
                var keyOrId = (item.TryGetProperty("id", out var i1) || item.TryGetProperty("Id", out i1) ||
                               item.TryGetProperty("key", out i1) || item.TryGetProperty("Key", out i1)) &&
                              i1.ValueKind == JsonValueKind.String
                    ? i1.GetString()
                    : null;
                var translated = (item.TryGetProperty("translated", out var t1) || item.TryGetProperty("Translated", out t1)) &&
                                 t1.ValueKind == JsonValueKind.String
                    ? t1.GetString()
                    : null;
                Add(result, sourceEntries, keyOrId, original, translated);
            }
        }

        if ((doc.RootElement.TryGetProperty("blocks", out var blocksNode) ||
             doc.RootElement.TryGetProperty("Blocks", out blocksNode)) &&
            blocksNode.ValueKind == JsonValueKind.Array)
        {
            ParseBlockArray(blocksNode);
        }

        if ((doc.RootElement.TryGetProperty("groups", out var groupsNode) ||
             doc.RootElement.TryGetProperty("Groups", out groupsNode)) &&
            groupsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groupsNode.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if ((group.TryGetProperty("blocks", out var groupBlocks) ||
                     group.TryGetProperty("Blocks", out groupBlocks)) &&
                    groupBlocks.ValueKind == JsonValueKind.Array)
                {
                    ParseBlockArray(groupBlocks);
                }
            }
        }

        if (result.Count == 0 &&
            ((doc.RootElement.TryGetProperty("entries", out var entriesNode) ||
              doc.RootElement.TryGetProperty("Entries", out entriesNode)) &&
             entriesNode.ValueKind == JsonValueKind.Object))
        {
            foreach (var property in entriesNode.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                Add(result, sourceEntries, null, property.Name, property.Value.GetString());
            }
        }

        if (result.Count == 0)
        {
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = property.Name;
                if (name.StartsWith("k_", StringComparison.Ordinal))
                {
                    Add(result, sourceEntries, name, null, property.Value.GetString());
                }
                else
                {
                    Add(result, sourceEntries, null, name, property.Value.GetString());
                }
            }
        }

        return result.Values.ToList();
    }

    private static string NormalizeBlockPagePath(string rawPage)
    {
        var normalized = rawPage.Trim().Replace('\\', '/').TrimStart('/');
        normalized = normalized.Replace(".json", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(".html", string.Empty, StringComparison.OrdinalIgnoreCase);
        return normalized;
    }

    private static List<string> ResolveInjectionTargets(string siteOutputPath, IReadOnlyList<string>? targetPages, bool applyToAllPages)
    {
        if (applyToAllPages)
        {
            return Directory.EnumerateFiles(siteOutputPath, "*.html", SearchOption.AllDirectories)
                .Where(path =>
                {
                    var rel = Path.GetRelativePath(siteOutputPath, path).Replace('\\', '/');
                    return !rel.StartsWith($"{LocalizationGenerator.CatalogRootFolderName}/", StringComparison.OrdinalIgnoreCase);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in targetPages ?? [])
        {
            if (string.IsNullOrWhiteSpace(page))
            {
                continue;
            }

            var normalized = page.Trim().Replace('\\', '/').TrimStart('/');
            if (!normalized.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".html";
            }

            var sourcePath = Path.Combine(siteOutputPath, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(sourcePath))
            {
                selected.Add(sourcePath);
            }

            var localizedRoot = Path.Combine(siteOutputPath, LocalizationGenerator.LocalizedRootFolderName);
            if (!Directory.Exists(localizedRoot))
            {
                continue;
            }

            foreach (var langDir in Directory.EnumerateDirectories(localizedRoot))
            {
                var localizedPath = Path.Combine(langDir, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(localizedPath))
                {
                    selected.Add(localizedPath);
                }
            }
        }

        return selected.ToList();
    }

    private static bool InjectCss(IDocument document, string href)
    {
        if (document.Head is null)
        {
            return false;
        }

        if (document.Head.QuerySelector($"link[data-site-mirror-injection][href='{href}']") is not null)
        {
            return false;
        }

        var link = document.CreateElement("link");
        link.SetAttribute("rel", "stylesheet");
        link.SetAttribute("href", href);
        link.SetAttribute("data-site-mirror-injection", "1");
        document.Head.AppendChild(link);
        return true;
    }

    private static bool InjectJs(IDocument document, string src)
    {
        if (document.Body is null && document.Head is null)
        {
            return false;
        }

        var root = document.Body ?? document.Head!;
        if (document.QuerySelector($"script[data-site-mirror-injection][src='{src}']") is not null)
        {
            return false;
        }

        var script = document.CreateElement("script");
        script.SetAttribute("src", src);
        script.SetAttribute("defer", string.Empty);
        script.SetAttribute("data-site-mirror-injection", "1");
        root.AppendChild(script);
        return true;
    }

    private async Task SaveInjectionMetadataAsync(
        string assetId,
        string siteHost,
        string version,
        string assetType,
        string name,
        string description,
        string relativeFilePath,
        IReadOnlyList<string> injectedPages,
        CancellationToken cancellationToken)
    {
        // Reuse configured DB connection used by crawl repository.
        // We intentionally keep this lightweight and schema-first.
        if (string.IsNullOrWhiteSpace(_dbConnectionString))
        {
            return;
        }

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_dbConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string insertAsset = """
                INSERT INTO dbo.InjectionAssets
                (AssetId, SiteHost, Version, AssetType, Name, Description, RelativeFilePath, CreatedAtUtc)
                VALUES
                (@AssetId, @SiteHost, @Version, @AssetType, @Name, @Description, @RelativeFilePath, @CreatedAtUtc);
                """;
            await using (var cmd = new Microsoft.Data.SqlClient.SqlCommand(insertAsset, connection, (Microsoft.Data.SqlClient.SqlTransaction)transaction))
            {
                cmd.Parameters.AddWithValue("@AssetId", assetId);
                cmd.Parameters.AddWithValue("@SiteHost", siteHost);
                cmd.Parameters.AddWithValue("@Version", version);
                cmd.Parameters.AddWithValue("@AssetType", assetType);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@Description", description);
                cmd.Parameters.AddWithValue("@RelativeFilePath", relativeFilePath);
                cmd.Parameters.AddWithValue("@CreatedAtUtc", DateTimeOffset.UtcNow);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            const string insertTarget = """
                INSERT INTO dbo.InjectionAssetTargets (AssetId, TargetPagePath)
                VALUES (@AssetId, @TargetPagePath);
                """;
            foreach (var page in injectedPages)
            {
                await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(insertTarget, connection, (Microsoft.Data.SqlClient.SqlTransaction)transaction);
                cmd.Parameters.AddWithValue("@AssetId", assetId);
                cmd.Parameters.AddWithValue("@TargetPagePath", page);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> LoadInjectionTargetsAsync(
        Microsoft.Data.SqlClient.SqlConnection connection,
        string assetId,
        CancellationToken cancellationToken)
    {
        const string q = """
            SELECT TargetPagePath
            FROM dbo.InjectionAssetTargets
            WHERE AssetId = @AssetId
            ORDER BY TargetPagePath;
            """;
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(q, connection);
        cmd.Parameters.AddWithValue("@AssetId", assetId);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    private static List<BlockItemPayload> FlattenBlockItems(BlockPagePayload blockPage)
    {
        if (blockPage.Groups.Count > 0)
        {
            return blockPage.Groups.SelectMany(group => group.Blocks).ToList();
        }

        return blockPage.Blocks.ToList();
    }

    private static List<string> ResolveLocalizedPages(string localizedRoot, string? pagePath)
    {
        if (!string.IsNullOrWhiteSpace(pagePath))
        {
            var normalized = pagePath.Trim().Replace('\\', '/').TrimStart('/');
            if (!normalized.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".html";
            }

            var fullPath = Path.GetFullPath(Path.Combine(localizedRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Localized page not found: {normalized}", fullPath);
            }

            return [fullPath];
        }

        return Directory
            .EnumerateFiles(localizedRoot, "*.html", SearchOption.AllDirectories)
            .ToList();
    }

    private static int FixDocumentLinks(IDocument document, string siteHost, string version, string language)
    {
        var fixedCount = 0;
        foreach (var element in document.QuerySelectorAll("a[href], area[href], form[action]"))
        {
            var attr = element.HasAttribute("href") ? "href" : "action";
            var raw = element.GetAttribute(attr);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var rewritten = RewriteLocalizedNavigationPath(raw!, siteHost, version, language);
            if (!string.Equals(rewritten, raw, StringComparison.Ordinal))
            {
                element.SetAttribute(attr, rewritten);
                fixedCount++;
            }
        }

        return fixedCount;
    }

    private static string RewriteLocalizedNavigationPath(string value, string siteHost, string version, string language)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/mirror/", StringComparison.Ordinal) ||
            trimmed.StartsWith("/_site-mirror", StringComparison.Ordinal))
        {
            return value;
        }

        var pathEnd = trimmed.IndexOfAny(['?', '#']);
        var pathOnly = pathEnd >= 0 ? trimmed[..pathEnd] : trimmed;
        var suffix = pathEnd >= 0 ? trimmed[pathEnd..] : string.Empty;
        var normalizedPath = StripLocalePrefix(pathOnly);
        return $"/mirror/{siteHost}/{version}/{LocalizationGenerator.LocalizedRootFolderName}/{language}{normalizedPath}{suffix}";
    }

    private static string StripLocalePrefix(string path)
    {
        var normalized = path.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "/";
        }

        if (!LocalePathSegmentRegex.IsMatch(segments[0]))
        {
            return normalized;
        }

        var remaining = segments.Skip(1).ToArray();
        return remaining.Length == 0 ? "/" : "/" + string.Join('/', remaining);
    }

    private async Task PersistCrawlSafeAsync(
        CrawlRecord crawl,
        IReadOnlyList<CrawlPageRecord> pages)
    {
        try
        {
            // Intentionally not using the HTTP request's CancellationToken: that token fires when the
            // client disconnects (close Swagger tab, proxy/timeout) and would cancel these DB calls even
            // if the server-side mirror work is still finishing or about to return 200.
            await _crawlRepository.SaveCrawlAsync(crawl, pages, cancellationToken: default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist crawl {CrawlId} to SQL Server.", crawl.CrawlId);
        }
    }

    private sealed record PageExecutionResult
    {
        public required string RequestedUrl { get; init; }

        public string RequestedUrlKey { get; init; } = string.Empty;

        public required string FinalUrl { get; init; }

        public required string EntryFilePath { get; init; }

        public required string EntryFileRelativePath { get; init; }

        public required string FrontendPreviewPath { get; init; }

        public required int FilesSaved { get; init; }

        public required IReadOnlyList<Uri> DiscoveredLinks { get; init; }

        public string PageStatus { get; init; } = "completed";

        public string? PageError { get; init; }

        public bool SkippedFromStore { get; init; }
    }

    private sealed class TranslationCatalogPayload
    {
        public string Language { get; init; } = "en";

        public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class CommonBlockCatalogPayload
    {
        public string Language { get; init; } = "fa";
        public List<CommonBlockEntryPayload> Entries { get; init; } = [];
    }

    private sealed record CommonBlockEntryPayload
    {
        public string Key { get; init; } = string.Empty;
        public string Original { get; init; } = string.Empty;
        public string Translated { get; init; } = string.Empty;
    }

    private sealed class BlockPagePayload
    {
        public string Page { get; init; } = "/";

        public List<BlockItemPayload> Blocks { get; init; } = [];

        public List<BlockGroupPayload> Groups { get; init; } = [];
    }

    private sealed record BlockItemPayload
    {
        public string Id { get; init; } = string.Empty;

        public string Type { get; init; } = "paragraph";

        public string Original { get; init; } = string.Empty;

        public string Translated { get; init; } = string.Empty;
    }

    private sealed record BlockGroupPayload
    {
        public string Id { get; init; } = string.Empty;

        public string HeadingType { get; init; } = "intro";

        public string Heading { get; init; } = string.Empty;

        public List<BlockItemPayload> Blocks { get; init; } = [];
    }
}
