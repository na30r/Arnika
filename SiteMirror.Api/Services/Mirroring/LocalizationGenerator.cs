using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

namespace SiteMirror.Api.Services.Mirroring;

internal sealed partial class LocalizationGenerator(ILogger<LocalizationGenerator> logger)
{
    public const string LocalizedRootFolderName = "_localized";
    public const string CatalogRootFolderName = "_i18n";
    public const string DoNotTranslateFileName = "do-not-translate.json";
public const string PerPageTemplatesFolderName = "pages";
    public const string PerPageBlocksFolderName = "blocks";
    public const string CommonBlockFileName = "_common.json";

    private static readonly HashSet<string> NonTranslatableContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "code", "pre", "template"
    };

    private static readonly HashSet<string> TranslatableAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "alt", "placeholder", "aria-label", "aria-description", "label"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<LocalizationGenerationResult> GenerateLocalizedCopiesAsync(
        string mirrorRoot,
        IReadOnlyList<string> languages,
        IReadOnlyList<string>? doNotTranslateTexts,
        IReadOnlyList<string>? generalTranslationClasses,
        CancellationToken cancellationToken)
    {
        var normalizedLanguages = NormalizeLanguages(languages);
        if (normalizedLanguages.Count == 0)
        {
            normalizedLanguages = ["en"];
        }
        var defaultLanguage = normalizedLanguages[0];

        var excludedRootSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LocalizedRootFolderName,
            CatalogRootFolderName
        };
        var sourceFiles = EnumerateSourceFiles(mirrorRoot, excludedRootSegments).ToList();
        var sourceHtmlFiles = sourceFiles
            .Where(path => string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sourceHtmlFiles.Count == 0)
        {
            logger.LogInformation("No HTML files found for localization in {MirrorRoot}", mirrorRoot);
            return new LocalizationGenerationResult
            {
                DefaultLanguage = defaultLanguage,
                AvailableLanguages = normalizedLanguages
            };
        }

        var sourceEntries = await BuildSourceCatalogAsync(mirrorRoot, sourceHtmlFiles, cancellationToken);
        var i18nFolder = Path.Combine(mirrorRoot, CatalogRootFolderName);
        Directory.CreateDirectory(i18nFolder);

        await SaveCatalogAsync(Path.Combine(i18nFolder, "source.json"), "en", sourceEntries, cancellationToken);
        await SavePerPageTemplatesAsync(mirrorRoot, i18nFolder, sourceHtmlFiles, cancellationToken);
        await SavePerPageBlocksAsync(
            mirrorRoot,
            i18nFolder,
            sourceHtmlFiles,
            generalTranslationClasses,
            cancellationToken);
        _ = await LoadOrCreateLanguageCatalogAsync("en", i18nFolder, sourceEntries, cancellationToken);
        var effectiveDoNotTranslateTexts = await ResolveDoNotTranslateTextsAsync(i18nFolder, doNotTranslateTexts, cancellationToken);
        var doNotTranslateSet = BuildDoNotTranslateSet(effectiveDoNotTranslateTexts);
        foreach (var language in normalizedLanguages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var languageCatalog = await LoadOrCreateLanguageCatalogAsync(
                language,
                i18nFolder,
                sourceEntries,
                cancellationToken);

            var languageOutputRoot = Path.Combine(mirrorRoot, LocalizedRootFolderName, language);
            if (Directory.Exists(languageOutputRoot))
            {
                Directory.Delete(languageOutputRoot, recursive: true);
            }

            CopySourceFiles(mirrorRoot, languageOutputRoot, sourceFiles);
            await TranslateHtmlFilesForLanguageAsync(
                language,
                languageOutputRoot,
                i18nFolder,
                sourceHtmlFiles,
                languageCatalog,
                doNotTranslateSet,
                cancellationToken);
        }

        logger.LogInformation(
            "Generated localized mirror copies for languages: {Languages}",
            string.Join(", ", normalizedLanguages));

        return new LocalizationGenerationResult
        {
            DefaultLanguage = defaultLanguage,
            AvailableLanguages = normalizedLanguages
        };
    }

    private async Task SavePerPageTemplatesAsync(
        string mirrorRoot,
        string i18nFolder,
        IReadOnlyList<string> sourceHtmlFiles,
        CancellationToken cancellationToken)
    {
        var perPageRoot = Path.Combine(i18nFolder, PerPageTemplatesFolderName);
        if (Directory.Exists(perPageRoot))
        {
            Directory.Delete(perPageRoot, recursive: true);
        }
        Directory.CreateDirectory(perPageRoot);

        var parser = new HtmlParser();
        foreach (var relativeHtmlPath in sourceHtmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var htmlPath = Path.Combine(mirrorRoot, relativeHtmlPath);
            var html = await File.ReadAllTextAsync(htmlPath, Encoding.UTF8, cancellationToken);
            var document = await parser.ParseDocumentAsync(html, cancellationToken);
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);

            ProcessTranslatableTokens(document, relativeHtmlPath, (key, value, _) =>
            {
                if (!entries.ContainsKey(key))
                {
                    entries[key] = value;
                }
            });

            var jsonRelativePath = relativeHtmlPath
                .Replace('\\', '/')
                .TrimStart('/')
                .Replace(".html", ".json", StringComparison.OrdinalIgnoreCase);
            var templatePath = Path.Combine(perPageRoot, jsonRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
            await SaveCatalogAsync(templatePath, "source", entries, cancellationToken);
        }
    }

    private async Task SavePerPageBlocksAsync(
        string mirrorRoot,
        string i18nFolder,
        IReadOnlyList<string> sourceHtmlFiles,
        IReadOnlyList<string>? generalTranslationClasses,
        CancellationToken cancellationToken)
    {
        var perPageBlocksRoot = Path.Combine(i18nFolder, PerPageBlocksFolderName);
        if (Directory.Exists(perPageBlocksRoot))
        {
            Directory.Delete(perPageBlocksRoot, recursive: true);
        }
        Directory.CreateDirectory(perPageBlocksRoot);
        var rootIndex = new List<RootBlockEntry>();
        var usedRootFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commonKeys = await LoadCommonBlockKeysAsync(i18nFolder, cancellationToken);
        var commonClasses = NormalizeCommonClassNames(generalTranslationClasses);
        var commonEntries = await LoadCommonEntriesByKeyAsync(i18nFolder, cancellationToken);

        var parser = new HtmlParser();
        foreach (var relativeHtmlPath in sourceHtmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var htmlPath = Path.Combine(mirrorRoot, relativeHtmlPath);
            var html = await File.ReadAllTextAsync(htmlPath, Encoding.UTF8, cancellationToken);
            var document = await parser.ParseDocumentAsync(html, cancellationToken);

            var normalizedPage = "/" + relativeHtmlPath
                .Replace('\\', '/')
                .TrimStart('/')
                .Replace(".html", string.Empty, StringComparison.OrdinalIgnoreCase);
            var grouped = ExtractSemanticBlocksWithGroups(
                document,
                normalizedPage,
                commonKeys,
                commonClasses,
                commonEntries);
            var payload = new BlockPageDocument
            {
                Page = normalizedPage,
                // Avoid duplicate payload size: when groups exist, blocks are already nested there.
                Blocks = grouped.Groups.Count > 0 ? [] : grouped.Blocks,
                Groups = grouped.Groups
            };

            var jsonRelativePath = relativeHtmlPath
                .Replace('\\', '/')
                .TrimStart('/')
                .Replace(".html", ".json", StringComparison.OrdinalIgnoreCase);
            var blockPath = Path.Combine(perPageBlocksRoot, jsonRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(blockPath)!);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await File.WriteAllTextAsync(blockPath, json, Encoding.UTF8, cancellationToken);

            // Also create a root-level alias file so translators can access
            // page JSON quickly without traversing nested folders.
            var rootAliasFileName = BuildRootAliasFileName(jsonRelativePath, usedRootFileNames);
            var rootAliasPath = Path.Combine(perPageBlocksRoot, rootAliasFileName);
            await File.WriteAllTextAsync(rootAliasPath, json, Encoding.UTF8, cancellationToken);

            rootIndex.Add(new RootBlockEntry
            {
                Page = normalizedPage,
                NestedPath = jsonRelativePath,
                RootFile = rootAliasFileName
            });
        }

        await SaveCommonEntriesAsync(perPageBlocksRoot, commonEntries, cancellationToken);

        var indexPath = Path.Combine(perPageBlocksRoot, "_root-index.json");
        var indexPayload = new RootBlockIndex
        {
            Pages = rootIndex
                .OrderBy(item => item.Page, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        var indexJson = JsonSerializer.Serialize(indexPayload, JsonOptions);
        await File.WriteAllTextAsync(indexPath, indexJson, Encoding.UTF8, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> RegenerateLocalizedPagesAsync(
        string mirrorRoot,
        string language,
        IReadOnlyList<string>? targetPages,
        IReadOnlyList<string>? doNotTranslateTexts,
        CancellationToken cancellationToken)
    {
        var normalizedLanguage = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedLanguage))
        {
            throw new ArgumentException("Language is required.", nameof(language));
        }

        var excludedRootSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LocalizedRootFolderName,
            CatalogRootFolderName
        };
        var sourceFiles = EnumerateSourceFiles(mirrorRoot, excludedRootSegments).ToList();
        var sourceHtmlFiles = sourceFiles
            .Where(path => string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase))
            .Select(path => path.Replace('\\', '/'))
            .ToList();

        if (sourceHtmlFiles.Count == 0)
        {
            logger.LogInformation("No HTML files found for localization in {MirrorRoot}", mirrorRoot);
            return [];
        }

        var targetHtmlFiles = ResolveTargetHtmlFiles(sourceHtmlFiles, targetPages);
        if (targetHtmlFiles.Count == 0)
        {
            return [];
        }

        var sourceEntries = await BuildSourceCatalogAsync(mirrorRoot, targetHtmlFiles, cancellationToken);
        var i18nFolder = Path.Combine(mirrorRoot, CatalogRootFolderName);
        Directory.CreateDirectory(i18nFolder);

        var languageCatalog = await LoadOrCreateLanguageCatalogAsync(
            normalizedLanguage,
            i18nFolder,
            sourceEntries,
            cancellationToken);

        var effectiveDoNotTranslateTexts = await ResolveDoNotTranslateTextsAsync(i18nFolder, doNotTranslateTexts, cancellationToken);
        var doNotTranslateSet = BuildDoNotTranslateSet(effectiveDoNotTranslateTexts);

        var languageOutputRoot = Path.Combine(mirrorRoot, LocalizedRootFolderName, normalizedLanguage);
        if (!Directory.Exists(languageOutputRoot))
        {
            CopySourceFiles(mirrorRoot, languageOutputRoot, sourceFiles);
        }
        else
        {
            // Re-copy only targeted source files to ensure we always translate from fresh original HTML.
            CopySourceFiles(mirrorRoot, languageOutputRoot, targetHtmlFiles);
        }

        await TranslateHtmlFilesForLanguageAsync(
            normalizedLanguage,
            languageOutputRoot,
            i18nFolder,
            targetHtmlFiles,
            languageCatalog,
            doNotTranslateSet,
            cancellationToken);

        return targetHtmlFiles;
    }

    private async Task<Dictionary<string, string>> BuildSourceCatalogAsync(
        string mirrorRoot,
        IReadOnlyList<string> sourceHtmlFiles,
        CancellationToken cancellationToken)
    {
        var sourceEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        var parser = new HtmlParser();

        foreach (var relativeHtmlPath in sourceHtmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var htmlPath = Path.Combine(mirrorRoot, relativeHtmlPath);
            var html = await File.ReadAllTextAsync(htmlPath, Encoding.UTF8, cancellationToken);
            var document = await parser.ParseDocumentAsync(html, cancellationToken);

            ProcessTranslatableTokens(document, relativeHtmlPath, (key, value, _) =>
            {
                if (!sourceEntries.ContainsKey(key))
                {
                    sourceEntries[key] = value;
                }
            });
        }

        return sourceEntries;
    }

    private async Task TranslateHtmlFilesForLanguageAsync(
        string language,
        string languageOutputRoot,
        string i18nFolder,
        IReadOnlyList<string> sourceHtmlFiles,
        LanguageCatalog languageCatalog,
        HashSet<string> doNotTranslateSet,
        CancellationToken cancellationToken)
    {
        var parser = new HtmlParser();
        var commonOriginals = await LoadCommonOriginalsForTokenPassAsync(i18nFolder, cancellationToken);
        foreach (var relativeHtmlPath in sourceHtmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localizedHtmlPath = Path.Combine(languageOutputRoot, relativeHtmlPath);
            if (!File.Exists(localizedHtmlPath))
            {
                continue;
            }

            var html = await File.ReadAllTextAsync(localizedHtmlPath, Encoding.UTF8, cancellationToken);
            var document = await parser.ParseDocumentAsync(html, cancellationToken);

            ProcessTranslatableTokens(document, relativeHtmlPath, (key, sourceValue, setValue) =>
            {
                var sourceValueKey = NormalizeForKey(sourceValue);
                if (doNotTranslateSet.Contains(sourceValueKey))
                {
                    return;
                }

                // Enforce source-of-truth split:
                // - _common content comes from token catalogs (fa.json via ResolveLocalizedValue)
                // - all page-specific content must come from block JSON (applied below)
                if (!string.IsNullOrWhiteSpace(sourceValueKey) && !commonOriginals.Contains(sourceValueKey))
                {
                    return;
                }

                var localizedValue = ResolveLocalizedValue(key, sourceValue, languageCatalog);
                if (!string.Equals(localizedValue, sourceValue, StringComparison.Ordinal))
                {
                    setValue(localizedValue);
                }
            });

            // Apply paragraph/list block translations after token-level replacements so translators can
            // translate full sentences from docs.json while preserving inline links.
            ApplyPageBlockTranslations(
                document,
                relativeHtmlPath,
                i18nFolder,
                languageCatalog,
                doNotTranslateSet);

            if (document.DocumentElement is not null)
            {
                document.DocumentElement.SetAttribute("lang", language);
                document.DocumentElement.SetAttribute("data-site-mirror-lang", language);
                if (IsRtlLanguage(language))
                {
                    ApplyRtlLayout(document);
                }
                else
                {
                    document.DocumentElement.SetAttribute("dir", "ltr");
                }
            }

            var updatedHtml = document.DocumentElement?.OuterHtml ?? html;
            await File.WriteAllTextAsync(localizedHtmlPath, updatedHtml, Encoding.UTF8, cancellationToken);
        }
    }

    private static string ResolveLocalizedValue(
        string key,
        string sourceValue,
        LanguageCatalog languageCatalog)
    {
        if (languageCatalog.ByKey.TryGetValue(key, out var translatedByKey) &&
            !string.IsNullOrWhiteSpace(translatedByKey))
        {
            // If key-based value is already a real translation, prefer it.
            // If it is still identical to source text, allow source-text mapping
            // to override (used by block-based translation updates).
            if (!string.Equals(
                    NormalizeForKey(translatedByKey),
                    NormalizeForKey(sourceValue),
                    StringComparison.Ordinal))
            {
                return translatedByKey;
            }
        }

        var normalizedSourceValue = NormalizeForKey(sourceValue);
        if (!string.IsNullOrWhiteSpace(normalizedSourceValue) &&
            languageCatalog.BySourceText.TryGetValue(normalizedSourceValue, out var translatedBySource) &&
            !string.IsNullOrWhiteSpace(translatedBySource))
        {
            return translatedBySource;
        }

        if (!string.IsNullOrWhiteSpace(translatedByKey))
        {
            return translatedByKey;
        }

        return sourceValue;
    }

    private static async Task<HashSet<string>> LoadCommonOriginalsForTokenPassAsync(
        string i18nFolder,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var commonPath = Path.Combine(i18nFolder, PerPageBlocksFolderName, CommonBlockFileName);
        if (!File.Exists(commonPath))
        {
            return result;
        }

        try
        {
            var raw = await File.ReadAllTextAsync(commonPath, Encoding.UTF8, cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            if ((!doc.RootElement.TryGetProperty("entries", out var entriesNode) &&
                 !doc.RootElement.TryGetProperty("Entries", out entriesNode)) ||
                entriesNode.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in entriesNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var original = (item.TryGetProperty("original", out var originalNode) ||
                                item.TryGetProperty("Original", out originalNode)) &&
                               originalNode.ValueKind == JsonValueKind.String
                    ? NormalizeForKey(originalNode.GetString() ?? string.Empty)
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(original))
                {
                    result.Add(original);
                }
            }
        }
        catch
        {
            // Best-effort only: fallback keeps existing behavior if common file parsing fails.
        }

        return result;
    }

    private static void ApplyPageBlockTranslations(
        IDocument document,
        string relativeHtmlPath,
        string i18nFolder,
        LanguageCatalog languageCatalog,
        HashSet<string> doNotTranslateSet)
    {
        var blockPath = Path.Combine(
            i18nFolder,
            PerPageBlocksFolderName,
            relativeHtmlPath
                .Replace('\\', '/')
                .TrimStart('/')
                .Replace(".html", ".json", StringComparison.OrdinalIgnoreCase)
                .Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(blockPath))
        {
            return;
        }

        BlockPageDocument? blockDoc;
        try
        {
            var raw = File.ReadAllText(blockPath, Encoding.UTF8);
            blockDoc = JsonSerializer.Deserialize<BlockPageDocument>(raw);
        }
        catch
        {
            return;
        }

        if (blockDoc is null)
        {
            return;
        }

        var blockItems = blockDoc.Groups.Count > 0
            ? blockDoc.Groups.SelectMany(group => group.Blocks).ToList()
            : blockDoc.Blocks.ToList();
        if (blockItems.Count == 0)
        {
            return;
        }

        var translatedBySignature = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in blockItems)
        {
            if (string.IsNullOrWhiteSpace(block.Type) ||
                string.Equals(block.Type, "inline_text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var original = NormalizeForKey(block.Original ?? string.Empty);
            var translated = (block.Translated ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(original) ||
                string.IsNullOrWhiteSpace(translated) ||
                string.Equals(original, NormalizeForKey(translated), StringComparison.Ordinal))
            {
                continue;
            }

            if (doNotTranslateSet.Contains(original))
            {
                continue;
            }

            var signature = $"{block.Type.Trim().ToLowerInvariant()}|{original}";
            // Use one deterministic translation per semantic signature and apply it to all
            // matching DOM occurrences (desktop/mobile duplicates, hidden nav copies, etc.).
            translatedBySignature[signature] = translated;
        }

        if (translatedBySignature.Count == 0)
        {
            return;
        }

        foreach (var element in document.All)
        {
            if (NonTranslatableContainers.Contains(element.LocalName) || IsWithinNonTranslatableContainer(element))
            {
                continue;
            }

            var blockType = ResolveSemanticBlockType(element);
            if (blockType is null ||
                string.Equals(blockType, "inline_text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(blockType, "paragraph", StringComparison.OrdinalIgnoreCase) &&
                IsRedundantParagraphInsideListItem(element))
            {
                continue;
            }

            var original = NormalizeForKey(element.TextContent ?? string.Empty);
            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var signature = $"{blockType}|{original}";
            if (!translatedBySignature.TryGetValue(signature, out var translated) || string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }
            if (TryApplyTranslatedTextPreservingLinks(element, translated, languageCatalog))
            {
                continue;
            }

            element.TextContent = translated;
        }
    }

    private static bool TryApplyTranslatedTextPreservingLinks(
        IElement element,
        string translated,
        LanguageCatalog languageCatalog)
    {
        if (TryApplyTranslatedInlineLinks(element, translated))
        {
            return true;
        }

        var anchors = element.QuerySelectorAll("a")
            .OfType<IElement>()
            .Where(a => !string.IsNullOrWhiteSpace(NormalizeForKey(a.TextContent ?? string.Empty)))
            .ToList();
        if (anchors.Count == 0)
        {
            return false;
        }

        var parts = new List<(string text, IElement? anchor)>();
        var searchFrom = 0;
        foreach (var anchor in anchors)
        {
            var anchorText = NormalizeForKey(anchor.TextContent ?? string.Empty);
            if (string.IsNullOrWhiteSpace(anchorText))
            {
                return false;
            }

            string? translatedAnchorText = null;
            var anchorKey = NormalizeForKey(anchorText);
            if (!string.IsNullOrWhiteSpace(anchorKey) &&
                languageCatalog.BySourceText.TryGetValue(anchorKey, out var bySource) &&
                !string.IsNullOrWhiteSpace(bySource))
            {
                translatedAnchorText = bySource.Trim();
            }

            var candidates = new List<string> { anchorText };
            if (!string.IsNullOrWhiteSpace(translatedAnchorText) &&
                !candidates.Contains(translatedAnchorText, StringComparer.Ordinal))
            {
                candidates.Add(translatedAnchorText);
            }

            var foundIndex = -1;
            var matchedText = string.Empty;
            foreach (var candidate in candidates)
            {
                var candidateIndex = translated.IndexOf(candidate, searchFrom, StringComparison.Ordinal);
                if (candidateIndex < 0)
                {
                    continue;
                }

                if (foundIndex < 0 || candidateIndex < foundIndex)
                {
                    foundIndex = candidateIndex;
                    matchedText = candidate;
                }
            }

            if (foundIndex < 0)
            {
                return false;
            }

            parts.Add((translated.Substring(searchFrom, foundIndex - searchFrom), null));
            parts.Add((string.Empty, anchor));
            searchFrom = foundIndex + matchedText.Length;
        }

        parts.Add((translated.Substring(searchFrom), null));

        foreach (var child in element.ChildNodes.ToArray())
        {
            child.Parent?.RemoveChild(child);
        }

        var document = element.Owner;
        if (document is null)
        {
            return false;
        }

        foreach (var (text, anchor) in parts)
        {
            if (!string.IsNullOrEmpty(text))
            {
                element.AppendChild(document.CreateTextNode(text));
            }

            if (anchor is not null)
            {
                element.AppendChild(anchor.Clone(true));
            }
        }

        return true;
    }

    private static bool TryApplyTranslatedInlineLinks(IElement element, string translated)
    {
        // Allow translators to define links directly in block translation text using:
        // [visible text](https://example.com) or [text](/relative/path)
        var matches = RegexInlineLink().Matches(translated);
        if (matches.Count == 0)
        {
            return false;
        }

        var document = element.Owner;
        if (document is null)
        {
            return false;
        }

        var parts = new List<(string text, string? href)>();
        var cursor = 0;
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            if (match.Index > cursor)
            {
                parts.Add((translated.Substring(cursor, match.Index - cursor), null));
            }

            var linkText = match.Groups[1].Value;
            var href = match.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(linkText) || string.IsNullOrWhiteSpace(href))
            {
                return false;
            }

            parts.Add((linkText, href));
            cursor = match.Index + match.Length;
        }

        if (cursor < translated.Length)
        {
            parts.Add((translated[cursor..], null));
        }

        foreach (var child in element.ChildNodes.ToArray())
        {
            child.Parent?.RemoveChild(child);
        }

        foreach (var (text, href) in parts)
        {
            if (href is null)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    element.AppendChild(document.CreateTextNode(text));
                }

                continue;
            }

            var anchor = document.CreateElement("a");
            anchor.SetAttribute("href", href);
            anchor.TextContent = text;

            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer nofollow");
            }

            element.AppendChild(anchor);
        }

        return true;
    }

    private static string BuildInitialTranslatedValue(IElement element, string fallbackOriginal)
    {
        if (element.QuerySelector("a[href]") is null)
        {
            return fallbackOriginal;
        }

        var seeded = BuildInlineLinkMarkdown(element);
        var normalized = NormalizeForKey(seeded);
        return string.IsNullOrWhiteSpace(normalized) ? fallbackOriginal : normalized;
    }

    private static string BuildInlineLinkMarkdown(INode node)
    {
        if (node is IText textNode)
        {
            return textNode.Data ?? string.Empty;
        }

        if (node is not IElement element)
        {
            return string.Empty;
        }

        if (string.Equals(element.LocalName, "a", StringComparison.OrdinalIgnoreCase))
        {
            var href = element.GetAttribute("href")?.Trim() ?? string.Empty;
            var linkText = NormalizeForKey(element.TextContent ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(href) && !string.IsNullOrWhiteSpace(linkText))
            {
                return $"[{linkText}]({href})";
            }
        }

        var sb = new StringBuilder();
        foreach (var child in element.ChildNodes)
        {
            sb.Append(BuildInlineLinkMarkdown(child));
        }

        return sb.ToString();
    }

    private static async Task<IReadOnlyList<string>> ResolveDoNotTranslateTextsAsync(
        string i18nFolder,
        IReadOnlyList<string>? requestedDoNotTranslateTexts,
        CancellationToken cancellationToken)
    {
        var normalizedRequested = requestedDoNotTranslateTexts?
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var configPath = Path.Combine(i18nFolder, DoNotTranslateFileName);
        if (normalizedRequested is { Count: > 0 })
        {
            await SaveDoNotTranslateTextsAsync(configPath, normalizedRequested, cancellationToken);
            return normalizedRequested;
        }

        if (!File.Exists(configPath))
        {
            return [];
        }

        try
        {
            var raw = await File.ReadAllTextAsync(configPath, Encoding.UTF8, cancellationToken);
            var config = JsonSerializer.Deserialize<DoNotTranslateCatalog>(raw);
            return config?.Texts?
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Task SaveDoNotTranslateTextsAsync(
        string configPath,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var payload = new DoNotTranslateCatalog
        {
            Texts = texts
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList()
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return File.WriteAllTextAsync(configPath, json, Encoding.UTF8, cancellationToken);
    }

    private static HashSet<string> BuildDoNotTranslateSet(IReadOnlyList<string>? doNotTranslateTexts)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        if (doNotTranslateTexts is null)
        {
            return normalized;
        }

        foreach (var text in doNotTranslateTexts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalizedValue = NormalizeForKey(text);
            if (!string.IsNullOrWhiteSpace(normalizedValue))
            {
                normalized.Add(normalizedValue);
            }
        }

        return normalized;
    }

    private static bool IsRtlLanguage(string language) =>
        language.StartsWith("fa", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("he", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("ur", StringComparison.OrdinalIgnoreCase);

    private static void ApplyRtlLayout(IDocument document)
    {
        if (document.DocumentElement is null)
        {
            return;
        }

        document.DocumentElement.SetAttribute("dir", "rtl");
        var body = document.Body;
        if (body is not null)
        {
            body.SetAttribute("dir", "rtl");
        }

        var head = document.Head;
        if (head is null)
        {
            return;
        }

        var existingStyle = head.QuerySelector("style[data-site-mirror-rtl='1']");
        if (existingStyle is not null)
        {
            return;
        }

        var style = document.CreateElement("style");
        style.SetAttribute("data-site-mirror-rtl", "1");
        style.TextContent = """
            html[dir='rtl'], html[dir='rtl'] body {
                direction: rtl !important;
                text-align: right !important;
            }
            html[dir='rtl'] section,
            html[dir='rtl'] article,
            html[dir='rtl'] main,
            html[dir='rtl'] nav,
            html[dir='rtl'] header,
            html[dir='rtl'] footer,
            html[dir='rtl'] aside,
            html[dir='rtl'] div,
            html[dir='rtl'] p,
            html[dir='rtl'] li,
            html[dir='rtl'] ul,
            html[dir='rtl'] ol,
            html[dir='rtl'] table,
            html[dir='rtl'] form,
            html[dir='rtl'] input,
            html[dir='rtl'] textarea,
            html[dir='rtl'] button,
            html[dir='rtl'] label,
            html[dir='rtl'] h1,
            html[dir='rtl'] h2,
            html[dir='rtl'] h3,
            html[dir='rtl'] h4,
            html[dir='rtl'] h5,
            html[dir='rtl'] h6 {
                direction: rtl !important;
                text-align: right !important;
            }
            """;
        head.AppendChild(style);
    }

    private async Task<LanguageCatalog> LoadOrCreateLanguageCatalogAsync(
        string language,
        string i18nFolder,
        IReadOnlyDictionary<string, string> sourceEntries,
        CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(i18nFolder, $"{language}.json");
        var loadedByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var loadedBySource = new Dictionary<string, string>(StringComparer.Ordinal);

        if (File.Exists(filePath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
                ParseLanguageCatalog(raw, loadedByKey, loadedBySource);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read translation file {FilePath}, rebuilding from source entries.", filePath);
            }
        }

        foreach (var (key, sourceValue) in sourceEntries)
        {
            if (loadedByKey.ContainsKey(key))
            {
                continue;
            }

            var normalizedSourceValue = NormalizeForKey(sourceValue);
            if (!string.IsNullOrWhiteSpace(normalizedSourceValue) &&
                loadedBySource.TryGetValue(normalizedSourceValue, out var translatedBySource) &&
                !string.IsNullOrWhiteSpace(translatedBySource))
            {
                loadedByKey[key] = translatedBySource;
                continue;
            }

            loadedByKey[key] = sourceValue;
        }

        await SaveCatalogAsync(filePath, language, loadedByKey, cancellationToken);
        return new LanguageCatalog
        {
            ByKey = loadedByKey,
            BySourceText = loadedBySource
        };
    }

    private static void ParseLanguageCatalog(
        string raw,
        Dictionary<string, string> byKey,
        Dictionary<string, string> bySourceText)
    {
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if ((doc.RootElement.TryGetProperty("entries", out var entriesNode) ||
             doc.RootElement.TryGetProperty("Entries", out entriesNode)) &&
            entriesNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in entriesNode.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString() ?? string.Empty;
                    if (property.Name.StartsWith("k_", StringComparison.Ordinal))
                    {
                        byKey[property.Name] = value;
                        continue;
                    }

                    var normalizedSource = NormalizeForKey(property.Name);
                    if (!string.IsNullOrWhiteSpace(normalizedSource))
                    {
                        bySourceText[normalizedSource] = value;
                    }
                }
            }
        }
        else
        {
            // Backward compatibility: allow plain object map files where keys are either
            // generated token keys (k_xxx) or raw source texts.
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString() ?? string.Empty;
                if (property.Name.StartsWith("k_", StringComparison.Ordinal))
                {
                    byKey[property.Name] = value;
                    continue;
                }

                if (string.Equals(property.Name, "language", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var normalizedSource = NormalizeForKey(property.Name);
                if (!string.IsNullOrWhiteSpace(normalizedSource))
                {
                    bySourceText[normalizedSource] = value;
                }
            }
        }

        if (doc.RootElement.TryGetProperty("translations", out var translationsNode) &&
            translationsNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in translationsNode.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var normalizedSource = NormalizeForKey(property.Name);
                if (!string.IsNullOrWhiteSpace(normalizedSource))
                {
                    bySourceText[normalizedSource] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
    }

    private static Task SaveCatalogAsync(
        string path,
        string language,
        IReadOnlyDictionary<string, string> entries,
        CancellationToken cancellationToken)
    {
        var payload = new TranslationCatalog
        {
            Language = language,
            Entries = entries
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string mirrorRoot, HashSet<string> excludedRootSegments)
    {
        foreach (var file in Directory.EnumerateFiles(mirrorRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(mirrorRoot, file);
            var rootSegment = GetFirstPathSegment(relativePath);
            if (!string.IsNullOrWhiteSpace(rootSegment) &&
                excludedRootSegments.Contains(rootSegment))
            {
                continue;
            }

            yield return relativePath;
        }
    }

    private static void CopySourceFiles(string sourceRoot, string destinationRoot, IEnumerable<string> relativeFiles)
    {
        foreach (var relativePath in relativeFiles)
        {
            var sourcePath = Path.Combine(sourceRoot, relativePath);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    internal static string BuildGlobalTextKey(string sourceValue)
    {
        var normalizedValue = NormalizeForKey(sourceValue);
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(normalizedValue));
        return $"k_{Convert.ToHexString(bytes).ToLowerInvariant()[..12]}";
    }

    private static string BuildTranslationKey(string relativeHtmlPath, int ordinal, string sourceValue)
    {
        // Global/shared key strategy:
        // keys are generated from normalized source text only so repeated
        // strings (navbar/footer/common labels) share the same key across pages.
        // This allows one translation entry to update all pages together.
        return BuildGlobalTextKey(sourceValue);
    }

    private static string BuildRootAliasFileName(string nestedJsonPath, HashSet<string> usedNames)
    {
        var normalized = nestedJsonPath.Replace('\\', '/').TrimStart('/');
        var alias = normalized.Replace('/', '_');
        if (usedNames.Add(alias))
        {
            return alias;
        }

        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..8];
        var stem = Path.GetFileNameWithoutExtension(alias);
        var ext = Path.GetExtension(alias);
        var withHash = $"{stem}--{hash}{ext}";
        if (usedNames.Add(withHash))
        {
            return withHash;
        }

        var suffix = 2;
        while (true)
        {
            var candidate = $"{stem}--{hash}-{suffix}{ext}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
            suffix++;
        }
    }

    private static string BuildBlockId(string pagePath, int ordinal, string blockType, string sourceValue)
    {
        var normalizedPath = pagePath.Trim().Replace('\\', '/').ToLowerInvariant();
        var normalizedValue = NormalizeForKey(sourceValue);
        var normalizedType = blockType.Trim().ToLowerInvariant();
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{normalizedPath}|{ordinal}|{normalizedType}|{normalizedValue}"));
        return $"b_{Convert.ToHexString(bytes).ToLowerInvariant()[..12]}";
    }

    private static string BuildGroupId(string pagePath, int ordinal, string headingTag, string headingText)
    {
        var normalizedPath = pagePath.Trim().Replace('\\', '/').ToLowerInvariant();
        var normalizedHeadingTag = headingTag.Trim().ToLowerInvariant();
        var normalizedHeadingText = NormalizeForKey(headingText);
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{normalizedPath}|{ordinal}|{normalizedHeadingTag}|{normalizedHeadingText}"));
        return $"g_{Convert.ToHexString(bytes).ToLowerInvariant()[..12]}";
    }

    private static string NormalizeForKey(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return RegexWhitespace().Replace(trimmed, " ");
    }

    private static void ProcessTranslatableTokens(
        IDocument document,
        string relativeHtmlPath,
        Action<string, string, Action<string>> onToken)
    {
        var ordinal = 0;
        foreach (var element in document.All)
        {
            if (NonTranslatableContainers.Contains(element.LocalName) || IsWithinNonTranslatableContainer(element))
            {
                continue;
            }

            foreach (var attribute in element.Attributes.ToArray())
            {
                if (!TranslatableAttributes.Contains(attribute.Name))
                {
                    continue;
                }

                var value = attribute.Value;
                if (!IsTranslatableValue(value))
                {
                    continue;
                }

                ordinal++;
                var key = BuildTranslationKey(relativeHtmlPath, ordinal, value);
                onToken(key, value, replacement => element.SetAttribute(attribute.Name, replacement));
            }

            foreach (var textNode in element.ChildNodes.OfType<IText>())
            {
                var value = textNode.Data;
                if (!IsTranslatableValue(value))
                {
                    continue;
                }

                ordinal++;
                var key = BuildTranslationKey(relativeHtmlPath, ordinal, value);
                onToken(key, value, replacement => textNode.Data = replacement);
            }
        }
    }

    private static bool IsTranslatableValue(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        return !trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithinNonTranslatableContainer(IElement element)
    {
        var parent = element.ParentElement;
        while (parent is not null)
        {
            if (NonTranslatableContainers.Contains(parent.LocalName))
            {
                return true;
            }

            parent = parent.ParentElement;
        }

        return false;
    }

    private static GroupedBlocksResult ExtractSemanticBlocksWithGroups(
        IDocument document,
        string pagePath,
        HashSet<string> commonKeys,
        HashSet<string> commonClassNames,
        Dictionary<string, CommonBlockEntry> commonEntriesByKey)
    {
        var blocks = new List<BlockDocumentItem>();
        var groups = new List<BlockGroupDocumentItem>();
        var blockOrdinal = 0;
        var groupOrdinal = 0;
        string? activeGroupId = null;
        BlockGroupDocumentItem? activeGroup = null;

        foreach (var element in document.All)
        {
            if (NonTranslatableContainers.Contains(element.LocalName) || IsWithinNonTranslatableContainer(element))
            {
                continue;
            }

            var blockType = ResolveSemanticBlockType(element);
            if (blockType is null)
            {
                continue;
            }

            // <li><p>…</p></li> (common in Next.js / MDX): the <li> already becomes a list_item with
            // the same TextContent as the inner <p>. Skip the inner <p> to avoid duplicate blocks.
            if (string.Equals(blockType, "paragraph", StringComparison.OrdinalIgnoreCase) &&
                IsRedundantParagraphInsideListItem(element))
            {
                continue;
            }

            var original = NormalizeForKey(element.TextContent ?? string.Empty);
            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var commonKey = BuildGlobalTextKey(original);
            var inCommonArea = commonKeys.Contains(commonKey) || IsInsideCommonContainer(element, commonClassNames);

            // Inline tags are used only to improve common extraction. For page-specific blocks,
            // keeping them causes heavy duplication next to parent paragraph/list blocks.
            if (blockType == "inline_text" && !inCommonArea)
            {
                continue;
            }

            // If inline text is already represented by an ancestor semantic block in common area,
            // skip it to avoid repeated entries like li + a/span with same content.
            if (blockType == "inline_text" && HasAncestorPrimarySemanticBlock(element))
            {
                continue;
            }

            if (inCommonArea)
            {
                if (!commonEntriesByKey.ContainsKey(commonKey))
                {
                    commonEntriesByKey[commonKey] = new CommonBlockEntry
                    {
                        Key = commonKey,
                        Original = original,
                        Translated = string.Empty
                    };
                }
                continue;
            }

            var tagName = element.LocalName.ToLowerInvariant();
            if (tagName is "h1" or "h2" or "h3")
            {
                groupOrdinal++;
                activeGroupId = BuildGroupId(pagePath, groupOrdinal, tagName, original);
                activeGroup = new BlockGroupDocumentItem
                {
                    Id = activeGroupId,
                    HeadingType = tagName,
                    Heading = original
                };
                groups.Add(activeGroup);
            }
            else if (activeGroup is null)
            {
                // Content before first h1/h2/h3 goes in a deterministic intro group.
                groupOrdinal++;
                activeGroupId = BuildGroupId(pagePath, groupOrdinal, "intro", "intro");
                activeGroup = new BlockGroupDocumentItem
                {
                    Id = activeGroupId,
                    HeadingType = "intro",
                    Heading = string.Empty
                };
                groups.Add(activeGroup);
            }

            blockOrdinal++;
            var block = new BlockDocumentItem
            {
                Id = BuildBlockId(pagePath, blockOrdinal, blockType, original),
                Type = blockType,
                Original = original,
                // Prefill with source text; if this block contains links, seed markdown links with fixed hrefs.
                Translated = BuildInitialTranslatedValue(element, original),
                GroupId = activeGroupId
            };
            blocks.Add(block);
            activeGroup.Blocks.Add(block);
        }

        return new GroupedBlocksResult
        {
            Blocks = blocks,
            Groups = groups.Where(g => g.Blocks.Count > 0).ToList()
        };
    }

    private static bool IsInsideCommonContainer(IElement element, HashSet<string> commonClassNames)
    {
        var current = element;
        while (current is not null)
        {
            if (string.Equals(current.LocalName, "header", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current.LocalName, "footer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current.LocalName, "nav", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (commonClassNames.Count > 0)
            {
                foreach (var className in current.ClassList)
                {
                    var normalized = className?.Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(normalized) && commonClassNames.Contains(normalized))
                    {
                        return true;
                    }
                }
            }

            current = current.ParentElement;
        }

        return false;
    }

    private static bool HasAncestorPrimarySemanticBlock(IElement element)
    {
        var current = element.ParentElement;
        while (current is not null)
        {
            var tag = current.LocalName?.ToLowerInvariant();
            if (tag is "p" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                return true;
            }

            if (IsLinkRichTextContainer(current) || IsProseTextContainer(current))
            {
                return true;
            }

            current = current.ParentElement;
        }

        return false;
    }

    private static bool IsRedundantParagraphInsideListItem(IElement element)
    {
        if (!string.Equals(element.LocalName, "p", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parent = element.ParentElement;
        if (parent is null || !string.Equals(parent.LocalName, "li", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IElement? onlyElementChild = null;
        foreach (var child in parent.Children)
        {
            if (string.Equals(child.LocalName, "script", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(child.LocalName, "style", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (onlyElementChild is not null)
            {
                return false;
            }

            onlyElementChild = child;
        }

        return onlyElementChild is not null && ReferenceEquals(onlyElementChild, element);
    }

    private static HashSet<string> NormalizeCommonClassNames(IReadOnlyList<string>? generalTranslationClasses)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in generalTranslationClasses ?? [])
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var normalized = item.Trim().TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static async Task<Dictionary<string, CommonBlockEntry>> LoadCommonEntriesByKeyAsync(
        string i18nFolder,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(i18nFolder, PerPageBlocksFolderName, CommonBlockFileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, CommonBlockEntry>(StringComparer.Ordinal);
        }

        try
        {
            var raw = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
            var payload = JsonSerializer.Deserialize<CommonBlockCatalog>(raw);
            return payload?.Entries?
                .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                .ToDictionary(e => e.Key, e => e, StringComparer.Ordinal)
                   ?? new Dictionary<string, CommonBlockEntry>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, CommonBlockEntry>(StringComparer.Ordinal);
        }
    }

    private static Task SaveCommonEntriesAsync(
        string perPageBlocksRoot,
        Dictionary<string, CommonBlockEntry> commonEntriesByKey,
        CancellationToken cancellationToken)
    {
        var payload = new CommonBlockCatalog
        {
            Language = "fa",
            Entries = commonEntriesByKey.Values
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToList()
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var commonPath = Path.Combine(perPageBlocksRoot, CommonBlockFileName);
        return File.WriteAllTextAsync(commonPath, json, Encoding.UTF8, cancellationToken);
    }

    private static async Task<HashSet<string>> LoadCommonBlockKeysAsync(string i18nFolder, CancellationToken cancellationToken)
    {
        var commonPath = Path.Combine(i18nFolder, PerPageBlocksFolderName, CommonBlockFileName);
        if (!File.Exists(commonPath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var raw = await File.ReadAllTextAsync(commonPath, Encoding.UTF8, cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!doc.RootElement.TryGetProperty("entries", out var entriesNode) &&
                !doc.RootElement.TryGetProperty("Entries", out entriesNode))
            {
                return result;
            }

            if (entriesNode.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in entriesNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var translated = (item.TryGetProperty("translated", out var translatedNode) ||
                                  item.TryGetProperty("Translated", out translatedNode)) &&
                                 translatedNode.ValueKind == JsonValueKind.String
                    ? translatedNode.GetString()?.Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(translated))
                {
                    continue;
                }

                if ((item.TryGetProperty("key", out var keyNode) || item.TryGetProperty("Key", out keyNode)) &&
                    keyNode.ValueKind == JsonValueKind.String)
                {
                    var key = keyNode.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        result.Add(key);
                    }
                }
            }

            return result;
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static string? ResolveSemanticBlockType(IElement element)
    {
        var localName = element.LocalName;
        if (string.IsNullOrWhiteSpace(localName))
        {
            return null;
        }

        if (localName.StartsWith("h", StringComparison.OrdinalIgnoreCase) &&
            localName.Length == 2 &&
            char.IsDigit(localName[1]))
        {
            return "heading";
        }

        return localName.ToLowerInvariant() switch
        {
            "p" => "paragraph",
            "li" => "list_item",
            "a" => "inline_text",
            "button" => "inline_text",
            "span" => "inline_text",
            "div" or "section" or "article" => IsLinkRichTextContainer(element)
                ? "rich_text"
                : IsProseTextContainer(element)
                    ? "paragraph"
                    : null,
            _ => null
        };
    }

    /// <summary>
    /// Paragraph-like copy is sometimes a &lt;div&gt; with only text (no &lt;p&gt;, no links).
    /// Those were previously invisible to block extraction because only link-bearing divs qualified.
    /// </summary>
    private static bool IsProseTextContainer(IElement element)
    {
        var tag = element.LocalName.ToLowerInvariant();
        if (tag is not ("div" or "section" or "article"))
        {
            return false;
        }

        if (IsLinkRichTextContainer(element))
        {
            return false;
        }

        if (element.QuerySelector("p, li, h1, h2, h3, h4, h5, h6") is not null)
        {
            return false;
        }

        if (element.QuerySelector("a, button") is not null)
        {
            return false;
        }

        var text = element.TextContent?.Trim() ?? string.Empty;
        if (text.Length < 20 || text.Length > 8_000)
        {
            return false;
        }

        // Skip pure wrappers so we do not duplicate the same string on parent and child divs.
        var hasNonWhiteDirectText = element.ChildNodes.Any(n =>
            n.NodeType == NodeType.Text && !string.IsNullOrWhiteSpace(n.TextContent));
        var elementChildren = element.Children.OfType<IElement>().ToList();
        if (!hasNonWhiteDirectText &&
            elementChildren.Count == 1 &&
            elementChildren[0].LocalName is "div" or "section" or "article")
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Many doc sites use div/section containers for paragraph-like copy with inline links
    /// instead of a &lt;p&gt; tag. Capture those so link-heavy paragraphs are not lost.
    /// </summary>
    private static bool IsLinkRichTextContainer(IElement element)
    {
        var tag = element.LocalName.ToLowerInvariant();
        if (tag is not ("div" or "section" or "article"))
        {
            return false;
        }

        if (element.QuerySelector("p, li, h1, h2, h3, h4, h5, h6") is not null)
        {
            return false;
        }

        if (element.QuerySelector("a, button") is null)
        {
            return false;
        }

        var text = element.TextContent?.Trim() ?? string.Empty;
        if (text.Length < 20 || text.Length > 8_000)
        {
            return false;
        }

        return true;
    }

    private static string GetFirstPathSegment(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slashIndex = normalized.IndexOf('/');
        return slashIndex < 0 ? normalized : normalized[..slashIndex];
    }

    private static List<string> ResolveTargetHtmlFiles(
        IReadOnlyList<string> sourceHtmlFiles,
        IReadOnlyList<string>? targetPages)
    {
        if (targetPages is null || targetPages.Count == 0)
        {
            return sourceHtmlFiles.ToList();
        }

        var sourceSet = new HashSet<string>(sourceHtmlFiles, StringComparer.OrdinalIgnoreCase);
        var selected = new List<string>();
        foreach (var page in targetPages)
        {
            var normalized = NormalizeTargetPage(page);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!normalized.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".html";
            }

            if (!sourceSet.Contains(normalized))
            {
                continue;
            }

            if (!selected.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                selected.Add(normalized);
            }
        }

        return selected;
    }

    private static string NormalizeTargetPage(string? rawPage)
    {
        if (string.IsNullOrWhiteSpace(rawPage))
        {
            return string.Empty;
        }

        var page = rawPage.Trim().Replace('\\', '/');
        const string localizedPrefix = "/_localized/";
        var localizedIndex = page.IndexOf(localizedPrefix, StringComparison.OrdinalIgnoreCase);
        if (localizedIndex >= 0)
        {
            var remaining = page[(localizedIndex + localizedPrefix.Length)..];
            var slashIndex = remaining.IndexOf('/');
            page = slashIndex >= 0 ? remaining[(slashIndex + 1)..] : remaining;
        }

        page = page.TrimStart('/');
        return page;
    }

    private static List<string> NormalizeLanguages(IReadOnlyList<string> languages)
    {
        var normalized = new List<string>();
        foreach (var language in languages)
        {
            var trimmed = language?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-'))
            {
                continue;
            }

            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(trimmed);
            }
        }

        if (!normalized.Contains("en", StringComparer.OrdinalIgnoreCase))
        {
            normalized.Insert(0, "en");
        }

        return normalized;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex RegexWhitespace();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial System.Text.RegularExpressions.Regex RegexInlineLink();

    private sealed class TranslationCatalog
    {
        public string Language { get; init; } = "en";

        public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class DoNotTranslateCatalog
    {
        public List<string> Texts { get; init; } = [];
    }

    private sealed class BlockPageDocument
    {
        public string Page { get; init; } = "/";

        public List<BlockDocumentItem> Blocks { get; init; } = [];

        public List<BlockGroupDocumentItem> Groups { get; init; } = [];
    }

    private sealed class BlockDocumentItem
    {
        public string Id { get; init; } = string.Empty;

        public string Type { get; init; } = "paragraph";

        public string Original { get; init; } = string.Empty;

        public string Translated { get; init; } = string.Empty;

        public string? GroupId { get; init; }
    }

    private sealed class BlockGroupDocumentItem
    {
        public string Id { get; init; } = string.Empty;

        public string HeadingType { get; init; } = "intro";

        public string Heading { get; init; } = string.Empty;

        public List<BlockDocumentItem> Blocks { get; init; } = [];
    }

    private sealed class GroupedBlocksResult
    {
        public List<BlockDocumentItem> Blocks { get; init; } = [];

        public List<BlockGroupDocumentItem> Groups { get; init; } = [];
    }

    private sealed class RootBlockIndex
    {
        public List<RootBlockEntry> Pages { get; init; } = [];
    }

    private sealed class RootBlockEntry
    {
        public string Page { get; init; } = "/";

        public string NestedPath { get; init; } = string.Empty;

        public string RootFile { get; init; } = string.Empty;
    }

    private sealed class LanguageCatalog
    {
        public required Dictionary<string, string> ByKey { get; init; }

        public required Dictionary<string, string> BySourceText { get; init; }
    }

    private sealed class CommonBlockCatalog
    {
        public string Language { get; init; } = "fa";
        public List<CommonBlockEntry> Entries { get; init; } = [];
    }

    private sealed class CommonBlockEntry
    {
        public string Key { get; init; } = string.Empty;
        public string Original { get; init; } = string.Empty;
        public string Translated { get; init; } = string.Empty;
    }

    internal sealed class LocalizationGenerationResult
    {
        public required string DefaultLanguage { get; init; }

        public required IReadOnlyList<string> AvailableLanguages { get; init; }
    }
}
