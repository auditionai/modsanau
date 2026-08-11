using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class ProjectTextureRestoreService : IProjectTextureRestoreService
{
    public async Task<ProjectTextureRestoreResult> BeginRestoreAsync(
        IProjectArchiveWorkspace targetWorkspace,
        IProjectArchiveWorkspace pristineWorkspace,
        ModRelativePath texturePath,
        CancellationToken cancellationToken = default)
    {
        if (targetWorkspace is null || pristineWorkspace is null || !texturePath.IsValid)
        {
            return ProjectTextureRestoreResult.Failure("PROJECT_RESET_TEXTURE_REQUEST_INVALID");
        }

        string? temporaryPath = null;
        string? backupPath = null;
        try
        {
            var sourcePath = ResolveTexturePath(pristineWorkspace, texturePath);
            var targetPath = ResolveTexturePath(targetWorkspace, texturePath);
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0)
            {
                return ProjectTextureRestoreResult.Failure("PROJECT_RESET_PRISTINE_TEXTURE_MISSING");
            }

            var targetDirectory = Path.GetDirectoryName(targetPath)!;
            if (!Directory.Exists(targetDirectory))
            {
                return ProjectTextureRestoreResult.Failure("PROJECT_RESET_TARGET_DIRECTORY_MISSING");
            }

            var transactionId = Guid.NewGuid().ToString("N");
            temporaryPath = targetWorkspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
                Path.Combine("BuildOutput", "ResetTransactions", $"{transactionId}.restore.tmp"));
            backupPath = targetWorkspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
                Path.Combine("BuildOutput", "ResetTransactions", $"{transactionId}.backup"));
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);

            var targetExisted = File.Exists(targetPath);
            if (targetExisted)
            {
                await CopyDurablyAsync(targetPath, backupPath, cancellationToken).ConfigureAwait(false);
            }

            await CopyDurablyAsync(sourcePath, temporaryPath, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
            temporaryPath = null;
            return ProjectTextureRestoreResult.Success(
                new Transaction(targetPath, backupPath, targetExisted));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeleteIfPresent(temporaryPath);
            DeleteIfPresent(backupPath);
            return ProjectTextureRestoreResult.Failure("PROJECT_RESET_TEXTURE_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            DeleteIfPresent(temporaryPath);
            DeleteIfPresent(backupPath);
            return ProjectTextureRestoreResult.Failure("PROJECT_RESET_TEXTURE_IO_FAILED");
        }
    }

    private static string ResolveTexturePath(
        IProjectArchiveWorkspace workspace,
        ModRelativePath texturePath) => workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
        Path.Combine(
            "Extracted",
            workspace.ArchiveWorkspace.ExtractDirectoryRelativePath,
            texturePath.Value.Replace('/', Path.DirectorySeparatorChar)));

    private static async Task CopyDurablyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destinationStream = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
        await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        destinationStream.Flush(flushToDisk: true);
    }

    private static void DeleteIfPresent(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class Transaction(string targetPath, string backupPath, bool targetExisted)
        : IProjectTextureRestoreTransaction
    {
        private int _completed;

        public bool CleanupPending { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                try
                {
                    DeleteIfPresent(backupPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    CleanupPending = true;
                }
            }

            return Task.CompletedTask;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            if (targetExisted)
            {
                File.Move(backupPath, targetPath, overwrite: true);
            }
            else
            {
                DeleteIfPresent(targetPath);
                DeleteIfPresent(backupPath);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _completed) == 0)
            {
                await RollbackAsync().ConfigureAwait(false);
            }
        }
    }
}
