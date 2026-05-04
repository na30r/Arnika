namespace SiteMirror.Api.Models;

public sealed class MirrorSettings
{
    public const string SectionName = "MirrorSettings";

    public string OutputFolder { get; init; } = "../frontend/public/mirror";

    public string? ChromiumExecutablePath { get; init; }

    /// <summary>
    /// Reserved for future use. Only one queue worker runs: mirrors are globally serialized so output files are not
    /// written concurrently (avoids Windows file-sharing violations on the same paths).
    /// </summary>
    public int MirrorQueueMaxConcurrent { get; init; } = 1;
}
