namespace SiteMirror.Api.Services;

internal static class CrawlKeyHelper
{
    public static string NormalizeUriKey(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.ToString();
    }
}
