namespace SiteMirror.Api.Models;

/// <summary>Raw row from <c>MirrorUrlQueueItems</c> for batch status.</summary>
public sealed class MirrorQueueItemRow
{
    public required Guid ItemId { get; init; }

    public required string Url { get; init; }

    public required string Status { get; init; }

    public string? CrawlId { get; init; }

    public string? ResultJson { get; init; }

    public string? ErrorMessage { get; init; }
}
