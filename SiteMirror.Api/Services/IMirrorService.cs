using SiteMirror.Api.Models;

namespace SiteMirror.Api.Services;

public interface ISiteMirrorService
{
    Task<MirrorResult> MirrorAsync(MirrorRequest request, Guid? actingUserId, CancellationToken cancellationToken = default);

    Task<RewriteLinksResult> RewriteLinksAsync(RewriteLinksRequest request, CancellationToken cancellationToken = default);

    Task<CrawlStatusResult?> GetCrawlStatusAsync(string crawlId, CancellationToken cancellationToken = default);

    Task<UpdateTranslationsResult> UpdateTranslationsAsync(UpdateTranslationsRequest request, CancellationToken cancellationToken = default);

    Task<UpdateBlockTranslationsResult> UpdateBlockTranslationsAsync(UpdateBlockTranslationsRequest request, CancellationToken cancellationToken = default);
    Task<ApplyCommonBlockTranslationsResult> ApplyCommonBlockTranslationsAsync(
        string siteHost,
        string version,
        string language,
        Stream fileStream,
        CancellationToken cancellationToken = default);
    Task<ApplyCommonBlockTranslationsResult> UpdateCommonBlockTranslationsAsync(
        UpdateCommonBlockTranslationsRequest request,
        CancellationToken cancellationToken = default);

    Task<CreateInjectionAssetResult> CreateInjectionAssetAsync(CreateInjectionAssetRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InjectionAssetDto>> GetInjectionAssetsAsync(string siteHost, string version, CancellationToken cancellationToken = default);
    Task<InjectionAssetDto?> GetInjectionAssetAsync(string assetId, CancellationToken cancellationToken = default);
    Task<InjectionAssetDto> UpdateInjectionAssetAsync(string assetId, UpdateInjectionAssetRequest request, CancellationToken cancellationToken = default);
    Task DeleteInjectionAssetAsync(string assetId, CancellationToken cancellationToken = default);

    Task<FixPageLinksResult> FixPageLinksAsync(FixPageLinksRequest request, CancellationToken cancellationToken = default);

    Task<BlockTranslationFlatMergeResponse> MergeFlatBlockTranslationsAsync(
        BlockTranslationFlatMergeRequest request,
        CancellationToken cancellationToken = default);

    Task<BlockPageToFlatResponse> BlockPageToFlatAsync(
        BlockPageToFlatRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListMirrorBlockCatalogHostsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListMirrorBlockCatalogVersionsAsync(string siteHost, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListMirrorBlockCatalogPagePathsAsync(
        string siteHost,
        string version,
        CancellationToken cancellationToken = default);

    Task<BlockPageToFlatBatchResponse> BlockPagesToFlatBatchAsync(
        BlockPagesToFlatBatchRequest request,
        CancellationToken cancellationToken = default);

    Task<MirrorStorageAnalyzeResult> AnalyzeStorageReachabilityAsync(
        MirrorStorageAnalyzeRequest request,
        CancellationToken cancellationToken = default);

    Task<RebuildLocalizationResponse> RebuildLocalizationAsync(
        RebuildLocalizationRequest request,
        CancellationToken cancellationToken = default);
}
