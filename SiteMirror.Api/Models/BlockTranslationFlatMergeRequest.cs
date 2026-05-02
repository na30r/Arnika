namespace SiteMirror.Api.Models;

/// <summary>
/// Merge a flat { source text or block id : translation } map into a block page (docs.json shape).
/// Provide either <see cref="BlockPageJson"/> or mirror location fields to load the template.
/// </summary>
public sealed class BlockTranslationFlatMergeRequest
{
    /// <summary>Optional full JSON of a block page document (same shape as _i18n/blocks/docs.json).</summary>
    public string? BlockPageJson { get; init; }

    public string? SiteHost { get; init; }

    public string? Version { get; init; }

    /// <summary>Page path without extension, e.g. "docs" or "blog/post".</summary>
    public string? PagePath { get; init; }

    /// <summary>Flat map: English/source string or block id (b_…) → translation.</summary>
    public Dictionary<string, string> Translations { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Same as <see cref="Translations"/>; use whichever is convenient for your JSON tool.</summary>
    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When true, empty map values / empty <c>Translated</c> fall back to <c>Original</c>.
    /// When false, use <c>""</c> for those cases and when <c>Translated</c> only matches <c>Original</c> (untranslated placeholder).
    /// </summary>
    public bool EmptyTranslationUsesOriginal { get; init; } = true;
}
