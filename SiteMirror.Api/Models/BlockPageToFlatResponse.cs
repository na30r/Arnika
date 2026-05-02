namespace SiteMirror.Api.Models;

public sealed class BlockPageToFlatResponse
{
    /// <summary>Original/source text → translation (or original if empty and flag set).</summary>
    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Pretty-printed entries JSON for download.</summary>
    public string EntriesJson { get; init; } = string.Empty;
}
