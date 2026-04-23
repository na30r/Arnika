using System.ComponentModel.DataAnnotations;

namespace SiteMirror.Api.Models;

public sealed class RegisterRequest
{
    [Required, MinLength(2), MaxLength(64)]
    public string UserName { get; init; } = string.Empty;

    [MaxLength(32)]
    public string? PhoneNumber { get; init; }

    [Required, MinLength(6), MaxLength(128)]
    public string Password { get; init; } = string.Empty;
}

public sealed class LoginRequest
{
    [Required]
    public string UserName { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;
}

public sealed class AuthResponse
{
    public required string Token { get; init; }

    public required Guid UserId { get; init; }

    public required string UserName { get; init; }

    public string? PhoneNumber { get; init; }

    public DateTimeOffset? SubscriptionEndDateUtc { get; init; }
}

public sealed class UserProfileResponse
{
    public required Guid UserId { get; init; }

    public required string UserName { get; init; }

    public string? PhoneNumber { get; init; }

    public DateTimeOffset? SubscriptionEndDateUtc { get; init; }
}

public sealed class MirrorHistoryItem
{
    public required string CrawlId { get; init; }

    public required string SourceUrl { get; init; }

    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public required string Status { get; init; }

    public int ProcessedPages { get; init; }

    public int TotalFilesSaved { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}
