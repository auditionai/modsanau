using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Projects;

public sealed record ProjectBuildOptions(
    TimeSpan PackTimeout,
    int MaximumFileCount,
    long MaximumTotalBytes)
{
    public static ProjectBuildOptions Default { get; } =
        new(TimeSpan.FromMinutes(10), 100_000, 16L * 1024 * 1024 * 1024);
}

public sealed class ProjectBuildService(
    IAuditionProjectStore projectStore,
    IProjectValidator projectValidator,
    ISecureWorkspaceService workspaceService,
    IAuditionArchiveService archiveService,
    IPathSecurity pathSecurity,
    ProjectBuildOptions options,
    TimeProvider? timeProvider = null,
    ILogger<ProjectBuildService>? logger = null) : IProjectBuildService, IDisposable
{
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<ProjectBuildService> _logger = logger ?? NullLogger<ProjectBuildService>.Instance;
    private int _disposeState;

    public async Task<ProjectBuildResult> BuildAsync(
        ProjectBuildRequest request,
        IProgress<ProjectBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        if (!IsValidRequest(request))
        {
            return Fail(ProjectBuildFailureReason.InvalidRequest, "PROJECT_BUILD_REQUEST_INVALID", progress);
        }

        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Report(progress, ProjectBuildPhase.Preparing, 0, "PROJECT_BUILD_SAVING");
            var initialSave = await projectStore.SaveAsync(request.Project, cancellationToken).ConfigureAwait(false);
            if (!initialSave.Succeeded)
            {
                return initialSave.FailureReason == AuditionProjectStoreFailureReason.Cancelled
                    ? Cancel(progress)
                    : Fail(ProjectBuildFailureReason.SaveFailed, "PROJECT_BUILD_SAVE_FAILED", progress);
            }

            Report(progress, ProjectBuildPhase.Validating, 0, "PROJECT_BUILD_VALIDATING");
            var validation = await projectValidator.ValidateAsync(
                new(request.Project, request.Workspace), cancellationToken).ConfigureAwait(false);
            if (validation.Cancelled)
            {
                return Cancel(progress);
            }

            if (!validation.CanBuild)
            {
                return Fail(ProjectBuildFailureReason.ValidationFailed,
                    "PROJECT_BUILD_VALIDATION_FAILED", progress, validation);
            }

            await using var buildWorkspace = await workspaceService.CreateAsync(cancellationToken).ConfigureAwait(false);
            var archive = CreateArchiveDescriptor(request.Workspace);
            var buildArchiveWorkspace = ArchiveWorkspace.Create(buildWorkspace, archive);
            await CloneBuildInputsAsync(request.Workspace, buildArchiveWorkspace, progress, cancellationToken)
                .ConfigureAwait(false);

            Report(progress, ProjectBuildPhase.Packing, 0, "PROJECT_BUILD_PACKING");
            var archiveProgress = new InlineProgress<ArchiveProgress>(item =>
                Report(progress, ProjectBuildPhase.Packing, item.ProcessedItemCount, "PROJECT_BUILD_PACKING"));
            var packed = await archiveService.PackAsync(
                new(archive, buildArchiveWorkspace, options.PackTimeout), archiveProgress, cancellationToken)
                .ConfigureAwait(false);
            if (!packed.Command.Succeeded)
            {
                return packed.Command.FinalState == ArchiveOperationState.Cancelled
                    ? Cancel(progress)
                    : Fail(ProjectBuildFailureReason.PackFailed, "PROJECT_BUILD_PACK_FAILED", progress, validation);
            }

            Report(progress, ProjectBuildPhase.Verifying, packed.Command.ProcessedItemCount,
                "PROJECT_BUILD_VERIFYING");
            var candidatePath = ResolvePackedArchive(buildArchiveWorkspace);
            if (!await VerifyArchiveAsync(candidatePath, cancellationToken).ConfigureAwait(false))
            {
                return Fail(ProjectBuildFailureReason.OutputVerificationFailed,
                    "PROJECT_BUILD_OUTPUT_INVALID", progress, validation);
            }

            var outputHash = new Sha256Digest(
                await ProjectFileSha256.ComputeAsync(candidatePath, cancellationToken).ConfigureAwait(false));
            var outputRelativePath = new ModRelativePath(
                $"BuildOutput/Output/{archive.FileName}");
            OutputPromotion promotion;
            try
            {
                promotion = await PromoteOutputAsync(
                    request.Workspace,
                    candidatePath,
                    outputRelativePath,
                    outputHash,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException
                                              or InvalidDataException
                                              or ArgumentException)
            {
                return Fail(ProjectBuildFailureReason.OutputPromotionFailed,
                    "PROJECT_BUILD_OUTPUT_PROMOTION_FAILED", progress, validation);
            }

            await using var promotionScope = promotion;

            var updated = CreateCompletedProject(request.Project, outputRelativePath, outputHash);
            if (!updated.Succeeded)
            {
                return Fail(ProjectBuildFailureReason.ProjectUpdateFailed,
                    "PROJECT_BUILD_PROJECT_UPDATE_FAILED", progress, validation);
            }

            var finalSave = await projectStore.SaveAsync(updated.Project!, cancellationToken).ConfigureAwait(false);
            if (!finalSave.Succeeded)
            {
                return finalSave.FailureReason == AuditionProjectStoreFailureReason.Cancelled
                    ? Cancel(progress)
                    : Fail(ProjectBuildFailureReason.SaveFailed, "PROJECT_BUILD_FINAL_SAVE_FAILED", progress, validation);
            }

            promotion.Commit();
            Report(progress, ProjectBuildPhase.Completed, packed.Command.ProcessedItemCount,
                "PROJECT_BUILD_COMPLETED");
            return ProjectBuildResult.Success(updated.Project!, outputRelativePath, outputHash, validation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancel(progress);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or InvalidDataException
                                          or ArgumentException
                                          or OverflowException)
        {
            _logger.LogWarning("Project build failed with exception type {ExceptionType}",
                exception.GetType().Name);
            return Fail(ProjectBuildFailureReason.WorkspacePreparationFailed,
                "PROJECT_BUILD_OPERATION_FAILED", progress);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _buildGate.Dispose();
        }
    }

    private bool IsValidRequest(ProjectBuildRequest? request) =>
        request is not null
        && request.Project is not null
        && request.Workspace is not null
        && options.PackTimeout > TimeSpan.Zero
        && options.PackTimeout != Timeout.InfiniteTimeSpan
        && options.MaximumFileCount > 0
        && options.MaximumTotalBytes > 0;

    private static AuditionArchiveTemplate CreateArchiveDescriptor(IProjectArchiveWorkspace workspace)
    {
        var reference = workspace.Descriptor.ArchiveTemplate;
        var archiveWorkspace = workspace.ArchiveWorkspace;
        return new(
            reference.TemplateId.Value,
            archiveWorkspace.WorkingArchiveRelativePath,
            archiveWorkspace.WorkingArchiveRelativePath,
            reference.EngineType,
            reference.RegionProfileId,
            archiveWorkspace.ExtractDirectoryRelativePath,
            reference.TemplateVersion?.Value,
            reference.SourceSha256.Value,
            reference.CompatibleGameBuild?.Value);
    }

    private async Task CloneBuildInputsAsync(
        IProjectArchiveWorkspace source,
        ArchiveWorkspace destination,
        IProgress<ProjectBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceArchive = pathSecurity.ResolvePathWithinRoot(
            source.ArchiveWorkspace.SecureWorkspace.Paths.WorkingDirectory,
            source.ArchiveWorkspace.WorkingArchiveRelativePath);
        pathSecurity.EnsureNoReparsePoints(
            source.ArchiveWorkspace.SecureWorkspace.Paths.WorkingDirectory, sourceArchive);
        var sourceArchiveHash = await ProjectFileSha256.ComputeAsync(sourceArchive, cancellationToken)
            .ConfigureAwait(false);
        if (!ProjectFileSha256.EqualsHex(source.Descriptor.WorkingArchiveSha256, sourceArchiveHash))
        {
            throw new InvalidDataException("The project working archive did not match its recorded hash.");
        }

        var destinationArchive = pathSecurity.ResolvePathWithinRoot(
            destination.SecureWorkspace.Paths.WorkingDirectory,
            destination.WorkingArchiveRelativePath);
        await CopyFileDurablyAsync(sourceArchive, destinationArchive, cancellationToken).ConfigureAwait(false);

        var sourceRoot = pathSecurity.ResolvePathWithinRoot(
            source.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
            source.ArchiveWorkspace.ExtractDirectoryRelativePath);
        pathSecurity.EnsureNoReparsePoints(
            source.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory, sourceRoot);
        var destinationRoot = pathSecurity.ResolvePathWithinRoot(
            destination.SecureWorkspace.Paths.ExtractedDirectory,
            destination.ExtractDirectoryRelativePath);
        Directory.CreateDirectory(destinationRoot);

        var count = 0;
        long totalBytes = 0;
        await CopyTreeAsync(sourceRoot, sourceRoot, destinationRoot, progress,
            () =>
            {
                count = checked(count + 1);
                if (count > options.MaximumFileCount)
                {
                    throw new InvalidDataException("The build input file count exceeds policy.");
                }
            },
            length =>
            {
                totalBytes = checked(totalBytes + length);
                if (totalBytes > options.MaximumTotalBytes)
                {
                    throw new InvalidDataException("The build input byte count exceeds policy.");
                }
            }, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyTreeAsync(
        string root,
        string directory,
        string destinationRoot,
        IProgress<ProjectBuildProgress>? progress,
        Action countFile,
        Action<long> countBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pathSecurity.EnsureNoReparsePoints(root, directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Reparse points are not permitted in build input.");
            }

            var relative = Path.GetRelativePath(root, entry);
            var sourcePath = pathSecurity.ResolvePathWithinRoot(root, relative);
            var destinationPath = pathSecurity.ResolvePathWithinRoot(destinationRoot, relative);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(destinationPath);
                await CopyTreeAsync(root, sourcePath, destinationRoot, progress, countFile, countBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var length = new FileInfo(sourcePath).Length;
                countFile();
                countBytes(length);
                await CopyFileDurablyAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
                Report(progress, ProjectBuildPhase.Preparing, 0, "PROJECT_BUILD_COPYING_INPUTS");
            }
        }
    }

    private string ResolvePackedArchive(ArchiveWorkspace workspace)
    {
        var path = pathSecurity.ResolvePathWithinRoot(
            workspace.SecureWorkspace.Paths.WorkingDirectory,
            workspace.WorkingArchiveRelativePath);
        pathSecurity.EnsureNoReparsePoints(workspace.SecureWorkspace.Paths.WorkingDirectory, path);
        return path;
    }

    private static async Task<bool> VerifyArchiveAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var probe = new byte[1];
        return await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<OutputPromotion> PromoteOutputAsync(
        IProjectArchiveWorkspace workspace,
        string candidatePath,
        ModRelativePath outputRelativePath,
        Sha256Digest expectedHash,
        CancellationToken cancellationToken)
    {
        var secure = workspace.ArchiveWorkspace.SecureWorkspace;
        var outputPath = secure.ResolveRelativePath(
            outputRelativePath.Value.Replace('/', Path.DirectorySeparatorChar));
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        pathSecurity.EnsureNoReparsePoints(secure.Paths.BuildOutputDirectory, outputDirectory);
        pathSecurity.EnsureNoReparsePoints(secure.Paths.BuildOutputDirectory, outputPath);
        var transactionId = Guid.NewGuid().ToString("N");
        var temporaryPath = pathSecurity.ResolvePathWithinRoot(outputDirectory, $".{transactionId}.tmp");
        var backupPath = pathSecurity.ResolvePathWithinRoot(outputDirectory, $".{transactionId}.backup");
        var hadPrevious = File.Exists(outputPath);

        try
        {
            await CopyFileDurablyAsync(candidatePath, temporaryPath, cancellationToken).ConfigureAwait(false);
            var copiedHash = await ProjectFileSha256.ComputeAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!ProjectFileSha256.EqualsHex(expectedHash.Value, copiedHash))
            {
                throw new InvalidDataException("Promoted output hash did not match the verified build.");
            }

            if (hadPrevious)
            {
                File.Replace(temporaryPath, outputPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, outputPath);
            }

            return new(outputPath, backupPath, hadPrevious, _logger);
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(backupPath);
            throw;
        }
    }

    private AuditionProjectCreateResult CreateCompletedProject(
        AuditionProject project,
        ModRelativePath outputPath,
        Sha256Digest outputHash) => AuditionProject.Create(
        project.SchemaVersion,
        project.ProjectId,
        project.Name,
        project.GameId,
        project.ModId,
        project.TemplateIdentity,
        project.Workspace,
        project.EditedTextures,
        project.ImageAssets,
        project.AiAssets,
        project.EditState,
        new(ProjectBuildStatus.Succeeded, _timeProvider.GetUtcNow(), outputPath, outputHash),
        project.CreatedAt,
        _timeProvider.GetUtcNow());

    private static async Task CopyFileDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static ProjectBuildResult Fail(
        ProjectBuildFailureReason reason,
        string code,
        IProgress<ProjectBuildProgress>? progress,
        ProjectValidationResult? validation = null)
    {
        Report(progress, ProjectBuildPhase.Failed, 0, code);
        return ProjectBuildResult.Failure(reason, code, validation);
    }

    private static ProjectBuildResult Cancel(IProgress<ProjectBuildProgress>? progress)
    {
        Report(progress, ProjectBuildPhase.Cancelled, 0, "PROJECT_BUILD_CANCELLED");
        return ProjectBuildResult.CancelledResult();
    }

    private static void Report(
        IProgress<ProjectBuildProgress>? progress,
        ProjectBuildPhase phase,
        int processedItemCount,
        string code) => progress?.Report(new(phase, Math.Max(0, processedItemCount), code));

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Build artifact cleanup failed with exception type {ExceptionType}",
                exception.GetType().Name);
        }
    }

    private sealed class OutputPromotion(
        string outputPath,
        string backupPath,
        bool hadPrevious,
        ILogger logger) : IAsyncDisposable
    {
        private bool _committed;
        private bool _rolledBack;

        public void Commit()
        {
            _committed = true;
            TryDelete(backupPath);
        }

        public ValueTask DisposeAsync()
        {
            if (_committed || _rolledBack)
            {
                return ValueTask.CompletedTask;
            }

            try
            {
                if (hadPrevious && File.Exists(backupPath))
                {
                    File.Replace(backupPath, outputPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    TryDelete(outputPath);
                }
            }
            finally
            {
                _rolledBack = true;
                TryDelete(backupPath);
            }

            return ValueTask.CompletedTask;
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Build promotion cleanup failed with exception type {ExceptionType}",
                    exception.GetType().Name);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
