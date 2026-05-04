using System.Text.Json.Serialization;

namespace SiteMirror.Api.Models;

public sealed class MirrorQueueEnqueueRequest
{
    public string[]? Urls { get; init; }

    public string Version { get; init; } = "latest";

    public int LinkDrillCount { get; init; }

    public string[]? CrawlUrlAllowPrefixes { get; init; }

    public string[]? CrawlUrlDenyPrefixes { get; init; }

    public string[]? Languages { get; init; }

    public string[]? DoNotTranslateTexts { get; init; }

    public string[]? GeneralTranslationClasses { get; init; }

    public int ExtraWaitMs { get; init; } = 4_000;

    public bool AutoScroll { get; init; } = true;

    public int ScrollStepPx { get; init; } = 1_200;

    public int ScrollDelayMs { get; init; } = 150;

    public int MaxScrollRounds { get; init; } = 20;

    public MirrorQueueTemplate ToTemplate() => new()
    {
        Version = Version,
        LinkDrillCount = LinkDrillCount,
        CrawlUrlAllowPrefixes = CrawlUrlAllowPrefixes,
        CrawlUrlDenyPrefixes = CrawlUrlDenyPrefixes,
        Languages = Languages,
        DoNotTranslateTexts = DoNotTranslateTexts,
        GeneralTranslationClasses = GeneralTranslationClasses,
        ExtraWaitMs = ExtraWaitMs,
        AutoScroll = AutoScroll,
        ScrollStepPx = ScrollStepPx,
        ScrollDelayMs = ScrollDelayMs,
        MaxScrollRounds = MaxScrollRounds
    };
}

public sealed class MirrorQueueEnqueueResponse
{
    public required string BatchId { get; init; }

    public required int QueuedCount { get; init; }
}

public sealed class MirrorQueueBatchStatusResponse
{
    public required string BatchId { get; init; }

    public required IReadOnlyList<MirrorQueueItemStatusDto> Items { get; init; }

    /// <summary>True when every item is completed or failed (response is from DB before purge on this request).</summary>
    public bool AllFinished { get; init; }

    /// <summary>Rows were removed from the queue table after this snapshot (batch fully done).</summary>
    public bool PurgedFromDatabase { get; init; }
}

public sealed class MirrorQueueItemStatusDto
{
    public required Guid ItemId { get; init; }

    public required string Url { get; init; }

    public required string Status { get; init; }

    public string? CrawlId { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>Populated when status is <c>completed</c>; deserialized mirror summary.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorResult? Result { get; init; }
}
