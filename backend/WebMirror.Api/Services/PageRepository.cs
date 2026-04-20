using System.Data;
using Microsoft.Data.SqlClient;
using WebMirror.Api.Data;
using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public sealed class PageRepository(IDbConnectionFactory connectionFactory) : IPageRepository
{
    public async Task<PageEntity?> GetByOriginalUrlAsync(string originalUrl, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT TOP (1) Id, OriginalUrl, LocalPath, Status, CreatedAt, UpdatedAt
            FROM dbo.Pages
            WHERE OriginalUrl = @OriginalUrl;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@OriginalUrl", SqlDbType.NVarChar, 2048) { Value = originalUrl });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PageEntity
        {
            Id = reader.GetInt64(0),
            OriginalUrl = reader.GetString(1),
            LocalPath = reader.GetString(2),
            Status = reader.GetString(3),
            CreatedAt = reader.GetDateTimeOffset(4),
            UpdatedAt = reader.GetDateTimeOffset(5)
        };
    }

    public async Task<long> UpsertAsync(string originalUrl, string localPath, string status, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        const string sql = """
            MERGE dbo.Pages AS target
            USING (SELECT @OriginalUrl AS OriginalUrl) AS source
            ON target.OriginalUrl = source.OriginalUrl
            WHEN MATCHED THEN
                UPDATE SET LocalPath = @LocalPath, Status = @Status, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (OriginalUrl, LocalPath, Status, CreatedAt, UpdatedAt)
                VALUES (@OriginalUrl, @LocalPath, @Status, SYSUTCDATETIME(), SYSUTCDATETIME())
            OUTPUT inserted.Id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@OriginalUrl", SqlDbType.NVarChar, 2048) { Value = originalUrl });
        command.Parameters.Add(new SqlParameter("@LocalPath", SqlDbType.NVarChar, 2048) { Value = localPath });
        command.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 32) { Value = status });

        var id = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(id);
    }
}
