using Microsoft.AspNetCore.Http;

namespace SiteMirror.Api.Models;

public sealed class UploadCommonBlockTranslationsForm
{
    public string SiteHost { get; init; } = string.Empty;
    public string Version { get; init; } = "latest";
    public string Language { get; init; } = "fa";
    public IFormFile? File { get; init; }
}

public sealed class ApplyCommonBlockTranslationsResult
{
    public required string SiteHost { get; init; }
    public required string Version { get; init; }
    public required string Language { get; init; }
    public required string CommonFilePath { get; init; }
    public required int UpdatedCommonCount { get; init; }
    public required int RebuiltPageCount { get; init; }
    public IReadOnlyList<string> RebuiltPages { get; init; } = [];
}
