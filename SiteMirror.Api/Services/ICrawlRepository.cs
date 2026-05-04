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
        Guid? forUserId,
        CancellationToken cancellationToken = default);

    Task<CrawlStatusResult?> GetCrawlAsync(string crawlId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MirrorHistoryItem>> GetMirrorHistoryForUserAsync(
        Guid userId,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Append-only log of translation key/values (catalog, common blocks, or per-page blocks). No-op when connection string is empty or rows empty.
    /// </summary>
    Task AppendTranslationArchiveAsync(
        string scope,
        string siteHost,
        string version,
        string language,
        string? pagePath,
        IReadOnlyList<TranslationArchiveRow> rows,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranslationArchiveRecordDto>> QueryTranslationArchiveAsync(
        string? siteHost,
        string? version,
        string? language,
        string? scope,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Latest <c>TranslatedValue</c> per <c>TranslationKey</c> for scope <c>catalog</c> (by <see cref="TranslationArchiveRecordDto.SavedAtUtc"/>).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetLatestCatalogEntriesFromArchiveAsync(
        string siteHost,
        string version,
        string language,
        CancellationToken cancellationToken = default);

    Task EnqueueMirrorUrlBatchAsync(
        string batchId,
        Guid? userId,
        IReadOnlyList<string> urls,
        MirrorQueueTemplate template,
        CancellationToken cancellationToken = default);

    Task<MirrorQueueClaimedItem?> TryClaimMirrorQueueItemAsync(CancellationToken cancellationToken = default);

    Task CompleteMirrorQueueItemAsync(
        Guid itemId,
        string status,
        string? crawlId,
        MirrorResult? result,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MirrorQueueItemRow>> ListMirrorQueueBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    Task DeleteMirrorQueueBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default);
}
