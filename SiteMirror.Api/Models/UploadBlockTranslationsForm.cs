using Microsoft.AspNetCore.Http;

namespace SiteMirror.Api.Models;

public sealed class UploadBlockTranslationsForm
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    public string Language { get; init; } = "fa";

    // Relative source page path, e.g. "docs" or "blog/1".
    public string PagePath { get; init; } = string.Empty;

    public IFormFile? File { get; init; }
}
