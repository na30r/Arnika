namespace SiteMirror.Api.Models;

public sealed class MirrorBatchResult
{
    public required string CrawlId { get; init; }

    public required string SourceUrl { get; init; }

    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public required int RequestedLinkLimit { get; init; }

    public required int ProcessedPages { get; init; }

    public required int TotalFilesSaved { get; init; }

    public required string DefaultLanguage { get; init; }

    public required IReadOnlyList<string> AvailableLanguages { get; init; }

    public required IReadOnlyList<CrawlPageInfo> Pages { get; init; }
}
