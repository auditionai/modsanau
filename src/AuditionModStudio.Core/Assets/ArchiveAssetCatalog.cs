namespace AuditionModStudio.Core.Assets;

public sealed record ArchiveAssetCatalog(
    IReadOnlyList<ArchiveAsset> Assets,
    int DirectoryCount,
    long TotalByteSize)
{
    public int TotalFileCount => Assets.Count;

    public int Count(ArchiveAssetKind kind) => Assets.Count(asset => asset.AssetKind == kind);
}
