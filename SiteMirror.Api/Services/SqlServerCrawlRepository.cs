using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services.Mirroring;

namespace SiteMirror.Api.Services;

public sealed class SqlServerCrawlRepository : ICrawlRepository, IMirrorContentAddressRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _connectionString;
    private readonly ILogger<SqlServerCrawlRepository> _logger;
    private readonly MirrorSettings _mirrorSettings;

    public SqlServerCrawlRepository(
        IOptions<DatabaseSettings> options,
        IOptions<MirrorSettings> mirrorOptions,
        ILogger<SqlServerCrawlRepository> logger)
    {
        _connectionString = options.Value.ConnectionString ?? string.Empty;
        _mirrorSettings = mirrorOptions.Value;
        _logger = logger;
    }

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_connectionString) && _mirrorSettings.ContentAddressedMirrorFiles;

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
                    UserId UNIQUEIDENTIFIER NULL,
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
            END
            ELSE
            BEGIN
                IF COL_LENGTH('dbo.CrawlRuns', 'UserId') IS NULL
                    ALTER TABLE dbo.CrawlRuns ADD UserId UNIQUEIDENTIFIER NULL;
            END

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

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'InjectionAssets')
            BEGIN
                CREATE TABLE dbo.InjectionAssets
                (
                    AssetId NVARCHAR(64) NOT NULL PRIMARY KEY,
                    SiteHost NVARCHAR(255) NOT NULL,
                    Version NVARCHAR(128) NOT NULL,
                    AssetType NVARCHAR(16) NOT NULL,
                    Name NVARCHAR(255) NOT NULL,
                    Description NVARCHAR(MAX) NOT NULL,
                    RelativeFilePath NVARCHAR(2048) NOT NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL
                );
            END

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'InjectionAssetTargets')
            BEGIN
                CREATE TABLE dbo.InjectionAssetTargets
                (
                    AssetId NVARCHAR(64) NOT NULL,
                    TargetPagePath NVARCHAR(2048) NOT NULL,
                    CONSTRAINT FK_InjectionAssetTargets_InjectionAssets
                        FOREIGN KEY (AssetId) REFERENCES dbo.InjectionAssets(AssetId) ON DELETE CASCADE
                );
            END

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TranslationArchive')
            BEGIN
                CREATE TABLE dbo.TranslationArchive
                (
                    Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    Scope NVARCHAR(32) NOT NULL,
                    SiteHost NVARCHAR(255) NOT NULL,
                    Version NVARCHAR(128) NOT NULL,
                    Language NVARCHAR(32) NOT NULL,
                    PagePath NVARCHAR(2048) NULL,
                    TranslationKey NVARCHAR(2048) NOT NULL,
                    OriginalText NVARCHAR(MAX) NULL,
                    TranslatedValue NVARCHAR(MAX) NOT NULL,
                    SavedAtUtc DATETIMEOFFSET NOT NULL
                );
                CREATE INDEX IX_TranslationArchive_SiteVersionLang
                    ON dbo.TranslationArchive (SiteHost, Version, Language, Scope, SavedAtUtc DESC);
            END

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'MirrorUrlQueueItems')
            BEGIN
                CREATE TABLE dbo.MirrorUrlQueueItems
                (
                    ItemId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
                    BatchId NVARCHAR(64) NOT NULL,
                    UserId UNIQUEIDENTIFIER NULL,
                    Url NVARCHAR(2048) NOT NULL,
                    OptionsJson NVARCHAR(MAX) NOT NULL,
                    Status NVARCHAR(32) NOT NULL,
                    CrawlId NVARCHAR(64) NULL,
                    ResultJson NVARCHAR(MAX) NULL,
                    ErrorMessage NVARCHAR(MAX) NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL,
                    StartedAtUtc DATETIMEOFFSET NULL,
                    CompletedAtUtc DATETIMEOFFSET NULL
                );
                CREATE INDEX IX_MirrorUrlQueueItems_BatchId ON dbo.MirrorUrlQueueItems (BatchId);
                CREATE INDEX IX_MirrorUrlQueueItems_Status_Created ON dbo.MirrorUrlQueueItems (Status, CreatedAtUtc);
            END

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'MirrorContentBlobs')
            BEGIN
                CREATE TABLE dbo.MirrorContentBlobs
                (
                    SiteHost NVARCHAR(255) NOT NULL,
                    Version NVARCHAR(128) NOT NULL,
                    ContentSha256 CHAR(64) NOT NULL,
                    RelativePath NVARCHAR(2048) NOT NULL,
                    ByteLength BIGINT NOT NULL,
                    MediaTypeHint NVARCHAR(255) NULL,
                    CreatedAtUtc DATETIMEOFFSET NOT NULL,
                    CONSTRAINT PK_MirrorContentBlobs PRIMARY KEY (SiteHost, Version, ContentSha256)
                );
            END

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'MirrorUrlResourceMap')
            BEGIN
                CREATE TABLE dbo.MirrorUrlResourceMap
                (
                    SiteHost NVARCHAR(255) NOT NULL,
                    Version NVARCHAR(128) NOT NULL,
                    UrlKey NVARCHAR(2048) NOT NULL,
                    ContentSha256 CHAR(64) NOT NULL,
                    RelativePath NVARCHAR(2048) NOT NULL,
                    ByteLength BIGINT NOT NULL,
                    MediaTypeHint NVARCHAR(255) NULL,
                    MappedAtUtc DATETIMEOFFSET NOT NULL,
                    CONSTRAINT PK_MirrorUrlResourceMap PRIMARY KEY (SiteHost, Version, UrlKey)
                );
            END

            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MirrorContentAddressResult> RegisterOrGetContentAsync(
        string siteHost,
        string version,
        string mirrorSiteRoot,
        string urlKey,
        byte[] body,
        string? mediaType,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Content-addressed mirroring is not enabled (database or setting).");
        }

        if (body.Length == 0)
        {
            throw new ArgumentException("Body must not be empty.", nameof(body));
        }

        var hashHex = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var ext = MirrorPathHelper.GetExtensionForMediaType(mediaType, ".bin");
        var relativePath = Path.Combine("_cas", hashHex[..2], $"{hashHex}{ext}");

        await EnsureSchemaAsync(cancellationToken);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string selectBlobSql = """
            SELECT RelativePath
            FROM dbo.MirrorContentBlobs
            WHERE SiteHost = @SiteHost AND Version = @Version AND ContentSha256 = @Hash
            """;

        async Task<string?> TrySelectPathAsync()
        {
            await using var cmd = new SqlCommand(selectBlobSql, connection);
            cmd.Parameters.AddWithValue("@SiteHost", siteHost);
            cmd.Parameters.AddWithValue("@Version", version);
            cmd.Parameters.AddWithValue("@Hash", hashHex);
            var o = await cmd.ExecuteScalarAsync(cancellationToken);
            return o is string s ? s : null;
        }

        async Task UpsertUrlMapAsync(string pathForUrl, CancellationToken ct)
        {
            const string mergeSql = """
                MERGE dbo.MirrorUrlResourceMap AS t
                USING (SELECT @SiteHost AS SiteHost, @Version AS Version, @UrlKey AS UrlKey) AS s
                ON t.SiteHost = s.SiteHost AND t.Version = s.Version AND t.UrlKey = s.UrlKey
                WHEN MATCHED THEN
                    UPDATE SET
                        ContentSha256 = @Hash,
                        RelativePath = @RelPath,
                        ByteLength = @ByteLength,
                        MediaTypeHint = @MediaType,
                        MappedAtUtc = SYSDATETIMEOFFSET()
                WHEN NOT MATCHED THEN
                    INSERT (SiteHost, Version, UrlKey, ContentSha256, RelativePath, ByteLength, MediaTypeHint, MappedAtUtc)
                    VALUES (@SiteHost, @Version, @UrlKey, @Hash, @RelPath, @ByteLength, @MediaType, SYSDATETIMEOFFSET());
                """;
            await using var cmd = new SqlCommand(mergeSql, connection);
            cmd.Parameters.AddWithValue("@SiteHost", siteHost);
            cmd.Parameters.AddWithValue("@Version", version);
            cmd.Parameters.AddWithValue("@UrlKey", urlKey);
            cmd.Parameters.AddWithValue("@Hash", hashHex);
            cmd.Parameters.AddWithValue("@RelPath", pathForUrl);
            cmd.Parameters.AddWithValue("@ByteLength", (long)body.Length);
            cmd.Parameters.AddWithValue("@MediaType", (object?)mediaType ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var existingPath = await TrySelectPathAsync();
        if (existingPath is not null)
        {
            await UpsertUrlMapAsync(existingPath, cancellationToken);
            var diskPath = Path.Combine(mirrorSiteRoot, existingPath);
            var mustWrite = !File.Exists(diskPath);
            if (mustWrite)
            {
                _logger.LogWarning(
                    "CAS blob row exists for hash {Hash} but file missing at {Path}; caller will rewrite.",
                    hashHex,
                    diskPath);
            }

            return new MirrorContentAddressResult
            {
                RelativePath = existingPath,
                CallerMustWriteFile = mustWrite,
                ContentSha256Hex = hashHex
            };
        }

        const string insertBlobSql = """
            INSERT INTO dbo.MirrorContentBlobs (SiteHost, Version, ContentSha256, RelativePath, ByteLength, MediaTypeHint, CreatedAtUtc)
            VALUES (@SiteHost, @Version, @Hash, @RelPath, @ByteLength, @MediaType, SYSDATETIMEOFFSET());
            """;

        try
        {
            await using (var ins = new SqlCommand(insertBlobSql, connection))
            {
                ins.Parameters.AddWithValue("@SiteHost", siteHost);
                ins.Parameters.AddWithValue("@Version", version);
                ins.Parameters.AddWithValue("@Hash", hashHex);
                ins.Parameters.AddWithValue("@RelPath", relativePath);
                ins.Parameters.AddWithValue("@ByteLength", (long)body.Length);
                ins.Parameters.AddWithValue("@MediaType", (object?)mediaType ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(cancellationToken);
            }

            await UpsertUrlMapAsync(relativePath, cancellationToken);
            return new MirrorContentAddressResult
            {
                RelativePath = relativePath,
                CallerMustWriteFile = true,
                ContentSha256Hex = hashHex
            };
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            var pathAfterRace = await TrySelectPathAsync();
            if (pathAfterRace is null)
            {
                throw;
            }

            await UpsertUrlMapAsync(pathAfterRace, cancellationToken);
            var diskPath = Path.Combine(mirrorSiteRoot, pathAfterRace);
            return new MirrorContentAddressResult
            {
                RelativePath = pathAfterRace,
                CallerMustWriteFile = !File.Exists(diskPath),
                ContentSha256Hex = hashHex
            };
        }
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
                        UserId = @UserId,
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
                        CrawlId, UserId, SourceUrl, SiteHost, Version, Status, RequestedLinkLimit,
                        ProcessedPages, TotalFilesSaved, DefaultLanguage, AvailableLanguagesJson,
                        CreatedAtUtc, UpdatedAtUtc, ErrorMessage
                    )
                    VALUES
                    (
                        @CrawlId, @UserId, @SourceUrl, @SiteHost, @Version, @Status, @RequestedLinkLimit,
                        @ProcessedPages, @TotalFilesSaved, @DefaultLanguage, @AvailableLanguagesJson,
                        @CreatedAtUtc, @UpdatedAtUtc, @ErrorMessage
                    );
                """;

            await using (var upsertCrawlCommand = new SqlCommand(upsertCrawlSql, connection, (SqlTransaction)transaction))
            {
                upsertCrawlCommand.Parameters.AddWithValue("@CrawlId", crawl.CrawlId);
                upsertCrawlCommand.Parameters.AddWithValue("@UserId", (object?)crawl.UserId ?? DBNull.Value);
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
        Guid? forUserId,
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
              AND ((@ForUserId IS NULL AND r.UserId IS NULL) OR (r.UserId = @ForUserId))
            ORDER BY r.CreatedAtUtc DESC, p.CreatedAtUtc DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@SiteHost", siteHost);
        command.Parameters.AddWithValue("@Version", version);
        command.Parameters.AddWithValue("@RequestedUrlKey", requestedUrlKey);
        command.Parameters.AddWithValue("@ForUserId", (object?)forUserId ?? DBNull.Value);

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

    public async Task<IReadOnlyList<MirrorHistoryItem>> GetMirrorHistoryForUserAsync(
        Guid userId,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || userId == Guid.Empty)
        {
            return Array.Empty<MirrorHistoryItem>();
        }

        var limit = Math.Clamp(take, 1, 200);
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string q = """
            SELECT TOP (@Take)
                CrawlId, SourceUrl, SiteHost, Version, Status, ProcessedPages, TotalFilesSaved, CreatedAtUtc
            FROM dbo.CrawlRuns
            WHERE UserId = @UserId
            ORDER BY CreatedAtUtc DESC;
            """;
        await using var cmd = new SqlCommand(q, connection);
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@Take", limit);
        var list = new List<MirrorHistoryItem>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new MirrorHistoryItem
            {
                CrawlId = r.GetString(r.GetOrdinal("CrawlId")),
                SourceUrl = r.GetString(r.GetOrdinal("SourceUrl")),
                SiteHost = r.GetString(r.GetOrdinal("SiteHost")),
                Version = r.GetString(r.GetOrdinal("Version")),
                Status = r.GetString(r.GetOrdinal("Status")),
                ProcessedPages = r.GetInt32(r.GetOrdinal("ProcessedPages")),
                TotalFilesSaved = r.GetInt32(r.GetOrdinal("TotalFilesSaved")),
                CreatedAtUtc = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("CreatedAtUtc"))
            });
        }

        return list;
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
                CrawlId, UserId, SourceUrl, SiteHost, Version, Status, RequestedLinkLimit,
                ProcessedPages, TotalFilesSaved, DefaultLanguage, AvailableLanguagesJson,
                CreatedAtUtc, UpdatedAtUtc, ErrorMessage
            FROM dbo.CrawlRuns
            WHERE CrawlId = @CrawlId;
            """;

        var hasUserId = await ColumnExistsAsync(connection, "CrawlRuns", "UserId", cancellationToken);
        var crawlSelect = hasUserId
            ? crawlSql
            : """
            SELECT
                CrawlId, SourceUrl, SiteHost, Version, Status, RequestedLinkLimit,
                ProcessedPages, TotalFilesSaved, DefaultLanguage, AvailableLanguagesJson,
                CreatedAtUtc, UpdatedAtUtc, ErrorMessage
            FROM dbo.CrawlRuns
            WHERE CrawlId = @CrawlId;
            """;

        CrawlRecord? crawl = null;
        await using (var crawlCommand = new SqlCommand(crawlSelect, connection))
        {
            crawlCommand.Parameters.AddWithValue("@CrawlId", crawlId);
            await using var reader = await crawlCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var availableLanguagesJson = reader.GetString(reader.GetOrdinal("AvailableLanguagesJson"));
                var availableLanguages = JsonSerializer.Deserialize<List<string>>(availableLanguagesJson, JsonOptions) ?? [];
                var ordUid = hasUserId ? reader.GetOrdinal("UserId") : -1;
                var uid = hasUserId && !reader.IsDBNull(ordUid) ? reader.GetGuid(ordUid) : (Guid?)null;
                crawl = new CrawlRecord
                {
                    CrawlId = reader.GetString(reader.GetOrdinal("CrawlId")),
                    UserId = uid,
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

    public async Task AppendTranslationArchiveAsync(
        string scope,
        string siteHost,
        string version,
        string language,
        string? pagePath,
        IReadOnlyList<TranslationArchiveRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || rows.Count == 0)
        {
            return;
        }

        var normalizedScope = scope.Trim();
        if (normalizedScope.Length > 32)
        {
            normalizedScope = normalizedScope[..32];
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string insertSql = """
            INSERT INTO dbo.TranslationArchive
                (Scope, SiteHost, Version, Language, PagePath, TranslationKey, OriginalText, TranslatedValue, SavedAtUtc)
            VALUES
                (@Scope, @SiteHost, @Version, @Language, @PagePath, @TranslationKey, @OriginalText, @TranslatedValue, @SavedAtUtc);
            """;

        var savedAt = DateTimeOffset.UtcNow;
        try
        {
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.TranslationKey))
                {
                    continue;
                }

                await using var cmd = new SqlCommand(insertSql, connection, transaction);
                cmd.Parameters.AddWithValue("@Scope", normalizedScope);
                cmd.Parameters.AddWithValue("@SiteHost", siteHost);
                cmd.Parameters.AddWithValue("@Version", version);
                cmd.Parameters.AddWithValue("@Language", language);
                var effectivePagePath = string.IsNullOrWhiteSpace(row.PagePath) ? pagePath : row.PagePath.Trim();
                cmd.Parameters.AddWithValue("@PagePath", (object?)effectivePagePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@TranslationKey", row.TranslationKey);
                cmd.Parameters.Add(new SqlParameter("@OriginalText", SqlDbType.NVarChar, -1)
                {
                    Value = (object?)row.OriginalText ?? DBNull.Value
                });
                cmd.Parameters.Add(new SqlParameter("@TranslatedValue", SqlDbType.NVarChar, -1)
                {
                    Value = row.TranslatedValue ?? string.Empty
                });
                cmd.Parameters.AddWithValue("@SavedAtUtc", savedAt);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<TranslationArchiveRecordDto>> QueryTranslationArchiveAsync(
        string? siteHost,
        string? version,
        string? language,
        string? scope,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return Array.Empty<TranslationArchiveRecordDto>();
        }

        var limit = Math.Clamp(take, 1, 5000);
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (@Take)
                Id, Scope, SiteHost, Version, Language, PagePath, TranslationKey, OriginalText, TranslatedValue, SavedAtUtc
            FROM dbo.TranslationArchive
            WHERE (@SiteHost IS NULL OR SiteHost = @SiteHost)
              AND (@Version IS NULL OR Version = @Version)
              AND (@Language IS NULL OR Language = @Language)
              AND (@Scope IS NULL OR Scope = @Scope)
            ORDER BY SavedAtUtc DESC, Id DESC;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Take", limit);
        cmd.Parameters.AddWithValue("@SiteHost", string.IsNullOrWhiteSpace(siteHost) ? (object)DBNull.Value : siteHost.Trim());
        cmd.Parameters.AddWithValue("@Version", string.IsNullOrWhiteSpace(version) ? (object)DBNull.Value : version.Trim());
        cmd.Parameters.AddWithValue("@Language", string.IsNullOrWhiteSpace(language) ? (object)DBNull.Value : language.Trim());
        cmd.Parameters.AddWithValue("@Scope", string.IsNullOrWhiteSpace(scope) ? (object)DBNull.Value : scope.Trim());

        var list = new List<TranslationArchiveRecordDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ordPagePath = reader.GetOrdinal("PagePath");
        var ordOrig = reader.GetOrdinal("OriginalText");
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new TranslationArchiveRecordDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                Scope = reader.GetString(reader.GetOrdinal("Scope")),
                SiteHost = reader.GetString(reader.GetOrdinal("SiteHost")),
                Version = reader.GetString(reader.GetOrdinal("Version")),
                Language = reader.GetString(reader.GetOrdinal("Language")),
                PagePath = reader.IsDBNull(ordPagePath) ? null : reader.GetString(ordPagePath),
                TranslationKey = reader.GetString(reader.GetOrdinal("TranslationKey")),
                OriginalText = reader.IsDBNull(ordOrig) ? null : reader.GetString(ordOrig),
                TranslatedValue = reader.GetString(reader.GetOrdinal("TranslatedValue")),
                SavedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("SavedAtUtc"))
            });
        }

        return list;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetLatestCatalogEntriesFromArchiveAsync(
        string siteHost,
        string version,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) ||
            string.IsNullOrWhiteSpace(siteHost) ||
            string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(language))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TranslationKey, TranslatedValue
            FROM (
                SELECT TranslationKey, TranslatedValue,
                    ROW_NUMBER() OVER (PARTITION BY TranslationKey ORDER BY SavedAtUtc DESC, Id DESC) AS rn
                FROM dbo.TranslationArchive
                WHERE SiteHost = @SiteHost
                  AND Version = @Version
                  AND Language = @Language
                  AND Scope = N'catalog'
            ) x
            WHERE x.rn = 1;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SiteHost", siteHost.Trim());
        cmd.Parameters.AddWithValue("@Version", version.Trim());
        cmd.Parameters.AddWithValue("@Language", language.Trim());

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var val = reader.GetString(1);
            dict[key] = val;
        }

        return dict;
    }

    public async Task EnqueueMirrorUrlBatchAsync(
        string batchId,
        Guid? userId,
        IReadOnlyList<string> urls,
        MirrorQueueTemplate template,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("Database connection string is not configured.");
        }

        if (urls.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        var optionsJson = JsonSerializer.Serialize(template, JsonOptions);
        var now = DateTimeOffset.UtcNow;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string insertSql = """
            INSERT INTO dbo.MirrorUrlQueueItems
                (BatchId, UserId, Url, OptionsJson, Status, CreatedAtUtc)
            VALUES
                (@BatchId, @UserId, @Url, @OptionsJson, N'pending', @CreatedAtUtc);
            """;

        try
        {
            foreach (var url in urls)
            {
                await using var cmd = new SqlCommand(insertSql, connection, (SqlTransaction)transaction);
                cmd.Parameters.AddWithValue("@BatchId", batchId);
                cmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Url", url);
                cmd.Parameters.AddWithValue("@OptionsJson", optionsJson);
                cmd.Parameters.AddWithValue("@CreatedAtUtc", now);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MirrorQueueClaimedItem?> TryClaimMirrorQueueItemAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return null;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        Guid? itemId = null;
        try
        {
            const string selectSql = """
                SELECT TOP (1) ItemId
                FROM dbo.MirrorUrlQueueItems WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = N'pending'
                ORDER BY CreatedAtUtc ASC;
                """;

            await using (var cmd = new SqlCommand(selectSql, connection, (SqlTransaction)transaction))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    itemId = reader.GetGuid(0);
                }
            }

            if (itemId is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            const string updateSql = """
                UPDATE dbo.MirrorUrlQueueItems
                SET Status = N'running', StartedAtUtc = SYSDATETIMEOFFSET()
                WHERE ItemId = @ItemId AND Status = N'pending';
                """;

            await using (var updateCmd = new SqlCommand(updateSql, connection, (SqlTransaction)transaction))
            {
                updateCmd.Parameters.AddWithValue("@ItemId", itemId.Value);
                var updated = await updateCmd.ExecuteNonQueryAsync(cancellationToken);
                if (updated == 0)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }
            }

            MirrorQueueClaimedItem? claimed = null;
            const string readSql = """
                SELECT BatchId, Url, OptionsJson, UserId
                FROM dbo.MirrorUrlQueueItems
                WHERE ItemId = @ItemId;
                """;

            await using (var readCmd = new SqlCommand(readSql, connection, (SqlTransaction)transaction))
            {
                readCmd.Parameters.AddWithValue("@ItemId", itemId.Value);
                await using var reader = await readCmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    claimed = new MirrorQueueClaimedItem
                    {
                        ItemId = itemId.Value,
                        BatchId = reader.GetString(0),
                        Url = reader.GetString(1),
                        OptionsJson = reader.GetString(2),
                        UserId = reader.IsDBNull(3) ? null : reader.GetGuid(3)
                    };
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return claimed;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task CompleteMirrorQueueItemAsync(
        Guid itemId,
        string status,
        string? crawlId,
        MirrorResult? result,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        var resultJson = result is null ? null : JsonSerializer.Serialize(result, JsonOptions);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE dbo.MirrorUrlQueueItems
            SET Status = @Status,
                CrawlId = @CrawlId,
                ResultJson = @ResultJson,
                ErrorMessage = @ErrorMessage,
                CompletedAtUtc = SYSDATETIMEOFFSET()
            WHERE ItemId = @ItemId;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@CrawlId", (object?)crawlId ?? DBNull.Value);
        cmd.Parameters.Add(new SqlParameter("@ResultJson", SqlDbType.NVarChar, -1)
        {
            Value = (object?)resultJson ?? DBNull.Value
        });
        cmd.Parameters.Add(new SqlParameter("@ErrorMessage", SqlDbType.NVarChar, -1)
        {
            Value = (object?)errorMessage ?? DBNull.Value
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MirrorQueueItemRow>> ListMirrorQueueBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(batchId))
        {
            return Array.Empty<MirrorQueueItemRow>();
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT ItemId, Url, Status, CrawlId, ResultJson, ErrorMessage
            FROM dbo.MirrorUrlQueueItems
            WHERE BatchId = @BatchId
            ORDER BY CreatedAtUtc ASC;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@BatchId", batchId);

        var list = new List<MirrorQueueItemRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ordCrawl = reader.GetOrdinal("CrawlId");
        var ordRes = reader.GetOrdinal("ResultJson");
        var ordErr = reader.GetOrdinal("ErrorMessage");
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new MirrorQueueItemRow
            {
                ItemId = reader.GetGuid(reader.GetOrdinal("ItemId")),
                Url = reader.GetString(reader.GetOrdinal("Url")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                CrawlId = reader.IsDBNull(ordCrawl) ? null : reader.GetString(ordCrawl),
                ResultJson = reader.IsDBNull(ordRes) ? null : reader.GetString(ordRes),
                ErrorMessage = reader.IsDBNull(ordErr) ? null : reader.GetString(ordErr)
            });
        }

        return list;
    }

    public async Task DeleteMirrorQueueBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(batchId))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            DELETE FROM dbo.MirrorUrlQueueItems
            WHERE BatchId = @BatchId;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@BatchId", batchId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
