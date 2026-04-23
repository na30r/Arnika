using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public sealed class SqlServerCrawlRepository : ICrawlRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _connectionString;
    private readonly ILogger<SqlServerCrawlRepository> _logger;

    public SqlServerCrawlRepository(
        IOptions<DatabaseSettings> options,
        ILogger<SqlServerCrawlRepository> logger)
    {
        _connectionString = options.Value.ConnectionString ?? string.Empty;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning("Database connection string is empty; crawl history will not be persisted.");
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CrawlRuns')
            BEGIN
                CREATE TABLE dbo.CrawlRuns
                (
                    CrawlId NVARCHAR(64) NOT NULL PRIMARY KEY,
                    SourceUrl NVARCHAR(2048) NOT NULL,
                    SiteHost NVARCHAR(255) NOT NULL,
                    Version NVARCHAR(128) NOT NULL,
                    Status NVARCHAR(32) NOT NULL,
                    RequestedLinkLimit INT NOT NULL,
                    ProcessedPages INT NOT NULL,
                    TotalFilesSaved INT NOT NULL,
                    DefaultLanguage NVARCHAR(32) NOT NULL,
                    AvailableLanguagesJson NVARCHAR(MAX) NOT NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL,
                    UpdatedAtUtc DATETIMEOFFSET NOT NULL,
                    ErrorMessage NVARCHAR(MAX) NULL
                );
            END;

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CrawlPages')
            BEGIN
                CREATE TABLE dbo.CrawlPages
                (
                    CrawlId NVARCHAR(64) NOT NULL,
                    SiteHost NVARCHAR(255) NOT NULL,
                    Version NVARCHAR(128) NOT NULL,
                    QueueOrder INT NOT NULL,
                    RequestedUrl NVARCHAR(2048) NOT NULL,
                    RequestedUrlKey NVARCHAR(2048) NOT NULL,
                    FinalUrl NVARCHAR(2048) NOT NULL,
                    FrontendPreviewPath NVARCHAR(2048) NOT NULL,
                    EntryFileRelativePath NVARCHAR(2048) NOT NULL,
                    FilesSaved INT NOT NULL,
                    PageStatus NVARCHAR(32) NOT NULL,
                    ErrorMessage NVARCHAR(MAX) NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL,
                    CONSTRAINT PK_CrawlPages PRIMARY KEY (CrawlId, QueueOrder),
                    CONSTRAINT FK_CrawlPages_CrawlRuns FOREIGN KEY (CrawlId) REFERENCES dbo.CrawlRuns(CrawlId) ON DELETE CASCADE
                );
            END
            ELSE
            BEGIN
                IF COL_LENGTH('dbo.CrawlPages', 'SiteHost') IS NULL
                    ALTER TABLE dbo.CrawlPages ADD SiteHost NVARCHAR(255) NULL;
                IF COL_LENGTH('dbo.CrawlPages', 'Version') IS NULL
                    ALTER TABLE dbo.CrawlPages ADD Version NVARCHAR(128) NULL;
                IF COL_LENGTH('dbo.CrawlPages', 'RequestedUrlKey') IS NULL
                    ALTER TABLE dbo.CrawlPages ADD RequestedUrlKey NVARCHAR(2048) NULL;
                IF COL_LENGTH('dbo.CrawlPages', 'PageStatus') IS NULL
                    ALTER TABLE dbo.CrawlPages ADD PageStatus NVARCHAR(32) NULL;
                IF COL_LENGTH('dbo.CrawlPages', 'ErrorMessage') IS NULL
                    ALTER TABLE dbo.CrawlPages ADD ErrorMessage NVARCHAR(MAX) NULL;
            END;

            UPDATE cp SET
                SiteHost = COALESCE(cp.SiteHost, r.SiteHost),
                Version = COALESCE(cp.Version, r.Version),
                RequestedUrlKey = COALESCE(NULLIF(RTRIM(cp.RequestedUrlKey), ''), NULLIF(RTRIM(cp.RequestedUrl), '')),
                PageStatus = COALESCE(NULLIF(RTRIM(cp.PageStatus), ''), N'completed')
            FROM dbo.CrawlPages cp
            INNER JOIN dbo.CrawlRuns r ON r.CrawlId = cp.CrawlId
            WHERE cp.SiteHost IS NULL OR cp.Version IS NULL
               OR NULLIF(RTRIM(cp.RequestedUrlKey), '') IS NULL
               OR NULLIF(RTRIM(cp.PageStatus), '') IS NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrawlPages_SiteVersionUrlKey' AND object_id = OBJECT_ID('dbo.CrawlPages'))
            BEGIN
                CREATE NONCLUSTERED INDEX IX_CrawlPages_SiteVersionUrlKey
                ON dbo.CrawlPages (SiteHost, Version, RequestedUrlKey)
                INCLUDE (FinalUrl, EntryFileRelativePath, FilesSaved, PageStatus)
                WHERE PageStatus = N'completed'
                  AND NULLIF(SiteHost, N'') IS NOT NULL
                  AND NULLIF(Version, N'') IS NOT NULL
                  AND NULLIF(RequestedUrlKey, N'') IS NOT NULL;
            END;
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCrawlAsync(
        CrawlRecord crawl,
        IReadOnlyList<CrawlPageRecord> pages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string upsertCrawlSql = """
                MERGE dbo.CrawlRuns AS target
                USING (SELECT @CrawlId AS CrawlId) AS source
                ON target.CrawlId = source.CrawlId
                WHEN MATCHED THEN
                    UPDATE SET
                        SourceUrl = @SourceUrl,
                        SiteHost = @SiteHost,
                        Version = @Version,
                        Status = @Status,
                        RequestedLinkLimit = @RequestedLinkLimit,
                        ProcessedPages = @ProcessedPages,
                        TotalFilesSaved = @TotalFilesSaved,
                        DefaultLanguage = @DefaultLanguage,
                        AvailableLanguagesJson = @AvailableLanguagesJson,
                        CreatedAtUtc = @CreatedAtUtc,
                        UpdatedAtUtc = @UpdatedAtUtc,
                        ErrorMessage = @ErrorMessage
                WHEN NOT MATCHED THEN
                    INSERT
                    (
                        CrawlId, SourceUrl, SiteHost, Version, Status, RequestedLinkLimit,
                        ProcessedPages, TotalFilesSaved, DefaultLanguage, AvailableLanguagesJson,
                        CreatedAtUtc, UpdatedAtUtc, ErrorMessage
                    )
                    VALUES
                    (
                        @CrawlId, @SourceUrl, @SiteHost, @Version, @Status, @RequestedLinkLimit,
                        @ProcessedPages, @TotalFilesSaved, @DefaultLanguage, @AvailableLanguagesJson,
                        @CreatedAtUtc, @UpdatedAtUtc, @ErrorMessage
                    );
                """;

            await using (var upsertCrawlCommand = new SqlCommand(upsertCrawlSql, connection, (SqlTransaction)transaction))
            {
                upsertCrawlCommand.Parameters.AddWithValue("@CrawlId", crawl.CrawlId);
                upsertCrawlCommand.Parameters.AddWithValue("@SourceUrl", crawl.SourceUrl);
                upsertCrawlCommand.Parameters.AddWithValue("@SiteHost", crawl.SiteHost);
                upsertCrawlCommand.Parameters.AddWithValue("@Version", crawl.Version);
                upsertCrawlCommand.Parameters.AddWithValue("@Status", crawl.Status);
                upsertCrawlCommand.Parameters.AddWithValue("@RequestedLinkLimit", crawl.RequestedLinkLimit);
                upsertCrawlCommand.Parameters.AddWithValue("@ProcessedPages", crawl.ProcessedPages);
                upsertCrawlCommand.Parameters.AddWithValue("@TotalFilesSaved", crawl.TotalFilesSaved);
                upsertCrawlCommand.Parameters.AddWithValue("@DefaultLanguage", crawl.DefaultLanguage);
                upsertCrawlCommand.Parameters.AddWithValue("@AvailableLanguagesJson", JsonSerializer.Serialize(crawl.AvailableLanguages, JsonOptions));
                upsertCrawlCommand.Parameters.AddWithValue("@CreatedAtUtc", crawl.CreatedAtUtc);
                upsertCrawlCommand.Parameters.AddWithValue("@UpdatedAtUtc", crawl.UpdatedAtUtc);
                upsertCrawlCommand.Parameters.AddWithValue("@ErrorMessage", (object?)crawl.ErrorMessage ?? DBNull.Value);
                await upsertCrawlCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string mergePageSql = """
                MERGE dbo.CrawlPages AS target
                USING (SELECT @CrawlId AS CrawlId, @QueueOrder AS QueueOrder) AS source
                ON target.CrawlId = source.CrawlId AND target.QueueOrder = source.QueueOrder
                WHEN MATCHED THEN
                    UPDATE SET
                        SiteHost = @SiteHost,
                        Version = @Version,
                        RequestedUrl = @RequestedUrl,
                        RequestedUrlKey = @RequestedUrlKey,
                        FinalUrl = @FinalUrl,
                        FrontendPreviewPath = @FrontendPreviewPath,
                        EntryFileRelativePath = @EntryFileRelativePath,
                        FilesSaved = @FilesSaved,
                        PageStatus = @PageStatus,
                        ErrorMessage = @ErrorMessage,
                        CreatedAtUtc = @CreatedAtUtc
                WHEN NOT MATCHED THEN
                    INSERT
                    (
                        CrawlId, SiteHost, Version, QueueOrder, RequestedUrl, RequestedUrlKey,
                        FinalUrl, FrontendPreviewPath, EntryFileRelativePath, FilesSaved,
                        PageStatus, ErrorMessage, CreatedAtUtc
                    )
                    VALUES
                    (
                        @CrawlId, @SiteHost, @Version, @QueueOrder, @RequestedUrl, @RequestedUrlKey,
                        @FinalUrl, @FrontendPreviewPath, @EntryFileRelativePath, @FilesSaved,
                        @PageStatus, @ErrorMessage, @CreatedAtUtc
                    );
                """;

            foreach (var page in pages)
            {
                await using var mergePageCommand = new SqlCommand(mergePageSql, connection, (SqlTransaction)transaction);
                mergePageCommand.Parameters.AddWithValue("@CrawlId", page.CrawlId);
                mergePageCommand.Parameters.AddWithValue("@SiteHost", page.SiteHost);
                mergePageCommand.Parameters.AddWithValue("@Version", page.Version);
                mergePageCommand.Parameters.AddWithValue("@QueueOrder", page.QueueOrder);
                mergePageCommand.Parameters.AddWithValue("@RequestedUrl", page.RequestedUrl);
                mergePageCommand.Parameters.AddWithValue("@RequestedUrlKey", page.RequestedUrlKey);
                mergePageCommand.Parameters.AddWithValue("@FinalUrl", page.FinalUrl);
                mergePageCommand.Parameters.AddWithValue("@FrontendPreviewPath", page.FrontendPreviewPath);
                mergePageCommand.Parameters.AddWithValue("@EntryFileRelativePath", page.EntryFileRelativePath);
                mergePageCommand.Parameters.AddWithValue("@FilesSaved", page.FilesSaved);
                mergePageCommand.Parameters.AddWithValue("@PageStatus", page.PageStatus);
                mergePageCommand.Parameters.AddWithValue("@ErrorMessage", (object?)page.ErrorMessage ?? DBNull.Value);
                mergePageCommand.Parameters.AddWithValue("@CreatedAtUtc", page.CreatedAtUtc);
                await mergePageCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CompletedPageSnapshot?> TryGetCompletedPageAsync(
        string siteHost,
        string version,
        string requestedUrlKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) ||
            string.IsNullOrWhiteSpace(siteHost) ||
            string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(requestedUrlKey))
        {
            return null;
        }

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1)
                p.RequestedUrl, p.FinalUrl, p.EntryFileRelativePath, p.FilesSaved
            FROM dbo.CrawlPages p
            INNER JOIN dbo.CrawlRuns r ON r.CrawlId = p.CrawlId
            WHERE p.SiteHost = @SiteHost
              AND p.Version = @Version
              AND p.RequestedUrlKey = @RequestedUrlKey
              AND p.PageStatus = N'completed'
            ORDER BY r.CreatedAtUtc DESC, p.CreatedAtUtc DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@SiteHost", siteHost);
        command.Parameters.AddWithValue("@Version", version);
        command.Parameters.AddWithValue("@RequestedUrlKey", requestedUrlKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CompletedPageSnapshot
        {
            RequestedUrl = reader.GetString(reader.GetOrdinal("RequestedUrl")),
            FinalUrl = reader.GetString(reader.GetOrdinal("FinalUrl")),
            EntryFileRelativePath = reader.GetString(reader.GetOrdinal("EntryFileRelativePath")),
            FilesSaved = reader.GetInt32(reader.GetOrdinal("FilesSaved"))
        };
    }

    public async Task<CrawlStatusResult?> GetCrawlAsync(string crawlId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return null;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string crawlSql = """
            SELECT
                CrawlId, SourceUrl, SiteHost, Version, Status, RequestedLinkLimit,
                ProcessedPages, TotalFilesSaved, DefaultLanguage, AvailableLanguagesJson,
                CreatedAtUtc, UpdatedAtUtc, ErrorMessage
            FROM dbo.CrawlRuns
            WHERE CrawlId = @CrawlId;
            """;

        CrawlRecord? crawl = null;
        await using (var crawlCommand = new SqlCommand(crawlSql, connection))
        {
            crawlCommand.Parameters.AddWithValue("@CrawlId", crawlId);
            await using var reader = await crawlCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var availableLanguagesJson = reader.GetString(reader.GetOrdinal("AvailableLanguagesJson"));
                var availableLanguages = JsonSerializer.Deserialize<List<string>>(availableLanguagesJson, JsonOptions) ?? [];
                crawl = new CrawlRecord
                {
                    CrawlId = reader.GetString(reader.GetOrdinal("CrawlId")),
                    SourceUrl = reader.GetString(reader.GetOrdinal("SourceUrl")),
                    SiteHost = reader.GetString(reader.GetOrdinal("SiteHost")),
                    Version = reader.GetString(reader.GetOrdinal("Version")),
                    Status = reader.GetString(reader.GetOrdinal("Status")),
                    RequestedLinkLimit = reader.GetInt32(reader.GetOrdinal("RequestedLinkLimit")),
                    ProcessedPages = reader.GetInt32(reader.GetOrdinal("ProcessedPages")),
                    TotalFilesSaved = reader.GetInt32(reader.GetOrdinal("TotalFilesSaved")),
                    DefaultLanguage = reader.GetString(reader.GetOrdinal("DefaultLanguage")),
                    AvailableLanguages = availableLanguages,
                    CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CreatedAtUtc")),
                    UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("UpdatedAtUtc")),
                    ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("ErrorMessage"))
                };
            }
        }

        if (crawl is null)
        {
            return null;
        }

        // Dynamic columns for databases created before new CrawlPages columns.
        var hasSiteHost = await ColumnExistsAsync(connection, "CrawlPages", "SiteHost", cancellationToken);
        var hasUrlKey = await ColumnExistsAsync(connection, "CrawlPages", "RequestedUrlKey", cancellationToken);
        var hasPageStatus = await ColumnExistsAsync(connection, "CrawlPages", "PageStatus", cancellationToken);
        var hasErrorMsg = await ColumnExistsAsync(connection, "CrawlPages", "ErrorMessage", cancellationToken);

        var listCols = "CrawlId, QueueOrder, RequestedUrl, FinalUrl, FrontendPreviewPath, EntryFileRelativePath, FilesSaved, CreatedAtUtc"
            + (hasSiteHost ? ", SiteHost" : "")
            + (hasUrlKey ? ", RequestedUrlKey" : "")
            + (hasPageStatus ? ", PageStatus" : "")
            + (hasErrorMsg ? ", ErrorMessage" : "");

        var pagesSql = $"""
            SELECT {listCols}
            FROM dbo.CrawlPages
            WHERE CrawlId = @CrawlId
            ORDER BY QueueOrder ASC;
            """;

        var pages = new List<CrawlPageRecord>();
        await using (var pagesCommand = new SqlCommand(pagesSql, connection))
        {
            pagesCommand.Parameters.AddWithValue("@CrawlId", crawlId);
            await using var reader = await pagesCommand.ExecuteReaderAsync(cancellationToken);
            var ordSite = hasSiteHost ? reader.GetOrdinal("SiteHost") : -1;
            var ordKey = hasUrlKey ? reader.GetOrdinal("RequestedUrlKey") : -1;
            var ordSt = hasPageStatus ? reader.GetOrdinal("PageStatus") : -1;
            var ordErr = hasErrorMsg ? reader.GetOrdinal("ErrorMessage") : -1;
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = hasUrlKey && ordKey >= 0 && !reader.IsDBNull(ordKey)
                    ? reader.GetString(ordKey)
                    : reader.GetString(reader.GetOrdinal("RequestedUrl"));
                var pStatus = hasPageStatus && ordSt >= 0 && !reader.IsDBNull(ordSt)
                    ? reader.GetString(ordSt)
                    : "completed";
                var siteH = hasSiteHost && ordSite >= 0 && !reader.IsDBNull(ordSite) ? reader.GetString(ordSite) : crawl.SiteHost;
                var err = hasErrorMsg && ordErr >= 0 && !reader.IsDBNull(ordErr) ? reader.GetString(ordErr) : null;
                pages.Add(new CrawlPageRecord
                {
                    CrawlId = reader.GetString(reader.GetOrdinal("CrawlId")),
                    SiteHost = siteH,
                    Version = crawl.Version,
                    QueueOrder = reader.GetInt32(reader.GetOrdinal("QueueOrder")),
                    RequestedUrl = reader.GetString(reader.GetOrdinal("RequestedUrl")),
                    RequestedUrlKey = key,
                    FinalUrl = reader.GetString(reader.GetOrdinal("FinalUrl")),
                    FrontendPreviewPath = reader.GetString(reader.GetOrdinal("FrontendPreviewPath")),
                    EntryFileRelativePath = reader.GetString(reader.GetOrdinal("EntryFileRelativePath")),
                    FilesSaved = reader.GetInt32(reader.GetOrdinal("FilesSaved")),
                    PageStatus = pStatus,
                    ErrorMessage = err,
                    CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CreatedAtUtc"))
                });
            }
        }

        return new CrawlStatusResult
        {
            Crawl = crawl,
            Pages = pages
        };
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        const string q = "SELECT 1 FROM sys.columns c INNER JOIN sys.tables t ON t.object_id = c.object_id WHERE t.name = @T AND c.name = @C;";
        await using var cmd = new SqlCommand(q, connection);
        cmd.Parameters.AddWithValue("@T", table);
        cmd.Parameters.AddWithValue("@C", column);
        var o = await cmd.ExecuteScalarAsync(cancellationToken);
        return o is not null;
    }
}
