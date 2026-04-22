namespace SiteMirror.Api.Models;

public sealed class MirrorSettings
{
    public const string SectionName = "MirrorSettings";

    public string OutputFolder { get; init; } = "../frontend/public/mirror";

    public string? ChromiumExecutablePath { get; init; }
}
