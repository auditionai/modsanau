namespace AuditionModStudio.Core.Assets;

public sealed record ArchiveAssetScanResult(
    bool IsSuccess,
    ArchiveAssetCatalog? Catalog,
    ArchiveAssetScanFailureReason FailureReason,
    string? ErrorCode)
{
    public static ArchiveAssetScanResult Success(ArchiveAssetCatalog catalog) =>
        new(true, catalog, ArchiveAssetScanFailureReason.None, null);

    public static ArchiveAssetScanResult Failure(ArchiveAssetScanFailureReason reason, string errorCode) =>
        new(false, null, reason, errorCode);
}
