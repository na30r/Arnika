namespace SiteMirror.Api.Models;

/// <summary>
/// Merge flat Original → Translated maps from several <c>_i18n/blocks/*.json</c> pages (later pages overwrite duplicate keys).
/// </summary>
public sealed class BlockPagesToFlatBatchRequest
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public IReadOnlyList<string> PagePaths { get; init; } = [];

    public bool UseOriginalWhenTranslatedEmpty { get; init; } = true;
}
