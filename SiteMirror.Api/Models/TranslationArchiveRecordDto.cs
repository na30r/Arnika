namespace SiteMirror.Api.Models;

public sealed class TranslationArchiveRecordDto
{
    public long Id { get; init; }

    public string Scope { get; init; } = string.Empty;

    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string? PagePath { get; init; }

    public string TranslationKey { get; init; } = string.Empty;

    public string? OriginalText { get; init; }

    public string TranslatedValue { get; init; } = string.Empty;

    public DateTimeOffset SavedAtUtc { get; init; }
}
