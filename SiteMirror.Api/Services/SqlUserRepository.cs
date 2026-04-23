using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public sealed class SqlUserRepository : IUserRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqlUserRepository> _logger;

    public SqlUserRepository(IOptions<DatabaseSettings> options, ILogger<SqlUserRepository> logger)
    {
        _connectionString = options.Value.ConnectionString ?? string.Empty;
        _logger = logger;
    }

    public async Task EnsureUserSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users')
            BEGIN
                CREATE TABLE dbo.Users
                (
                    UserId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                    UserName NVARCHAR(64) NOT NULL,
                    PhoneNumber NVARCHAR(32) NULL,
                    PasswordHash NVARCHAR(512) NOT NULL,
                    SubscriptionEndDateUtc DATETIMEOFFSET NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL
                );
                CREATE UNIQUE INDEX IX_Users_UserName ON dbo.Users (UserName);
            END
            """;
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<UserRecord?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        await EnsureUserSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string q = """
            SELECT UserId, UserName, PhoneNumber, PasswordHash, SubscriptionEndDateUtc, CreatedAtUtc
            FROM dbo.Users
            WHERE UserName = @UserName;
            """;
        await using var cmd = new SqlCommand(q, connection);
        cmd.Parameters.AddWithValue("@UserName", userName.Trim());
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadUser(r);
    }

    public async Task<UserRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || userId == Guid.Empty)
        {
            return null;
        }

        await EnsureUserSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string q = """
            SELECT UserId, UserName, PhoneNumber, PasswordHash, SubscriptionEndDateUtc, CreatedAtUtc
            FROM dbo.Users
            WHERE UserId = @UserId;
            """;
        await using var cmd = new SqlCommand(q, connection);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadUser(r);
    }

    public async Task CreateUserAsync(
        Guid userId,
        string userName,
        string? phoneNumber,
        string passwordHash,
        DateTimeOffset? subscriptionEndDateUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("Database is not configured.");
        }

        await EnsureUserSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string q = """
            INSERT INTO dbo.Users (UserId, UserName, PhoneNumber, PasswordHash, SubscriptionEndDateUtc, CreatedAtUtc)
            VALUES (@UserId, @UserName, @PhoneNumber, @PasswordHash, @SubEnd, SYSUTCDATETIME());
            """;
        await using var cmd = new SqlCommand(q, connection);
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@UserName", userName.Trim());
        cmd.Parameters.AddWithValue("@PhoneNumber", (object?)phoneNumber?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PasswordHash", passwordHash);
        cmd.Parameters.AddWithValue("@SubEnd", (object?)subscriptionEndDateUtc ?? DBNull.Value);
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            throw new InvalidOperationException("Username is already taken.");
        }
    }

    private static UserRecord ReadUser(IDataRecord r)
    {
        var subOrdinal = r.GetOrdinal("SubscriptionEndDateUtc");
        return new UserRecord
        {
            UserId = r.GetGuid(r.GetOrdinal("UserId")),
            UserName = r.GetString(r.GetOrdinal("UserName")),
            PhoneNumber = r.IsDBNull(r.GetOrdinal("PhoneNumber")) ? null : r.GetString(r.GetOrdinal("PhoneNumber")),
            PasswordHash = r.GetString(r.GetOrdinal("PasswordHash")),
            SubscriptionEndDateUtc = r.IsDBNull(subOrdinal) ? null : r.GetFieldValue<DateTimeOffset>(subOrdinal),
            CreatedAtUtc = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("CreatedAtUtc"))
        };
    }
}

public static class PasswordHashing
{
    public static string Hash(string password, string? pepper = null)
    {
        var p = (pepper ?? string.Empty) + password;
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(p), salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string stored, string? pepper = null)
    {
        if (string.IsNullOrEmpty(stored) || !stored.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = stored.Split('.', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var p = (pepper ?? string.Empty) + password;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(p), salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
