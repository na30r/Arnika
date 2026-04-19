using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public interface ICrawlerService
{
    Task<CrawlResult> CrawlAsync(string url, CancellationToken cancellationToken);
}
