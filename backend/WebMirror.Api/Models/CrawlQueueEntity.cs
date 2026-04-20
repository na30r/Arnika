namespace WebMirror.Api.Models;

public sealed class CrawlQueueEntity
{
    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = CrawlQueueStatus.Pending;
    public int Depth { get; set; }
    public int MaxDepth { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class CrawlQueueStatus
{
    public const string Pending = "Pending";
    public const string InProgress = "InProgress";
    public const string Done = "Done";
    public const string Failed = "Failed";
}
