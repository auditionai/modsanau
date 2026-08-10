using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Archives;

public sealed class KeydatService(IPathSecurity pathSecurity) : IKeydatService, IDisposable
{
    private readonly SemaphoreSlim _copyLock = new(1, 1);
    private int _disposeState;

    public KeydatDescriptor Describe(ISecureWorkspace workspace, string archiveRelativePath)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRelativePath);

        var workingRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(workspace.Paths.WorkingDirectory));
        var archivePath = pathSecurity.ResolvePathWithinRoot(workingRoot, archiveRelativePath);
        pathSecurity.EnsureNoReparsePoints(workingRoot, archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The working archive was not found.");
        }

        var archiveRelative = Path.GetRelativePath(workingRoot, archivePath);
        var keydatRelative = Path.ChangeExtension(archiveRelative, ".keydat");
        var keydatPath = pathSecurity.ResolvePathWithinRoot(workingRoot, keydatRelative);
        pathSecurity.EnsureNoReparsePoints(workingRoot, keydatPath);

        if (!File.Exists(keydatPath))
        {
            return new(archivePath, keydatPath, KeydatStatus.Missing, null, null);
        }

        var length = new FileInfo(keydatPath).Length;
        return new(
            archivePath,
            keydatPath,
            length == 0 ? KeydatStatus.Invalid : KeydatStatus.PresentUnverified,
            length,
            null);
    }

    public async Task<KeydatDescriptor> CopyToWorkspaceAsync(
        ISecureWorkspace workspace,
        string archiveRelativePath,
        string trustedSourceRoot,
        string sourceRelativePath,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedSourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        if (expectedSha256 is not null && !FileSha256.IsValidHex(expectedSha256))
        {
            throw new ArgumentException("Expected SHA-256 must be 64 hexadecimal characters.", nameof(expectedSha256));
        }

        await _copyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var descriptor = Describe(workspace, archiveRelativePath);
            if (descriptor.Status == KeydatStatus.Invalid)
            {
                throw new InvalidDataException("The existing workspace keydat is structurally invalid.");
            }

            if (descriptor.Status == KeydatStatus.PresentUnverified)
            {
                return await WithVerifiedHashAsync(descriptor, expectedSha256, cancellationToken)
                    .ConfigureAwait(false);
            }

            var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedSourceRoot));
            var sourcePath = pathSecurity.ResolvePathWithinRoot(sourceRoot, sourceRelativePath);
            pathSecurity.EnsureNoReparsePoints(sourceRoot, sourcePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The trusted keydat source was not found.");
            }

            if (new FileInfo(sourcePath).Length == 0)
            {
                throw new InvalidDataException("The trusted keydat source is structurally invalid.");
            }

            var sourceHash = await FileSha256.ComputeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            EnsureExpectedHash(expectedSha256, sourceHash);

            var destinationRoot = Path.GetDirectoryName(descriptor.KeydatPath)!;
            var temporaryRelativePath = $".{Path.GetFileName(descriptor.KeydatPath)}.{Guid.NewGuid():N}.tmp";
            var temporaryPath = pathSecurity.ResolvePathWithinRoot(destinationRoot, temporaryRelativePath);
            pathSecurity.EnsureNoReparsePoints(workspace.Paths.WorkingDirectory, temporaryPath);

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

                var copyHash = await FileSha256.ComputeAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                if (!FileSha256.EqualsHex(sourceHash, copyHash))
                {
                    throw new IOException("The keydat working copy failed SHA-256 verification.");
                }

                File.Move(temporaryPath, descriptor.KeydatPath);
                var completed = Describe(workspace, archiveRelativePath);
                return completed with { Sha256 = copyHash };
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

    private static async Task<KeydatDescriptor> WithVerifiedHashAsync(
        KeydatDescriptor descriptor,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        var hash = await FileSha256.ComputeAsync(descriptor.KeydatPath, cancellationToken).ConfigureAwait(false);
        EnsureExpectedHash(expectedSha256, hash);
        return descriptor with { Sha256 = hash };
    }

    private static void EnsureExpectedHash(string? expectedSha256, string actualSha256)
    {
        if (expectedSha256 is not null && !FileSha256.EqualsHex(expectedSha256, actualSha256))
        {
            throw new InvalidDataException("The keydat SHA-256 does not match the trusted expected value.");
        }
    }
}
