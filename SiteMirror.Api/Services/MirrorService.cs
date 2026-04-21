using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services.Mirroring;

namespace SiteMirror.Api.Services;

public sealed class MirrorService : ISiteMirrorService
{
    private readonly MirrorSettings _settings;

    public MirrorService(IOptions<MirrorSettings> options)
    {
        _settings = options.Value;
    }

    public async Task<MirrorResult> MirrorAsync(MirrorRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var startUri))
        {
            throw new ArgumentException("Invalid URL.", nameof(request.Url));
        }

        var waitMs = request.ExtraWaitMs <= 0 ? 4_000 : request.ExtraWaitMs;
        var outputRoot = string.IsNullOrWhiteSpace(_settings.OutputFolder) ? "mirror-output" : _settings.OutputFolder;
        var chromiumExecutablePath = string.IsNullOrWhiteSpace(_settings.ChromiumExecutablePath)
            ? null
            : Path.GetFullPath(_settings.ChromiumExecutablePath);

        if (!string.IsNullOrWhiteSpace(chromiumExecutablePath) && !File.Exists(chromiumExecutablePath))
        {
            throw new FileNotFoundException($"Chromium executable not found: {chromiumExecutablePath}", chromiumExecutablePath);
        }

        Directory.CreateDirectory(outputRoot);

        var runStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var siteFolderName = $"{SanitizeFileName(startUri.Host)}-{runStamp}";
        var siteOutputPath = Path.GetFullPath(Path.Combine(outputRoot, siteFolderName));
        Directory.CreateDirectory(siteOutputPath);

        if (string.IsNullOrWhiteSpace(chromiumExecutablePath))
        {
            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = chromiumExecutablePath
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });

        var page = await context.NewPageAsync();
        var mirror = new MirrorState(startUri, siteOutputPath);
        var responseTasks = new ConcurrentBag<Task>();

        EventHandler<IResponse> responseHandler = (_, response) =>
        {
            responseTasks.Add(PersistResponseAsync(response, mirror));
        };
        page.Response += responseHandler;

        string renderedHtml;
        try
        {
            await page.GotoAsync(startUri.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 120_000
            });

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(waitMs);
            renderedHtml = await page.ContentAsync();
        }
        finally
        {
            page.Response -= responseHandler;
        }

        await WaitForPendingResponseSavesAsync(responseTasks, cancellationToken);
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var finalUri))
        {
            finalUri = startUri;
        }

        await mirror.SaveRenderedDocumentAsync(finalUri, renderedHtml);
        await mirror.DownloadLinkedResourcesAsync(cancellationToken);
        await mirror.RewriteHtmlDocumentsAsync(cancellationToken);

        var entryFilePath = mirror.GetEntryFile(finalUri);
        var relativeEntry = Path.GetRelativePath(siteOutputPath, entryFilePath).Replace('\\', '/');

        return new MirrorResult
        {
            SourceUrl = startUri.ToString(),
            FinalUrl = finalUri.ToString(),
            OutputFolder = siteOutputPath,
            EntryFilePath = entryFilePath,
            EntryFileRelativePath = relativeEntry,
            FilesSaved = mirror.TotalFilesWritten,
            UsedChromiumExecutablePath = chromiumExecutablePath,
            WaitMs = waitMs
        };
    }

    private static async Task PersistResponseAsync(IResponse response, MirrorState mirror)
    {
        try
        {
            if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var responseUri) || !IsHttpOrHttps(responseUri))
            {
                return;
            }

            Uri? requestUri = null;
            if (Uri.TryCreate(response.Request.Url, UriKind.Absolute, out var parsedRequestUri) && IsHttpOrHttps(parsedRequestUri))
            {
                requestUri = parsedRequestUri;
            }

            var bodyTask = response.BodyAsync();
            var completed = await Task.WhenAny(bodyTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != bodyTask)
            {
                return;
            }

            var body = await bodyTask;
            await mirror.SaveResponseAsync(responseUri, body, response.Headers, response.Status, requestUri);
        }
        catch
        {
            // Continue mirroring on individual response failures.
        }
    }

    private static async Task WaitForPendingResponseSavesAsync(ConcurrentBag<Task> responseTasks, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = responseTasks.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var allPending = Task.WhenAll(pending);
            var completed = await Task.WhenAny(allPending, Task.Delay(remaining, cancellationToken));
            if (completed != allPending)
            {
                return;
            }
        }
    }

    private static bool IsHttpOrHttps(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) ? '-' : c);
        }

        return builder.ToString();
    }
}
