namespace SiteMirror.Api.Models;

public sealed class RewriteLinksRequest
{
    public string HtmlFilePath { get; init; } = string.Empty;

    public string? MirrorRootFolder { get; init; }

    public string? RootUrl { get; init; }

    public bool RewriteAllHtmlFiles { get; init; }
}
