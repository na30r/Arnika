namespace SiteMirror.Api.Models;

public sealed class MirrorResult
{
    public required string CrawlId { get; init; }

    public required string SourceUrl { get; init; }

    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public required string DefaultLanguage { get; init; }

    public required IReadOnlyList<string> AvailableLanguages { get; init; }

    public required string FinalUrl { get; init; }

    public required string OutputFolder { get; init; }

    public required string EntryFilePath { get; init; }

    public required string EntryFileRelativePath { get; init; }

    public required string FrontendPreviewPath { get; init; }

    public required int RequestedLinkLimit { get; init; }

    public required int ProcessedPages { get; init; }

    /// <summary>Number of entry URLs skipped because they were already mirrored for this version.</summary>
    public int SkippedPages { get; init; }

    public required IReadOnlyList<CrawlPageInfo> Pages { get; init; }

    public int FilesSaved { get; init; }

    public string? UsedChromiumExecutablePath { get; init; }

    public int WaitMs { get; init; }
}
