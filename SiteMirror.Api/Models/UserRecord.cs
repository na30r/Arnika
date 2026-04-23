namespace SiteMirror.Api.Models;

public sealed class UserRecord
{
    public required Guid UserId { get; init; }

    public required string UserName { get; init; }

    public string? PhoneNumber { get; init; }

    public required string PasswordHash { get; init; }

    public DateTimeOffset? SubscriptionEndDateUtc { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }
}
