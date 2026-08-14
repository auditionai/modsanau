using System.Collections.Immutable;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Core.Exports;

public enum BatchDestinationConflictPolicy
{
    RejectDuplicates,
    SerializeConflicts
}

public enum BatchBuildExportJobState
{
    Queued,
    Validating,
    Building,
    Exporting,
    Succeeded,
    Failed,
    Cancelled
}

public enum BatchBuildExportFailureReason
{
    None,
    InvalidBatch,
    InvalidJob,
    DestinationInvalid,
    DestinationConflict,
    QueueRejected,
    BuildFailed,
    ExportFailed,
    UnexpectedFailure,
    Cancelled
}

public sealed record BatchBuildExportJobRequest(
    Guid JobId,
    AuditionProject Project,
    IProjectArchiveWorkspace Workspace,
    string OutputDirectory,
    ArchiveExportOverwritePolicy OverwritePolicy);

public sealed record BatchBuildExportRequest(
    IReadOnlyList<BatchBuildExportJobRequest> Jobs,
    BatchDestinationConflictPolicy ConflictPolicy);

public sealed record BatchBuildExportJobProgress(
    Guid JobId,
    int JobIndex,
    BatchBuildExportJobState State,
    int CompletedUnits,
    int TotalUnits,
    string DiagnosticCode);

public sealed record BatchBuildExportJobResult(
    Guid JobId,
    int JobIndex,
    BatchBuildExportJobState State,
    BatchBuildExportFailureReason FailureReason,
    string DiagnosticCode,
    string? OutputFileName,
    string? OutputPath,
    long Size,
    Sha256Digest? Sha256,
    AuditionProject? Project)
{
    public bool Succeeded => State == BatchBuildExportJobState.Succeeded;
    public bool Cancelled => State == BatchBuildExportJobState.Cancelled;

    public static BatchBuildExportJobResult Success(
        Guid jobId,
        int jobIndex,
        string outputFileName,
        ArchiveExportResult export,
        AuditionProject project) => new(
            jobId,
            jobIndex,
            BatchBuildExportJobState.Succeeded,
            BatchBuildExportFailureReason.None,
            "BATCH_BUILD_EXPORT_JOB_COMPLETED",
            outputFileName,
            export.Destination?.FullPath,
            export.Size,
            export.Sha256,
            project);

    public static BatchBuildExportJobResult Failure(
        Guid jobId,
        int jobIndex,
        BatchBuildExportFailureReason reason,
        string diagnosticCode,
        string? outputFileName = null) => new(
            jobId,
            jobIndex,
            BatchBuildExportJobState.Failed,
            reason,
            diagnosticCode,
            outputFileName,
            null,
            0,
            null,
            null);

    public static BatchBuildExportJobResult CancelledResult(
        Guid jobId,
        int jobIndex,
        string? outputFileName = null) => new(
            jobId,
            jobIndex,
            BatchBuildExportJobState.Cancelled,
            BatchBuildExportFailureReason.Cancelled,
            "BATCH_BUILD_EXPORT_JOB_CANCELLED",
            outputFileName,
            null,
            0,
            null,
            null);
}

public sealed record BatchBuildExportResult(
    bool Accepted,
    bool Cancelled,
    string DiagnosticCode,
    ImmutableArray<BatchBuildExportJobResult> Jobs)
{
    public int SucceededCount => Jobs.Count(job => job.Succeeded);
    public int FailedCount => Jobs.Count(job => job.State == BatchBuildExportJobState.Failed);
    public int CancelledCount => Jobs.Count(job => job.Cancelled);

    public static BatchBuildExportResult Completed(ImmutableArray<BatchBuildExportJobResult> jobs) =>
        new(true, jobs.Any(job => job.Cancelled), "BATCH_BUILD_EXPORT_COMPLETED", jobs);

    public static BatchBuildExportResult Rejected(string diagnosticCode) =>
        new(false, false, diagnosticCode, []);
}

public sealed record BatchBuildExportOptions(int MaximumConcurrency, int MaximumJobs)
{
    public static BatchBuildExportOptions Default { get; } = new(2, 256);

    public bool IsValid => MaximumConcurrency is > 0 and <= 8
                           && MaximumJobs is > 0 and <= 10_000;
}

public interface IBatchBuildExportService
{
    Task<BatchBuildExportResult> ExecuteAsync(
        BatchBuildExportRequest request,
        IProgress<BatchBuildExportJobProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
