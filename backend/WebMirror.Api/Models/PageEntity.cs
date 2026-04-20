namespace WebMirror.Api.Models;

public sealed class PageEntity
{
    public long Id { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string Status { get; set; } = PageStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class PageStatus
{
    public const string Pending = "Pending";
    public const string Crawled = "Crawled";
    public const string Failed = "Failed";
}
