using System.Security.Cryptography;
using System.Text;

namespace SiteMirror.Api.Services.Mirroring;

internal sealed class MirrorPathHelper
{
    private readonly Uri _rootUri;

    public MirrorPathHelper(Uri rootUri)
    {
        _rootUri = rootUri;
    }

    public string MapUriToRelativePath(Uri resourceUri, string? mediaType)
    {
        var normalizedPath = resourceUri.AbsolutePath;
        if (string.IsNullOrWhiteSpace(normalizedPath) || normalizedPath.EndsWith('/'))
        {
            normalizedPath += "index.html";
        }

        var fileName = Path.GetFileName(normalizedPath);
        if (!Path.HasExtension(fileName))
        {
            normalizedPath += GuessExtensionFromMediaType(mediaType, ".html");
        }

        var extension = Path.GetExtension(normalizedPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = GuessExtensionFromMediaType(mediaType, ".bin");
            normalizedPath += extension;
        }

        var query = resourceUri.Query;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(query))).ToLowerInvariant()[..8];
            var dir = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(normalizedPath);
            var ext = Path.GetExtension(normalizedPath);
            var merged = $"{baseName}-{hash}{ext}";
            normalizedPath = string.IsNullOrWhiteSpace(dir) ? merged : $"{dir}/{merged}";
        }

        return normalizedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
    }

    public Uri ResolveFileUri(string relativePath, IReadOnlyDictionary<string, Uri> htmlSourceByRelativePath)
    {
        if (htmlSourceByRelativePath.TryGetValue(relativePath, out var sourceUri))
        {
            return sourceUri;
        }

        var withSlashes = relativePath.Replace('\\', '/').TrimStart('/');
        return new Uri($"{_rootUri.Scheme}://{_rootUri.Host}/{withSlashes}", UriKind.Absolute);
    }

    public static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) ? '-' : c);
        }

        return builder.ToString();
    }

    public static bool IsHttpOrHttps(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    public string NormalizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    public static string ParseMediaType(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("content-type", out var value))
        {
            var pair = headers.FirstOrDefault(kv => string.Equals(kv.Key, "content-type", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return string.Empty;
            }

            value = pair.Value;
        }

        var semicolonIndex = value.IndexOf(';');
        return semicolonIndex < 0 ? value : value[..semicolonIndex];
    }

    private static string GuessExtensionFromMediaType(string? mediaType, string defaultExtension)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            var m when m is not null && m.Contains("text/html", StringComparison.Ordinal) => ".html",
            var m when m is not null && m.Contains("text/css", StringComparison.Ordinal) => ".css",
            var m when m is not null && m.Contains("javascript", StringComparison.Ordinal) => ".js",
            var m when m is not null && m.Contains("application/json", StringComparison.Ordinal) => ".json",
            var m when m is not null && m.Contains("image/png", StringComparison.Ordinal) => ".png",
            var m when m is not null && m.Contains("image/jpeg", StringComparison.Ordinal) => ".jpg",
            var m when m is not null && m.Contains("image/webp", StringComparison.Ordinal) => ".webp",
            var m when m is not null && m.Contains("image/svg+xml", StringComparison.Ordinal) => ".svg",
            var m when m is not null && m.Contains("font/woff2", StringComparison.Ordinal) => ".woff2",
            var m when m is not null && m.Contains("font/woff", StringComparison.Ordinal) => ".woff",
            var m when m is not null && m.Contains("font/ttf", StringComparison.Ordinal) => ".ttf",
            _ => defaultExtension
        };
    }
}
