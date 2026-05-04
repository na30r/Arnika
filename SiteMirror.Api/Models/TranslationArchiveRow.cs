namespace SiteMirror.Api.Models;

/// <summary>
/// One row appended to dbo.TranslationArchive when catalog/common/block translations are saved (audit trail; not read by the mirror pipeline yet).
/// </summary>
public sealed class TranslationArchiveRow
{
    public string TranslationKey { get; init; } = string.Empty;

    public string? OriginalText { get; init; }

    public string TranslatedValue { get; init; } = string.Empty;

    /// <summary>Route-style path when known (e.g. <c>/docs</c>, <c>/_common</c>); optional.</summary>
    public string? PagePath { get; init; }
}
