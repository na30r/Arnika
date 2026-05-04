namespace SiteMirror.Api.Models;

/// <summary>
/// Applies latest archived <see cref="TranslationArchive"/> rows with Scope=catalog to the on-disk language catalog and rebuilds localized HTML.
/// </summary>
public sealed class ApplyTranslationArchiveRequest
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    public string Language { get; init; } = "en";
}
