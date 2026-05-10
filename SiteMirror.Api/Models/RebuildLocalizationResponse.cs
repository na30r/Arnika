namespace SiteMirror.Api.Models;

public sealed class RebuildLocalizationResponse
{
    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public bool RefreshedI18nFromHtml { get; init; }

    public required string DefaultLanguage { get; init; }

    public required IReadOnlyList<string> Languages { get; init; }

    /// <summary>Approximate HTML files touched when <see cref="RebuildLocalizationRequest.RefreshI18nFromHtml"/> is <c>false</c>.</summary>
    public int RebuiltHtmlFileCount { get; init; }
}
