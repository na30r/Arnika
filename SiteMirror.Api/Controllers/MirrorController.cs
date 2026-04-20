using Microsoft.AspNetCore.Mvc;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services;

namespace SiteMirror.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class MirrorController(ISiteMirrorService mirrorService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(MirrorResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MirrorResult>> StartMirror([FromBody] MirrorRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await mirrorService.MirrorAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
