namespace WebMirror.Api.Services;

public interface IRateLimiterService
{
    Task WaitTurnAsync(CancellationToken cancellationToken);
}
