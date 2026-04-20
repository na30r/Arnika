using Microsoft.Extensions.Options;
using WebMirror.Api.Models;
using WebMirror.Api.Options;

namespace WebMirror.Api.Services;

public sealed class CrawlOrchestrator : ICrawlOrchestrator
{
    private readonly ICrawlerService _crawlerService;
    private readonly IAssetService _assetService;
    private readonly ILinkRewriterService _linkRewriterService;
    private readonly IStorageService _storageService;
    private readonly IPageRepository _pageRepository;
    private readonly IAssetRepository _assetRepository;
    private readonly ICrawlQueueRepository _queueRepository;
    private readonly IUrlMapper _urlMapper;
    private readonly IRateLimiterService _rateLimiter;
    private readonly ILogger<CrawlOrchestrator> _logger;
    private readonly MirrorOptions _options;

    public CrawlOrchestrator(
        ICrawlerService crawlerService,
        IAssetService assetService,
        ILinkRewriterService linkRewriterService,
        IStorageService storageService,
        IPageRepository pageRepository,
        IAssetRepository assetRepository,
        ICrawlQueueRepository queueRepository,
        IUrlMapper urlMapper,
        IRateLimiterService rateLimiter,
        IOptions<MirrorOptions> options,
        ILogger<CrawlOrchestrator> logger)
    {
        _crawlerService = crawlerService;
        _assetService = assetService;
        _linkRewriterService = linkRewriterService;
        _storageService = storageService;
        _pageRepository = pageRepository;
        _assetRepository = assetRepository;
        _queueRepository = queueRepository;
        _urlMapper = urlMapper;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<long> EnqueueAsync(CrawlRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Invalid URL.");
        }

        if (!IsDomainAllowed(uri.Host))
        {
            throw new InvalidOperationException("Domain is not allowed by whitelist policy.");
        }

        var effectiveDepth = request.MaxDepth <= 0 ? _options.MaxDepth : Math.Min(request.MaxDepth, _options.MaxDepth);
        return await _queueRepository.EnqueueAsync(uri.ToString(), 0, effectiveDepth, cancellationToken);
    }

    public async Task<CrawlStatusResponse?> GetStatusAsync(long queueId, CancellationToken cancellationToken)
    {
        var queueItem = await _queueRepository.GetByIdAsync(queueId, cancellationToken);
        if (queueItem is null)
        {
            return null;
        }

        return new CrawlStatusResponse(
            queueItem.Id,
            queueItem.Url,
            queueItem.Status,
            queueItem.Depth,
            queueItem.MaxDepth,
            queueItem.RetryCount,
            queueItem.ErrorMessage);
    }

    public async Task ProcessQueueItemAsync(CrawlQueueEntity queueItem, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(queueItem.Url, UriKind.Absolute, out var currentUri))
        {
            await _queueRepository.MarkFailedAsync(queueItem.Id, "Invalid URL in queue item.", cancellationToken);
            return;
        }

        if (!IsDomainAllowed(currentUri.Host))
        {
            await _queueRepository.MarkDoneAsync(queueItem.Id, cancellationToken);
            _logger.LogWarning("Skipping URL outside domain whitelist: {Url}", queueItem.Url);
            return;
        }

        try
        {
            await _queueRepository.MarkInProgressAsync(queueItem.Id, cancellationToken);
            await _rateLimiter.WaitTurnAsync(cancellationToken);

            var existingPage = await _pageRepository.GetByOriginalUrlAsync(queueItem.Url, cancellationToken);
            if (existingPage?.Status == PageStatus.Crawled)
            {
                await _queueRepository.MarkDoneAsync(queueItem.Id, cancellationToken);
                return;
            }

            var pageId = existingPage?.Id
                ?? await _pageRepository.UpsertAsync(
                    queueItem.Url,
                    _urlMapper.MapToLocalRoute(currentUri),
                    PageStatus.Pending,
                    cancellationToken);

            var crawlResult = await _crawlerService.CrawlAsync(queueItem.Url, cancellationToken);
            var downloadedAssets = await _assetService.DownloadAndStoreAsync(crawlResult.Assets, cancellationToken);
            var rewrittenHtml = _linkRewriterService.RewriteHtml(crawlResult.Html, currentUri, downloadedAssets);

            var localRoute = _urlMapper.MapToLocalRoute(currentUri);
            await _storageService.SavePageHtmlAsync(localRoute, rewrittenHtml, cancellationToken);
            await _pageRepository.UpsertAsync(queueItem.Url, localRoute, PageStatus.Crawled, cancellationToken);

            foreach (var asset in downloadedAssets)
            {
                await _assetRepository.UpsertAsync(asset.OriginalUrl, asset.LocalPath, pageId, cancellationToken);
            }

            await EnqueueDiscoveredLinksAsync(queueItem, currentUri, crawlResult.InternalLinks, cancellationToken);
            await _queueRepository.MarkDoneAsync(queueItem.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed processing crawl for {Url}", queueItem.Url);
            var shouldRetry = queueItem.RetryCount + 1 < _options.MaxRetries;
            if (shouldRetry)
            {
                await _queueRepository.IncrementRetryAsync(queueItem.Id, ex.Message, cancellationToken);
            }
            else
            {
                await _queueRepository.MarkFailedAsync(queueItem.Id, ex.Message, cancellationToken);
                await _pageRepository.UpsertAsync(queueItem.Url, _urlMapper.MapToLocalRoute(currentUri), PageStatus.Failed, cancellationToken);
            }
        }
    }

    private async Task EnqueueDiscoveredLinksAsync(
        CrawlQueueEntity queueItem,
        Uri currentUri,
        IReadOnlyCollection<string> discoveredLinks,
        CancellationToken cancellationToken)
    {
        if (queueItem.Depth >= queueItem.MaxDepth)
        {
            return;
        }

        foreach (var link in discoveredLinks)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var linkUri))
            {
                continue;
            }

            if (!string.Equals(linkUri.Host, currentUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsDomainAllowed(linkUri.Host))
            {
                continue;
            }

            await _queueRepository.EnqueueAsync(linkUri.ToString(), queueItem.Depth + 1, queueItem.MaxDepth, cancellationToken);
        }
    }

    private bool IsDomainAllowed(string host)
    {
        if (_options.DomainWhitelist.Count == 0)
        {
            return true;
        }

        return _options.DomainWhitelist
            .Any(allowed => string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase));
    }
}
