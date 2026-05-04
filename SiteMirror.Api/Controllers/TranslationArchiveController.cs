using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services;

namespace SiteMirror.Api.Controllers;

[ApiController]
[Route("api/translation-archive")]
[Authorize]
public sealed class TranslationArchiveController(
    ICrawlRepository crawlRepository,
    ISiteMirrorService mirrorService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>Matches on-disk language catalogs (<c>_i18n/{lang}.json</c>): PascalCase <c>Language</c> / <c>Entries</c>, indented.</summary>
    private static readonly JsonSerializerOptions CatalogFileJsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed class TranslationCatalogFileBody
    {
        public string Language { get; init; } = string.Empty;

        public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TranslationArchiveRecordDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TranslationArchiveRecordDto>>> Query(
        [FromQuery] string? siteHost,
        [FromQuery] string? version,
        [FromQuery] string? language,
        [FromQuery] string? scope,
        [FromQuery] int take = 1000,
        CancellationToken cancellationToken = default)
    {
        var rows = await crawlRepository.QueryTranslationArchiveAsync(
            siteHost,
            version,
            language,
            scope,
            take,
            cancellationToken);
        return Ok(rows);
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download(
        [FromQuery] string? siteHost,
        [FromQuery] string? version,
        [FromQuery] string? language,
        [FromQuery] string? scope,
        /// <summary><c>catalog</c> (default when siteHost, version, and language are set): <c>{ "Language", "Entries" }</c> from latest <c>catalog</c> rows. <c>rows</c>: raw archive rows (respects <paramref name="scope"/>).</summary>
        [FromQuery] string? format = null,
        [FromQuery] int take = 5000,
        CancellationToken cancellationToken = default)
    {
        var useRows = string.Equals(format, "rows", StringComparison.OrdinalIgnoreCase);
        var hasSite = !string.IsNullOrWhiteSpace(siteHost);
        var hasVer = !string.IsNullOrWhiteSpace(version);
        var hasLang = !string.IsNullOrWhiteSpace(language);
        var wantCatalogFile =
            !useRows
            && hasSite
            && hasVer
            && hasLang
            && (string.Equals(format, "catalog", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(format));

        if (wantCatalogFile)
        {
            var lang = language!.Trim().ToLowerInvariant();
            var entries = await crawlRepository.GetLatestCatalogEntriesFromArchiveAsync(
                siteHost!.Trim(),
                version!.Trim(),
                lang,
                cancellationToken);
            var body = new TranslationCatalogFileBody
            {
                Language = lang,
                Entries = entries
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            };
            var catalogJson = JsonSerializer.Serialize(body, CatalogFileJsonOptions);
            var bytes = Encoding.UTF8.GetBytes(catalogJson);
            var safeHost = SanitizeFileToken(siteHost!);
            var safeVer = SanitizeFileToken(version!);
            var safeLang = SanitizeFileToken(lang);
            var fileName = $"translation-catalog_{safeHost}_{safeVer}_{safeLang}.json";
            return File(bytes, "application/json; charset=utf-8", fileName);
        }

        var rows = await crawlRepository.QueryTranslationArchiveAsync(
            siteHost,
            version,
            language,
            scope,
            take,
            cancellationToken);

        var json = JsonSerializer.Serialize(rows, JsonOptions);
        var rowBytes = Encoding.UTF8.GetBytes(json);
        var safeHostR = string.IsNullOrWhiteSpace(siteHost) ? "all" : SanitizeFileToken(siteHost);
        var safeVerR = string.IsNullOrWhiteSpace(version) ? "all" : SanitizeFileToken(version);
        var safeLangR = string.IsNullOrWhiteSpace(language) ? "all" : SanitizeFileToken(language);
        var safeScope = string.IsNullOrWhiteSpace(scope) ? "all" : SanitizeFileToken(scope);
        var fileNameRows = $"translation-archive_{safeHostR}_{safeVerR}_{safeLangR}_{safeScope}.json";
        return File(rowBytes, "application/json; charset=utf-8", fileNameRows);
    }

    /// <summary>
    /// Merges latest DB catalog rows into the site language JSON and regenerates localized pages (same pipeline as manual translation update).
    /// </summary>
    [HttpPost("apply-catalog")]
    [ProducesResponseType(typeof(UpdateTranslationsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UpdateTranslationsResult>> ApplyCatalogFromArchive(
        [FromBody] ApplyTranslationArchiveRequest body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(body.SiteHost))
        {
            return BadRequest(new { message = "SiteHost is required." });
        }

        var lang = body.Language?.Trim().ToLowerInvariant() ?? "en";
        if (string.IsNullOrWhiteSpace(lang))
        {
            return BadRequest(new { message = "Language is required." });
        }

        var entries = await crawlRepository.GetLatestCatalogEntriesFromArchiveAsync(
            body.SiteHost.Trim(),
            body.Version?.Trim() ?? "latest",
            lang,
            cancellationToken);

        if (entries.Count == 0)
        {
            return BadRequest(new
            {
                message = "No archived catalog rows found for this site, version, and language. Save translations from the Translation Review flow first."
            });
        }

        var request = new UpdateTranslationsRequest
        {
            SiteHost = body.SiteHost.Trim(),
            Version = body.Version?.Trim() ?? "latest",
            Language = lang,
            Entries = new Dictionary<string, string>(entries, StringComparer.Ordinal),
            DoNotTranslateTexts = null,
            TargetPages = null
        };

        var result = await mirrorService.UpdateTranslationsAsync(request, cancellationToken);
        return Ok(result);
    }

    private static string SanitizeFileToken(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        var s = sb.ToString();
        return string.IsNullOrEmpty(s) ? "x" : s[..Math.Min(s.Length, 48)];
    }
}
