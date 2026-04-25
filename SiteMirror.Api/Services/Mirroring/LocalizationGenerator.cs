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
        IReadOnlyList<string> sourceHtmlFiles,
        LanguageCatalog languageCatalog,
        HashSet<string> doNotTranslateSet,
        CancellationToken cancellationToken)
    {
        var parser = new HtmlParser();
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

                var localizedValue = ResolveLocalizedValue(key, sourceValue, languageCatalog);
                if (!string.Equals(localizedValue, sourceValue, StringComparison.Ordinal))
                {
                    setValue(localizedValue);
                }
            });

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
            return translatedByKey;
        }

        var normalizedSourceValue = NormalizeForKey(sourceValue);
        if (!string.IsNullOrWhiteSpace(normalizedSourceValue) &&
            languageCatalog.BySourceText.TryGetValue(normalizedSourceValue, out var translatedBySource) &&
            !string.IsNullOrWhiteSpace(translatedBySource))
        {
            return translatedBySource;
        }

        return sourceValue;
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

        if (doc.RootElement.TryGetProperty("entries", out var entriesNode) &&
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

    private static string BuildTranslationKey(string relativeHtmlPath, int ordinal, string sourceValue)
    {
        var normalizedPath = relativeHtmlPath.Replace('\\', '/').ToLowerInvariant();
        var normalizedValue = NormalizeForKey(sourceValue);
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{normalizedPath}|{ordinal}|{normalizedValue}"));
        return $"k_{Convert.ToHexString(bytes).ToLowerInvariant()[..12]}";
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

    private static string GetFirstPathSegment(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slashIndex = normalized.IndexOf('/');
        return slashIndex < 0 ? normalized : normalized[..slashIndex];
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

    private sealed class TranslationCatalog
    {
        public string Language { get; init; } = "en";

        public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class DoNotTranslateCatalog
    {
        public List<string> Texts { get; init; } = [];
    }

    private sealed class LanguageCatalog
    {
        public required Dictionary<string, string> ByKey { get; init; }

        public required Dictionary<string, string> BySourceText { get; init; }
    }

    internal sealed class LocalizationGenerationResult
    {
        public required string DefaultLanguage { get; init; }

        public required IReadOnlyList<string> AvailableLanguages { get; init; }
    }
}
