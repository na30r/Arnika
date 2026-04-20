namespace WebMirror.Api.Models;

public sealed record CrawlRequest(string Url, int MaxDepth = 2);
public sealed record CrawlResponse(long QueueId, string Url, string Status);
public sealed record CrawlStatusResponse(
    long QueueId,
    string Url,
    string Status,
    int Depth,
    int MaxDepth,
    int RetryCount,
    string? ErrorMessage);

public sealed record CrawlResult(
    string Url,
    string Domain,
    string Html,
    string LocalRoute,
    IReadOnlyCollection<AssetReference> Assets,
    IReadOnlyCollection<string> InternalLinks);

public sealed record AssetReference(string OriginalUrl, string TagName, string AttributeName);

public sealed record DownloadedAsset(string OriginalUrl, string LocalPath);
