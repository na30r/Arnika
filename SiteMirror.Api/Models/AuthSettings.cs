namespace SiteMirror.Api.Models;

public sealed class AuthSettings
{
    public const string SectionName = "Auth";

    public string JwtSecret { get; init; } = "ChangeThisInProduction_UseLongRandomString_AtLeast32Chars!!";

    public string Issuer { get; init; } = "SiteMirror.Api";

    public string Audience { get; init; } = "SiteMirror.Clients";

    public int AccessTokenMinutes { get; init; } = 10080; // 7 days

    /// <summary>When true, <see cref="DevBypassUserName"/> + <see cref="DevBypassPassword"/> log in without DB (local dev only).</summary>
    public bool DevBypassEnabled { get; init; }

    public string DevBypassUserName { get; init; } = "dev";

    public string DevBypassPassword { get; init; } = "dev";

    /// <summary>Fixed user id for the dev user (valid GUID string).</summary>
    public string DevBypassUserId { get; init; } = "00000000-0000-0000-0000-00000000DE11";
}
