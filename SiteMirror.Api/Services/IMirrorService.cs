using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public interface ISiteMirrorService
{
    Task<MirrorResult> MirrorAsync(MirrorRequest request, CancellationToken cancellationToken = default);
}
