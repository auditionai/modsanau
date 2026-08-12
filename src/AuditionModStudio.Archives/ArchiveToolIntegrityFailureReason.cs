namespace AuditionModStudio.Archives;

public enum ArchiveToolIntegrityFailureReason
{
    None,
    ExecutableMissing,
    OutsideTrustedLocation,
    ReparsePointRejected,
    FilenameMismatch,
    HashMismatch,
    UnapprovedTool,
    InvalidManifest,
    UnexpectedCompanion,
}
