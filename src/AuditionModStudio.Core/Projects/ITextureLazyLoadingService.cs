using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;

namespace AuditionModStudio.Core.Projects;

public interface ITextureLazyLoadingService
{
    Task<TextureThumbnailLoadResult> LoadThumbnailAsync(
        TextureThumbnailLoadRequest request,
        CancellationToken cancellationToken = default);

    Task<SelectedTextureLoadResult> LoadSelectedTextureAsync(
        SelectedTextureLoadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TextureThumbnailLoadRequest(
    IProjectArchiveWorkspace Workspace,
    TextureAsset Asset,
    int MaximumDimension);

public sealed record SelectedTextureLoadRequest(
    IProjectArchiveWorkspace Workspace,
    TextureAsset Asset,
    DdsMetadata ExpectedMetadata);

public enum TextureLazyLoadFailureReason
{
    None,
    InvalidRequest,
    ThumbnailFailed,
    PreviewFailed,
    MetadataChanged,
    ImageImportFailed,
    Cancelled
}

public sealed record TextureThumbnailLoadResult(
    bool Succeeded,
    bool Cancelled,
    TextureLazyLoadFailureReason FailureReason,
    string? DiagnosticCode,
    InternalImage? Image,
    ThumbnailCacheSource CacheSource)
{
    public static TextureThumbnailLoadResult Success(InternalImage image, ThumbnailCacheSource cacheSource) =>
        new(true, false, TextureLazyLoadFailureReason.None, null, image, cacheSource);

    public static TextureThumbnailLoadResult Failure(TextureLazyLoadFailureReason reason, string code) =>
        new(false, reason == TextureLazyLoadFailureReason.Cancelled, reason, code, null, ThumbnailCacheSource.None);
}

public sealed record SelectedTextureLoadResult(
    bool Succeeded,
    bool Cancelled,
    TextureLazyLoadFailureReason FailureReason,
    string? DiagnosticCode,
    InternalImage? Image)
{
    public static SelectedTextureLoadResult Success(InternalImage image) =>
        new(true, false, TextureLazyLoadFailureReason.None, null, image);

    public static SelectedTextureLoadResult Failure(TextureLazyLoadFailureReason reason, string code) =>
        new(false, reason == TextureLazyLoadFailureReason.Cancelled, reason, code, null);
}
