namespace SiteMirror.Api.Models;

/// <summary>Serialized per batch row (all fields except <see cref="MirrorRequest.Url"/>).</summary>
public sealed class MirrorQueueTemplate
{
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

    public MirrorRequest ToRequest(string url) => new()
    {
        Url = url,
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
