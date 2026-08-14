using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class TextureLazyLoadingService(
    IThumbnailCache thumbnailCache,
    IDdsPreviewService previewService,
    IImageImportService imageImportService) : ITextureLazyLoadingService
{
    public async Task<TextureThumbnailLoadResult> LoadThumbnailAsync(
        TextureThumbnailLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidate(request?.Workspace, request?.Asset, out var relativePath, out var sourceHash)
            || request!.MaximumDimension is <= 0 or > 1_024)
        {
            return TextureThumbnailLoadResult.Failure(
                TextureLazyLoadFailureReason.InvalidRequest, "TEXTURE_THUMBNAIL_REQUEST_INVALID");
        }

        try
        {
            var result = await thumbnailCache.GetOrCreateAsync(
                new(request.Workspace, relativePath, sourceHash, request.MaximumDimension),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return TextureThumbnailLoadResult.Failure(
                    result.Cancelled ? TextureLazyLoadFailureReason.Cancelled : TextureLazyLoadFailureReason.ThumbnailFailed,
                    result.DiagnosticCode ?? "TEXTURE_THUMBNAIL_LOAD_FAILED");
            }

            return TextureThumbnailLoadResult.Success(result.Image!, result.Source);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TextureThumbnailLoadResult.Failure(
                TextureLazyLoadFailureReason.Cancelled, "TEXTURE_THUMBNAIL_LOAD_CANCELLED");
        }
    }

    public async Task<SelectedTextureLoadResult> LoadSelectedTextureAsync(
        SelectedTextureLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidate(request?.Workspace, request?.Asset, out var relativePath, out _)
            || request!.ExpectedMetadata is null)
        {
            return SelectedTextureLoadResult.Failure(
                TextureLazyLoadFailureReason.InvalidRequest, "SELECTED_TEXTURE_REQUEST_INVALID");
        }

        try
        {
            var workspace = request.Workspace.ArchiveWorkspace;
            var sourceRelativePath = Path.Combine(
                "Extracted",
                workspace.ExtractDirectoryRelativePath,
                relativePath.Value.Replace('/', Path.DirectorySeparatorChar));
            var preview = await previewService.CreateAsync(
                new(workspace.SecureWorkspace, sourceRelativePath), cancellationToken).ConfigureAwait(false);
            if (!preview.Succeeded)
            {
                return SelectedTextureLoadResult.Failure(
                    preview.Cancelled ? TextureLazyLoadFailureReason.Cancelled : TextureLazyLoadFailureReason.PreviewFailed,
                    preview.DiagnosticCode ?? "SELECTED_TEXTURE_PREVIEW_FAILED");
            }

            if (preview.Metadata != request.ExpectedMetadata)
            {
                return SelectedTextureLoadResult.Failure(
                    TextureLazyLoadFailureReason.MetadataChanged, "SELECTED_TEXTURE_METADATA_CHANGED");
            }

            var imported = await imageImportService.ImportMemoryAsync(
                new(preview.Image!.EncodedPng), cancellationToken).ConfigureAwait(false);
            if (!imported.Succeeded)
            {
                return SelectedTextureLoadResult.Failure(
                    imported.Cancelled ? TextureLazyLoadFailureReason.Cancelled : TextureLazyLoadFailureReason.ImageImportFailed,
                    imported.DiagnosticCode ?? "SELECTED_TEXTURE_IMPORT_FAILED");
            }

            return SelectedTextureLoadResult.Success(imported.Image!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SelectedTextureLoadResult.Failure(
                TextureLazyLoadFailureReason.Cancelled, "SELECTED_TEXTURE_LOAD_CANCELLED");
        }
    }

    private static bool TryValidate(
        IProjectArchiveWorkspace? workspace,
        TextureAsset? asset,
        out ModRelativePath relativePath,
        out Sha256Digest sourceHash)
    {
        relativePath = default;
        sourceHash = default;
        return workspace is not null
               && asset is not null
               && ModRelativePath.TryCreate(asset.RelativePath, out relativePath)
               && TryCreateDigest(asset.Sha256, out sourceHash);
    }

    private static bool TryCreateDigest(string? value, out Sha256Digest digest)
    {
        if (value is null || value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            digest = default;
            return false;
        }

        digest = new(value);
        return true;
    }
}
