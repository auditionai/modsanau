namespace AuditionModStudio.Archives;

public sealed record ArchiveToolProvisioningResult(
    string ToolId,
    string WorkingCopyPath,
    string Sha256,
    string? ObservedFileVersion,
    bool ReusedExistingCopy);
