using Microsoft.Extensions.Options;
using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public static class DevAuthHelper
{
    public static bool IsDevUserId(IOptions<AuthSettings> auth, Guid userId)
    {
        var s = auth.Value;
        if (!s.DevBypassEnabled)
        {
            return false;
        }

        return Guid.TryParse(s.DevBypassUserId, out var id) && id == userId;
    }
}
