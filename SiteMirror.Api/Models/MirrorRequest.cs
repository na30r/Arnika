namespace SiteMirror.Api.Models;

public sealed class MirrorRequest
{
    public string Url { get; init; } = string.Empty;

    public string OutputFolder { get; init; } = "mirror-output";

    public int ExtraWaitMs { get; init; } = 4_000;

    public string? ChromiumExecutablePath { get; init; }
}
