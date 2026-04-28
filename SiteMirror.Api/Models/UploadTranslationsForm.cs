using Microsoft.AspNetCore.Http;

namespace SiteMirror.Api.Models;

public sealed class UploadTranslationsForm
{
    public string SiteHost { get; init; } = string.Empty;

    public string Version { get; init; } = "latest";

    public string Language { get; init; } = "fa";

    public IFormFile? File { get; init; }

    public string[]? DoNotTranslateTexts { get; init; }

    public string[]? TargetPages { get; init; }
}
