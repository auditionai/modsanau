using AuditionModStudio.Core.Archives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Archives;

public sealed class AcvTool5ArchiveEngine(
    IArchiveToolProvisioningService provisioningService,
    IKeydatService keydatService,
    IArchiveToolRunner runner,
    IGameRegionProfileResolver regionProfiles,
    AcvTool5ArchiveEngineOptions options,
    ILogger<AcvTool5ArchiveEngine>? logger = null) : IArchiveEngine
{
    private readonly ILogger<AcvTool5ArchiveEngine> _logger = logger ?? NullLogger<AcvTool5ArchiveEngine>.Instance;

    public ArchiveEngineType EngineType => ArchiveEngineType.AcvTool5;

    public Task<ArchiveCommandResult> ExtractAsync(
        ArchiveExtractRequest request,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            ArchiveOperation.Extract,
            request.Archive,
            request.Workspace,
            request.Timeout,
            progress,
            cancellationToken);

    public Task<ArchiveCommandResult> PackAsync(
        ArchivePackRequest request,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            ArchiveOperation.Pack,
            request.Archive,
            request.Workspace,
            request.Timeout,
            progress,
            cancellationToken);

    private async Task<ArchiveCommandResult> ExecuteAsync(
        ArchiveOperation operation,
        AuditionArchiveTemplate archive,
        ArchiveWorkspace workspace,
        TimeSpan timeout,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            return Failure(operation, ArchiveFailureReason.InvalidArchive, "archive.timeout_invalid", "A finite positive timeout is required.");
        }

        if (!regionProfiles.TryResolve(archive.RegionProfileId, out var regionProfile))
        {
            return Failure(operation, ArchiveFailureReason.InvalidArchive, "archive.region_unsupported", "The archive region profile is not registered.");
        }

        ArchiveToolProvisioningResult provisionedTool;
        progress?.Report(new(operation, ArchiveOperationState.ProvisioningTool, 0));
        try
        {
            provisionedTool = await provisioningService.ProvisionAsync(
                    ArchiveToolIds.AcvTool5,
                    options.TrustedToolSourceRoot,
                    options.ToolSourceRelativePath,
                    workspace.SecureWorkspace,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(operation);
        }
        catch (ArchiveToolIntegrityException exception)
        {
            _logger.LogWarning(
                "Archive tool integrity rejected engine {EngineId} with reason {FailureReason}",
                EngineType.Id,
                exception.Result.FailureReason);
            return Failure(operation, ArchiveFailureReason.ToolIntegrityFailed, "archive.tool_integrity_failed", "The approved archive tool failed integrity validation.");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Archive tool provisioning failed for engine {EngineId}", EngineType.Id);
            return Failure(operation, ArchiveFailureReason.ToolProvisioningFailed, "archive.tool_provisioning_failed", "The approved archive tool could not be prepared.");
        }

        progress?.Report(new(operation, ArchiveOperationState.PreparingKeydat, 0));
        KeydatDescriptor keydat;
        try
        {
            keydat = keydatService.Describe(workspace.SecureWorkspace, workspace.WorkingArchiveRelativePath);
        }
        catch (Exception exception) when (exception is IOException
                                          or ArgumentException
                                          or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Workspace keydat inspection failed");
            return Failure(operation, ArchiveFailureReason.KeydatInvalid, "archive.keydat_inspection_failed", "The workspace keydat could not be inspected.");
        }

        if (keydat.Status == KeydatStatus.Invalid)
        {
            return Failure(operation, ArchiveFailureReason.KeydatInvalid, "archive.keydat_invalid", "The workspace keydat is invalid.");
        }

        var runnerProgress = new InlineProgress<AcvTool5Progress>(item =>
        {
            var semanticState = item.State switch
            {
                AcvTool5RunnerState.WaitingForCountrySelection => ArchiveOperationState.PreparingKeydat,
                AcvTool5RunnerState.Extracting => ArchiveOperationState.Extracting,
                AcvTool5RunnerState.Packing => ArchiveOperationState.Packing,
                AcvTool5RunnerState.VerifyingArtifacts => ArchiveOperationState.Verifying,
                AcvTool5RunnerState.Completed => ArchiveOperationState.Completed,
                AcvTool5RunnerState.Cancelled => ArchiveOperationState.Cancelled,
                AcvTool5RunnerState.TimedOut => ArchiveOperationState.TimedOut,
                AcvTool5RunnerState.Failed => ArchiveOperationState.Failed,
                _ => operation == ArchiveOperation.Extract
                    ? ArchiveOperationState.Extracting
                    : ArchiveOperationState.Packing,
            };
            progress?.Report(new(operation, semanticState, item.ProcessedItemCount, item.CurrentItemPath));
        });

        AcvTool5RunResult runnerResult;
        try
        {
            runnerResult = await runner.RunAsync(
                    new(
                        operation == ArchiveOperation.Extract ? AcvTool5Operation.Extract : AcvTool5Operation.Pack,
                        workspace.SecureWorkspace,
                        provisionedTool.WorkingCopyPath,
                        workspace.WorkingArchiveRelativePath,
                        workspace.ExtractDirectoryRelativePath,
                        regionProfile,
                        timeout,
                        options.MaximumDiagnosticCharacters),
                    runnerProgress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(operation);
        }
        catch (ArchiveToolIntegrityException)
        {
            return Failure(operation, ArchiveFailureReason.ToolIntegrityFailed, "archive.tool_integrity_failed", "The archive tool failed its pre-launch integrity validation.");
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Archive runner failed for {Operation}", operation);
            return Failure(operation, ArchiveFailureReason.RunnerFailed, "archive.runner_failed", "The archive engine could not complete the requested operation.");
        }

        var result = MapRunnerResult(operation, workspace, runnerResult);
        progress?.Report(new(operation, result.FinalState, result.ProcessedItemCount));
        return result;
    }

    private static ArchiveCommandResult MapRunnerResult(
        ArchiveOperation operation,
        ArchiveWorkspace workspace,
        AcvTool5RunResult runnerResult)
    {
        var count = runnerResult.Progress.Count == 0
            ? 0
            : runnerResult.Progress.Max(item => item.ProcessedItemCount);
        var diagnostics = runnerResult.Diagnostics
            .Select((message, index) => new ArchiveDiagnostic($"archive.runner.{index + 1}", message))
            .ToArray();

        if (runnerResult.State == AcvTool5RunnerState.Completed && runnerResult.Succeeded)
        {
            return new(
                operation,
                ArchiveOperationState.Completed,
                true,
                count,
                ArchiveFailureReason.None,
                operation == ArchiveOperation.Extract
                    ? workspace.ExtractDirectoryRelativePath
                    : workspace.WorkingArchiveRelativePath,
                diagnostics);
        }

        var (state, reason) = runnerResult.State switch
        {
            AcvTool5RunnerState.Cancelled => (ArchiveOperationState.Cancelled, ArchiveFailureReason.Cancelled),
            AcvTool5RunnerState.TimedOut => (ArchiveOperationState.TimedOut, ArchiveFailureReason.Timeout),
            _ when runnerResult.ExitCode == 0 => (ArchiveOperationState.Failed, ArchiveFailureReason.ArtifactValidationFailed),
            _ => (ArchiveOperationState.Failed, ArchiveFailureReason.RunnerFailed),
        };

        return new(operation, state, false, count, reason, null, diagnostics);
    }

    private static ArchiveCommandResult Failure(
        ArchiveOperation operation,
        ArchiveFailureReason reason,
        string code,
        string message) => ArchiveCommandResult.Failure(
            operation,
            ArchiveOperationState.Failed,
            reason,
            code,
            message);

    private static ArchiveCommandResult Cancelled(ArchiveOperation operation) =>
        ArchiveCommandResult.Failure(
            operation,
            ArchiveOperationState.Cancelled,
            ArchiveFailureReason.Cancelled,
            "archive.cancelled",
            "The archive operation was cancelled.");
}
