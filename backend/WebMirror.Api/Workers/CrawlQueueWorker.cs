using Microsoft.Extensions.Options;
using WebMirror.Api.Options;
using WebMirror.Api.Services;

namespace WebMirror.Api.Workers;

public sealed class CrawlQueueWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CrawlQueueWorker> _logger;
    private readonly MirrorOptions _options;

    public CrawlQueueWorker(
        IServiceProvider serviceProvider,
        IOptions<MirrorOptions> options,
        ILogger<CrawlQueueWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Crawl worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var queueRepository = scope.ServiceProvider.GetRequiredService<ICrawlQueueRepository>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ICrawlOrchestrator>();

                var nextItem = await queueRepository.TryDequeuePendingAsync(stoppingToken);
                if (nextItem is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                try
                {
                    await orchestrator.ProcessQueueItemAsync(nextItem, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Crawl job failed for {Url}", nextItem.Url);
                    if (nextItem.RetryCount + 1 < _options.MaxRetries)
                    {
                        await queueRepository.IncrementRetryAsync(nextItem.Id, ex.Message, stoppingToken);
                    }
                    else
                    {
                        await queueRepository.MarkFailedAsync(nextItem.Id, ex.Message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected crawl worker error");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }
}
