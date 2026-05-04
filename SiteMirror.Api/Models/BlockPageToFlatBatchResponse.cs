namespace SiteMirror.Api.Models;

public sealed class BlockPageToFlatBatchResponse
{
    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);

    public string EntriesJson { get; init; } = string.Empty;

    /// <summary>Page paths that were merged successfully (in request order).</summary>
    public IReadOnlyList<string> IncludedPagePaths { get; init; } = [];

    public IReadOnlyList<BlockPageToFlatBatchFailure> Failures { get; init; } = [];
}

public sealed class BlockPageToFlatBatchFailure
{
    public string PagePath { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
