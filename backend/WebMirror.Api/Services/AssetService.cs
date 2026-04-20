using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public sealed class AssetService(
    IHttpClientFactory httpClientFactory,
    IStorageService storageService,
    IAssetRepository assetRepository,
    ILogger<AssetService> logger) : IAssetService
{
    public async Task<IReadOnlyCollection<DownloadedAsset>> DownloadAndStoreAsync(
        IReadOnlyCollection<AssetReference> assets,
        CancellationToken cancellationToken)
    {
        var downloaded = new List<DownloadedAsset>();
        if (assets.Count == 0)
        {
            return downloaded;
        }

        var httpClient = httpClientFactory.CreateClient();

        foreach (var asset in assets)
        {
            if (!Uri.TryCreate(asset.OriginalUrl, UriKind.Absolute, out var assetUri))
            {
                continue;
            }

            var existing = await assetRepository.GetByOriginalUrlAsync(asset.OriginalUrl, cancellationToken);
            if (existing is not null)
            {
                downloaded.Add(new DownloadedAsset(existing.OriginalUrl, existing.LocalPath));
                continue;
            }

            try
            {
                using var response = await httpClient.GetAsync(assetUri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Asset download failed for {AssetUrl} with status {StatusCode}", assetUri, response.StatusCode);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                var localPath = await storageService.SaveAssetAsync(assetUri, contentType, stream, cancellationToken);
                downloaded.Add(new DownloadedAsset(asset.OriginalUrl, localPath));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to download asset {AssetUrl}", assetUri);
            }
        }

        return downloaded
            .GroupBy(x => x.OriginalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }
}
