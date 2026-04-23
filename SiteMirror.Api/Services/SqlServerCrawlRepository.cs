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
                    QueueOrder INT NOT NULL,
                    RequestedUrl NVARCHAR(2048) NOT NULL,
                    FinalUrl NVARCHAR(2048) NOT NULL,
                    FrontendPreviewPath NVARCHAR(2048) NOT NULL,
                    EntryFileRelativePath NVARCHAR(2048) NOT NULL,
                    FilesSaved INT NOT NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL,
                    CONSTRAINT PK_CrawlPages PRIMARY KEY (CrawlId, QueueOrder),
                    CONSTRAINT FK_CrawlPages_CrawlRuns FOREIGN KEY (CrawlId) REFERENCES dbo.CrawlRuns(CrawlId) ON DELETE CASCADE
                );
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

            const string deletePagesSql = "DELETE FROM dbo.CrawlPages WHERE CrawlId = @CrawlId;";
            await using (var deletePagesCommand = new SqlCommand(deletePagesSql, connection, (SqlTransaction)transaction))
            {
                deletePagesCommand.Parameters.AddWithValue("@CrawlId", crawl.CrawlId);
                await deletePagesCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string insertPageSql = """
                INSERT INTO dbo.CrawlPages
                (
                    CrawlId, QueueOrder, RequestedUrl, FinalUrl, FrontendPreviewPath,
                    EntryFileRelativePath, FilesSaved, CreatedAtUtc
                )
                VALUES
                (
                    @CrawlId, @QueueOrder, @RequestedUrl, @FinalUrl, @FrontendPreviewPath,
                    @EntryFileRelativePath, @FilesSaved, @CreatedAtUtc
                );
                """;

            foreach (var page in pages)
            {
                await using var insertPageCommand = new SqlCommand(insertPageSql, connection, (SqlTransaction)transaction);
                insertPageCommand.Parameters.AddWithValue("@CrawlId", page.CrawlId);
                insertPageCommand.Parameters.AddWithValue("@QueueOrder", page.QueueOrder);
                insertPageCommand.Parameters.AddWithValue("@RequestedUrl", page.RequestedUrl);
                insertPageCommand.Parameters.AddWithValue("@FinalUrl", page.FinalUrl);
                insertPageCommand.Parameters.AddWithValue("@FrontendPreviewPath", page.FrontendPreviewPath);
                insertPageCommand.Parameters.AddWithValue("@EntryFileRelativePath", page.EntryFileRelativePath);
                insertPageCommand.Parameters.AddWithValue("@FilesSaved", page.FilesSaved);
                insertPageCommand.Parameters.AddWithValue("@CreatedAtUtc", page.CreatedAtUtc);
                await insertPageCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
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

        const string pagesSql = """
            SELECT
                CrawlId, QueueOrder, RequestedUrl, FinalUrl, FrontendPreviewPath,
                EntryFileRelativePath, FilesSaved, CreatedAtUtc
            FROM dbo.CrawlPages
            WHERE CrawlId = @CrawlId
            ORDER BY QueueOrder ASC;
            """;

        var pages = new List<CrawlPageRecord>();
        await using (var pagesCommand = new SqlCommand(pagesSql, connection))
        {
            pagesCommand.Parameters.AddWithValue("@CrawlId", crawlId);
            await using var reader = await pagesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pages.Add(new CrawlPageRecord
                {
                    CrawlId = reader.GetString(reader.GetOrdinal("CrawlId")),
                    QueueOrder = reader.GetInt32(reader.GetOrdinal("QueueOrder")),
                    RequestedUrl = reader.GetString(reader.GetOrdinal("RequestedUrl")),
                    FinalUrl = reader.GetString(reader.GetOrdinal("FinalUrl")),
                    FrontendPreviewPath = reader.GetString(reader.GetOrdinal("FrontendPreviewPath")),
                    EntryFileRelativePath = reader.GetString(reader.GetOrdinal("EntryFileRelativePath")),
                    FilesSaved = reader.GetInt32(reader.GetOrdinal("FilesSaved")),
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
}
