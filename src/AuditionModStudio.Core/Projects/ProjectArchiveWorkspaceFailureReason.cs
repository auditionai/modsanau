namespace AuditionModStudio.Core.Projects;

public enum ProjectArchiveWorkspaceFailureReason
{
    None,
    InvalidRequest,
    SourceMissing,
    SourceHashMismatch,
    WorkspaceCreationFailed,
    WorkingCopyFailed,
    ManifestWriteFailed,
    Cancelled,
}
