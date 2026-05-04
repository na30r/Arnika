using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog.Events;
using SiteMirror.Api.Logging;

namespace SiteMirror.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class LogsController : ControllerBase
{
    private readonly InMemoryLogBuffer _buffer;

    public LogsController(InMemoryLogBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// Recent server log lines (newest first). Requires authentication.
    /// </summary>
    [HttpGet]
    [Authorize]
    public ActionResult<IReadOnlyList<LogLineResponse>> Get([FromQuery] string? level, [FromQuery] int take = 500)
    {
        take = Math.Clamp(take, 1, 5000);
        var lines = _buffer.Snapshot();

        IEnumerable<LogLineDto> filtered = lines;
        if (!string.IsNullOrWhiteSpace(level))
        {
            var want = level.Trim();
            if (Enum.TryParse<LogEventLevel>(want, ignoreCase: true, out var parsed))
                want = parsed.ToString();
            filtered = filtered.Where(l =>
                string.Equals(l.Level, want, StringComparison.OrdinalIgnoreCase));
        }

        var result = filtered
            .Reverse()
            .Take(take)
            .Select(l => new LogLineResponse(
                l.Timestamp.ToUniversalTime().ToString("o"),
                l.Level,
                l.Message,
                l.Exception,
                l.Properties))
            .ToList();

        return Ok(result);
    }
}

public sealed record LogLineResponse(
    string TimestampIso,
    string Level,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, string> Properties);
