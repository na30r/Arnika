namespace SiteMirror.Api.Models;

/// <summary>
/// Rebuilds <c>_i18n</c> and/or <c>_localized/{lang}</c> for an on-disk mirror folder.
/// </summary>
public sealed class RebuildLocalizationRequest
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    /// <summary>
    /// When <c>true</c> (default), regenerates <c>_i18n/pages</c>, <c>_i18n/blocks</c>, and language catalogs from HTML
    /// under the mirror root, then rebuilds <c>_localized</c> for each listed language.
    /// When <c>false</c>, only re-applies translations to <c>_localized/{{lang}}</c> using existing <c>_i18n</c>.
    /// </summary>
    public bool RefreshI18nFromHtml { get; init; } = true;

    /// <summary>Languages to rebuild (e.g. <c>en</c>, <c>fa</c>). First entry becomes the default language for full regen.</summary>
    public string[] Languages { get; init; } = [];

    public string[]? DoNotTranslateTexts { get; init; }

    public string[]? GeneralTranslationClasses { get; init; }
}
