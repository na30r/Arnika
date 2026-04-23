namespace SiteMirror.Api.Models;

public sealed class CrawlPageInfo
{
    public required string Url { get; init; }

    public required string FinalUrl { get; init; }

    public required string FrontendPreviewPath { get; init; }

    public required string EntryFileRelativePath { get; init; }

    public int FilesSaved { get; init; }

    /// <summary>completed | failed | skipped</summary>
    public string PageStatus { get; init; } = "completed";
}
