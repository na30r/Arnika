using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services;

namespace SiteMirror.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly JwtTokenService _jwt;
    private readonly IOptions<AuthSettings> _authSettings;
    private readonly ICrawlRepository _crawls;

    public AuthController(
        IUserRepository users,
        JwtTokenService jwt,
        IOptions<AuthSettings> authSettings,
        ICrawlRepository crawls)
    {
        _users = users;
        _jwt = jwt;
        _authSettings = authSettings;
        _crawls = crawls;
    }

    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Username and password are required." });
        }

        await _users.EnsureUserSchemaAsync(cancellationToken);
        var id = Guid.NewGuid();
        var subEnd = DateTimeOffset.UtcNow.AddDays(30);
        var hash = PasswordHashing.Hash(request.Password, _authSettings.Value.JwtSecret);
        try
        {
            await _users.CreateUserAsync(
                id,
                request.UserName,
                request.PhoneNumber,
                hash,
                subEnd,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }

        var user = await _users.GetByIdAsync(id, cancellationToken);
        if (user is null)
        {
            return StatusCode(500, new { message = "User was not persisted." });
        }

        return Ok(MapAuthResponse(user, _jwt.CreateToken(user.UserId, user.UserName)));
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (TryDevBypassLogin(request, out var dev))
        {
            return Ok(dev);
        }

        await _users.EnsureUserSchemaAsync(cancellationToken);
        var user = await _users.GetByUserNameAsync(request.UserName, cancellationToken);
        if (user is null)
        {
            return Unauthorized(new { message = "Invalid username or password." });
        }

        if (!PasswordHashing.Verify(request.Password, user.PasswordHash, _authSettings.Value.JwtSecret))
        {
            return Unauthorized(new { message = "Invalid username or password." });
        }

        if (user.SubscriptionEndDateUtc.HasValue && user.SubscriptionEndDateUtc.Value < DateTimeOffset.UtcNow)
        {
            return StatusCode(402, new { message = "Subscription has expired. Please renew to continue." });
        }

        return Ok(MapAuthResponse(user, _jwt.CreateToken(user.UserId, user.UserName)));
    }

    [HttpGet("profile")]
    [Authorize]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var id))
        {
            return Unauthorized();
        }

        if (DevAuthHelper.IsDevUserId(_authSettings, id))
        {
            var a = _authSettings.Value;
            return Ok(new UserProfileResponse
            {
                UserId = id,
                UserName = a.DevBypassUserName,
                PhoneNumber = null,
                SubscriptionEndDateUtc = DateTimeOffset.UtcNow.AddYears(10)
            });
        }

        var user = await _users.GetByIdAsync(id, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        return Ok(new UserProfileResponse
        {
            UserId = user.UserId,
            UserName = user.UserName,
            PhoneNumber = user.PhoneNumber,
            SubscriptionEndDateUtc = user.SubscriptionEndDateUtc
        });
    }

    [HttpGet("mirror-history")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<MirrorHistoryItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMirrorHistory([FromQuery] int take = 50, CancellationToken cancellationToken = default)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var id))
        {
            return Unauthorized();
        }

        if (DevAuthHelper.IsDevUserId(_authSettings, id))
        {
            return Ok(Array.Empty<MirrorHistoryItem>());
        }

        var list = await _crawls.GetMirrorHistoryForUserAsync(id, take, cancellationToken);
        return Ok(list);
    }

    private static AuthResponse MapAuthResponse(UserRecord user, string token) =>
        new()
        {
            Token = token,
            UserId = user.UserId,
            UserName = user.UserName,
            PhoneNumber = user.PhoneNumber,
            SubscriptionEndDateUtc = user.SubscriptionEndDateUtc
        };

    private bool TryDevBypassLogin(LoginRequest request, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AuthResponse? response)
    {
        response = null;
        var a = _authSettings.Value;
        if (!a.DevBypassEnabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return false;
        }

        if (!string.Equals(request.UserName.Trim(), a.DevBypassUserName, StringComparison.Ordinal)
            || !string.Equals(request.Password, a.DevBypassPassword, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParse(a.DevBypassUserId, out var id))
        {
            return false;
        }

        response = new AuthResponse
        {
            Token = _jwt.CreateToken(id, a.DevBypassUserName),
            UserId = id,
            UserName = a.DevBypassUserName,
            PhoneNumber = null,
            SubscriptionEndDateUtc = DateTimeOffset.UtcNow.AddYears(10)
        };
        return true;
    }
}
