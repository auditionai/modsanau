using System.Collections.Immutable;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class SmartModScanService(
    IGameCatalog gameCatalog,
    IModCatalog modCatalog,
    ITextureManifestCatalog manifestCatalog,
    IArchiveAssetScanner assetScanner,
    IDdsMetadataReader metadataReader,
    IThumbnailCache thumbnailCache,
    IPathSecurity pathSecurity) : ISmartModScanService
{
    public async Task<SmartModScanResult> ScanAsync(
        SmartModScanRequest request,
        IProgress<SmartModScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.GameId.IsValid || request.Workspace is null
            || request.MaximumThumbnailDimension is <= 0 or > 1_024)
        {
            return Failure(SmartModScanFailureReason.InvalidRequest, "SMART_SCAN_REQUEST_INVALID");
        }

        if (!gameCatalog.TryGetGame(request.GameId, out _))
        {
            return Failure(SmartModScanFailureReason.UnknownGame, "SMART_SCAN_GAME_UNKNOWN");
        }

        if (!request.ModId.IsValid || !modCatalog.TryGetMod(request.GameId, request.ModId, out _))
        {
            return Failure(SmartModScanFailureReason.UnknownMod, "SMART_SCAN_MOD_UNKNOWN");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(SmartModScanPhase.ScanningAssets, 0, 0, null));
            var scanResult = await assetScanner.ScanAsync(
                request.Workspace,
                progress is null ? null : new InlineProgress<ArchiveAssetScanProgress>(scan =>
                    progress.Report(new(SmartModScanPhase.ScanningAssets, scan.FilesDiscovered, scan.ProcessedCount, scan.CurrentRelativePath))),
                cancellationToken).ConfigureAwait(false);
            if (!scanResult.IsSuccess)
            {
                return scanResult.FailureReason == ArchiveAssetScanFailureReason.Cancelled
                    ? Failure(SmartModScanFailureReason.Cancelled, "SMART_SCAN_CANCELLED")
                    : Failure(SmartModScanFailureReason.AssetScanFailed, scanResult.ErrorCode ?? "SMART_SCAN_ASSET_SCAN_FAILED");
            }

            var observedCatalog = scanResult.Catalog!;
            var textures = observedCatalog.Assets
                .OfType<TextureAsset>()
                .OrderBy(asset => asset.RelativePath, StringComparer.Ordinal)
                .ToArray();
            var smartTextures = ImmutableArray.CreateBuilder<SmartTextureAsset>(textures.Length);
            var mappedSlotIds = new HashSet<TextureSlotId>();
            var mappedAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extractedRoot = ResolveExtractedRoot(request);

            for (var index = 0; index < textures.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = textures[index];
                var fullPath = pathSecurity.ResolvePathWithinRoot(extractedRoot, asset.RelativePath);
                pathSecurity.EnsureNoReparsePoints(extractedRoot, fullPath);

                progress?.Report(new(SmartModScanPhase.ReadingMetadata, textures.Length, index, asset.RelativePath));
                var metadata = await metadataReader.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (!metadata.IsSuccess)
                {
                    return metadata.FailureReason == DdsMetadataFailureReason.Cancelled
                        ? Failure(SmartModScanFailureReason.Cancelled, "SMART_SCAN_CANCELLED", asset.RelativePath)
                        : Failure(SmartModScanFailureReason.DdsMetadataReadFailed,
                            metadata.ErrorCode ?? "SMART_SCAN_DDS_METADATA_FAILED", asset.RelativePath);
                }

                progress?.Report(new(SmartModScanPhase.GeneratingThumbnail, textures.Length, index, asset.RelativePath));
                var thumbnail = await thumbnailCache.GetOrCreateAsync(
                    new(request.Workspace, new(asset.RelativePath), new(asset.Sha256),
                        request.MaximumThumbnailDimension),
                    cancellationToken).ConfigureAwait(false);
                if (!thumbnail.Succeeded)
                {
                    return thumbnail.Cancelled
                        ? Failure(SmartModScanFailureReason.Cancelled, "SMART_SCAN_CANCELLED", asset.RelativePath)
                        : Failure(SmartModScanFailureReason.ThumbnailGenerationFailed,
                            thumbnail.DiagnosticCode!, asset.RelativePath);
                }

                progress?.Report(new(SmartModScanPhase.ResolvingManifest, textures.Length, index, asset.RelativePath));
                var resolution = manifestCatalog.Resolve(request.GameId, request.ModId, new(asset.RelativePath));
                if (resolution.Slot is not null
                    && (!mappedSlotIds.Add(resolution.Slot.Id) || !mappedAssetPaths.Add(asset.RelativePath)))
                {
                    return Failure(SmartModScanFailureReason.AmbiguousMapping,
                        "SMART_SCAN_MAPPING_AMBIGUOUS", asset.RelativePath);
                }

                var unknown = resolution.Slot is null;
                smartTextures.Add(new(asset, metadata.Metadata!, thumbnail.Image!, resolution, unknown, unknown));
                progress?.Report(new(SmartModScanPhase.ResolvingManifest, textures.Length, index + 1, asset.RelativePath));
            }

            var groups = smartTextures
                .GroupBy(item => item.Asset.DirectoryRelativePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new SmartTextureGroup(
                    group.Key,
                    group.OrderBy(item => item.Asset.RelativePath, StringComparer.Ordinal).ToImmutableArray()))
                .ToImmutableArray();
            var missing = manifestCatalog.TryGetManifest(request.GameId, request.ModId, out var manifest)
                ? manifest.Slots.Where(slot => !mappedSlotIds.Contains(slot.Id)).ToImmutableArray()
                : [];
            progress?.Report(new(SmartModScanPhase.Completed, textures.Length, textures.Length, null));
            return SmartModScanResult.Success(observedCatalog, groups, missing);
        }
        catch (OperationCanceledException)
        {
            return Failure(SmartModScanFailureReason.Cancelled, "SMART_SCAN_CANCELLED");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Failure(SmartModScanFailureReason.AssetScanFailed, "SMART_SCAN_PATH_REJECTED");
        }
    }

    private string ResolveExtractedRoot(SmartModScanRequest request)
    {
        var workspace = request.Workspace.ArchiveWorkspace;
        var root = pathSecurity.ResolvePathWithinRoot(
            workspace.SecureWorkspace.Paths.ExtractedDirectory,
            workspace.ExtractDirectoryRelativePath);
        pathSecurity.EnsureNoReparsePoints(workspace.SecureWorkspace.Paths.ExtractedDirectory, root);
        return root;
    }

    private static SmartModScanResult Failure(
        SmartModScanFailureReason reason,
        string diagnosticCode,
        string? path = null) => SmartModScanResult.Failure(reason, diagnosticCode, path);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
