namespace WebMirror.Api.Migrations;

public interface IMigrationRunner
{
    Task ApplyPendingMigrationsAsync(CancellationToken cancellationToken);
}
