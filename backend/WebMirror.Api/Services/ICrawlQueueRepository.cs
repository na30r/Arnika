using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public interface ICrawlQueueRepository
{
    Task<long> EnqueueAsync(string url, int depth, int maxDepth, CancellationToken cancellationToken);
    Task<CrawlQueueEntity?> GetByIdAsync(long id, CancellationToken cancellationToken);
    Task<CrawlQueueEntity?> TryDequeuePendingAsync(CancellationToken cancellationToken);
    Task MarkInProgressAsync(long id, CancellationToken cancellationToken);
    Task MarkDoneAsync(long id, CancellationToken cancellationToken);
    Task MarkFailedAsync(long id, string errorMessage, CancellationToken cancellationToken);
    Task IncrementRetryAsync(long id, string errorMessage, CancellationToken cancellationToken);
}
