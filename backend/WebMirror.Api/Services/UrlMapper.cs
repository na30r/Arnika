namespace WebMirror.Api.Services;

public sealed class UrlMapper : IUrlMapper
{
    public string MapToLocalRoute(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeSegment);
        var path = string.Join('/', segments);
        return string.IsNullOrWhiteSpace(path)
            ? $"/mirror/{host}"
            : $"/mirror/{host}/{path}";
    }

    public string MapToLocalAssetPath(Uri assetUri)
    {
        var host = assetUri.Host.ToLowerInvariant();
        var rawPath = assetUri.AbsolutePath.Trim('/');
        var extension = Path.GetExtension(rawPath);
        var hash = ComputeHash(assetUri.AbsoluteUri);

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        var fileName = $"{hash[..20]}{extension}";
        return $"/mirror/{host}/assets/{fileName}";
    }

    private static string SanitizeSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "index";
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(input.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
