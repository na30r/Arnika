namespace SiteMirror.Api.Models;

public sealed class CrawlRecord
{
    public required string CrawlId { get; init; }

    public required string SourceUrl { get; init; }

    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public required string Status { get; init; }

    public required int RequestedLinkLimit { get; init; }

    public required int ProcessedPages { get; init; }

    public required int TotalFilesSaved { get; init; }

    public required string DefaultLanguage { get; init; }

    public required IReadOnlyList<string> AvailableLanguages { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public string? ErrorMessage { get; init; }
}
