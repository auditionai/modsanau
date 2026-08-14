using System.Collections.Immutable;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace AuditionModStudio.Projects;

public sealed class BatchBuildExportService(
    IArchiveExportDestinationValidator destinationValidator,
    IProjectBuildService buildService,
    IArchiveExportService exportService,
    IBackgroundTaskManager taskManager,
    BatchBuildExportOptions options,
    ILogger<BatchBuildExportService> logger) : IBatchBuildExportService
{
    public async Task<BatchBuildExportResult> ExecuteAsync(
        BatchBuildExportRequest request,
        IProgress<BatchBuildExportJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidBatch(request))
        {
            return BatchBuildExportResult.Rejected("BATCH_BUILD_EXPORT_REQUEST_INVALID");
        }

        var jobs = request.Jobs;
        var results = new BatchBuildExportJobResult?[jobs.Count];
        var prepared = new List<PreparedJob>(jobs.Count);
        for (var index = 0; index < jobs.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelOutstanding(jobs, results, progress);
                return BatchBuildExportResult.Completed(results.Select(result => result!).ToImmutableArray());
            }

            var job = jobs[index];
            Report(progress, job.JobId, index, BatchBuildExportJobState.Queued, 0,
                "BATCH_BUILD_EXPORT_JOB_QUEUED");
            Report(progress, job.JobId, index, BatchBuildExportJobState.Validating, 5,
                "BATCH_BUILD_EXPORT_DESTINATION_VALIDATING");

            var preflight = Preflight(job, index);
            if (preflight.Result is not null)
            {
                results[index] = preflight.Result;
                ReportTerminal(progress, preflight.Result);
            }
            else
            {
                prepared.Add(preflight.Prepared!);
            }
        }

        ApplyDestinationConflictPolicy(request.ConflictPolicy, prepared, results, progress);

        using var concurrency = new SemaphoreSlim(options.MaximumConcurrency, options.MaximumConcurrency);
        var pathGates = prepared
            .Where(job => results[job.Index] is null)
            .GroupBy(job => job.Destination.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                _ => new SemaphoreSlim(1, 1),
                StringComparer.OrdinalIgnoreCase);
        try
        {
            var executions = prepared
                .Where(job => results[job.Index] is null)
                .Select(job => ExecutePreparedAsync(
                    job,
                    concurrency,
                    pathGates[job.Destination.FullPath],
                    results,
                    progress,
                    cancellationToken))
                .ToArray();
            await Task.WhenAll(executions).ConfigureAwait(false);
        }
        finally
        {
            foreach (var gate in pathGates.Values)
            {
                gate.Dispose();
            }
        }

        var completed = results
            .Select((result, index) => result ?? BatchBuildExportJobResult.Failure(
                jobs[index].JobId,
                index,
                BatchBuildExportFailureReason.InvalidJob,
                "BATCH_BUILD_EXPORT_RESULT_MISSING"))
            .ToImmutableArray();
        return BatchBuildExportResult.Completed(completed);
    }

    private static void CancelOutstanding(
        IReadOnlyList<BatchBuildExportJobRequest> jobs,
        BatchBuildExportJobResult?[] results,
        IProgress<BatchBuildExportJobProgress>? progress)
    {
        for (var index = 0; index < jobs.Count; index++)
        {
            if (results[index] is not null)
            {
                continue;
            }

            var result = BatchBuildExportJobResult.CancelledResult(jobs[index].JobId, index);
            results[index] = result;
            ReportTerminal(progress, result);
        }
    }

    private bool IsValidBatch(BatchBuildExportRequest? request) =>
        request is not null
        && request.Jobs is not null
        && request.Jobs.Count is > 0
        && request.Jobs.Count <= options.MaximumJobs
        && options.IsValid
        && Enum.IsDefined(request.ConflictPolicy)
        && request.Jobs.All(job => job is not null && job.JobId != Guid.Empty)
        && request.Jobs.Select(job => job.JobId).Distinct().Count() == request.Jobs.Count;

    private PreflightResult Preflight(BatchBuildExportJobRequest job, int index)
    {
        try
        {
            if (job.Project is null
                || job.Workspace is null
                || string.IsNullOrWhiteSpace(job.OutputDirectory)
                || !Enum.IsDefined(job.OverwritePolicy)
                || job.Workspace.Descriptor.ProjectId != job.Project.ProjectId
                || !ArchiveExportFileContract.TryCreate(
                    job.Workspace.ArchiveWorkspace.WorkingArchiveRelativePath, out var contract))
            {
                return PreflightResult.Failed(BatchBuildExportJobResult.Failure(
                    job.JobId, index, BatchBuildExportFailureReason.InvalidJob,
                    "BATCH_BUILD_EXPORT_JOB_INVALID"));
            }

            var outputFileName = $"project-{job.Project.ProjectId:N}{contract.Extension}";
            var destination = destinationValidator.Validate(new(
                job.OutputDirectory,
                outputFileName,
                contract,
                job.OverwritePolicy));
            if (!destination.Succeeded || destination.Destination is null)
            {
                return PreflightResult.Failed(BatchBuildExportJobResult.Failure(
                    job.JobId, index, BatchBuildExportFailureReason.DestinationInvalid,
                    "BATCH_BUILD_EXPORT_DESTINATION_INVALID", outputFileName));
            }

            return PreflightResult.Success(new(
                job,
                index,
                outputFileName,
                destination.Destination));
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return PreflightResult.Failed(BatchBuildExportJobResult.Failure(
                job.JobId, index, BatchBuildExportFailureReason.InvalidJob,
                "BATCH_BUILD_EXPORT_JOB_INVALID"));
        }
    }

    private static void ApplyDestinationConflictPolicy(
        BatchDestinationConflictPolicy conflictPolicy,
        IReadOnlyList<PreparedJob> prepared,
        BatchBuildExportJobResult?[] results,
        IProgress<BatchBuildExportJobProgress>? progress)
    {
        foreach (var group in prepared
                     .GroupBy(job => job.Destination.FullPath, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var conflict = conflictPolicy == BatchDestinationConflictPolicy.RejectDuplicates
                           || group.Any(job =>
                               job.Request.OverwritePolicy != ArchiveExportOverwritePolicy.ReplaceExisting);
            if (!conflict)
            {
                continue;
            }

            foreach (var job in group)
            {
                var result = BatchBuildExportJobResult.Failure(
                    job.Request.JobId,
                    job.Index,
                    BatchBuildExportFailureReason.DestinationConflict,
                    "BATCH_BUILD_EXPORT_DESTINATION_CONFLICT",
                    job.OutputFileName);
                results[job.Index] = result;
                ReportTerminal(progress, result);
            }
        }
    }

    private async Task ExecutePreparedAsync(
        PreparedJob job,
        SemaphoreSlim concurrency,
        SemaphoreSlim pathGate,
        BatchBuildExportJobResult?[] results,
        IProgress<BatchBuildExportJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        var concurrencyEntered = false;
        var pathEntered = false;
        try
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            concurrencyEntered = true;
            await pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            pathEntered = true;
            results[job.Index] = await EnqueueAndWaitAsync(job, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            results[job.Index] = BatchBuildExportJobResult.CancelledResult(
                job.Request.JobId, job.Index, job.OutputFileName);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Batch build/export job {JobId} failed with exception type {ExceptionType}",
                job.Request.JobId,
                exception.GetType().Name);
            results[job.Index] = BatchBuildExportJobResult.Failure(
                job.Request.JobId,
                job.Index,
                BatchBuildExportFailureReason.UnexpectedFailure,
                "BATCH_BUILD_EXPORT_UNEXPECTED_FAILURE",
                job.OutputFileName);
        }
        finally
        {
            if (pathEntered)
            {
                pathGate.Release();
            }

            if (concurrencyEntered)
            {
                concurrency.Release();
            }

            ReportTerminal(progress, results[job.Index]!);
        }
    }

    private async Task<BatchBuildExportJobResult> EnqueueAndWaitAsync(
        PreparedJob job,
        IProgress<BatchBuildExportJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        BatchBuildExportJobResult? operationResult = null;
        var enqueue = await taskManager.EnqueueAsync(
            new BackgroundTaskRequest(
                BackgroundTaskKind.Build,
                async (taskProgress, taskCancellationToken) =>
                {
                    var buildProgress = new InlineProgress<ProjectBuildProgress>(value =>
                    {
                        var state = value.Phase is ProjectBuildPhase.Packing
                            or ProjectBuildPhase.Verifying
                            or ProjectBuildPhase.Completed
                            ? BatchBuildExportJobState.Building
                            : BatchBuildExportJobState.Validating;
                        var units = value.Phase switch
                        {
                            ProjectBuildPhase.Packing => 40,
                            ProjectBuildPhase.Verifying => 60,
                            ProjectBuildPhase.Completed => 70,
                            _ => 15
                        };
                        Report(progress, job.Request.JobId, job.Index, state, units,
                            state == BatchBuildExportJobState.Validating
                                ? "BATCH_BUILD_EXPORT_PROJECT_VALIDATING"
                                : "BATCH_BUILD_EXPORT_BUILDING");
                        taskProgress.Report(new(units, 100, "batch.build_export.building"));
                    });
                    var build = await buildService.BuildAsync(
                        new(job.Request.Project, job.Request.Workspace),
                        buildProgress,
                        taskCancellationToken).ConfigureAwait(false);
                    if (!build.Succeeded || build.Project is null)
                    {
                        operationResult = build.Cancelled
                            ? BatchBuildExportJobResult.CancelledResult(
                                job.Request.JobId, job.Index, job.OutputFileName)
                            : BatchBuildExportJobResult.Failure(
                                job.Request.JobId,
                                job.Index,
                                BatchBuildExportFailureReason.BuildFailed,
                                "BATCH_BUILD_EXPORT_BUILD_FAILED",
                                job.OutputFileName);
                        taskCancellationToken.ThrowIfCancellationRequested();
                        return BackgroundTaskExecutionResult.Failure(operationResult.DiagnosticCode);
                    }

                    Report(progress, job.Request.JobId, job.Index, BatchBuildExportJobState.Exporting, 75,
                        "BATCH_BUILD_EXPORT_EXPORTING");
                    var exportProgress = new InlineProgress<ArchiveExportProgress>(value =>
                    {
                        var units = value.Phase switch
                        {
                            ArchiveExportPhase.Copying => 82,
                            ArchiveExportPhase.VerifyingCandidate => 90,
                            ArchiveExportPhase.Promoting => 95,
                            ArchiveExportPhase.Completed => 100,
                            _ => 76
                        };
                        Report(progress, job.Request.JobId, job.Index, BatchBuildExportJobState.Exporting, units,
                            "BATCH_BUILD_EXPORT_EXPORTING");
                        taskProgress.Report(new(units, 100, "batch.build_export.exporting"));
                    });
                    var export = await exportService.ExportAsync(
                        new(
                            build.Project,
                            job.Request.Workspace,
                            job.Destination.CanonicalDirectory,
                            job.OutputFileName,
                            job.Request.OverwritePolicy),
                        exportProgress,
                        taskCancellationToken).ConfigureAwait(false);
                    if (!export.Succeeded)
                    {
                        operationResult = export.Cancelled
                            ? BatchBuildExportJobResult.CancelledResult(
                                job.Request.JobId, job.Index, job.OutputFileName)
                            : BatchBuildExportJobResult.Failure(
                                job.Request.JobId,
                                job.Index,
                                BatchBuildExportFailureReason.ExportFailed,
                                "BATCH_BUILD_EXPORT_EXPORT_FAILED",
                                job.OutputFileName);
                        taskCancellationToken.ThrowIfCancellationRequested();
                        return BackgroundTaskExecutionResult.Failure(operationResult.DiagnosticCode);
                    }

                    operationResult = BatchBuildExportJobResult.Success(
                        job.Request.JobId,
                        job.Index,
                        job.OutputFileName,
                        export,
                        build.Project);
                    return BackgroundTaskExecutionResult.Success();
                }),
            cancellationToken).ConfigureAwait(false);
        if (!enqueue.Succeeded)
        {
            return enqueue.FailureReason == BackgroundTaskEnqueueFailureReason.Cancelled
                ? BatchBuildExportJobResult.CancelledResult(job.Request.JobId, job.Index, job.OutputFileName)
                : BatchBuildExportJobResult.Failure(
                    job.Request.JobId,
                    job.Index,
                    BatchBuildExportFailureReason.QueueRejected,
                    "BATCH_BUILD_EXPORT_QUEUE_REJECTED",
                    job.OutputFileName);
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var registration = (CancellationRegistrationState)state!;
                registration.Manager.TryCancel(registration.TaskId);
            },
            new CancellationRegistrationState(taskManager, enqueue.TaskId));
        var snapshot = await taskManager.WaitForCompletionAsync(enqueue.TaskId, CancellationToken.None)
            .ConfigureAwait(false);
        if (snapshot?.State == BackgroundTaskState.Cancelled)
        {
            return BatchBuildExportJobResult.CancelledResult(
                job.Request.JobId, job.Index, job.OutputFileName);
        }

        return operationResult ?? BatchBuildExportJobResult.Failure(
            job.Request.JobId,
            job.Index,
            BatchBuildExportFailureReason.BuildFailed,
            "BATCH_BUILD_EXPORT_BACKGROUND_FAILED",
            job.OutputFileName);
    }

    private static void Report(
        IProgress<BatchBuildExportJobProgress>? progress,
        Guid jobId,
        int jobIndex,
        BatchBuildExportJobState state,
        int completedUnits,
        string diagnosticCode) => progress?.Report(new(
            jobId,
            jobIndex,
            state,
            completedUnits,
            100,
            diagnosticCode));

    private static void ReportTerminal(
        IProgress<BatchBuildExportJobProgress>? progress,
        BatchBuildExportJobResult result) => Report(
            progress,
            result.JobId,
            result.JobIndex,
            result.State,
            100,
            result.DiagnosticCode);

    private sealed record PreparedJob(
        BatchBuildExportJobRequest Request,
        int Index,
        string OutputFileName,
        ArchiveExportDestination Destination);

    private sealed record PreflightResult(PreparedJob? Prepared, BatchBuildExportJobResult? Result)
    {
        public static PreflightResult Success(PreparedJob prepared) => new(prepared, null);
        public static PreflightResult Failed(BatchBuildExportJobResult result) => new(null, result);
    }

    private sealed record CancellationRegistrationState(
        IBackgroundTaskManager Manager,
        BackgroundTaskId TaskId);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
