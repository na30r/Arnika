using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services;

namespace SiteMirror.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class MirrorController(
    ISiteMirrorService mirrorService,
    IUserRepository userRepository,
    CrawlReadDbContext crawlReadDbContext) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(MirrorResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    public async Task<ActionResult<MirrorResult>> StartMirror([FromBody] MirrorRequest request, CancellationToken cancellationToken)
    {
        try
        {
            Guid? userId = null;
            var hasAuthHeader = !string.IsNullOrWhiteSpace(Request.Headers.Authorization);
            if (hasAuthHeader)
            {
                if (User.Identity?.IsAuthenticated != true)
                {
                    return Unauthorized(new { message = "Invalid or expired Bearer token." });
                }

                var userClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(userClaim) || !Guid.TryParse(userClaim, out var parsedUserId))
                {
                    return Unauthorized(new { message = "Invalid token payload." });
                }

                var user = await userRepository.GetByIdAsync(parsedUserId, cancellationToken);
                if (user is null)
                {
                    return Unauthorized(new { message = "User not found." });
                }

                if (user.SubscriptionEndDateUtc.HasValue && user.SubscriptionEndDateUtc.Value < DateTimeOffset.UtcNow)
                {
                    return StatusCode(402, new
                    {
                        message = "Subscription has expired. Renew to run mirrors.",
                        subscriptionEndDateUtc = user.SubscriptionEndDateUtc
                    });
                }

                userId = parsedUserId;
            }

            var result = await mirrorService.MirrorAsync(request, userId, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (TimeoutException ex)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                message = ex.Message,
                hint = "The target page did not finish loading within configured timeout. Try a smaller wait time or increase navigation timeout in settings."
            });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                message = ex.Message,
                hint = "Playwright navigation timed out. This is common on pages with long-running network traffic."
            });
        }
    }

    [HttpPost("rewrite-links")]
    [ProducesResponseType(typeof(RewriteLinksResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RewriteLinksResult>> RewriteLinks([FromBody] RewriteLinksRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await mirrorService.RewriteLinksAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("crawls/{crawlId}")]
    [ProducesResponseType(typeof(CrawlStatusResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CrawlStatusResult>> GetCrawlStatus([FromRoute] string crawlId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await mirrorService.GetCrawlStatusAsync(crawlId, cancellationToken);
            if (result is null)
            {
                return NotFound(new { message = $"Crawl not found: {crawlId}" });
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("update-translations")]
    [ProducesResponseType(typeof(UpdateTranslationsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UpdateTranslationsResult>> UpdateTranslations(
        [FromBody] UpdateTranslationsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await mirrorService.UpdateTranslationsAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("update-translations/upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UpdateTranslationsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UpdateTranslationsResult>> UploadTranslations(
        [FromForm] UploadTranslationsForm form,
        CancellationToken cancellationToken)
    {
        if (form.File is null || form.File.Length == 0)
        {
            return BadRequest(new { message = "Translation file is required." });
        }

        try
        {
            Dictionary<string, string> entries;
            await using (var stream = form.File.OpenReadStream())
            {
                entries = await ParseTranslationEntriesAsync(stream, cancellationToken);
            }

            var request = new UpdateTranslationsRequest
            {
                SiteHost = form.SiteHost,
                Version = form.Version,
                Language = form.Language,
                Entries = entries,
                DoNotTranslateTexts = form.DoNotTranslateTexts,
                TargetPages = form.TargetPages
            };

            var result = await mirrorService.UpdateTranslationsAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (JsonException)
        {
            return BadRequest(new
            {
                message = "Invalid translation JSON. Expected either {\"entries\": {...}} or a flat {\"k_xxx\": \"...\"} object."
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("fix-page-links")]
    [ProducesResponseType(typeof(FixPageLinksResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FixPageLinksResult>> FixPageLinks(
        [FromBody] FixPageLinksRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await mirrorService.FixPageLinksAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("sites")]
    [ProducesResponseType(typeof(IReadOnlyList<CrawledSitesResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CrawledSitesResult>>> GetCrawledSites(CancellationToken cancellationToken)
    {
        var runs = await crawlReadDbContext.CrawlRuns
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var pages = await crawlReadDbContext.CrawlPages
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var grouped = runs
            .GroupBy(run => new { run.SiteHost, run.Version })
            .Select(group =>
            {
                var groupPages = pages
                    .Where(page => string.Equals(page.SiteHost, group.Key.SiteHost, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(page.Version, group.Key.Version, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(page => page.EntryFileRelativePath)
                    .Select(pageGroup => pageGroup
                        .OrderByDescending(p => p.CreatedAtUtc)
                        .First())
                    .OrderBy(p => p.EntryFileRelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new CrawledSitePageItem
                    {
                        EntryFileRelativePath = p.EntryFileRelativePath,
                        FrontendPreviewPath = p.FrontendPreviewPath
                    })
                    .ToList();

                var crawlRuns = group
                    .Select(run => new CrawledSiteRunItem
                    {
                        CrawlId = run.CrawlId,
                        Status = run.Status,
                        CreatedAtUtc = run.CreatedAtUtc,
                        ProcessedPages = run.ProcessedPages,
                        TotalFilesSaved = run.TotalFilesSaved
                    })
                    .ToList();

                return new CrawledSitesResult
                {
                    SiteHost = group.Key.SiteHost,
                    Version = group.Key.Version,
                    CrawlRuns = crawlRuns,
                    Pages = groupPages
                };
            })
            .OrderBy(item => item.SiteHost, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(grouped);
    }

    [HttpPost("injections")]
    [ProducesResponseType(typeof(CreateInjectionAssetResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateInjectionAssetResult>> CreateInjectionAsset(
        [FromBody] CreateInjectionAssetRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await mirrorService.CreateInjectionAssetAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("update-block-translations")]
    [ProducesResponseType(typeof(UpdateBlockTranslationsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UpdateBlockTranslationsResult>> UpdateBlockTranslations(
        [FromBody] UpdateBlockTranslationsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await mirrorService.UpdateBlockTranslationsAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("update-block-translations/upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UpdateBlockTranslationsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UpdateBlockTranslationsResult>> UploadBlockTranslations(
        [FromForm] UploadBlockTranslationsForm form,
        CancellationToken cancellationToken)
    {
        if (form.File is null || form.File.Length == 0)
        {
            return BadRequest(new { message = "Translation file is required." });
        }

        try
        {
            Dictionary<string, string> entries;
            await using (var stream = form.File.OpenReadStream())
            {
                entries = await ParseBlockTranslationEntriesAsync(stream, cancellationToken);
            }

            var request = new UpdateBlockTranslationsRequest
            {
                SiteHost = form.SiteHost,
                Version = form.Version,
                Language = form.Language,
                PagePath = form.PagePath,
                Entries = entries
            };

            var result = await mirrorService.UpdateBlockTranslationsAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (JsonException)
        {
            return BadRequest(new
            {
                message = "Invalid block translation JSON. Expected {\"blocks\":[{\"id\":\"b_x\",\"translated\":\"...\"}]} or a flat {\"b_x\":\"...\"} object."
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<Dictionary<string, string>> ParseTranslationEntriesAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Root JSON must be an object.");
        }

        JsonElement entriesNode;
        if (doc.RootElement.TryGetProperty("entries", out var entriesCamel) &&
            entriesCamel.ValueKind == JsonValueKind.Object)
        {
            entriesNode = entriesCamel;
        }
        else if (doc.RootElement.TryGetProperty("Entries", out var entriesPascal) &&
                 entriesPascal.ValueKind == JsonValueKind.Object)
        {
            entriesNode = entriesPascal;
        }
        else
        {
            // Fallback: treat full object as a flat entries map.
            entriesNode = doc.RootElement;
        }

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in entriesNode.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var key = property.Name?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            entries[key] = property.Value.GetString() ?? string.Empty;
        }

        return entries;
    }

    private static async Task<Dictionary<string, string>> ParseBlockTranslationEntriesAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Root JSON must be an object.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if ((doc.RootElement.TryGetProperty("blocks", out var blocks) ||
             doc.RootElement.TryGetProperty("Blocks", out blocks)) &&
            blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in blocks.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!(item.TryGetProperty("id", out var idNode) || item.TryGetProperty("Id", out idNode)) ||
                    idNode.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idNode.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var translated = (item.TryGetProperty("translated", out var translatedNode) ||
                                  item.TryGetProperty("Translated", out translatedNode)) &&
                                 translatedNode.ValueKind == JsonValueKind.String
                    ? translatedNode.GetString() ?? string.Empty
                    : string.Empty;
                result[id] = translated;
            }

            return result;
        }

        if ((doc.RootElement.TryGetProperty("groups", out var groups) ||
             doc.RootElement.TryGetProperty("Groups", out groups)) &&
            groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!(group.TryGetProperty("blocks", out var groupBlocks) ||
                      group.TryGetProperty("Blocks", out groupBlocks)) ||
                    groupBlocks.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in groupBlocks.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!(item.TryGetProperty("id", out var idNode) || item.TryGetProperty("Id", out idNode)) ||
                        idNode.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var id = idNode.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var translated = (item.TryGetProperty("translated", out var translatedNode) ||
                                      item.TryGetProperty("Translated", out translatedNode)) &&
                                     translatedNode.ValueKind == JsonValueKind.String
                        ? translatedNode.GetString() ?? string.Empty
                        : string.Empty;
                    result[id] = translated;
                }
            }

            return result;
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var key = property.Name?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[key] = property.Value.GetString() ?? string.Empty;
        }

        return result;
    }
}
