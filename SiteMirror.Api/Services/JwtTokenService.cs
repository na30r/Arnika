using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public sealed class JwtTokenService
{
    private readonly AuthSettings _settings;

    public JwtTokenService(IOptions<AuthSettings> options)
    {
        _settings = options.Value;
    }

    public string CreateToken(Guid userId, string userName)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(NormalizeKey(_settings.JwtSecret)));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, userName),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        };
        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_settings.AccessTokenMinutes),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string NormalizeKey(string secret)
    {
        if (secret.Length >= 32)
        {
            return secret;
        }

        return secret.PadRight(32, 'x');
    }
}
