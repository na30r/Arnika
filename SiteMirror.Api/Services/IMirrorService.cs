using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public interface ISiteMirrorService
{
    Task<MirrorResult> MirrorAsync(MirrorRequest request, Guid? actingUserId, CancellationToken cancellationToken = default);

    Task<RewriteLinksResult> RewriteLinksAsync(RewriteLinksRequest request, CancellationToken cancellationToken = default);

    Task<CrawlStatusResult?> GetCrawlStatusAsync(string crawlId, CancellationToken cancellationToken = default);
}
