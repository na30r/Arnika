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
        var englishEntries = await LoadOrCreateLanguageEntriesAsync("en", i18nFolder, sourceEntries, cancellationToken);

        foreach (var language in normalizedLanguages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var languageEntries = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
                ? englishEntries
                : await LoadOrCreateLanguageEntriesAsync(language, i18nFolder, sourceEntries, cancellationToken);

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
                sourceEntries,
                languageEntries,
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
        IReadOnlyDictionary<string, string> sourceEntries,
        IReadOnlyDictionary<string, string> languageEntries,
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
                var localizedValue = ResolveLocalizedValue(key, sourceValue, sourceEntries, languageEntries);
                if (!string.Equals(localizedValue, sourceValue, StringComparison.Ordinal))
                {
                    setValue(localizedValue);
                }
            });

            if (document.DocumentElement is not null)
            {
                document.DocumentElement.SetAttribute("lang", language);
                document.DocumentElement.SetAttribute("data-site-mirror-lang", language);
            }

            var updatedHtml = document.DocumentElement?.OuterHtml ?? html;
            await File.WriteAllTextAsync(localizedHtmlPath, updatedHtml, Encoding.UTF8, cancellationToken);
        }
    }

    private static string ResolveLocalizedValue(
        string key,
        string sourceValue,
        IReadOnlyDictionary<string, string> sourceEntries,
        IReadOnlyDictionary<string, string> languageEntries)
    {
        if (languageEntries.TryGetValue(key, out var translated) && !string.IsNullOrWhiteSpace(translated))
        {
            return translated;
        }

        if (sourceEntries.TryGetValue(key, out var sourceCatalogValue))
        {
            return sourceCatalogValue;
        }

        return sourceValue;
    }

    private async Task<Dictionary<string, string>> LoadOrCreateLanguageEntriesAsync(
        string language,
        string i18nFolder,
        IReadOnlyDictionary<string, string> sourceEntries,
        CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(i18nFolder, $"{language}.json");
        var loadedEntries = new Dictionary<string, string>(StringComparer.Ordinal);

        if (File.Exists(filePath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
                var loadedCatalog = JsonSerializer.Deserialize<TranslationCatalog>(raw);
                if (loadedCatalog?.Entries is not null)
                {
                    foreach (var (key, value) in loadedCatalog.Entries)
                    {
                        loadedEntries[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read translation file {FilePath}, rebuilding from source entries.", filePath);
            }
        }

        foreach (var (key, sourceValue) in sourceEntries)
        {
            if (!loadedEntries.ContainsKey(key))
            {
                loadedEntries[key] = sourceValue;
            }
        }

        await SaveCatalogAsync(filePath, language, loadedEntries, cancellationToken);
        return loadedEntries;
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

    internal sealed class LocalizationGenerationResult
    {
        public required string DefaultLanguage { get; init; }

        public required IReadOnlyList<string> AvailableLanguages { get; init; }
    }
}
