namespace SiteMirror.Api.Services;

public sealed class MirrorQueueClaimedItem
{
    public required Guid ItemId { get; init; }

    public required string BatchId { get; init; }

    public required string Url { get; init; }

    public required string OptionsJson { get; init; }

    public Guid? UserId { get; init; }
}
