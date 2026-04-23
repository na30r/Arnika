using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public interface ICrawlRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    Task SaveCrawlAsync(
        CrawlRecord crawl,
        IReadOnlyList<CrawlPageRecord> pages,
        CancellationToken cancellationToken = default);

    Task<CrawlStatusResult?> GetCrawlAsync(string crawlId, CancellationToken cancellationToken = default);
}
