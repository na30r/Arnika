using HtmlAgilityPack;
using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public interface ILinkRewriterService
{
    string RewriteHtml(
        string html,
        Uri pageUri,
        IReadOnlyCollection<DownloadedAsset> downloadedAssets);
}
