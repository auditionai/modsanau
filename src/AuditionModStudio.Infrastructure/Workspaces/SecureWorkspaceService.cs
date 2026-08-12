using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Workspaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Infrastructure.Workspaces;

public sealed class SecureWorkspaceService : ISecureWorkspaceService, ISecureWorkspaceRecoveryService,
    ISecureWorkspaceRetentionService, ISecureWorkspaceRemovalService, IWorkspaceCrashRecoveryService,
    IHostedService, IAsyncDisposable
{
    private const string LockFileName = ".workspace.lock";
    private const int MaximumCreationAttempts = 10;
    private readonly ConcurrentDictionary<string, SecureWorkspaceLease> _activeWorkspaces = new();
    private readonly ConcurrentDictionary<string, byte> _retainedWorkspaces = new();
    private readonly IAppPaths _appPaths;
    private readonly IPathSecurity _pathSecurity;
    private readonly ILogger<SecureWorkspaceService> _logger;
    private readonly IWorkspaceProtection _workspaceProtection;
    private ImmutableArray<WorkspaceRecoveryCandidate> _detectedCandidates = [];
    private int _disposeState;

    public SecureWorkspaceService(
        IAppPaths appPaths,
        IPathSecurity pathSecurity,
        ILogger<SecureWorkspaceService>? logger = null,
        IWorkspaceProtection? workspaceProtection = null)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _pathSecurity = pathSecurity ?? throw new ArgumentNullException(nameof(pathSecurity));
        _logger = logger ?? NullLogger<SecureWorkspaceService>.Instance;
        _workspaceProtection = workspaceProtection ?? new WindowsWorkspaceProtection();
    }

    public ImmutableArray<WorkspaceRecoveryCandidate> DetectedCandidates => _detectedCandidates;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Workspace crash-recovery discovery failed with {DiagnosticCode}",
                result.DiagnosticCode);
            return;
        }

        _logger.LogInformation("Workspace crash-recovery discovery found {CandidateCount} candidates",
            result.Candidates.Length);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
            try { _workspaceProtection.Protect(root); }
            catch
            {
                try { Directory.Delete(root, recursive: false); }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Workspace ACL failure cleanup failed with {ExceptionType}",
                        cleanupException.GetType().Name);
                }
                throw;
            }
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

    public ValueTask<ISecureWorkspace?> TryOpenExistingAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWorkspaceId(workspaceId))
        {
            return ValueTask.FromResult<ISecureWorkspace?>(null);
        }

        if (_activeWorkspaces.TryGetValue(workspaceId, out var active))
        {
            return ValueTask.FromResult<ISecureWorkspace?>(active);
        }

        EnsureWorkspaceRoot();
        var root = _pathSecurity.ResolvePathWithinRoot(_appPaths.WorkspacesDirectory, workspaceId);
        if (!Directory.Exists(root))
        {
            return ValueTask.FromResult<ISecureWorkspace?>(null);
        }

        FileStream? lockStream = null;
        try
        {
            _pathSecurity.EnsureNoReparsePoints(_appPaths.WorkspacesDirectory, root);
            _workspaceProtection.Protect(root);
            if (!_workspaceProtection.IsProtected(root))
                return ValueTask.FromResult<ISecureWorkspace?>(null);
            EnsureTreeHasNoReparsePoints(root);
            var paths = GetExistingWorkspacePaths(root);
            var lockPath = _pathSecurity.ResolvePathWithinRoot(root, LockFileName);
            if (!File.Exists(lockPath))
            {
                return ValueTask.FromResult<ISecureWorkspace?>(null);
            }

            lockStream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            var retained = false;
            using (var reader = new StreamReader(lockStream, Encoding.UTF8, leaveOpen: true))
            {
                if (!string.Equals(reader.ReadLine(), "version=1", StringComparison.Ordinal))
                {
                    lockStream.Dispose();
                    return ValueTask.FromResult<ISecureWorkspace?>(null);
                }

                retained = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Contains("retained=1", StringComparer.Ordinal);
            }

            lockStream.Position = 0;
            var lease = new SecureWorkspaceLease(
                workspaceId,
                paths,
                lockStream,
                _pathSecurity,
                CleanupOwnedAsync,
                retained);
            if (_activeWorkspaces.TryAdd(workspaceId, lease))
            {
                if (retained)
                {
                    _retainedWorkspaces.TryAdd(workspaceId, 0);
                }

                lockStream = null;
                return ValueTask.FromResult<ISecureWorkspace?>(lease);
            }

            lockStream.Dispose();
            return ValueTask.FromResult<ISecureWorkspace?>(
                _activeWorkspaces.TryGetValue(workspaceId, out active) ? active : null);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            lockStream?.Dispose();
            return ValueTask.FromResult<ISecureWorkspace?>(null);
        }
    }

    public void Retain(ISecureWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!_activeWorkspaces.TryGetValue(workspace.Id, out var active)
            || !ReferenceEquals(active, workspace))
        {
            throw new InvalidOperationException("Only an active owned workspace can be retained.");
        }

        active.MarkRetained();
        _retainedWorkspaces.TryAdd(workspace.Id, 0);
    }

    public async Task<bool> RemoveAsync(
        ISecureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_activeWorkspaces.TryGetValue(workspace.Id, out var active)
            || !ReferenceEquals(active, workspace))
        {
            return false;
        }

        _retainedWorkspaces.TryRemove(workspace.Id, out _);
        await workspace.DisposeAsync().ConfigureAwait(false);
        return !Directory.Exists(workspace.Paths.RootDirectory);
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
                using (var stream = new FileStream(
                           lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                           bufferSize: 4096, FileOptions.None))
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var lines = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Contains("retained=1", StringComparer.Ordinal))
                    {
                        continue;
                    }

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

    public Task<WorkspaceRecoveryScanResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWorkspaceRoot();
            var candidates = Directory.EnumerateDirectories(
                    _appPaths.WorkspacesDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(id => id is not null && IsWorkspaceId(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => InspectRecoveryCandidate(id!, cancellationToken))
                .ToImmutableArray();
            ImmutableInterlocked.InterlockedExchange(ref _detectedCandidates, candidates);
            return Task.FromResult(WorkspaceRecoveryScanResult.Success(candidates));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(WorkspaceRecoveryScanResult.Failure(
                "WORKSPACE_RECOVERY_SCAN_CANCELLED", cancelled: true));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return Task.FromResult(WorkspaceRecoveryScanResult.Failure("WORKSPACE_RECOVERY_SCAN_FAILED"));
        }
    }

    public async ValueTask<WorkspaceRecoveryActionResult> RecoverAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceId(workspaceId))
        {
            return WorkspaceRecoveryActionResult.Failure(
                WorkspaceRecoveryActionFailureReason.InvalidWorkspaceId, "WORKSPACE_RECOVERY_ID_INVALID");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = InspectRecoveryCandidate(workspaceId, cancellationToken);
            if (candidate.State != WorkspaceRecoveryState.StaleRecoverable)
            {
                return CandidateActionFailure(candidate, recovery: true);
            }

            var workspace = await TryOpenExistingAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            if (workspace is null)
            {
                return WorkspaceRecoveryActionResult.Failure(
                    WorkspaceRecoveryActionFailureReason.RecoveryFailed, "WORKSPACE_RECOVERY_OPEN_FAILED");
            }

            RemoveDetectedCandidate(workspaceId);
            return WorkspaceRecoveryActionResult.Success(workspace);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WorkspaceRecoveryActionResult.Failure(
                WorkspaceRecoveryActionFailureReason.Cancelled, "WORKSPACE_RECOVERY_CANCELLED");
        }
    }

    public Task<WorkspaceRecoveryActionResult> CleanupAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceId(workspaceId))
        {
            return Task.FromResult(WorkspaceRecoveryActionResult.Failure(
                WorkspaceRecoveryActionFailureReason.InvalidWorkspaceId, "WORKSPACE_CLEANUP_ID_INVALID"));
        }

        if (_activeWorkspaces.ContainsKey(workspaceId))
        {
            return Task.FromResult(WorkspaceRecoveryActionResult.Failure(
                WorkspaceRecoveryActionFailureReason.ActiveOrInaccessible, "WORKSPACE_CLEANUP_ACTIVE"));
        }

        var lockAcquired = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWorkspaceRoot();
            var root = _pathSecurity.ResolvePathWithinRoot(_appPaths.WorkspacesDirectory, workspaceId);
            if (!Directory.Exists(root))
            {
                return Task.FromResult(WorkspaceRecoveryActionResult.Failure(
                    WorkspaceRecoveryActionFailureReason.NotFound, "WORKSPACE_CLEANUP_NOT_FOUND"));
            }

            _pathSecurity.EnsureNoReparsePoints(_appPaths.WorkspacesDirectory, root);
            EnsureTreeHasNoReparsePoints(root);
            var lockPath = _pathSecurity.ResolvePathWithinRoot(root, LockFileName);
            if (!File.Exists(lockPath))
            {
                return Task.FromResult(WorkspaceRecoveryActionResult.Failure(
                    WorkspaceRecoveryActionFailureReason.UnsafeWorkspace, "WORKSPACE_CLEANUP_MARKER_MISSING"));
            }

            using (var stream = new FileStream(
                       lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete,
                       bufferSize: 4096, FileOptions.None))
            {
                lockAcquired = true;
                if (!TryReadLockMetadata(stream, out _))
                {
                    return Task.FromResult(WorkspaceRecoveryActionResult.Failure(
                        WorkspaceRecoveryActionFailureReason.UnsafeWorkspace, "WORKSPACE_CLEANUP_MARKER_INVALID"));
                }

                cancellationToken.ThrowIfCancellationRequested();
                DeleteWorkspaceIfSafe(root, requireLockMarker: true);
            }

            RemoveDetectedCandidate(workspaceId);
            return Task.FromResult(WorkspaceRecoveryActionResult.Success());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(WorkspaceRecoveryActionResult.Failure(
                WorkspaceRecoveryActionFailureReason.Cancelled, "WORKSPACE_CLEANUP_CANCELLED"));
        }
        catch (IOException)
        {
            return Task.FromResult(WorkspaceRecoveryActionResult.Failure(
                lockAcquired ? WorkspaceRecoveryActionFailureReason.CleanupFailed
                    : WorkspaceRecoveryActionFailureReason.ActiveOrInaccessible,
                lockAcquired ? "WORKSPACE_CLEANUP_FAILED" : "WORKSPACE_CLEANUP_LOCKED"));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return Task.FromResult(WorkspaceRecoveryActionResult.Failure(
                WorkspaceRecoveryActionFailureReason.CleanupFailed, "WORKSPACE_CLEANUP_FAILED"));
        }
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

    private WorkspaceRecoveryCandidate InspectRecoveryCandidate(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_activeWorkspaces.ContainsKey(workspaceId))
        {
            return Candidate(workspaceId, WorkspaceRecoveryState.ActiveOrInaccessible,
                "WORKSPACE_RECOVERY_ACTIVE");
        }

        var root = _pathSecurity.ResolvePathWithinRoot(_appPaths.WorkspacesDirectory, workspaceId);
        if (!Directory.Exists(root))
        {
            return Candidate(workspaceId, WorkspaceRecoveryState.Unsafe,
                "WORKSPACE_RECOVERY_NOT_FOUND");
        }

        try
        {
            _pathSecurity.EnsureNoReparsePoints(_appPaths.WorkspacesDirectory, root);
            EnsureTreeHasNoReparsePoints(root);
            var lockPath = _pathSecurity.ResolvePathWithinRoot(root, LockFileName);
            if (!File.Exists(lockPath))
            {
                return Candidate(workspaceId, WorkspaceRecoveryState.Unsafe,
                    "WORKSPACE_RECOVERY_MARKER_MISSING");
            }

            using var stream = new FileStream(
                lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 4096, FileOptions.None);
            if (!TryReadLockMetadata(stream, out var metadata))
            {
                return Candidate(workspaceId, WorkspaceRecoveryState.Unsafe,
                    "WORKSPACE_RECOVERY_MARKER_INVALID");
            }

            var complete = Directory.Exists(Path.Combine(root, "Working"))
                           && Directory.Exists(Path.Combine(root, "Extracted"))
                           && Directory.Exists(Path.Combine(root, "BuildOutput"));
            return new(
                workspaceId,
                complete ? WorkspaceRecoveryState.StaleRecoverable : WorkspaceRecoveryState.StaleCleanupOnly,
                metadata.Retained,
                metadata.ProcessId,
                metadata.CreatedAtUtc,
                complete,
                true,
                complete ? "WORKSPACE_RECOVERY_STALE_RECOVERABLE" : "WORKSPACE_RECOVERY_STALE_INCOMPLETE");
        }
        catch (IOException)
        {
            return Candidate(workspaceId, WorkspaceRecoveryState.ActiveOrInaccessible,
                "WORKSPACE_RECOVERY_ACTIVE_OR_INACCESSIBLE");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return Candidate(workspaceId, WorkspaceRecoveryState.Unsafe,
                "WORKSPACE_RECOVERY_UNSAFE");
        }
    }

    private static WorkspaceRecoveryCandidate Candidate(
        string workspaceId,
        WorkspaceRecoveryState state,
        string diagnosticCode) => new(
        workspaceId, state, false, null, null, false, false, diagnosticCode);

    private static WorkspaceRecoveryActionResult CandidateActionFailure(
        WorkspaceRecoveryCandidate candidate,
        bool recovery)
    {
        if (candidate.DiagnosticCode == "WORKSPACE_RECOVERY_NOT_FOUND")
        {
            return WorkspaceRecoveryActionResult.Failure(
                WorkspaceRecoveryActionFailureReason.NotFound, "WORKSPACE_RECOVERY_NOT_FOUND");
        }

        return candidate.State switch
        {
            WorkspaceRecoveryState.ActiveOrInaccessible => WorkspaceRecoveryActionResult.Failure(
                WorkspaceRecoveryActionFailureReason.ActiveOrInaccessible, "WORKSPACE_RECOVERY_ACTIVE_OR_INACCESSIBLE"),
            WorkspaceRecoveryState.Unsafe => WorkspaceRecoveryActionResult.Failure(
                WorkspaceRecoveryActionFailureReason.UnsafeWorkspace, "WORKSPACE_RECOVERY_UNSAFE"),
            _ => WorkspaceRecoveryActionResult.Failure(
                recovery ? WorkspaceRecoveryActionFailureReason.NotRecoverable
                    : WorkspaceRecoveryActionFailureReason.CleanupFailed,
                recovery ? "WORKSPACE_RECOVERY_NOT_RECOVERABLE" : "WORKSPACE_CLEANUP_FAILED"),
        };
    }

    private void RemoveDetectedCandidate(string workspaceId)
    {
        ImmutableInterlocked.Update(ref _detectedCandidates,
            candidates => candidates.Where(candidate => candidate.WorkspaceId != workspaceId).ToImmutableArray());
    }

    private static bool TryReadLockMetadata(FileStream stream, out WorkspaceLockMetadata metadata)
    {
        metadata = default;
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "version", "sessionId", "processId", "createdUtc", "retained",
        };
        foreach (var line in reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1
                || !allowedKeys.Contains(line[..separator])
                || !values.TryAdd(line[..separator], line[(separator + 1)..]))
            {
                return false;
            }
        }

        if (!values.TryGetValue("version", out var version)
            || !string.Equals(version, "1", StringComparison.Ordinal))
        {
            return false;
        }

        int? processId = null;
        if (values.TryGetValue("processId", out var processText))
        {
            if (!int.TryParse(processText, out var parsedProcessId) || parsedProcessId <= 0)
            {
                return false;
            }
            processId = parsedProcessId;
        }

        DateTimeOffset? createdAtUtc = null;
        if (values.TryGetValue("createdUtc", out var createdText))
        {
            if (!DateTimeOffset.TryParseExact(
                    createdText, "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsedCreated))
            {
                return false;
            }
            createdAtUtc = parsedCreated;
        }

        if (values.TryGetValue("sessionId", out var sessionId) && !IsWorkspaceId(sessionId))
        {
            return false;
        }

        if (values.TryGetValue("retained", out var retained) && retained != "1")
        {
            return false;
        }

        metadata = new(
            retained == "1",
            processId,
            createdAtUtc);
        return true;
    }

    private static void WriteLockMetadata(FileStream stream)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.WriteLine("version=1");
        writer.WriteLine($"sessionId={RandomNumberGenerator.GetHexString(32).ToLowerInvariant()}");
        writer.WriteLine($"processId={Environment.ProcessId}");
        writer.WriteLine($"createdUtc={DateTimeOffset.UtcNow:O}");
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private readonly record struct WorkspaceLockMetadata(
        bool Retained,
        int? ProcessId,
        DateTimeOffset? CreatedAtUtc);

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

    private static SecureWorkspacePaths GetExistingWorkspacePaths(string root)
    {
        var working = Path.Combine(root, "Working");
        var extracted = Path.Combine(root, "Extracted");
        var buildOutput = Path.Combine(root, "BuildOutput");
        if (!Directory.Exists(working) || !Directory.Exists(extracted) || !Directory.Exists(buildOutput))
        {
            throw new InvalidDataException("The existing managed workspace is incomplete.");
        }

        return new(root, working, extracted, buildOutput);
    }

    private ValueTask CleanupOwnedAsync(SecureWorkspaceLease workspace)
    {
        if (_activeWorkspaces.TryRemove(
                new KeyValuePair<string, SecureWorkspaceLease>(workspace.Id, workspace)))
        {
            if (!_retainedWorkspaces.TryRemove(workspace.Id, out _))
            {
                DeleteWorkspaceIfSafe(workspace.Paths.RootDirectory, requireLockMarker: true);
            }
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

    private static bool IsWorkspaceId(string? value)
    {
        return value is not null && value.Length == 32
            && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

internal sealed class SecureWorkspaceLease : ISecureWorkspace
{
    private readonly Func<SecureWorkspaceLease, ValueTask> _cleanup;
    private readonly IPathSecurity _pathSecurity;
    private FileStream? _lockStream;
    private int _retained;

    public SecureWorkspaceLease(
        string id,
        SecureWorkspacePaths paths,
        FileStream lockStream,
        IPathSecurity pathSecurity,
        Func<SecureWorkspaceLease, ValueTask> cleanup,
        bool retained = false)
    {
        Id = id;
        Paths = paths;
        _lockStream = lockStream;
        _pathSecurity = pathSecurity;
        _cleanup = cleanup;
        _retained = retained ? 1 : 0;
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

    public void MarkRetained()
    {
        var stream = _lockStream ?? throw new ObjectDisposedException(nameof(SecureWorkspaceLease));
        lock (stream)
        {
            if (_retained != 0)
            {
                return;
            }

            stream.Position = stream.Length;
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.WriteLine("retained=1");
            writer.Flush();
            stream.Flush(flushToDisk: true);
            _retained = 1;
        }
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
