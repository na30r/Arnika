namespace SiteMirror.Api.Models;

public sealed class CrawlPageRecord
{
    public required string CrawlId { get; init; }

    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public required int QueueOrder { get; init; }

    public required string RequestedUrl { get; init; }

    /// <summary>Normalized URL (no fragment) for deduplication across crawls.</summary>
    public required string RequestedUrlKey { get; init; }

    public required string FinalUrl { get; init; }

    public required string FrontendPreviewPath { get; init; }

    public required string EntryFileRelativePath { get; init; }

    public int FilesSaved { get; init; }

    public required string PageStatus { get; init; }

    public string? ErrorMessage { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}
