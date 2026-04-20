using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public interface IPageRepository
{
    Task<PageEntity?> GetByOriginalUrlAsync(string originalUrl, CancellationToken cancellationToken);
    Task<long> UpsertAsync(string originalUrl, string localPath, string status, CancellationToken cancellationToken);
}
