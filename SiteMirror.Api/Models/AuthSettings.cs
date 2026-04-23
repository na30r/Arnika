namespace SiteMirror.Api.Models;

public sealed class AuthSettings
{
    public const string SectionName = "Auth";

    public string JwtSecret { get; init; } = "ChangeThisInProduction_UseLongRandomString_AtLeast32Chars!!";

    public string Issuer { get; init; } = "SiteMirror.Api";

    public string Audience { get; init; } = "SiteMirror.Clients";

    public int AccessTokenMinutes { get; init; } = 10080; // 7 days
}
