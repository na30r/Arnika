using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public interface ICrawlOrchestrator
{
    Task<long> EnqueueAsync(CrawlRequest request, CancellationToken cancellationToken);
    Task<CrawlStatusResponse?> GetStatusAsync(long queueId, CancellationToken cancellationToken);
    Task ProcessQueueItemAsync(CrawlQueueEntity queueItem, CancellationToken cancellationToken);
}
