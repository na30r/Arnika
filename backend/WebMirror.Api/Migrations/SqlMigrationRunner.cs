using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WebMirror.Api.Data;

namespace WebMirror.Api.Migrations;

public sealed class SqlMigrationRunner(
    IDbConnectionFactory connectionFactory,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<SqlMigrationRunner> logger) : IMigrationRunner
{
    private static readonly Regex GoSplitRegex = new(
        @"^\s*GO\s*($|\-\-.*$)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task ApplyPendingMigrationsAsync(CancellationToken cancellationToken)
    {
        await EnsureDatabaseExistsAsync(cancellationToken);

        var migrationDirectory = ResolveMigrationDirectory();
        if (!Directory.Exists(migrationDirectory))
        {
            logger.LogWarning("Migration directory not found at {Path}; skipping DB migration.", migrationDirectory);
            return;
        }

        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await EnsureMigrationHistoryTableAsync(connection, cancellationToken);

        var files = Directory
            .GetFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (await HasMigrationRunAsync(connection, name, cancellationToken))
            {
                continue;
            }

            logger.LogInformation("Applying SQL migration {MigrationName}", name);
            var sql = await File.ReadAllTextAsync(file, cancellationToken);
            await ExecuteSqlBatchAsync(connection, sql, cancellationToken);
            await MarkMigrationAsRunAsync(connection, name, cancellationToken);
        }
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var rawConnectionString = configuration.GetConnectionString("MirrorDb")
            ?? throw new InvalidOperationException("Connection string 'MirrorDb' is not configured.");

        var appDbBuilder = new SqlConnectionStringBuilder(rawConnectionString);
        var targetDbName = appDbBuilder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(targetDbName))
        {
            throw new InvalidOperationException("Connection string 'MirrorDb' must include Database/Initial Catalog.");
        }

        var adminBuilder = new SqlConnectionStringBuilder(rawConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var masterConnection = new SqlConnection(adminBuilder.ConnectionString);
        await masterConnection.OpenAsync(cancellationToken);

        const string sql = """
            IF DB_ID(@DatabaseName) IS NULL
            BEGIN
                DECLARE @createSql NVARCHAR(MAX) = N'CREATE DATABASE [' + REPLACE(@DatabaseName, ']', ']]') + N']';
                EXEC(@createSql);
            END
            """;

        await using var command = new SqlCommand(sql, masterConnection);
        command.Parameters.Add(new SqlParameter("@DatabaseName", SqlDbType.NVarChar, 128) { Value = targetDbName });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string ResolveMigrationDirectory()
    {
        var fromContentRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "..", "database", "migrations"));
        if (Directory.Exists(fromContentRoot))
        {
            return fromContentRoot;
        }

        // Fallback for production publish layouts where relative depth differs.
        return Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "database", "migrations"));
    }

    private static async Task EnsureMigrationHistoryTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID('dbo.SchemaMigrations', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SchemaMigrations (
                    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
                    ScriptName NVARCHAR(255) NOT NULL UNIQUE,
                    AppliedAt DATETIMEOFFSET NOT NULL CONSTRAINT DF_SchemaMigrations_AppliedAt DEFAULT SYSUTCDATETIME()
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasMigrationRunAsync(SqlConnection connection, string scriptName, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(1) FROM dbo.SchemaMigrations WHERE ScriptName = @ScriptName;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@ScriptName", SqlDbType.NVarChar, 255) { Value = scriptName });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) > 0;
    }

    private static async Task MarkMigrationAsRunAsync(SqlConnection connection, string scriptName, CancellationToken cancellationToken)
    {
        const string sql = "INSERT INTO dbo.SchemaMigrations (ScriptName) VALUES (@ScriptName);";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@ScriptName", SqlDbType.NVarChar, 255) { Value = scriptName });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteSqlBatchAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var statements = GoSplitRegex
            .Split(sql)
            .Select(segment => segment.Trim())
            .Where(segment => !string.IsNullOrWhiteSpace(segment));

        foreach (var statement in statements)
        {
            await using var command = new SqlCommand(statement, connection);
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
