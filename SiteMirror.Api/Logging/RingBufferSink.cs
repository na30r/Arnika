using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace SiteMirror.Api.Logging;

public sealed class RingBufferSink : ILogEventSink
{
    private readonly InMemoryLogBuffer _buffer;
    private readonly IFormatProvider? _formatProvider;

    public RingBufferSink(InMemoryLogBuffer buffer, IFormatProvider? formatProvider = null)
    {
        _buffer = buffer;
        _formatProvider = formatProvider;
    }

    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage(_formatProvider);
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in logEvent.Properties)
            props[p.Key] = p.Value.ToString().Trim('"');

        _buffer.Add(new LogLineDto(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            message,
            logEvent.Exception?.ToString(),
            props));
    }
}
