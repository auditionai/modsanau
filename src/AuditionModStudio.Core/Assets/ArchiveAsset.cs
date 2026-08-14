namespace AuditionModStudio.Core.Assets;

public record ArchiveAsset(
    string RelativePath,
    string FileName,
    string Extension,
    string DirectoryRelativePath,
    long FileSize,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256,
    ArchiveAssetKind AssetKind)
{
    public string Identity => RelativePath;
}

public sealed record TextureAsset(
    string RelativePath,
    string FileName,
    string Extension,
    string DirectoryRelativePath,
    long FileSize,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256)
    : ArchiveAsset(
        RelativePath,
        FileName,
        Extension,
        DirectoryRelativePath,
        FileSize,
        LastWriteTimeUtc,
        Sha256,
        ArchiveAssetKind.Dds);
