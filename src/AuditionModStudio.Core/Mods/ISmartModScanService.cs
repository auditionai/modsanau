using System.Collections.Immutable;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Core.Mods;

public interface ISmartModScanService
{
    Task<SmartModScanResult> ScanAsync(
        SmartModScanRequest request,
        IProgress<SmartModScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record SmartModScanRequest(
    GameId GameId,
    ModId ModId,
    IProjectArchiveWorkspace Workspace,
    int MaximumThumbnailDimension = 256);

public enum SmartModScanPhase
{
    ScanningAssets,
    ReadingMetadata,
    GeneratingThumbnail,
    ResolvingManifest,
    Completed
}

public sealed record SmartModScanProgress(
    SmartModScanPhase Phase,
    int TotalTextures,
    int CompletedTextures,
    string? CurrentRelativePath);

public sealed record SmartTextureAsset(
    TextureAsset Asset,
    DdsMetadata Metadata,
    InternalImage Thumbnail,
    TextureManifestResolution ManifestResolution,
    bool UnknownSemantics,
    bool CanBeLabeled);

public sealed record SmartTextureGroup(
    string DirectoryRelativePath,
    ImmutableArray<SmartTextureAsset> Textures);

public enum SmartModScanFailureReason
{
    None,
    InvalidRequest,
    UnknownGame,
    UnknownMod,
    AssetScanFailed,
    DdsMetadataReadFailed,
    ThumbnailGenerationFailed,
    AmbiguousMapping,
    Cancelled
}

public sealed record SmartModScanResult(
    bool Succeeded,
    bool Cancelled,
    SmartModScanFailureReason FailureReason,
    string? DiagnosticCode,
    string? FailedRelativePath,
    ArchiveAssetCatalog? ObservedCatalog,
    ImmutableArray<SmartTextureGroup> Groups,
    ImmutableArray<TextureSlot> MissingManifestSlots)
{
    public int TextureCount => Groups.Sum(group => group.Textures.Length);

    public static SmartModScanResult Success(
        ArchiveAssetCatalog observedCatalog,
        ImmutableArray<SmartTextureGroup> groups,
        ImmutableArray<TextureSlot> missingManifestSlots) =>
        new(true, false, SmartModScanFailureReason.None, null, null, observedCatalog, groups, missingManifestSlots);

    public static SmartModScanResult Failure(
        SmartModScanFailureReason reason,
        string diagnosticCode,
        string? failedRelativePath = null) =>
        new(false, reason == SmartModScanFailureReason.Cancelled, reason, diagnosticCode,
            failedRelativePath, null, [], []);
}
