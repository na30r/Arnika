namespace SiteMirror.Api.Models;

public sealed class UpdateTranslationsRequest
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    public string Language { get; init; } = "fa";

    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);

    public string[]? DoNotTranslateTexts { get; init; }

    public string[]? TargetPages { get; init; }
}
