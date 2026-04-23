namespace SiteMirror.Api.Models;

/// <summary>
/// Data from a previous successful crawl of the same site/version/URL, used to skip re-fetching.
/// </summary>
public sealed class CompletedPageSnapshot
{
    public required string RequestedUrl { get; init; }

    public required string FinalUrl { get; init; }

    public required string EntryFileRelativePath { get; init; }

    public int FilesSaved { get; init; }
}
