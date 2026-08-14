namespace AuditionModStudio.Core.Assets;

public enum ArchiveAssetScanFailureReason
{
    None,
    ExtractedDirectoryMissing,
    InvalidExtractedRoot,
    AccessDenied,
    ReparsePointRejected,
    FileReadFailed,
    DuplicateIdentity,
    Cancelled,
}
