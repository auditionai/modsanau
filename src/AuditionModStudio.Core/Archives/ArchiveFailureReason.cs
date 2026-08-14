namespace AuditionModStudio.Core.Archives;

public enum ArchiveFailureReason
{
    None,
    InvalidArchive,
    InvalidWorkspace,
    UnsupportedEngine,
    ToolProvisioningFailed,
    ToolIntegrityFailed,
    KeydatInvalid,
    RunnerFailed,
    Timeout,
    Cancelled,
    ArtifactValidationFailed,
}
