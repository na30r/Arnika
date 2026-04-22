namespace SiteMirror.Api.Models;

public sealed class RewriteLinksResult
{
    public required string StartHtmlPath { get; init; }

    public required string MirrorRootFolder { get; init; }

    public int HtmlFilesDiscovered { get; init; }

    public int HtmlFilesRewritten { get; init; }
}
