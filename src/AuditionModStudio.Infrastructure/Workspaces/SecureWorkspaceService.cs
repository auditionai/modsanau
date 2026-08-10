using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Infrastructure.Workspaces;

public sealed class SecureWorkspaceService : ISecureWorkspaceService, IAsyncDisposable
{
    private const string LockFileName = ".workspace.lock";
    private const int MaximumCreationAttempts = 10;
    private readonly ConcurrentDictionary<string, SecureWorkspaceLease> _activeWorkspaces = new();
    private readonly IAppPaths _appPaths;
    private readonly IPathSecurity _pathSecurity;
    private int _disposeState;

    public SecureWorkspaceService(IAppPaths appPaths, IPathSecurity pathSecurity)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _pathSecurity = pathSecurity ?? throw new ArgumentNullException(nameof(pathSecurity));
    }

    public ValueTask<ISecureWorkspace> CreateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureWorkspaceRoot();

        for (var attempt = 0; attempt < MaximumCreationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
            var root = _pathSecurity.ResolvePathWithinRoot(_appPaths.WorkspacesDirectory, id);

            if (Directory.Exists(root))
            {
                continue;
            }

            Directory.CreateDirectory(root);
            _pathSecurity.EnsureNoReparsePoints(_appPaths.WorkspacesDirectory, root);

            var lockPath = Path.Combine(root, LockFileName);
            FileStream? lockStream = null;
            var ownsMarker = false;

            try
            {
                lockStream = new FileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                ownsMarker = true;
                WriteLockMetadata(lockStream);

                var paths = CreateWorkspacePaths(root);
                var lease = new SecureWorkspaceLease(
                    id,
                    paths,
                    lockStream,
                    _pathSecurity,
                    CleanupOwnedAsync);

                if (!_activeWorkspaces.TryAdd(id, lease))
                {
                    throw new InvalidOperationException("A duplicate active workspace identifier was generated.");
                }

                lockStream = null;
                return ValueTask.FromResult<ISecureWorkspace>(lease);
            }
            catch
            {
                lockStream?.Dispose();
                if (ownsMarker)
                {
                    DeleteWorkspaceIfSafe(root, requireLockMarker: true);
                }

                throw;
            }
        }

        throw new IOException("Unable to allocate a unique secure workspace.");
    }

    public Task<int> CleanupAbandonedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureWorkspaceRoot();

        var cleanedCount = 0;
        foreach (var directory in Directory.EnumerateDirectories(
                     _appPaths.WorkspacesDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!IsWorkspaceId(id) || _activeWorkspaces.ContainsKey(id))
            {
                continue;
            }

            _pathSecurity.EnsureNoReparsePoints(_appPaths.WorkspacesDirectory, directory);
            var lockPath = Path.Combine(directory, LockFileName);
            if (!File.Exists(lockPath))
            {
                continue;
            }

            try
            {
                using (new FileStream(
                           lockPath,
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.None,
                           bufferSize: 1,
                           FileOptions.None))
                {
                    EnsureTreeHasNoReparsePoints(directory);
                }

                DeleteWorkspaceIfSafe(directory, requireLockMarker: true);
                cleanedCount++;
            }
            catch (IOException)
            {
                // An exclusive lock failure means another process still owns this workspace.
            }
            catch (UnauthorizedAccessException)
            {
                // Lack of access is not evidence that a workspace is abandoned or safe to delete.
            }
        }

        return Task.FromResult(cleanedCount);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        foreach (var workspace in _activeWorkspaces.Values.ToArray())
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void WriteLockMetadata(FileStream stream)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.WriteLine("version=1");
        writer.WriteLine($"processId={Environment.ProcessId}");
        writer.WriteLine($"createdUtc={DateTimeOffset.UtcNow:O}");
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private void EnsureWorkspaceRoot()
    {
        _pathSecurity.EnsureNoReparsePoints(
            _appPaths.RootDirectory,
            _appPaths.WorkspacesDirectory);
        Directory.CreateDirectory(_appPaths.WorkspacesDirectory);
        _pathSecurity.EnsureNoReparsePoints(
            _appPaths.RootDirectory,
            _appPaths.WorkspacesDirectory);
    }

    private static SecureWorkspacePaths CreateWorkspacePaths(string root)
    {
        var working = Directory.CreateDirectory(Path.Combine(root, "Working")).FullName;
        var extracted = Directory.CreateDirectory(Path.Combine(root, "Extracted")).FullName;
        var buildOutput = Directory.CreateDirectory(Path.Combine(root, "BuildOutput")).FullName;
        return new SecureWorkspacePaths(root, working, extracted, buildOutput);
    }

    private ValueTask CleanupOwnedAsync(SecureWorkspaceLease workspace)
    {
        if (_activeWorkspaces.TryRemove(
                new KeyValuePair<string, SecureWorkspaceLease>(workspace.Id, workspace)))
        {
            DeleteWorkspaceIfSafe(workspace.Paths.RootDirectory, requireLockMarker: true);
        }

        return ValueTask.CompletedTask;
    }

    private void DeleteWorkspaceIfSafe(string workspaceRoot, bool requireLockMarker)
    {
        if (!Directory.Exists(workspaceRoot))
        {
            return;
        }

        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspaceRoot));
        if (!IsWorkspaceId(id))
        {
            throw new InvalidOperationException("Only a direct managed workspace can be removed.");
        }

        var expectedRoot = _pathSecurity.ResolvePathWithinRoot(_appPaths.WorkspacesDirectory, id);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)),
                Path.TrimEndingDirectorySeparator(expectedRoot),
                comparison))
        {
            throw new InvalidOperationException("Only a direct managed workspace can be removed.");
        }

        _pathSecurity.EnsureNoReparsePoints(_appPaths.WorkspacesDirectory, workspaceRoot);
        if (requireLockMarker && !File.Exists(Path.Combine(workspaceRoot, LockFileName)))
        {
            throw new InvalidOperationException("The managed workspace marker is missing.");
        }

        EnsureTreeHasNoReparsePoints(workspaceRoot);
        Directory.Delete(workspaceRoot, recursive: true);
    }

    private static void EnsureTreeHasNoReparsePoints(string root)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);

        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         currentDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "A reparse point was found inside a managed workspace.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                }
            }
        }
    }

    private static bool IsWorkspaceId(string value)
    {
        return value.Length == 32
            && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

internal sealed class SecureWorkspaceLease : ISecureWorkspace
{
    private readonly Func<SecureWorkspaceLease, ValueTask> _cleanup;
    private readonly IPathSecurity _pathSecurity;
    private FileStream? _lockStream;

    public SecureWorkspaceLease(
        string id,
        SecureWorkspacePaths paths,
        FileStream lockStream,
        IPathSecurity pathSecurity,
        Func<SecureWorkspaceLease, ValueTask> cleanup)
    {
        Id = id;
        Paths = paths;
        _lockStream = lockStream;
        _pathSecurity = pathSecurity;
        _cleanup = cleanup;
    }

    public string Id { get; }

    public SecureWorkspacePaths Paths { get; }

    public string ResolveRelativePath(string relativePath)
    {
        ObjectDisposedException.ThrowIf(_lockStream is null, this);
        var resolved = _pathSecurity.ResolvePathWithinRoot(Paths.RootDirectory, relativePath);
        _pathSecurity.EnsureNoReparsePoints(Paths.RootDirectory, resolved);
        return resolved;
    }

    public async ValueTask DisposeAsync()
    {
        var lockStream = Interlocked.Exchange(ref _lockStream, null);
        if (lockStream is null)
        {
            return;
        }

        await lockStream.DisposeAsync().ConfigureAwait(false);
        await _cleanup(this).ConfigureAwait(false);
    }
}
