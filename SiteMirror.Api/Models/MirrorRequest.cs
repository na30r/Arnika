namespace SiteMirror.Api.Models;

public sealed class MirrorRequest
{
    public string Url { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    public int LinkDrillCount { get; init; } = 0;

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
