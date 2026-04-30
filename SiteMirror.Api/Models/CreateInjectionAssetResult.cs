namespace SiteMirror.Api.Models;

public sealed class CreateInjectionAssetResult
{
    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public required string AssetId { get; init; }

    public required string AssetType { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string RelativeFilePath { get; init; }

    public required int PagesInjected { get; init; }

    public IReadOnlyList<string> InjectedPages { get; init; } = [];
}
