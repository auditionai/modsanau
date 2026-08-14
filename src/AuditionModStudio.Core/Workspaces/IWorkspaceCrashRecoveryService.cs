using System.Collections.Immutable;

namespace AuditionModStudio.Core.Workspaces;

public interface IWorkspaceCrashRecoveryService
{
    ImmutableArray<WorkspaceRecoveryCandidate> DetectedCandidates { get; }

    Task<WorkspaceRecoveryScanResult> DetectAsync(CancellationToken cancellationToken = default);

    ValueTask<WorkspaceRecoveryActionResult> RecoverAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);

    Task<WorkspaceRecoveryActionResult> CleanupAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);
}

public enum WorkspaceRecoveryState
{
    ActiveOrInaccessible,
    StaleRecoverable,
    StaleCleanupOnly,
    Unsafe
}

public sealed record WorkspaceRecoveryCandidate(
    string WorkspaceId,
    WorkspaceRecoveryState State,
    bool Retained,
    int? PreviousProcessId,
    DateTimeOffset? CreatedAtUtc,
    bool CanRecover,
    bool CanCleanup,
    string DiagnosticCode);

public sealed record WorkspaceRecoveryScanResult(
    bool Succeeded,
    bool Cancelled,
    string? DiagnosticCode,
    ImmutableArray<WorkspaceRecoveryCandidate> Candidates)
{
    public static WorkspaceRecoveryScanResult Success(ImmutableArray<WorkspaceRecoveryCandidate> candidates) =>
        new(true, false, null, candidates);

    public static WorkspaceRecoveryScanResult Failure(string code, bool cancelled = false) =>
        new(false, cancelled, code, []);
}

public enum WorkspaceRecoveryActionFailureReason
{
    None,
    InvalidWorkspaceId,
    NotFound,
    ActiveOrInaccessible,
    UnsafeWorkspace,
    NotRecoverable,
    CleanupFailed,
    RecoveryFailed,
    Cancelled
}

public sealed record WorkspaceRecoveryActionResult(
    bool Succeeded,
    bool Cancelled,
    WorkspaceRecoveryActionFailureReason FailureReason,
    string? DiagnosticCode,
    ISecureWorkspace? Workspace)
{
    public static WorkspaceRecoveryActionResult Success(ISecureWorkspace? workspace = null) =>
        new(true, false, WorkspaceRecoveryActionFailureReason.None, null, workspace);

    public static WorkspaceRecoveryActionResult Failure(
        WorkspaceRecoveryActionFailureReason reason,
        string code) => new(false, reason == WorkspaceRecoveryActionFailureReason.Cancelled, reason, code, null);
}
