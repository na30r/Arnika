using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public interface ICrawlRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the crawl run and each page row (no full replace of all pages). Safe for incremental updates.
    /// </summary>
    Task SaveCrawlAsync(
        CrawlRecord crawl,
        IReadOnlyList<CrawlPageRecord> pages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a completed page for the same site, version, and URL key, if any prior crawl finished it.
    /// </summary>
    Task<CompletedPageSnapshot?> TryGetCompletedPageAsync(
        string siteHost,
        string version,
        string requestedUrlKey,
        CancellationToken cancellationToken = default);

    Task<CrawlStatusResult?> GetCrawlAsync(string crawlId, CancellationToken cancellationToken = default);
}
