namespace SiteMirror.Api.Models;

public sealed class MirrorStorageAnalyzeResult
{
    public required string SiteRoot { get; init; }

    public int TotalFiles { get; init; }

    public long TotalBytes { get; init; }

    public int ReachableFiles { get; init; }

    public long ReachableBytes { get; init; }

    /// <summary>
    /// Unreachable files not under <see cref="MirrorStorageAnalyzeRequest.ProtectedPathPrefixes"/>.
    /// </summary>
    public required IReadOnlyList<MirrorStorageUnusedFileDto> OrphanCandidates { get; init; }

    /// <summary>
    /// Unreachable but under a protected prefix (translation mirrors, etc.).
    /// </summary>
    public required IReadOnlyList<MirrorStorageUnusedFileDto> UnreachableProtected { get; init; }

    public long OrphanCandidatesBytes { get; init; }

    public long UnreachableProtectedBytes { get; init; }

    public bool OrphanCandidatesTruncated { get; init; }

    public bool UnreachableProtectedTruncated { get; init; }

    /// <summary>
    /// Short explanation of analysis limits for API consumers.
    /// </summary>
    public string Hint { get; init; } =
        "Reachability uses only static URLs in HTML and CSS. JavaScript dynamic imports and runtime fetches are not traced, so some listed orphans may still be required in the browser, and some required files may be missing from the graph.";
}

public sealed class MirrorStorageUnusedFileDto
{
    public required string RelativePath { get; init; }

    public long SizeBytes { get; init; }
}
