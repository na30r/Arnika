namespace WebMirror.Api.Services;

public interface IStorageService
{
    Task<string> SavePageHtmlAsync(string localRoute, string html, CancellationToken cancellationToken);
    Task<string> SaveAssetAsync(Uri sourceAssetUrl, string contentType, Stream content, CancellationToken cancellationToken);
}
