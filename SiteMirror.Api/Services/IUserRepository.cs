using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public interface IUserRepository
{
    Task EnsureUserSchemaAsync(CancellationToken cancellationToken = default);

    Task<UserRecord?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default);

    Task<UserRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task CreateUserAsync(
        Guid userId,
        string userName,
        string? phoneNumber,
        string passwordHash,
        DateTimeOffset? subscriptionEndDateUtc,
        CancellationToken cancellationToken = default);
}
