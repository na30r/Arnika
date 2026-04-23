namespace SiteMirror.Api.Models;

public sealed class CrawlStatusResult
{
    public required CrawlRecord Crawl { get; init; }

    public required IReadOnlyList<CrawlPageRecord> Pages { get; init; }
}
