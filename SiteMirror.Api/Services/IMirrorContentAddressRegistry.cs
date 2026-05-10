namespace SiteMirror.Api.Services;

/// <summary>
/// Persists URL → content-hash → shared on-disk path under <c>_cas/</c> so identical bytes reuse one file.
/// </summary>
public interface IMirrorContentAddressRegistry
{
    bool IsEnabled { get; }

    /// <summary>
    /// Resolves the relative path for <paramref name="body"/>; records <paramref name="urlKey"/> in the DB.
    /// When <c>CallerMustWriteFile</c> is true, the caller must create the file at
    /// <c>Path.Combine(mirrorSiteRoot, RelativePath)</c>.
    /// </summary>
    Task<MirrorContentAddressResult> RegisterOrGetContentAsync(
        string siteHost,
        string version,
        string mirrorSiteRoot,
        string urlKey,
        byte[] body,
        string? mediaType,
        CancellationToken cancellationToken = default);
}

public sealed class MirrorContentAddressResult
{
    public required string RelativePath { get; init; }

    /// <summary>
    /// When false, bytes already exist on disk for this content (another URL or a prior crawl).
    /// </summary>
    public required bool CallerMustWriteFile { get; init; }

    public required string ContentSha256Hex { get; init; }
}
