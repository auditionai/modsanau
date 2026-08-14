namespace AuditionModStudio.Core.Assets;

public sealed record ArchiveAssetScanProgress(
    int FilesDiscovered,
    int ProcessedCount,
    string? CurrentRelativePath);
