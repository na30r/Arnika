namespace SiteMirror.Api.Models;

public sealed class MirrorRequest
{
    public string Url { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    public int ExtraWaitMs { get; init; } = 4_000;

    public bool AutoScroll { get; init; } = true;

    public int ScrollStepPx { get; init; } = 1_200;

    public int ScrollDelayMs { get; init; } = 150;

    public int MaxScrollRounds { get; init; } = 20;
}
