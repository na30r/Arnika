using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public interface IAssetRepository
{
    Task<AssetEntity?> GetByOriginalUrlAsync(string originalUrl, CancellationToken cancellationToken);
    Task<long> UpsertAsync(string originalUrl, string localPath, long pageId, CancellationToken cancellationToken);
}
