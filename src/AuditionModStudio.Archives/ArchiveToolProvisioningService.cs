using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Archives;

public sealed class ArchiveToolProvisioningService(
    IPathSecurity pathSecurity,
    TrustedArchiveToolManifest manifest,
    ArchiveToolIntegrityPolicy integrityPolicy) : IArchiveToolProvisioningService, IDisposable
{
    private readonly SemaphoreSlim _copyLock = new(1, 1);
    private int _disposeState;

    public async Task<ArchiveToolProvisioningResult> ProvisionAsync(
        string toolId,
        string trustedSourceRoot,
        string sourceRelativePath,
        ISecureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!manifest.TryGetDescriptor(toolId, out var descriptor))
        {
            throw new ArchiveToolIntegrityException(new(
                false,
                ArchiveToolIntegrityFailureReason.UnapprovedTool,
                toolId,
                null,
                null,
                null));
        }

        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedSourceRoot));
        var sourcePath = pathSecurity.ResolvePathWithinRoot(sourceRoot, sourceRelativePath);
        pathSecurity.EnsureNoReparsePoints(sourceRoot, sourcePath);
        await integrityPolicy.EnsureApprovedAsync(toolId, sourcePath, sourceRoot, cancellationToken)
            .ConfigureAwait(false);

        var workingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Paths.WorkingDirectory));
        var destinationPath = pathSecurity.ResolvePathWithinRoot(workingRoot, descriptor.ExpectedFileName);
        pathSecurity.EnsureNoReparsePoints(workingRoot, destinationPath);

        await _copyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(destinationPath))
            {
                var existing = await integrityPolicy.VerifyAsync(toolId, destinationPath, workingRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (!existing.Approved)
                {
                    throw new ArchiveToolIntegrityException(existing);
                }

                return new(toolId, destinationPath, existing.ActualSha256!, existing.ObservedFileVersion, true);
            }

            var temporaryPath = pathSecurity.ResolvePathWithinRoot(
                workingRoot,
                $".{descriptor.ExpectedFileName}.{Guid.NewGuid():N}.tmp");
            var promoted = false;
            try
            {
                await using (var source = new FileStream(
                                 sourcePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 bufferSize: 81920,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 81920,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, destinationPath);
                promoted = true;
                var verified = await integrityPolicy.VerifyAsync(toolId, destinationPath, workingRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (!verified.Approved)
                {
                    File.Delete(destinationPath);
                    throw new ArchiveToolIntegrityException(verified);
                }

                return new(toolId, destinationPath, verified.ActualSha256!, verified.ObservedFileVersion, false);
            }
            catch
            {
                if (promoted && File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                throw;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _copyLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _copyLock.Dispose();
        }
    }
}
