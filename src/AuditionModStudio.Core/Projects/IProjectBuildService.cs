using AuditionModStudio.Core.Archives;

namespace AuditionModStudio.Core.Projects;

public enum ProjectBuildPhase
{
    Preparing,
    Validating,
    Packing,
    Verifying,
    Completed,
    Failed,
    Cancelled
}

public enum ProjectBuildFailureReason
{
    None,
    InvalidRequest,
    SaveFailed,
    ValidationFailed,
    WorkspacePreparationFailed,
    PackFailed,
    OutputVerificationFailed,
    OutputPromotionFailed,
    ProjectUpdateFailed,
    Cancelled
}

public sealed record ProjectBuildProgress(
    ProjectBuildPhase Phase,
    int ProcessedItemCount,
    string DiagnosticCode);

public sealed record ProjectBuildRequest(
    AuditionProject Project,
    IProjectArchiveWorkspace Workspace);

public sealed record ProjectBuildResult(
    bool Succeeded,
    bool Cancelled,
    ProjectBuildPhase FinalPhase,
    ProjectBuildFailureReason FailureReason,
    string DiagnosticCode,
    AuditionProject? Project,
    Mods.ModRelativePath? OutputArchiveRelativePath,
    Sha256Digest? OutputSha256,
    ProjectValidationResult? Validation)
{
    public static ProjectBuildResult Success(
        AuditionProject project,
        Mods.ModRelativePath outputPath,
        Sha256Digest outputSha256,
        ProjectValidationResult validation) =>
        new(true, false, ProjectBuildPhase.Completed, ProjectBuildFailureReason.None,
            "PROJECT_BUILD_COMPLETED", project, outputPath, outputSha256, validation);

    public static ProjectBuildResult Failure(
        ProjectBuildFailureReason reason,
        string code,
        ProjectValidationResult? validation = null) =>
        new(false, false, ProjectBuildPhase.Failed, reason, code, null, null, null, validation);

    public static ProjectBuildResult CancelledResult() =>
        new(false, true, ProjectBuildPhase.Cancelled, ProjectBuildFailureReason.Cancelled,
            "PROJECT_BUILD_CANCELLED", null, null, null, null);
}

public interface IProjectBuildService
{
    Task<ProjectBuildResult> BuildAsync(
        ProjectBuildRequest request,
        IProgress<ProjectBuildProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
