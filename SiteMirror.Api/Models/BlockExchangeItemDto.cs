namespace SiteMirror.Api.Models;

public sealed class BlockExchangeItemDto
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Original { get; init; } = string.Empty;

    public string Translated { get; init; } = string.Empty;

    public string? GroupId { get; init; }
}
