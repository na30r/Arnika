namespace SiteMirror.Api.Models;

/// <summary>
/// Options for finding mirror files that are not referenced from HTML/CSS via static URLs.
/// </summary>
public sealed class MirrorStorageAnalyzeRequest
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// Entry files relative to the site mirror root (e.g. <c>docs.html</c>).
    /// If empty, every <c>*.html</c> under the site root except <c>_localized/</c> and <c>_i18n/</c> is used.
    /// </summary>
    public IReadOnlyList<string>? EntryRelativePaths { get; init; }

    /// <summary>
    /// When true, <c>a[href]</c> links to same-site HTML are followed (multi-page closure).
    /// When false, only resource attributes (script, link, img, …) are used — other HTML pages may appear as orphans.
    /// </summary>
    public bool FollowNavigationalHtml { get; init; }

    /// <summary>
    /// Relative path prefixes (POSIX slashes, trailing slash) never listed as safe-to-delete candidates.
    /// Defaults to translation trees if null.
    /// </summary>
    public IReadOnlyList<string>? ProtectedPathPrefixes { get; init; }

    /// <summary>
    /// Cap for <see cref="MirrorStorageAnalyzeResult.OrphanCandidates"/> and protected lists (largest files first).
    /// </summary>
    public int MaxPathsPerList { get; init; } = 2000;
}
