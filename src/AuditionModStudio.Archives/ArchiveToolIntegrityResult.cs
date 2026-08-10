namespace AuditionModStudio.Archives;

public sealed record ArchiveToolIntegrityResult(
    bool Approved,
    ArchiveToolIntegrityFailureReason FailureReason,
    string ToolId,
    string? ExpectedSha256,
    string? ActualSha256,
    string? ObservedFileVersion);
