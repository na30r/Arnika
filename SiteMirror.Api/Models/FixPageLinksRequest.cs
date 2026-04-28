namespace SiteMirror.Api.Models;

public sealed class FixPageLinksRequest
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    public string Language { get; init; } = "en";

    // Relative localized page path, e.g. "docs.html" or "blog/1.html".
    // If omitted, all localized HTML pages for the language are fixed.
    public string? PagePath { get; init; }
}
