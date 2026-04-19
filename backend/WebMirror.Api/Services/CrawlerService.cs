using Microsoft.Playwright;
using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public sealed class CrawlerService(ILogger<CrawlerService> logger) : ICrawlerService
{
    public async Task<CrawlResult> CrawlAsync(string url, CancellationToken cancellationToken)
    {
        var targetUri = new Uri(url);

        await using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var page = await browser.NewPageAsync();
        await page.GotoAsync(targetUri.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 45000
        });

        // Additional wait helps with SPAs that hydrate after initial network idle.
        await page.WaitForTimeoutAsync(1200);
        var html = await page.ContentAsync();

        var assets = ExtractAssets(html, targetUri);
        var links = ExtractLinks(html, targetUri);
        var localRoute = $"/mirror/{targetUri.Host.ToLowerInvariant()}{targetUri.AbsolutePath.TrimEnd('/')}";
        if (localRoute.EndsWith('/'))
        {
            localRoute = localRoute.TrimEnd('/');
        }

        logger.LogInformation("Captured {Url}; assets: {AssetCount}, links: {LinkCount}", url, assets.Count, links.Count);

        return new CrawlResult(
            url,
            targetUri.Host.ToLowerInvariant(),
            html,
            string.IsNullOrWhiteSpace(localRoute) ? $"/mirror/{targetUri.Host.ToLowerInvariant()}" : localRoute,
            assets,
            links);
    }

    private static IReadOnlyCollection<AssetReference> ExtractAssets(string html, Uri baseUri)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        var assets = new List<AssetReference>();

        assets.AddRange(ReadAssetNodes(doc, "//script[@src]", "script", "src", baseUri));
        assets.AddRange(ReadAssetNodes(doc, "//link[@href and (contains(@rel,'stylesheet') or contains(@as,'style') or contains(@as,'script'))]", "link", "href", baseUri));
        assets.AddRange(ReadAssetNodes(doc, "//img[@src]", "img", "src", baseUri));
        assets.AddRange(ReadAssetNodes(doc, "//source[@src]", "source", "src", baseUri));
        assets.AddRange(ReadAssetNodes(doc, "//video[@src]", "video", "src", baseUri));
        assets.AddRange(ReadAssetNodes(doc, "//audio[@src]", "audio", "src", baseUri));

        return assets
            .GroupBy(x => x.OriginalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    private static IReadOnlyCollection<string> ExtractLinks(string html, Uri baseUri)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        var hrefNodes = doc.DocumentNode.SelectNodes("//a[@href]") ?? [];
        var urls = new List<string>();

        foreach (var node in hrefNodes)
        {
            var raw = node.GetAttributeValue("href", string.Empty)?.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#') || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(baseUri, raw, out var resolved))
            {
                urls.Add(resolved.GetLeftPart(UriPartial.Path).TrimEnd('/'));
            }
        }

        return urls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<AssetReference> ReadAssetNodes(
        HtmlAgilityPack.HtmlDocument doc,
        string xpath,
        string tagName,
        string attributeName,
        Uri baseUri)
    {
        var nodes = doc.DocumentNode.SelectNodes(xpath) ?? [];
        foreach (var node in nodes)
        {
            var value = node.GetAttributeValue(attributeName, string.Empty)?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (Uri.TryCreate(baseUri, value, out var absolute))
            {
                yield return new AssetReference(absolute.ToString(), tagName, attributeName);
            }
        }
    }
}
