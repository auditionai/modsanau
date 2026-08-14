namespace AuditionModStudio.Core.Projects;

public interface IProjectLoadService
{
    Task<ProjectLoadResult> LoadAsync(
        Guid projectId,
        IProgress<ProjectLoadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum ProjectLoadPhase
{
    LoadingProject,
    ResolvingTemplate,
    ValidatingWorkspace,
    RecoveringWorkspace,
    ValidatingMetadataCache,
    RecoveringMetadataCache,
    SavingRecoveredProject,
    Completed
}

public sealed record ProjectLoadProgress(ProjectLoadPhase Phase, int CompletedSteps, int TotalSteps);

public enum ProjectLoadFailureReason
{
    None,
    ProjectMissing,
    ProjectCorrupt,
    ProjectInvalid,
    TemplateMissing,
    TemplateMismatch,
    EntitlementDenied,
    EntitlementUnavailable,
    TemplateAcquisitionFailed,
    RegionProfileMissing,
    WorkspaceRecoveryFailed,
    ArchiveExtractionFailed,
    MetadataRecoveryFailed,
    ProjectSaveFailed,
    Cancelled
}

public sealed record ProjectLoadResult(
    bool Succeeded,
    ProjectLoadFailureReason FailureReason,
    string? DiagnosticCode,
    AuditionProject? Project,
    IProjectArchiveWorkspace? Workspace,
    bool MetadataCacheRecovered,
    bool WorkspaceReextracted)
{
    public static ProjectLoadResult Success(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        bool metadataCacheRecovered,
        bool workspaceReextracted) =>
        new(true, ProjectLoadFailureReason.None, null, project, workspace, metadataCacheRecovered, workspaceReextracted);

    public static ProjectLoadResult Failure(ProjectLoadFailureReason reason, string code) =>
        new(false, reason, code, null, null, false, false);
}

public interface IProjectArchiveWorkspaceRecoveryService
{
    Task<ProjectArchiveWorkspaceRecoveryResult> TryRecoverAsync(
        AuditionProject project,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectArchiveWorkspaceRecoveryResult(
    bool Recovered,
    string? DiagnosticCode,
    IProjectArchiveWorkspace? Workspace)
{
    public static ProjectArchiveWorkspaceRecoveryResult Success(IProjectArchiveWorkspace workspace) =>
        new(true, null, workspace);

    public static ProjectArchiveWorkspaceRecoveryResult Unavailable(string code) =>
        new(false, code, null);
}
