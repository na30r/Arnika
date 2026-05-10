using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services.Mirroring;

/// <summary>
/// Computes which files under a mirror site folder are reachable from entry HTML via static
/// <c>href</c>/<c>src</c>/<c>srcset</c>, <c>url()</c> in CSS, and optionally <c>a[href]</c>.
/// </summary>
internal static class MirrorReachabilityAnalyzer
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

    private static readonly string[] DefaultProtectedPrefixes = ["_i18n/", "_localized/"];

    public static MirrorStorageAnalyzeResult Run(string siteRootFullPath, MirrorStorageAnalyzeRequest request)
    {
        var siteRoot = Path.GetFullPath(siteRootFullPath);
        if (!Directory.Exists(siteRoot))
        {
            throw new DirectoryNotFoundException($"Mirror folder not found: {siteRoot}");
        }

        var fileMeta = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(siteRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(siteRoot, file).Replace('\\', '/');
            try
            {
                fileMeta[rel] = new FileInfo(file).Length;
            }
            catch
            {
                fileMeta[rel] = 0;
            }
        }

        var protectedPrefixes = (request.ProtectedPathPrefixes is { Count: > 0 }
                ? request.ProtectedPathPrefixes
                : DefaultProtectedPrefixes)
            .Select(NormalizePrefix)
            .ToArray();

        var entries = ResolveEntryPaths(siteRoot, fileMeta, request.EntryRelativePaths);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                "No entry HTML files found. Set entryRelativePaths or crawl HTML outside _localized/ and _i18n/.");
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var e in entries)
        {
            if (fileMeta.ContainsKey(e))
            {
                queue.Enqueue(e);
            }
        }

        var parser = new HtmlParser();
        while (queue.Count > 0)
        {
            var rel = queue.Dequeue();
            if (!reachable.Add(rel))
            {
                continue;
            }

            var fullPath = Path.Combine(siteRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var ext = Path.GetExtension(rel);
            if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                string html;
                try
                {
                    html = File.ReadAllText(fullPath);
                }
                catch
                {
                    continue;
                }

                var doc = parser.ParseDocument(html);
                CollectFromHtml(siteRoot, doc, rel, fileMeta, request.FollowNavigationalHtml, queue);
            }
            else if (ext.Equals(".css", StringComparison.OrdinalIgnoreCase))
            {
                string css;
                try
                {
                    css = File.ReadAllText(fullPath);
                }
                catch
                {
                    continue;
                }

                CollectFromCss(siteRoot, rel, css, fileMeta, queue);
            }
        }

        long totalBytes = fileMeta.Values.Sum();
        long reachableBytes = reachable.Sum(r => fileMeta.TryGetValue(r, out var z) ? z : 0);

        var unreachable = fileMeta.Keys.Where(f => !reachable.Contains(f)).ToList();
        var orphans = new List<MirrorStorageUnusedFileDto>();
        var prot = new List<MirrorStorageUnusedFileDto>();

        foreach (var u in unreachable)
        {
            var dto = new MirrorStorageUnusedFileDto
            {
                RelativePath = u,
                SizeBytes = fileMeta[u]
            };
            if (IsProtected(u, protectedPrefixes))
            {
                prot.Add(dto);
            }
            else
            {
                orphans.Add(dto);
            }
        }

        var max = Math.Max(100, request.MaxPathsPerList);
        var orphansSorted = orphans.OrderByDescending(o => o.SizeBytes).ToList();
        var protSorted = prot.OrderByDescending(o => o.SizeBytes).ToList();

        var orphanTrunc = orphansSorted.Count > max;
        var protTrunc = protSorted.Count > max;

        var orphanList = orphansSorted.Take(max).ToList();
        var protList = protSorted.Take(max).ToList();

        return new MirrorStorageAnalyzeResult
        {
            SiteRoot = siteRoot,
            TotalFiles = fileMeta.Count,
            TotalBytes = totalBytes,
            ReachableFiles = reachable.Count,
            ReachableBytes = reachableBytes,
            OrphanCandidates = orphanList,
            UnreachableProtected = protList,
            OrphanCandidatesBytes = orphans.Sum(o => o.SizeBytes),
            UnreachableProtectedBytes = prot.Sum(o => o.SizeBytes),
            OrphanCandidatesTruncated = orphanTrunc,
            UnreachableProtectedTruncated = protTrunc
        };
    }

    private static string NormalizePrefix(string p)
    {
        var t = p.Trim().Replace('\\', '/');
        if (t.Length == 0)
        {
            return t;
        }

        return t.EndsWith('/') ? t : t + "/";
    }

    private static bool IsProtected(string relativePath, string[] protectedPrefixes)
    {
        var n = relativePath.Replace('\\', '/');
        foreach (var p in protectedPrefixes)
        {
            if (p.Length == 0)
            {
                continue;
            }

            if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ResolveEntryPaths(
        string siteRoot,
        Dictionary<string, long> fileMeta,
        IReadOnlyList<string>? entryRelativePaths)
    {
        if (entryRelativePaths is { Count: > 0 })
        {
            return entryRelativePaths
                .Select(e => e.Trim().Replace('\\', '/').TrimStart('/'))
                .Where(fileMeta.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var entryExclude = DefaultProtectedPrefixes.Select(NormalizePrefix).ToArray();
        return fileMeta.Keys
            .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsProtected(f, entryExclude))
            .ToList();
    }

    private static void CollectFromHtml(
        string siteRootFull,
        IDocument document,
        string currentRel,
        Dictionary<string, long> fileMeta,
        bool followNavigationalHtml,
        Queue<string> queue)
    {
        foreach (var element in document.All)
        {
            foreach (var attribute in element.Attributes)
            {
                var name = attribute.Name;
                var value = attribute.Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (SrcSetAttributes.Contains(name))
                {
                    foreach (var u in SplitSrcSet(value))
                    {
                        TryEnqueue(siteRootFull, currentRel, u, fileMeta, queue, tryHtmlFallback: false);
                    }

                    continue;
                }

                if (UrlAttributes.Contains(name))
                {
                    if (!followNavigationalHtml &&
                        string.Equals(element.LocalName, "a", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(name, "href", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TryEnqueue(siteRootFull, currentRel, value, fileMeta, queue, tryHtmlFallback: true);
                    continue;
                }

                if (name.StartsWith("data-", StringComparison.OrdinalIgnoreCase) && HtmlRewriter.LooksLikeUrl(value))
                {
                    TryEnqueue(siteRootFull, currentRel, value, fileMeta, queue, tryHtmlFallback: false);
                }
            }

            if (string.Equals(element.LocalName, "style", StringComparison.OrdinalIgnoreCase))
            {
                CollectFromCss(siteRootFull, currentRel, element.TextContent, fileMeta, queue);
            }
            else if (element.HasAttribute("style"))
            {
                var inline = element.GetAttribute("style");
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    CollectFromCss(siteRootFull, currentRel, inline, fileMeta, queue);
                }
            }
        }
    }

    private static IEnumerable<string> SplitSrcSet(string srcSet)
    {
        foreach (var candidate in srcSet.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var url = HtmlRewriter.ExtractSrcSetUrl(candidate);
            if (!string.IsNullOrWhiteSpace(url))
            {
                yield return url;
            }
        }
    }

    private static void CollectFromCss(
        string siteRootFull,
        string currentRel,
        string css,
        Dictionary<string, long> fileMeta,
        Queue<string> queue)
    {
        foreach (Match match in CssUrlRegex.Matches(css))
        {
            var value = match.Groups["value"].Value.Trim();
            TryEnqueue(siteRootFull, currentRel, value, fileMeta, queue, tryHtmlFallback: false);
        }

        foreach (Match match in CssImportRegex.Matches(css))
        {
            var value = match.Groups["url1"].Success
                ? match.Groups["url1"].Value.Trim()
                : match.Groups["url2"].Value.Trim();
            TryEnqueue(siteRootFull, currentRel, value, fileMeta, queue, tryHtmlFallback: false);
        }
    }

    private static void TryEnqueue(
        string siteRootFull,
        string currentRel,
        string raw,
        Dictionary<string, long> fileMeta,
        Queue<string> queue,
        bool tryHtmlFallback)
    {
        var resolved = TryResolveToExistingRelativeFile(siteRootFull, currentRel, raw, fileMeta, tryHtmlFallback);
        if (resolved is not null && fileMeta.ContainsKey(resolved))
        {
            queue.Enqueue(resolved);
        }
    }

    private static string? TryResolveToExistingRelativeFile(
        string siteRootFull,
        string currentRelNormalized,
        string rawUrl,
        Dictionary<string, long> knownFiles,
        bool tryHtmlFallback)
    {
        var trimmed = rawUrl.Trim().Trim('"', '\'');
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return null;
        }

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var noQuery = trimmed.Split('?', 2)[0];
        var noFrag = noQuery.Split('#', 2)[0];
        if (noFrag.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(noFrag, UriKind.Absolute, out var absUri) &&
            (absUri.Scheme == Uri.UriSchemeHttp || absUri.Scheme == Uri.UriSchemeHttps))
        {
            return null;
        }

        var pathPart = noFrag.Replace('\\', '/');
        if (pathPart.StartsWith('/'))
        {
            pathPart = pathPart.TrimStart('/');
        }
        else
        {
            var curDir = Path.GetDirectoryName(currentRelNormalized.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(curDir))
            {
                curDir = ".";
            }

            pathPart = Path.Combine(curDir, pathPart).Replace('\\', '/');
        }

        var combinedRelative = pathPart.Replace('/', Path.DirectorySeparatorChar);
        var siteRoot = Path.GetFullPath(siteRootFull);
        var candidateAbs = Path.GetFullPath(Path.Combine(siteRoot, combinedRelative));
        if (!candidateAbs.StartsWith(siteRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rel = Path.GetRelativePath(siteRoot, candidateAbs).Replace('\\', '/');
        if (knownFiles.ContainsKey(rel))
        {
            return rel;
        }

        if (tryHtmlFallback && !rel.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            var withHtml = rel + ".html";
            if (knownFiles.ContainsKey(withHtml))
            {
                return withHtml;
            }
        }

        if (Directory.Exists(candidateAbs))
        {
            var indexRel = (string.IsNullOrEmpty(rel) ? string.Empty : rel.TrimEnd('/') + "/") + "index.html";
            if (knownFiles.ContainsKey(indexRel))
            {
                return indexRel;
            }
        }

        return null;
    }
}
