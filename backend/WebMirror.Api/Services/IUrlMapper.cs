namespace WebMirror.Api.Services;

public interface IUrlMapper
{
    string MapToLocalRoute(Uri uri);
    string MapToLocalAssetPath(Uri assetUri);
    bool IsInternalLink(Uri rootUri, Uri candidateUri);
}
