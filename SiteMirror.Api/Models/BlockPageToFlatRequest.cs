namespace SiteMirror.Api.Models;

/// <summary>
/// Build a flat Original → Translated map from a block page (for translators / round-trip).
/// </summary>
public sealed class BlockPageToFlatRequest
{
    public string? BlockPageJson { get; init; }

    public string? SiteHost { get; init; }

    public string? Version { get; init; }

    public string? PagePath { get; init; }

    /// <summary>
    /// If true, empty <c>Translated</c> is filled with <c>Original</c> in the flat map.
    /// If false, export <c>""</c> when <c>Translated</c> is empty or still identical to <c>Original</c> (placeholder).
    /// </summary>
    public bool UseOriginalWhenTranslatedEmpty { get; init; } = true;
}
