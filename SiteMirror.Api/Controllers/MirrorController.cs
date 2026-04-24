using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services;

namespace SiteMirror.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class MirrorController(
    ISiteMirrorService mirrorService,
    IUserRepository userRepository) : ControllerBase
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
}
