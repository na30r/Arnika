namespace SiteMirror.Api.Models;

public sealed class UpdateBlockTranslationsRequest
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    public string Language { get; init; } = "fa";

    // Relative source page path, e.g. "docs" or "blog/1".
    public string PagePath { get; init; } = string.Empty;

    // block.id -> translated value
    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);

    // block.id -> source/original value (optional, for EN/source edits)
    public Dictionary<string, string>? SourceEntries { get; init; }
}
