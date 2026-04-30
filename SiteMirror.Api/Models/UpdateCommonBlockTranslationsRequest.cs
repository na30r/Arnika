namespace SiteMirror.Api.Models;

public sealed class UpdateCommonBlockTranslationsRequest
{
    public string SiteHost { get; init; } = string.Empty;
    public string Version { get; init; } = "latest";
    public string Language { get; init; } = "fa";

    // Supports key-based entries (k_xxx -> translated) and source-text based entries.
    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);
}
