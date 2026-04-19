using System.Data;
using Microsoft.Data.SqlClient;
using WebMirror.Api.Models;

namespace WebMirror.Api.Services;

public sealed class CrawlQueueRepository(IDbConnectionFactory connectionFactory) : ICrawlQueueRepository
{
    public async Task<long> EnqueueAsync(string url, int depth, int maxDepth, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        const string sql = """
            MERGE dbo.CrawlQueue AS target
            USING (SELECT @Url AS Url) AS source
            ON target.Url = source.Url
            WHEN MATCHED THEN
                UPDATE SET
                    target.Status = CASE WHEN target.Status = @Done THEN @Pending ELSE target.Status END,
                    target.UpdatedAt = SYSUTCDATETIME(),
                    target.MaxDepth = CASE WHEN @MaxDepth > target.MaxDepth THEN @MaxDepth ELSE target.MaxDepth END
            WHEN NOT MATCHED THEN
                INSERT (Url, Status, Depth, MaxDepth, RetryCount, ErrorMessage, CreatedAt, UpdatedAt)
                VALUES (@Url, @Pending, @Depth, @MaxDepth, 0, NULL, SYSUTCDATETIME(), SYSUTCDATETIME())
            OUTPUT inserted.Id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@Url", SqlDbType.NVarChar, 2048) { Value = url });
        command.Parameters.Add(new SqlParameter("@Depth", SqlDbType.Int) { Value = depth });
        command.Parameters.Add(new SqlParameter("@MaxDepth", SqlDbType.Int) { Value = maxDepth });
        command.Parameters.Add(new SqlParameter("@Pending", SqlDbType.NVarChar, 32) { Value = CrawlQueueStatus.Pending });
        command.Parameters.Add(new SqlParameter("@Done", SqlDbType.NVarChar, 32) { Value = CrawlQueueStatus.Done });

        var id = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(id);
    }

    public async Task<CrawlQueueEntity?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT Id, Url, Status, Depth, MaxDepth, RetryCount, ErrorMessage, CreatedAt, UpdatedAt
            FROM dbo.CrawlQueue
            WHERE Id = @Id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = id });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapQueue(reader);
    }

    public async Task<CrawlQueueEntity?> TryDequeuePendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        const string sql = """
            ;WITH nextItem AS (
                SELECT TOP (1) *
                FROM dbo.CrawlQueue WITH (ROWLOCK, READPAST)
                WHERE Status = @Pending
                ORDER BY CreatedAt ASC
            )
            UPDATE nextItem
            SET Status = @InProgress,
                UpdatedAt = SYSUTCDATETIME()
            OUTPUT inserted.Id, inserted.Url, inserted.Status, inserted.Depth, inserted.MaxDepth, inserted.RetryCount, inserted.ErrorMessage, inserted.CreatedAt, inserted.UpdatedAt;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@Pending", SqlDbType.NVarChar, 32) { Value = CrawlQueueStatus.Pending });
        command.Parameters.Add(new SqlParameter("@InProgress", SqlDbType.NVarChar, 32) { Value = CrawlQueueStatus.InProgress });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapQueue(reader);
    }

    public async Task MarkInProgressAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE dbo.CrawlQueue
            SET Status = @InProgress,
                UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @Id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@InProgress", SqlDbType.NVarChar, 32) { Value = CrawlQueueStatus.InProgress });
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkDoneAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE dbo.CrawlQueue
            SET Status = @Done,
                ErrorMessage = NULL,
                UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @Id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@Done", SqlDbType.NVarChar, 32) { Value = CrawlQueueStatus.Done });
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE dbo.CrawlQueue
            SET Status = @Failed,
                ErrorMessage = @Error,
                UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @Id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@Failed", SqlDbType.NVarChar, 32) { Value = CrawlQueueStatus.Failed });
        command.Parameters.Add(new SqlParameter("@Error", SqlDbType.NVarChar, 4000) { Value = error });
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task IncrementRetryAsync(long id, string error, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE dbo.CrawlQueue
            SET Status = @Pending,
                RetryCount = RetryCount + 1,
                ErrorMessage = @Error,
                UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @Id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@Pending", SqlDbType.NVarChar, 32) { Value = CrawlQueueStatus.Pending });
        command.Parameters.Add(new SqlParameter("@Error", SqlDbType.NVarChar, 4000) { Value = error });
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CrawlQueueEntity MapQueue(SqlDataReader reader)
    {
        return new CrawlQueueEntity
        {
            Id = reader.GetInt64(0),
            Url = reader.GetString(1),
            Status = reader.GetString(2),
            Depth = reader.GetInt32(3),
            MaxDepth = reader.GetInt32(4),
            RetryCount = reader.GetInt32(5),
            ErrorMessage = reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAt = reader.GetDateTimeOffset(7),
            UpdatedAt = reader.GetDateTimeOffset(8)
        };
    }
}
