using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace SiteMirror.Api.Services.Mirroring;

internal sealed class HtmlRewriter
{
    private static readonly Regex CssUrlRegex = new(@"url\((?<quote>['""]?)(?<value>[^)'""]+)\k<quote>\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CssImportRegex = new(
        @"@import\s+(?:url\((?<quote1>['""]?)(?<url1>[^)'""]+)\k<quote1>\)|(?<quote2>['""])(?<url2>[^'""]+)\k<quote2>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "data", "action", "formaction", "poster", "background", "xlink:href", "imagesrc", "longdesc", "cite"
    };

    private static readonly HashSet<string> SrcSetAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "srcset", "imagesrcset"
    };

    /// <summary>
    /// Rewrites URL-bearing attributes and inline CSS blocks to local relative paths.
    /// </summary>
    public void RewriteHtmlDocument(IDocument document, Uri documentUri, Func<Uri, string, string?> rewriteUrl)
    {
        RewriteAttributeUrls(document, documentUri, rewriteUrl);
        RewriteCssBlocks(document, documentUri, rewriteUrl);
        InjectMirrorRuntimeScript(document);
    }

    /// <summary>
    /// Parses HTML and queues any linked resources for download.
    /// </summary>
    public void EnqueueResourcesFromHtml(Uri baseUri, string html, Action<Uri, string> enqueueUrl)
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var document = parser.ParseDocument(html);
        EnqueueUrlsFromDocument(document, baseUri, enqueueUrl, EnqueueResourcesFromSrcSet);
    }

    /// <summary>
    /// Parses CSS content and queues URL and @import references.
    /// </summary>
    public void EnqueueResourcesFromCss(Uri baseUri, string css, Action<Uri, string> enqueueUrl)
    {
        EnqueueResourcesFromCssInternal(baseUri, css, enqueueUrl);
    }

    private static void EnqueueUrlsFromDocument(IDocument document, Uri baseUri, Action<Uri, string> enqueueUrl, Action<Uri, string, Action<Uri, string>> enqueueSrcSet)
    {
        foreach (var element in document.All)
        {
            foreach (var attribute in element.Attributes)
            {
                var value = attribute.Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (SrcSetAttributes.Contains(attribute.Name))
                {
                    enqueueSrcSet(baseUri, value, enqueueUrl);
                    continue;
                }

                if (UrlAttributes.Contains(attribute.Name))
                {
                    if (string.Equals(element.LocalName, "a", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(attribute.Name, "href", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    enqueueUrl(baseUri, value);
                    continue;
                }

                if (attribute.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase) && LooksLikeUrl(value))
                {
                    enqueueUrl(baseUri, value);
                }
            }
        }

        foreach (var element in document.All.Where(e => e.HasAttribute("style")))
        {
            var inlineStyle = element.GetAttribute("style");
            if (!string.IsNullOrWhiteSpace(inlineStyle))
            {
                EnqueueResourcesFromCssInternal(baseUri, inlineStyle, enqueueUrl);
            }
        }

        foreach (var styleNode in document.QuerySelectorAll("style"))
        {
            EnqueueResourcesFromCssInternal(baseUri, styleNode.TextContent, enqueueUrl);
        }
    }

    private static void RewriteAttributeUrls(IDocument document, Uri docUri, Func<Uri, string, string?> rewriteUrl)
    {
        foreach (var element in document.All)
        {
            foreach (var attribute in element.Attributes.ToArray())
            {
                var value = attribute.Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (SrcSetAttributes.Contains(attribute.Name))
                {
                    element.SetAttribute(attribute.Name, RewriteSrcSet(docUri, value, rewriteUrl));
                    continue;
                }

                if (UrlAttributes.Contains(attribute.Name) ||
                    (attribute.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase) && LooksLikeUrl(value)))
                {
                    var rewritten = rewriteUrl(docUri, value);
                    if (rewritten is not null)
                    {
                        element.SetAttribute(attribute.Name, rewritten);
                    }
                }
            }
        }
    }

    private static void RewriteCssBlocks(IDocument document, Uri docUri, Func<Uri, string, string?> rewriteUrl)
    {
        foreach (var styleNode in document.QuerySelectorAll("style"))
        {
            styleNode.TextContent = RewriteCss(docUri, styleNode.TextContent, rewriteUrl);
        }

        foreach (var element in document.All.Where(e => e.HasAttribute("style")))
        {
            var inline = element.GetAttribute("style");
            if (!string.IsNullOrWhiteSpace(inline))
            {
                element.SetAttribute("style", RewriteCss(docUri, inline, rewriteUrl));
            }
        }
    }

    private static string RewriteCss(Uri docUri, string css, Func<Uri, string, string?> rewriteUrl)
    {
        var rewritten = CssUrlRegex.Replace(css, match =>
        {
            var raw = match.Groups["value"].Value.Trim();
            var replacement = rewriteUrl(docUri, raw);
            return replacement is null ? match.Value : $"url(\"{replacement}\")";
        });

        return CssImportRegex.Replace(rewritten, match =>
        {
            var raw = match.Groups["url1"].Success
                ? match.Groups["url1"].Value.Trim()
                : match.Groups["url2"].Value.Trim();
            var replacement = rewriteUrl(docUri, raw);
            if (replacement is null)
            {
                return match.Value;
            }

            return $"@import url(\"{replacement}\")";
        });
    }

    private static void EnqueueResourcesFromCssInternal(Uri baseUri, string css, Action<Uri, string> enqueueUrl)
    {
        foreach (Match match in CssUrlRegex.Matches(css))
        {
            var value = match.Groups["value"].Value.Trim();
            enqueueUrl(baseUri, value);
        }

        foreach (Match match in CssImportRegex.Matches(css))
        {
            var value = match.Groups["url1"].Success
                ? match.Groups["url1"].Value.Trim()
                : match.Groups["url2"].Value.Trim();
            enqueueUrl(baseUri, value);
        }
    }

    private static void EnqueueResourcesFromSrcSet(Uri baseUri, string srcSet, Action<Uri, string> enqueueUrl)
    {
        if (string.IsNullOrWhiteSpace(srcSet))
        {
            return;
        }

        var candidates = srcSet.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates)
        {
            var url = ExtractSrcSetUrl(candidate);
            if (!string.IsNullOrWhiteSpace(url))
            {
                enqueueUrl(baseUri, url);
            }
        }
    }

    private static string RewriteSrcSet(Uri baseUri, string srcSet, Func<Uri, string, string?> rewriteUrl)
    {
        var candidates = srcSet.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rewrittenCandidates = new List<string>(candidates.Length);
        foreach (var candidate in candidates)
        {
            var trimmed = candidate.Trim();
            var url = ExtractSrcSetUrl(trimmed);
            if (string.IsNullOrWhiteSpace(url))
            {
                rewrittenCandidates.Add(trimmed);
                continue;
            }

            var descriptor = trimmed[url.Length..].TrimStart();
            var rewrittenUrl = rewriteUrl(baseUri, url);
            if (string.IsNullOrWhiteSpace(rewrittenUrl))
            {
                rewrittenCandidates.Add(trimmed);
                continue;
            }

            rewrittenCandidates.Add(string.IsNullOrWhiteSpace(descriptor)
                ? rewrittenUrl
                : $"{rewrittenUrl} {descriptor}");
        }

        return string.Join(", ", rewrittenCandidates);
    }

    public static string ExtractSrcSetUrl(string candidate)
    {
        var trimmed = candidate.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var separatorIndex = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];
    }

    public static bool LooksLikeUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("//", StringComparison.Ordinal) ||
               trimmed.StartsWith("/", StringComparison.Ordinal) ||
               trimmed.StartsWith("./", StringComparison.Ordinal) ||
               trimmed.StartsWith("../", StringComparison.Ordinal) ||
               trimmed.StartsWith('#') ||
               trimmed.StartsWith("?", StringComparison.Ordinal);
    }

    private static void InjectMirrorRuntimeScript(IDocument document)
    {
        var head = document.Head;
        if (head is null)
        {
            return;
        }

        if (head.QuerySelector("script[data-site-mirror-runtime='1']") is not null)
        {
            return;
        }

        var runtimeScript = document.CreateElement("script");
        runtimeScript.SetAttribute("data-site-mirror-runtime", "1");
        runtimeScript.SetAttribute("src", "/mirror/_mirror-runtime.js");
        head.InsertBefore(runtimeScript, head.FirstChild);
    }
}
