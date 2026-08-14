using System.Collections.Immutable;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Projects;

public enum TextureBatchOutcomeStatus
{
    Changed,
    Failed,
    Skipped
}

public sealed record TextureBatchOutcome(
    ModRelativePath RelativePath,
    TextureBatchOutcomeStatus Status,
    string DiagnosticCode);

public sealed record TextureBatchBuildSummary(
    ImmutableArray<TextureBatchOutcome> Textures,
    int ChangedCount,
    int FailedCount,
    int SkippedCount);

public enum TextureBatchBuildFailureReason
{
    None,
    InvalidRequest,
    OutcomeMismatch,
    NoApprovedChanges,
    BuildFailed,
    Cancelled
}

public sealed record TextureBatchBuildRequest(
    AuditionProject Project,
    IProjectArchiveWorkspace Workspace,
    IReadOnlyCollection<TextureBatchOutcome> Outcomes);

public sealed record TextureBatchBuildResult(
    bool Succeeded,
    bool Cancelled,
    TextureBatchBuildFailureReason FailureReason,
    string DiagnosticCode,
    TextureBatchBuildSummary? Summary,
    ProjectBuildResult? Build)
{
    public static TextureBatchBuildResult Success(
        TextureBatchBuildSummary summary,
        ProjectBuildResult build) =>
        new(true, false, TextureBatchBuildFailureReason.None,
            "TEXTURE_BATCH_BUILD_COMPLETED", summary, build);

    public static TextureBatchBuildResult Failure(
        TextureBatchBuildFailureReason reason,
        string code,
        TextureBatchBuildSummary? summary = null,
        ProjectBuildResult? build = null) =>
        new(false, false, reason, code, summary, build);

    public static TextureBatchBuildResult CancelledResult(
        TextureBatchBuildSummary summary,
        ProjectBuildResult? build = null) =>
        new(false, true, TextureBatchBuildFailureReason.Cancelled,
            "TEXTURE_BATCH_BUILD_CANCELLED", summary, build);
}

public interface ITextureBatchBuildSummaryService
{
    Task<TextureBatchBuildResult> BuildAsync(
        TextureBatchBuildRequest request,
        IProgress<ProjectBuildProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
