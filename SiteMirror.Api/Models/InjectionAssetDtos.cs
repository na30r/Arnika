namespace SiteMirror.Api.Models;

public sealed class InjectionAssetDto
{
    public required string AssetId { get; init; }
    public required string SiteHost { get; init; }
    public required string Version { get; init; }
    public required string AssetType { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public required string RelativeFilePath { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public IReadOnlyList<string> TargetPages { get; init; } = [];
}

public sealed class UpdateInjectionAssetRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Content { get; init; }
}
