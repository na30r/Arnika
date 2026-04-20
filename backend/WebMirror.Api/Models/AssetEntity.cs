namespace WebMirror.Api.Models;

public sealed class AssetEntity
{
    public long Id { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public long PageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
