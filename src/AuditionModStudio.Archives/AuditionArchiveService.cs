using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Paths;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Archives;

public sealed class AuditionArchiveService(
    IEnumerable<IArchiveEngine> engines,
    IPathSecurity pathSecurity,
    ILogger<AuditionArchiveService>? logger = null) : IAuditionArchiveService
{
    private readonly IReadOnlyDictionary<string, IArchiveEngine> _engines = BuildEngineMap(engines);
    private readonly ILogger<AuditionArchiveService> _logger = logger ?? NullLogger<AuditionArchiveService>.Instance;

    public async Task<ArchiveExtractResult> ExtractAsync(
        ArchiveExtractRequest request,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress?.Report(new(ArchiveOperation.Extract, ArchiveOperationState.Preparing, 0));

        if (!TryResolveEngine(request.Archive.EngineType, ArchiveOperation.Extract, out var engine, out var failure))
        {
            return new(failure!);
        }

        try
        {
            ValidateWorkspace(request.Archive, request.Workspace);
            await CopyPristineToWorkingAsync(request, cancellationToken).ConfigureAwait(false);
            return new(await engine!.ExtractAsync(request, progress, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Archive extract was cancelled while preparing its isolated workspace");
            return new(ArchiveCommandResult.Failure(
                ArchiveOperation.Extract,
                ArchiveOperationState.Cancelled,
                ArchiveFailureReason.Cancelled,
                "archive.cancelled",
                "The archive operation was cancelled."));
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Archive extract preparation failed");
            return new(ArchiveCommandResult.Failure(
                ArchiveOperation.Extract,
                ArchiveOperationState.Failed,
                exception is InvalidDataException
                    ? ArchiveFailureReason.InvalidArchive
                    : ArchiveFailureReason.InvalidWorkspace,
                "archive.preparation_failed",
                "The archive source or isolated workspace is invalid."));
        }
    }

    public async Task<ArchivePackResult> PackAsync(
        ArchivePackRequest request,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress?.Report(new(ArchiveOperation.Pack, ArchiveOperationState.Preparing, 0));

        if (!TryResolveEngine(request.Archive.EngineType, ArchiveOperation.Pack, out var engine, out var failure))
        {
            return new(failure!);
        }

        try
        {
            ValidateWorkspace(request.Archive, request.Workspace);
            var workingArchive = pathSecurity.ResolvePathWithinRoot(
                request.Workspace.SecureWorkspace.Paths.WorkingDirectory,
                request.Workspace.WorkingArchiveRelativePath);
            pathSecurity.EnsureNoReparsePoints(
                request.Workspace.SecureWorkspace.Paths.WorkingDirectory,
                workingArchive);
            if (!File.Exists(workingArchive) || new FileInfo(workingArchive).Length == 0)
            {
                return new(ArchiveCommandResult.Failure(
                    ArchiveOperation.Pack,
                    ArchiveOperationState.Failed,
                    ArchiveFailureReason.InvalidWorkspace,
                    "archive.working_copy_missing",
                    "The working archive is missing or empty."));
            }

            return new(await engine!.PackAsync(request, progress, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ArchiveCommandResult.Failure(
                ArchiveOperation.Pack,
                ArchiveOperationState.Cancelled,
                ArchiveFailureReason.Cancelled,
                "archive.cancelled",
                "The archive operation was cancelled."));
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Archive pack preparation failed");
            return new(ArchiveCommandResult.Failure(
                ArchiveOperation.Pack,
                ArchiveOperationState.Failed,
                ArchiveFailureReason.InvalidWorkspace,
                "archive.workspace_invalid",
                "The isolated archive workspace is invalid."));
        }
    }

    private static IReadOnlyDictionary<string, IArchiveEngine> BuildEngineMap(IEnumerable<IArchiveEngine> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        var map = new Dictionary<string, IArchiveEngine>(StringComparer.OrdinalIgnoreCase);
        foreach (var engine in engines)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (!map.TryAdd(engine.EngineType.Id, engine))
            {
                throw new InvalidOperationException($"Archive engine '{engine.EngineType.Id}' is registered more than once.");
            }
        }

        return map;
    }

    private bool TryResolveEngine(
        ArchiveEngineType engineType,
        ArchiveOperation operation,
        out IArchiveEngine? engine,
        out ArchiveCommandResult? failure)
    {
        if (_engines.TryGetValue(engineType.Id, out engine))
        {
            failure = null;
            return true;
        }

        _logger.LogWarning("Archive operation rejected because engine {EngineId} is unsupported", engineType.Id);
        failure = ArchiveCommandResult.Failure(
            operation,
            ArchiveOperationState.Failed,
            ArchiveFailureReason.UnsupportedEngine,
            "archive.engine_unsupported",
            "The requested archive engine is not registered.");
        return false;
    }

    private void ValidateWorkspace(AuditionArchiveTemplate archive, ArchiveWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(workspace.SecureWorkspace);
        if (!string.Equals(workspace.WorkingArchiveRelativePath, archive.FileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The working archive name does not match the archive descriptor.", nameof(workspace));
        }

        _ = pathSecurity.ResolvePathWithinRoot(
            workspace.SecureWorkspace.Paths.WorkingDirectory,
            workspace.WorkingArchiveRelativePath);
        _ = pathSecurity.ResolvePathWithinRoot(
            workspace.SecureWorkspace.Paths.ExtractedDirectory,
            workspace.ExtractDirectoryRelativePath);
    }

    private async Task CopyPristineToWorkingAsync(
        ArchiveExtractRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Source);
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Source.TrustedRootDirectory));
        var sourcePath = pathSecurity.ResolvePathWithinRoot(sourceRoot, request.Archive.SourceRelativePath);
        pathSecurity.EnsureNoReparsePoints(sourceRoot, sourcePath);
        if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0)
        {
            throw new InvalidDataException("The pristine archive source is missing or empty.");
        }

        var sourceHash = await FileSha256.ComputeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (request.Archive.Sha256 is not null
            && !FileSha256.EqualsHex(request.Archive.Sha256, sourceHash))
        {
            throw new InvalidDataException("The pristine archive source failed SHA-256 verification.");
        }

        var workingRoot = request.Workspace.SecureWorkspace.Paths.WorkingDirectory;
        var destinationPath = pathSecurity.ResolvePathWithinRoot(
            workingRoot,
            request.Workspace.WorkingArchiveRelativePath);
        pathSecurity.EnsureNoReparsePoints(workingRoot, destinationPath);
        if (File.Exists(destinationPath))
        {
            var existingHash = await FileSha256.ComputeAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (!FileSha256.EqualsHex(sourceHash, existingHash))
            {
                throw new IOException("The existing working archive does not match the pristine source.");
            }

            return;
        }

        var temporaryPath = pathSecurity.ResolvePathWithinRoot(
            workingRoot,
            $".{request.Archive.FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            var copyHash = await FileSha256.ComputeAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!FileSha256.EqualsHex(sourceHash, copyHash)
                || request.Archive.Sha256 is not null
                && !FileSha256.EqualsHex(request.Archive.Sha256, copyHash))
            {
                throw new InvalidDataException("The working archive copy failed SHA-256 verification.");
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
