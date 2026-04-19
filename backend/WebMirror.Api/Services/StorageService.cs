using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WebMirror.Api.Options;

namespace WebMirror.Api.Services;

public sealed class StorageService : IStorageService
{
    private readonly IWebHostEnvironment _environment;
    private readonly MirrorOptions _options;

    public StorageService(IWebHostEnvironment environment, IOptions<MirrorOptions> options)
    {
        _environment = environment;
        _options = options.Value;
    }

    public async Task<string> SavePageHtmlAsync(string localRoute, string html, CancellationToken cancellationToken)
    {
        var outputPath = GetPageOutputPath(localRoute);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(outputPath, html, Encoding.UTF8, cancellationToken);
        return outputPath;
    }

    public async Task<string> SaveAssetAsync(Uri sourceAssetUrl, string contentType, Stream content, CancellationToken cancellationToken)
    {
        var localAssetPath = BuildAssetPath(sourceAssetUrl, contentType);
        var fullPath = GetFrontendPath(localAssetPath.TrimStart('/'));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var output = File.Create(fullPath);
        await content.CopyToAsync(output, cancellationToken);
        return localAssetPath;
    }

    private string GetPageOutputPath(string localRoute)
    {
        var normalizedRoute = NormalizePageStorageRoute(localRoute);
        if (string.IsNullOrEmpty(normalizedRoute))
        {
            normalizedRoute = "index";
        }

        var relative = Path.Combine("mirror-data", "pages", normalizedRoute, "index.html");
        return GetFrontendPath(relative);
    }

    private static string NormalizePageStorageRoute(string localRoute)
    {
        var normalized = localRoute.Trim('/');
        if (normalized.StartsWith("mirror/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["mirror/".Length..];
        }
        else if (string.Equals(normalized, "mirror", StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Empty;
        }

        return normalized;
    }

    private string GetFrontendPath(string relativePath)
    {
        var contentRoot = _environment.ContentRootPath;
        var frontendRoot = Path.GetFullPath(Path.Combine(contentRoot, _options.FrontendRoot));
        return Path.GetFullPath(Path.Combine(frontendRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string BuildAssetPath(Uri assetUri, string contentType)
    {
        var extension = GuessExtension(assetUri, contentType);
        var hashInput = assetUri.AbsoluteUri.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();
        var fileName = $"{hash[..16]}{extension}";
        return $"/mirror/{assetUri.Host}/assets/{fileName}";
    }

    private static string GuessExtension(Uri assetUri, string contentType)
    {
        var fromPath = Path.GetExtension(assetUri.AbsolutePath);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        return contentType.ToLowerInvariant() switch
        {
            var x when x.Contains("javascript", StringComparison.Ordinal) => ".js",
            var x when x.Contains("css", StringComparison.Ordinal) => ".css",
            var x when x.Contains("png", StringComparison.Ordinal) => ".png",
            var x when x.Contains("jpeg", StringComparison.Ordinal) || x.Contains("jpg", StringComparison.Ordinal) => ".jpg",
            var x when x.Contains("svg", StringComparison.Ordinal) => ".svg",
            var x when x.Contains("woff2", StringComparison.Ordinal) => ".woff2",
            var x when x.Contains("woff", StringComparison.Ordinal) => ".woff",
            var x when x.Contains("ttf", StringComparison.Ordinal) => ".ttf",
            _ => ".bin"
        };
    }
}
