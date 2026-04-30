namespace SiteMirror.Api.Models;

public sealed class UpdateBlockTranslationsResult
{
    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public required string Language { get; init; }

    public required string PagePath { get; init; }

    public required string BlockFilePath { get; init; }

    public required int UpdatedEntryCount { get; init; }

    public required int RebuiltPageCount { get; init; }

    public IReadOnlyList<string> RebuiltPages { get; init; } = [];
}
