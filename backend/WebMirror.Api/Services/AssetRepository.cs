using System.Data;
using Microsoft.Data.SqlClient;
using WebMirror.Api.Data;
using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public sealed class AssetRepository(IDbConnectionFactory connectionFactory) : IAssetRepository
{
    public async Task<AssetEntity?> GetByOriginalUrlAsync(string originalUrl, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT TOP (1) Id, OriginalUrl, LocalPath, PageId, CreatedAt
            FROM dbo.Assets
            WHERE OriginalUrl = @OriginalUrl;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@OriginalUrl", SqlDbType.NVarChar, 2048) { Value = originalUrl });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AssetEntity
        {
            Id = reader.GetInt64(0),
            OriginalUrl = reader.GetString(1),
            LocalPath = reader.GetString(2),
            PageId = reader.GetInt64(3),
            CreatedAt = reader.GetDateTimeOffset(4)
        };
    }

    public async Task<long> UpsertAsync(string originalUrl, string localPath, long pageId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        const string sql = """
            MERGE dbo.Assets AS target
            USING (SELECT @OriginalUrl AS OriginalUrl) AS source
            ON target.OriginalUrl = source.OriginalUrl
            WHEN MATCHED THEN
                UPDATE SET LocalPath = @LocalPath, PageId = @PageId
            WHEN NOT MATCHED THEN
                INSERT (OriginalUrl, LocalPath, PageId, CreatedAt)
                VALUES (@OriginalUrl, @LocalPath, @PageId, SYSUTCDATETIME())
            OUTPUT inserted.Id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@OriginalUrl", SqlDbType.NVarChar, 2048) { Value = originalUrl });
        command.Parameters.Add(new SqlParameter("@LocalPath", SqlDbType.NVarChar, 2048) { Value = localPath });
        command.Parameters.Add(new SqlParameter("@PageId", SqlDbType.BigInt) { Value = pageId });

        var id = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(id);
    }
}
