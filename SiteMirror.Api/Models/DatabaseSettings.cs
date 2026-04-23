namespace SiteMirror.Api.Models;

public sealed class DatabaseSettings
{
    public const string SectionName = "Database";

    public string ConnectionString { get; init; } = string.Empty;
}
