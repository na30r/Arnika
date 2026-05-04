using System.Text.Json;
using Microsoft.Extensions.Options;
using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public sealed class MirrorQueueBackgroundService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ICrawlRepository _crawlRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MirrorSettings _settings;
    private readonly ILogger<MirrorQueueBackgroundService> _logger;

    public MirrorQueueBackgroundService(
        ICrawlRepository crawlRepository,
        IServiceScopeFactory scopeFactory,
        IOptions<MirrorSettings> mirrorOptions,
        ILogger<MirrorQueueBackgroundService> logger)
    {
        _crawlRepository = crawlRepository;
        _scopeFactory = scopeFactory;
        _settings = mirrorOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One worker: MirrorService.MirrorAsync is gated (MirrorGlobalExecutionGate). Extra workers would only spin.
        if (_settings.MirrorQueueMaxConcurrent > 1)
        {
            _logger.LogInformation(
                "MirrorQueueMaxConcurrent={N} ignored; using one queue worker (mirrors run one-at-a-time for safe file IO).",
                _settings.MirrorQueueMaxConcurrent);
        }

        await RunWorkerAsync(0, stoppingToken);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await _crawlRepository.TryClaimMirrorQueueItemAsync(stoppingToken);
                if (claimed is null)
                {
                    await Task.Delay(400, stoppingToken);
                    continue;
                }

                _logger.LogInformation(
                    "Mirror queue worker {WorkerId} claimed item {ItemId} batch {BatchId} url {Url}",
                    workerId, claimed.ItemId, claimed.BatchId, claimed.Url);

                await ProcessClaimedAsync(claimed, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mirror queue worker {WorkerId} loop error", workerId);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task ProcessClaimedAsync(MirrorQueueClaimedItem claimed, CancellationToken stoppingToken)
    {
        MirrorQueueTemplate? template;
        try
        {
            template = JsonSerializer.Deserialize<MirrorQueueTemplate>(claimed.OptionsJson, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid OptionsJson for queue item {ItemId}", claimed.ItemId);
            await _crawlRepository.CompleteMirrorQueueItemAsync(
                claimed.ItemId, "failed", null, null, "Invalid stored mirror options JSON.", CancellationToken.None);
            return;
        }

        if (template is null)
        {
            await _crawlRepository.CompleteMirrorQueueItemAsync(
                claimed.ItemId, "failed", null, null, "Missing mirror options.", CancellationToken.None);
            return;
        }

        var request = template.ToRequest(claimed.Url);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var mirrorService = scope.ServiceProvider.GetRequiredService<ISiteMirrorService>();
            var result = await mirrorService.MirrorAsync(request, claimed.UserId, stoppingToken);
            await _crawlRepository.CompleteMirrorQueueItemAsync(
                claimed.ItemId, "completed", result.CrawlId, result, null, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await _crawlRepository.CompleteMirrorQueueItemAsync(
                claimed.ItemId, "failed", null, null, "Canceled.", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mirror queue item {ItemId} failed", claimed.ItemId);
            await _crawlRepository.CompleteMirrorQueueItemAsync(
                claimed.ItemId, "failed", null, null, ex.Message, CancellationToken.None);
        }
    }
}
