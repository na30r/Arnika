namespace SiteMirror.Api.Models;

public sealed class CreateInjectionAssetRequest
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    // css | js
    public string AssetType { get; init; } = "css";

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Content { get; init; } = string.Empty;

    public bool ApplyToAllPages { get; init; }

    // Relative paths like "docs.html" or "docs/app/page.html"
    public string[]? TargetPages { get; init; }
}
