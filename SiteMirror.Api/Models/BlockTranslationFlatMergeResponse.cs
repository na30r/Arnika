namespace SiteMirror.Api.Models;

public sealed class BlockTranslationFlatMergeResponse
{
    /// <summary>Pretty-printed block page JSON ready to save as docs.json.</summary>
    public string BlockPageJson { get; init; } = string.Empty;

    public List<BlockExchangeItemDto> Items { get; init; } = [];

    /// <summary>Keys from the request map that did not match any block id or Original text.</summary>
    public List<string> UnmatchedTranslationKeys { get; init; } = [];
}
