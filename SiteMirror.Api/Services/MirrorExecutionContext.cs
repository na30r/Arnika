using Microsoft.Playwright;

namespace SiteMirror.Api.Services;

internal sealed class MirrorExecutionContext
{
    public required IBrowserContext BrowserContext { get; init; }

    public required string SiteOutputPath { get; init; }

    public required string OutputRoot { get; init; }

    public required string SiteHost { get; init; }

    public required string Version { get; init; }

    public required string? ChromiumExecutablePath { get; init; }

    public required int WaitMs { get; init; }
}
