using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Projects;

public interface IProjectResetService
{
    Task<ProjectResetResult> ResetTextureAsync(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        ModRelativePath texturePath,
        IProgress<ProjectResetProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ProjectResetResult> ResetProjectAsync(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        IProgress<ProjectResetProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum ProjectResetPhase
{
    ValidatingProject,
    ResolvingTemplate,
    AcquiringPristineTemplate,
    CreatingFreshWorkspace,
    ExtractingPristineArchive,
    RestoringTexture,
    ScanningTextures,
    SavingProject,
    UpdatingMetadataCache,
    RemovingPreviousWorkspace,
    Completed
}

public sealed record ProjectResetProgress(ProjectResetPhase Phase, int CompletedSteps, int TotalSteps);

public enum ProjectResetFailureReason
{
    None,
    InvalidRequest,
    WorkspaceMismatch,
    TextureNotEdited,
    TemplateMissing,
    TemplateMismatch,
    EntitlementDenied,
    EntitlementUnavailable,
    TemplateAcquisitionFailed,
    RegionProfileMissing,
    WorkspaceCreationFailed,
    ArchiveExtractionFailed,
    TextureRestoreFailed,
    TextureScanFailed,
    ProjectModelInvalid,
    ProjectSaveFailed,
    RollbackFailed,
    Cancelled
}

public sealed record ProjectResetResult(
    bool Succeeded,
    ProjectResetFailureReason FailureReason,
    string? DiagnosticCode,
    AuditionProject? Project,
    IProjectArchiveWorkspace? Workspace,
    bool MetadataCacheRecoveryRequired,
    bool PreviousWorkspaceCleanupPending,
    bool TemporaryCleanupPending)
{
    public static ProjectResetResult Success(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        bool metadataCacheRecoveryRequired,
        bool previousWorkspaceCleanupPending,
        bool temporaryCleanupPending = false) =>
        new(true, ProjectResetFailureReason.None, null, project, workspace,
            metadataCacheRecoveryRequired, previousWorkspaceCleanupPending, temporaryCleanupPending);

    public static ProjectResetResult Failure(ProjectResetFailureReason reason, string code) =>
        new(false, reason, code, null, null, false, false, false);
}

public interface IProjectTextureRestoreService
{
    Task<ProjectTextureRestoreResult> BeginRestoreAsync(
        IProjectArchiveWorkspace targetWorkspace,
        IProjectArchiveWorkspace pristineWorkspace,
        ModRelativePath texturePath,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectTextureRestoreResult(
    bool Succeeded,
    string? DiagnosticCode,
    IProjectTextureRestoreTransaction? Transaction)
{
    public static ProjectTextureRestoreResult Success(IProjectTextureRestoreTransaction transaction) =>
        new(true, null, transaction);

    public static ProjectTextureRestoreResult Failure(string code) => new(false, code, null);
}

public interface IProjectTextureRestoreTransaction : IAsyncDisposable
{
    bool CleanupPending { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public interface IProjectArchiveWorkspaceRemovalService
{
    Task<bool> RemoveAsync(
        IProjectArchiveWorkspace workspace,
        CancellationToken cancellationToken = default);
}
