namespace SiteMirror.Api.Models;

public sealed class CrawlPageRecord
{
    public required string CrawlId { get; init; }

    public required int QueueOrder { get; init; }

    public required string RequestedUrl { get; init; }

    public required string FinalUrl { get; init; }

    public required string FrontendPreviewPath { get; init; }

    public required string EntryFileRelativePath { get; init; }

    public int FilesSaved { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}
