using System.Collections.Immutable;

namespace AuditionModStudio.Core.Tasks;

public interface IBackgroundTaskManager
{
    event EventHandler<BackgroundTaskNotification>? Notification;

    ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(
        BackgroundTaskRequest request,
        CancellationToken cancellationToken = default);

    bool TryCancel(BackgroundTaskId taskId);

    bool TryGetSnapshot(BackgroundTaskId taskId, out BackgroundTaskSnapshot snapshot);

    ImmutableArray<BackgroundTaskSnapshot> GetSnapshots();

    Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(
        BackgroundTaskId taskId,
        CancellationToken cancellationToken = default);
}

public readonly record struct BackgroundTaskId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("N");
}

public enum BackgroundTaskKind
{
    None,
    Extract,
    Scan,
    Thumbnail,
    Resize,
    Convert,
    Build,
    Download,
    Ai
}

public enum BackgroundTaskState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record BackgroundTaskProgress(int CompletedUnits, int TotalUnits, string? StageCode)
{
    public double Fraction => TotalUnits == 0 ? 0 : (double)CompletedUnits / TotalUnits;
}

public sealed record BackgroundTaskExecutionResult(bool Succeeded, string? DiagnosticCode)
{
    public static BackgroundTaskExecutionResult Success() => new(true, null);
    public static BackgroundTaskExecutionResult Failure(string diagnosticCode) => new(false, diagnosticCode);
}

public sealed record BackgroundTaskRequest(
    BackgroundTaskKind Kind,
    Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task<BackgroundTaskExecutionResult>> Operation);

public enum BackgroundTaskEnqueueFailureReason
{
    None,
    InvalidRequest,
    QueueFull,
    ShuttingDown,
    Cancelled
}

public sealed record BackgroundTaskEnqueueResult(
    bool Succeeded,
    BackgroundTaskEnqueueFailureReason FailureReason,
    string? DiagnosticCode,
    BackgroundTaskId TaskId)
{
    public static BackgroundTaskEnqueueResult Success(BackgroundTaskId taskId) =>
        new(true, BackgroundTaskEnqueueFailureReason.None, null, taskId);

    public static BackgroundTaskEnqueueResult Failure(
        BackgroundTaskEnqueueFailureReason reason,
        string diagnosticCode) => new(false, reason, diagnosticCode, default);
}

public sealed record BackgroundTaskSnapshot(
    BackgroundTaskId TaskId,
    BackgroundTaskKind Kind,
    BackgroundTaskState State,
    BackgroundTaskProgress? Progress,
    string? DiagnosticCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record BackgroundTaskNotification(long Sequence, BackgroundTaskSnapshot Snapshot);

public sealed record BackgroundTaskManagerOptions(
    int MaximumConcurrency,
    int MaximumQueuedTasks,
    int MaximumCompletedHistory)
{
    public static BackgroundTaskManagerOptions Default { get; } = new(2, 256, 1_024);

    public bool IsValid => MaximumConcurrency is > 0 and <= 32
                           && MaximumQueuedTasks is > 0 and <= 100_000
                           && MaximumCompletedHistory is > 0 and <= 100_000;
}
