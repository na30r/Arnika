namespace SiteMirror.Api.Logging;

public sealed record LogLineDto(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, string> Properties);
