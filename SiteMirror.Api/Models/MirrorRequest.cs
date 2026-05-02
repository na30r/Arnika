namespace SiteMirror.Api.Models;

public sealed class MirrorRequest
{
    public string Url { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    public int LinkDrillCount { get; init; } = 0;

    /// <summary>
    /// When non-empty, only same-site links whose URL is under one of these prefixes are queued for drill.
    /// Entries may be absolute (https://host/path) or root-relative (/path); fragment ignored when matching.
    /// </summary>
    public string[]? CrawlUrlAllowPrefixes { get; init; }

    /// <summary>
    /// Same-site links under any of these prefixes (and their children) are excluded from the drill queue.
    /// </summary>
    public string[]? CrawlUrlDenyPrefixes { get; init; }

    public string[]? Languages { get; init; }

    public string[]? DoNotTranslateTexts { get; init; }

    // CSS class names whose container content should be treated as common/global
    // (all nested translatable blocks go to _i18n/blocks/_common.json).
    public string[]? GeneralTranslationClasses { get; init; }

    public int ExtraWaitMs { get; init; } = 4_000;

    public bool AutoScroll { get; init; } = true;

    public int ScrollStepPx { get; init; } = 1_200;

    public int ScrollDelayMs { get; init; } = 150;

    public int MaxScrollRounds { get; init; } = 20;
}
