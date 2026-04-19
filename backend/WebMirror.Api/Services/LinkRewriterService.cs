using HtmlAgilityPack;
using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public sealed class LinkRewriterService(IUrlMapper urlMapper) : ILinkRewriterService
{
    public string RewriteHtml(
        string html,
        Uri pageUri,
        IReadOnlyCollection<DownloadedAsset> downloadedAssets)
    {
        var assetMap = downloadedAssets
            .GroupBy(x => x.OriginalUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LocalPath, StringComparer.OrdinalIgnoreCase);

        var document = new HtmlDocument();
        document.LoadHtml(html);

        RewriteLinks(document, pageUri);
        RewriteAssets(document, pageUri, assetMap);

        return document.DocumentNode.OuterHtml;
    }

    private void RewriteLinks(HtmlDocument document, Uri pageUri)
    {
        var linkNodes = document.DocumentNode.SelectNodes("//a[@href]");
        if (linkNodes is null)
        {
            return;
        }

        foreach (var node in linkNodes)
        {
            var href = node.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(pageUri, href, out var resolved))
            {
                continue;
            }

            if (!urlMapper.IsInternalLink(pageUri, resolved))
            {
                continue;
            }

            node.SetAttributeValue("href", urlMapper.MapToLocalRoute(resolved));
        }
    }

    private static void RewriteAssets(
        HtmlDocument document,
        Uri pageUri,
        IReadOnlyDictionary<string, string> assetMap)
    {
        var selectors = new (string xpath, string attr)[]
        {
            ("//script[@src]", "src"),
            ("//link[@href]", "href"),
            ("//img[@src]", "src"),
            ("//source[@src]", "src"),
            ("//video[@src]", "src"),
            ("//audio[@src]", "src")
        };

        foreach (var (xpath, attr) in selectors)
        {
            var nodes = document.DocumentNode.SelectNodes(xpath);
            if (nodes is null)
            {
                continue;
            }

            foreach (var node in nodes)
            {
                var value = node.GetAttributeValue(attr, string.Empty);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (!Uri.TryCreate(pageUri, value, out var resolved))
                {
                    continue;
                }

                if (assetMap.TryGetValue(resolved.AbsoluteUri, out var replacement))
                {
                    node.SetAttributeValue(attr, replacement);
                }
            }
        }
    }
}
