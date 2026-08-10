namespace AuditionModStudio.Core.Projects;

public enum ProjectArchiveWorkspaceValidationStatus
{
    Valid,
    ManifestMissing,
    ManifestCorrupt,
    ManifestMismatch,
    WorkingArchiveMissing,
    ExtractedDirectoryMissing,
    BuildOutputDirectoryMissing,
    WorkingArchiveHashMismatch,
    PathInvalid,
}
