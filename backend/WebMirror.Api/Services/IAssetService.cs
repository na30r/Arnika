using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public interface IAssetService
{
    Task<IReadOnlyCollection<DownloadedAsset>> DownloadAndStoreAsync(
        IReadOnlyCollection<AssetReference> assets,
        CancellationToken cancellationToken);
}
