namespace SiteMirror.Api.Models;

public sealed class MirrorSettings
{
    public const string SectionName = "MirrorSettings";

    public string OutputFolder { get; init; } = "mirror-output";

    public string? ChromiumExecutablePath { get; init; }
}
