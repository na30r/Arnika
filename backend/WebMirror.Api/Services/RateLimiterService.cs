using Microsoft.Extensions.Options;
using WebMirror.Api.Options;

namespace WebMirror.Api.Services;

public sealed class RateLimiterService(IOptions<MirrorOptions> options) : IRateLimiterService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastExecution = DateTimeOffset.MinValue;
    private readonly TimeSpan _minimumDelay = options.Value.RequestsPerMinute <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromMinutes(1d / options.Value.RequestsPerMinute);

    public async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        if (_minimumDelay == TimeSpan.Zero)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _lastExecution;
            if (elapsed < _minimumDelay)
            {
                await Task.Delay(_minimumDelay - elapsed, cancellationToken);
            }

            _lastExecution = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
