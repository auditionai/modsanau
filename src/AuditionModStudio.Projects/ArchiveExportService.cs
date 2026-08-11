using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Projects;

public interface IArchiveExportFileOperations
{
    bool FileExists(string path);
    long GetFileLength(string path);
    Task CopyDurablyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken);
    void Move(string sourcePath, string destinationPath);
    void Replace(string sourcePath, string destinationPath, string backupPath);
    void Delete(string path);
}

public sealed class SystemArchiveExportFileOperations : IArchiveExportFileOperations
{
    public bool FileExists(string path) => File.Exists(path);
    public long GetFileLength(string path) => new FileInfo(path).Length;
    public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
        ProjectFileSha256.ComputeAsync(path, cancellationToken);
    public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);
    public void Replace(string sourcePath, string destinationPath, string backupPath) =>
        File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);
    public void Delete(string path) => File.Delete(path);

    public static async Task CopyFileDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    Task IArchiveExportFileOperations.CopyDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        CopyFileDurablyAsync(sourcePath, destinationPath, cancellationToken);
}

public sealed class ArchiveExportService(
    IArchiveExportDestinationValidator destinationValidator,
    IPathSecurity pathSecurity,
    IArchiveExportFileOperations fileOperations,
    ILogger<ArchiveExportService>? logger = null) : IArchiveExportService, IDisposable
{
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private readonly ILogger<ArchiveExportService> _logger = logger ?? NullLogger<ArchiveExportService>.Instance;
    private int _disposeState;

    public async Task<ArchiveExportResult> ExportAsync(
        ArchiveExportRequest request,
        IProgress<ArchiveExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRequest(request))
        {
            return Fail(ArchiveExportFailureReason.InvalidRequest, "ARCHIVE_EXPORT_REQUEST_INVALID", progress);
        }

        try
        {
            await _exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancel(progress);
        }

        try
        {
            Report(progress, ArchiveExportPhase.Preparing, "ARCHIVE_EXPORT_PREPARING");
            var source = ResolveBuildArtifact(request);
            if (source is null)
            {
                return Fail(ArchiveExportFailureReason.BuildArtifactUnavailable,
                    "ARCHIVE_EXPORT_BUILD_ARTIFACT_UNAVAILABLE", progress);
            }

            if (!ArchiveExportFileContract.TryCreate(
                    Path.GetFileName(request.Workspace.ArchiveWorkspace.WorkingArchiveRelativePath),
                    out var fileContract))
            {
                return Fail(ArchiveExportFailureReason.InvalidRequest,
                    "ARCHIVE_EXPORT_FILE_CONTRACT_INVALID", progress);
            }

            Report(progress, ArchiveExportPhase.VerifyingSource, "ARCHIVE_EXPORT_VERIFYING_SOURCE");
            var sourceVerification = await VerifySourceAsync(source, request.Project, cancellationToken)
                .ConfigureAwait(false);
            if (!sourceVerification.Valid)
            {
                return Fail(ArchiveExportFailureReason.BuildArtifactInvalid,
                    sourceVerification.Code, progress);
            }

            var destinationRequest = new ArchiveExportDestinationRequest(
                request.OutputDirectory, request.OutputFileName, fileContract, request.OverwritePolicy);
            var destinationResult = destinationValidator.Validate(destinationRequest);
            if (!destinationResult.Succeeded)
            {
                return Fail(ArchiveExportFailureReason.DestinationInvalid,
                    destinationResult.DiagnosticCode, progress);
            }

            return await ExportTransactionAsync(
                source, sourceVerification.Size, sourceVerification.Hash!.Value, destinationRequest,
                destinationResult.Destination!, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancel(progress);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning("Archive export failed with exception type {ExceptionType}",
                exception.GetType().Name);
            return Fail(ArchiveExportFailureReason.CopyFailed,
                "ARCHIVE_EXPORT_OPERATION_FAILED", progress);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _exportGate.Dispose();
        }
    }

    private static bool IsValidRequest(ArchiveExportRequest? request) =>
        request is not null && request.Project is not null && request.Workspace is not null;

    private string? ResolveBuildArtifact(ArchiveExportRequest request)
    {
        var project = request.Project;
        var workspace = request.Workspace;
        if (project.BuildState is not
            {
                Status: ProjectBuildStatus.Succeeded,
                OutputArchiveRelativePath: { IsValid: true } output,
                OutputSha256: { IsValid: true }
            }
            || workspace.Descriptor.State != ProjectArchiveWorkspaceState.Ready
            || workspace.Descriptor.ProjectId != project.ProjectId
            || !string.Equals(workspace.Descriptor.WorkspaceId, project.Workspace.WorkspaceId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var secure = workspace.ArchiveWorkspace.SecureWorkspace;
        var source = secure.ResolveRelativePath(output.Value.Replace('/', Path.DirectorySeparatorChar));
        pathSecurity.EnsureNoReparsePoints(secure.Paths.BuildOutputDirectory, source);
        return source;
    }

    private async Task<(bool Valid, string Code, long Size, Sha256Digest? Hash)> VerifySourceAsync(
        string source,
        AuditionProject project,
        CancellationToken cancellationToken)
    {
        if (!fileOperations.FileExists(source))
        {
            return (false, "ARCHIVE_EXPORT_BUILD_ARTIFACT_MISSING", 0, null);
        }

        var size = fileOperations.GetFileLength(source);
        if (size <= 0)
        {
            return (false, "ARCHIVE_EXPORT_BUILD_ARTIFACT_EMPTY", 0, null);
        }

        var actual = await fileOperations.ComputeSha256Async(source, cancellationToken).ConfigureAwait(false);
        if (!ProjectFileSha256.EqualsHex(project.BuildState.OutputSha256!.Value.Value, actual))
        {
            return (false, "ARCHIVE_EXPORT_BUILD_ARTIFACT_HASH_MISMATCH", 0, null);
        }

        return (true, "ARCHIVE_EXPORT_BUILD_ARTIFACT_VALID", size, new(actual));
    }

    private async Task<ArchiveExportResult> ExportTransactionAsync(
        string source,
        long expectedSize,
        Sha256Digest expectedHash,
        ArchiveExportDestinationRequest destinationRequest,
        ArchiveExportDestination destination,
        IProgress<ArchiveExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var temporary = pathSecurity.ResolvePathWithinRoot(
            destination.CanonicalDirectory, $".{transactionId}.export.tmp");
        var backup = pathSecurity.ResolvePathWithinRoot(
            destination.CanonicalDirectory, $".{transactionId}.export.backup");
        var promoted = false;
        var hadPrevious = false;

        try
        {
            Report(progress, ArchiveExportPhase.Copying, "ARCHIVE_EXPORT_COPYING");
            await fileOperations.CopyDurablyAsync(source, temporary, cancellationToken).ConfigureAwait(false);

            Report(progress, ArchiveExportPhase.VerifyingCandidate, "ARCHIVE_EXPORT_VERIFYING_CANDIDATE");
            if (!await MatchesAsync(temporary, expectedSize, expectedHash, cancellationToken).ConfigureAwait(false))
            {
                return Fail(ArchiveExportFailureReason.VerificationFailed,
                    "ARCHIVE_EXPORT_CANDIDATE_MISMATCH", progress);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var revalidated = destinationValidator.Validate(destinationRequest);
            if (!revalidated.Succeeded
                || !string.Equals(revalidated.Destination!.FullPath, destination.FullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Fail(ArchiveExportFailureReason.DestinationInvalid,
                    revalidated.DiagnosticCode, progress);
            }

            Report(progress, ArchiveExportPhase.Promoting, "ARCHIVE_EXPORT_PROMOTING");
            hadPrevious = fileOperations.FileExists(destination.FullPath);
            if (hadPrevious)
            {
                fileOperations.Replace(temporary, destination.FullPath, backup);
            }
            else
            {
                fileOperations.Move(temporary, destination.FullPath);
            }
            promoted = true;

            if (!await MatchesAsync(destination.FullPath, expectedSize, expectedHash, CancellationToken.None)
                    .ConfigureAwait(false))
            {
                if (!Rollback(destination.FullPath, backup, hadPrevious))
                {
                    return Fail(ArchiveExportFailureReason.RollbackFailed,
                        "ARCHIVE_EXPORT_ROLLBACK_FAILED", progress);
                }
                promoted = false;
                return Fail(ArchiveExportFailureReason.VerificationFailed,
                    "ARCHIVE_EXPORT_FINAL_MISMATCH", progress);
            }

            TryDelete(backup);
            promoted = false;
            Report(progress, ArchiveExportPhase.Completed, "ARCHIVE_EXPORT_COMPLETED");
            return ArchiveExportResult.Success(destination, expectedSize, expectedHash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancel(progress);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (promoted && !Rollback(destination.FullPath, backup, hadPrevious))
            {
                return Fail(ArchiveExportFailureReason.RollbackFailed,
                    "ARCHIVE_EXPORT_ROLLBACK_FAILED", progress);
            }
            return Fail(promoted ? ArchiveExportFailureReason.PromotionFailed : ArchiveExportFailureReason.CopyFailed,
                promoted ? "ARCHIVE_EXPORT_PROMOTION_FAILED" : "ARCHIVE_EXPORT_COPY_FAILED", progress);
        }
        finally
        {
            TryDelete(temporary);
            if (!promoted)
            {
                TryDelete(backup);
            }
        }
    }

    private async Task<bool> MatchesAsync(
        string path,
        long expectedSize,
        Sha256Digest expectedHash,
        CancellationToken cancellationToken)
    {
        if (!fileOperations.FileExists(path) || fileOperations.GetFileLength(path) != expectedSize)
        {
            return false;
        }
        var actual = await fileOperations.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        return ProjectFileSha256.EqualsHex(expectedHash.Value, actual);
    }

    private bool Rollback(string destination, string backup, bool hadPrevious)
    {
        try
        {
            if (hadPrevious && fileOperations.FileExists(backup))
            {
                fileOperations.Replace(backup, destination, $"{backup}.failed");
                TryDelete($"{backup}.failed");
            }
            else if (!hadPrevious && fileOperations.FileExists(destination))
            {
                fileOperations.Delete(destination);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Archive export rollback failed with exception type {ExceptionType}",
                exception.GetType().Name);
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (fileOperations.FileExists(path))
            {
                fileOperations.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Archive export cleanup failed with exception type {ExceptionType}",
                exception.GetType().Name);
        }
    }

    private static ArchiveExportResult Fail(
        ArchiveExportFailureReason reason,
        string code,
        IProgress<ArchiveExportProgress>? progress)
    {
        Report(progress, ArchiveExportPhase.Failed, code);
        return ArchiveExportResult.Failure(reason, code);
    }

    private static ArchiveExportResult Cancel(IProgress<ArchiveExportProgress>? progress)
    {
        Report(progress, ArchiveExportPhase.Cancelled, "ARCHIVE_EXPORT_CANCELLED");
        return ArchiveExportResult.CancelledResult();
    }

    private static void Report(
        IProgress<ArchiveExportProgress>? progress,
        ArchiveExportPhase phase,
        string code) => progress?.Report(new(phase, code));
}
