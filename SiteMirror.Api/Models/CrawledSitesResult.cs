namespace SiteMirror.Api.Models;

public sealed class CrawledSitesResult
{
    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public IReadOnlyList<CrawledSiteRunItem> CrawlRuns { get; init; } = [];

    public IReadOnlyList<CrawledSitePageItem> Pages { get; init; } = [];
}

public sealed class CrawledSiteRunItem
{
    public required string CrawlId { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required int ProcessedPages { get; init; }

    public required int TotalFilesSaved { get; init; }
}

public sealed class CrawledSitePageItem
{
    public required string EntryFileRelativePath { get; init; }

    public required string FrontendPreviewPath { get; init; }
}
