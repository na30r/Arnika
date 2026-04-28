namespace SiteMirror.Api.Models;

public sealed class FixPageLinksResult
{
    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public required string Language { get; init; }

    public required int PagesProcessed { get; init; }

    public required int LinksFixed { get; init; }

    public IReadOnlyList<string> ProcessedPages { get; init; } = [];
}
