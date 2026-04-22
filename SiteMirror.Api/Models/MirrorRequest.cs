namespace SiteMirror.Api.Models;

public sealed class MirrorRequest
{
    public string Url { get; init; } = string.Empty;

    public int ExtraWaitMs { get; init; } = 4_000;
}
