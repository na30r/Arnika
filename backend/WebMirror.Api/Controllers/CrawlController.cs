using Microsoft.AspNetCore.Mvc;
using WebMirror.Api.Models;
using WebMirror.Api.Services;

namespace WebMirror.Api.Controllers;

[ApiController]
[Route("crawl")]
public sealed class CrawlController(ICrawlOrchestrator orchestrator) : ControllerBase
{

    [HttpPost]
    public async Task<ActionResult<CrawlResponse>> EnqueueAsync([FromBody] CrawlRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest("url is required.");
        }

        try
        {
            var queueId = await orchestrator.EnqueueAsync(request, cancellationToken);
            var status = await orchestrator.GetStatusAsync(queueId, cancellationToken);
            if (status is null)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Failed to read queued crawl status.");
            }

            return Ok(new CrawlResponse(status.QueueId, status.Url, status.Status));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<CrawlStatusResponse>> GetStatusAsync(long id, CancellationToken cancellationToken)
    {
        var status = await orchestrator.GetStatusAsync(id, cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }
}
