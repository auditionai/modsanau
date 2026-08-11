using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;

namespace IntegrationTests;

public sealed class SecureWorkspaceTests
{
    [Fact]
    public async Task Workspace_is_randomized_isolated_and_below_managed_temp_root()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            await using var service = new SecureWorkspaceService(paths, new PathSecurity());
            await using var workspace = await service.CreateAsync();

            Assert.Equal(32, workspace.Id.Length);
            Assert.Equal(
                Path.Combine(paths.WorkspacesDirectory, workspace.Id),
                workspace.Paths.RootDirectory);
            Assert.True(Directory.Exists(workspace.Paths.WorkingDirectory));
            Assert.True(Directory.Exists(workspace.Paths.ExtractedDirectory));
            Assert.True(Directory.Exists(workspace.Paths.BuildOutputDirectory));

            var safeTexturePath = workspace.ResolveRelativePath(@"Extracted\015\texture\gui\file.dds");
            Assert.StartsWith(workspace.Paths.RootDirectory, safeTexturePath, PathComparison);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Concurrent_workspace_creation_has_no_collisions()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            await using var service = new SecureWorkspaceService(paths, new PathSecurity());
            var creationTasks = Enumerable.Range(0, 32)
                .Select(_ => service.CreateAsync().AsTask())
                .ToArray();
            var workspaces = await Task.WhenAll(creationTasks);

            try
            {
                Assert.Equal(workspaces.Length, workspaces.Select(item => item.Id).Distinct().Count());
                Assert.All(workspaces, item => Assert.True(Directory.Exists(item.Paths.RootDirectory)));
            }
            finally
            {
                foreach (var workspace in workspaces)
                {
                    await workspace.DisposeAsync();
                }
            }
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Disposing_workspace_removes_only_that_managed_workspace()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            paths.EnsureDirectoriesExist();
            var projectSentinel = Path.Combine(paths.ProjectsDirectory, "project.keep");
            var pristineSentinel = Path.Combine(paths.SecureTemplateCacheDirectory, "015.ab");
            await File.WriteAllTextAsync(projectSentinel, "project");
            await File.WriteAllTextAsync(pristineSentinel, "pristine");

            await using var service = new SecureWorkspaceService(paths, new PathSecurity());
            var workspace = await service.CreateAsync();
            var workspaceRoot = workspace.Paths.RootDirectory;
            await workspace.DisposeAsync();

            Assert.False(Directory.Exists(workspaceRoot));
            Assert.True(Directory.Exists(paths.ProjectsDirectory));
            Assert.True(File.Exists(projectSentinel));
            Assert.True(File.Exists(pristineSentinel));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Abandoned_cleanup_skips_active_workspace_and_removes_unlocked_marker_workspace()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            await using var service = new SecureWorkspaceService(paths, new PathSecurity());
            await using var activeWorkspace = await service.CreateAsync();
            var abandonedId = new string('a', 32);
            var abandonedRoot = Path.Combine(paths.WorkspacesDirectory, abandonedId);
            Directory.CreateDirectory(abandonedRoot);
            await File.WriteAllTextAsync(Path.Combine(abandonedRoot, ".workspace.lock"), "version=1");

            var cleaned = await service.CleanupAbandonedAsync();

            Assert.Equal(1, cleaned);
            Assert.True(Directory.Exists(activeWorkspace.Paths.RootDirectory));
            Assert.False(Directory.Exists(abandonedRoot));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Workspace_resolver_rejects_escape_attempt()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            await using var service = new SecureWorkspaceService(paths, new PathSecurity());
            await using var workspace = await service.CreateAsync();

            Assert.Throws<ArgumentException>(
                () => workspace.ResolveRelativePath(@"..\..\Projects\project.json"));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Existing_active_workspace_is_recovered_by_exact_id()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            await using var service = new SecureWorkspaceService(paths, new PathSecurity());
            await using var created = await service.CreateAsync();

            var recovered = await service.TryOpenExistingAsync(created.Id);

            Assert.Same(created, recovered);
            Assert.Null(await service.TryOpenExistingAsync("../" + created.Id));
            Assert.Null(await service.TryOpenExistingAsync(created.Id.ToUpperInvariant()));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Abandoned_valid_workspace_can_be_reopened_without_creating_a_new_identity()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            paths.EnsureDirectoriesExist();
            var workspaceId = new string('a', 32);
            var root = Path.Combine(paths.WorkspacesDirectory, workspaceId);
            Directory.CreateDirectory(Path.Combine(root, "Working"));
            Directory.CreateDirectory(Path.Combine(root, "Extracted"));
            Directory.CreateDirectory(Path.Combine(root, "BuildOutput"));
            await File.WriteAllTextAsync(Path.Combine(root, ".workspace.lock"), "version=1\n");
            await using var service = new SecureWorkspaceService(paths, new PathSecurity());

            var recovered = await service.TryOpenExistingAsync(workspaceId);

            Assert.NotNull(recovered);
            Assert.Equal(workspaceId, recovered.Id);
            Assert.Equal(root, recovered.Paths.RootDirectory);
            await recovered.DisposeAsync();
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Retained_workspace_releases_lock_without_deleting_project_data()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            await using var service = new SecureWorkspaceService(paths, new PathSecurity());
            var workspace = await service.CreateAsync();
            var root = workspace.Paths.RootDirectory;

            service.Retain(workspace);
            await workspace.DisposeAsync();

            Assert.True(Directory.Exists(root));
            Assert.Equal(0, await service.CleanupAbandonedAsync());
            var reopened = await service.TryOpenExistingAsync(workspace.Id);
            Assert.NotNull(reopened);
            await reopened.DisposeAsync();
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Explicit_removal_deletes_only_exact_active_retained_workspace()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            await using var service = new SecureWorkspaceService(paths, new PathSecurity());
            var retained = await service.CreateAsync();
            await using var other = await service.CreateAsync();
            service.Retain(retained);
            var retainedRoot = retained.Paths.RootDirectory;

            var removed = await service.RemoveAsync(retained);

            Assert.True(removed);
            Assert.False(Directory.Exists(retainedRoot));
            Assert.True(Directory.Exists(other.Paths.RootDirectory));
            Assert.False(await service.RemoveAsync(retained));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Startup_detects_stale_retained_workspace_without_automatic_mutation()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            string workspaceId;
            string root;
            await using (var creator = new SecureWorkspaceService(paths, new PathSecurity()))
            {
                var workspace = await creator.CreateAsync();
                workspaceId = workspace.Id;
                root = workspace.Paths.RootDirectory;
                creator.Retain(workspace);
                await workspace.DisposeAsync();
            }

            await using var recovery = new SecureWorkspaceService(paths, new PathSecurity());
            await recovery.StartAsync(CancellationToken.None);

            var candidate = Assert.Single(recovery.DetectedCandidates);
            Assert.Equal(workspaceId, candidate.WorkspaceId);
            Assert.Equal(WorkspaceRecoveryState.StaleRecoverable, candidate.State);
            Assert.True(candidate.Retained);
            Assert.True(candidate.CanRecover);
            Assert.True(candidate.CanCleanup);
            Assert.NotNull(candidate.PreviousProcessId);
            Assert.NotNull(candidate.CreatedAtUtc);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Explicit_recovery_reopens_exact_stale_workspace_and_removes_offer()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            var workspaceId = await CreateRetainedWorkspaceAsync(paths);
            await using var recovery = new SecureWorkspaceService(paths, new PathSecurity());
            Assert.True((await recovery.DetectAsync()).Succeeded);

            var result = await recovery.RecoverAsync(workspaceId);

            Assert.True(result.Succeeded, result.DiagnosticCode);
            Assert.Equal(workspaceId, result.Workspace!.Id);
            Assert.Empty(recovery.DetectedCandidates);
            recovery.Retain(result.Workspace);
            await result.Workspace.DisposeAsync();
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Incomplete_stale_workspace_is_cleanup_only_and_exact_cleanup_preserves_other_roots()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            paths.EnsureDirectoriesExist();
            var workspaceId = new string('b', 32);
            var workspaceRoot = Path.Combine(paths.WorkspacesDirectory, workspaceId);
            Directory.CreateDirectory(workspaceRoot);
            await WriteSessionMarkerAsync(workspaceRoot);
            var sentinel = Path.Combine(paths.ProjectsDirectory, "keep.audproj");
            await File.WriteAllTextAsync(sentinel, "keep");
            await using var recovery = new SecureWorkspaceService(paths, new PathSecurity());

            var scan = await recovery.DetectAsync();
            var candidate = Assert.Single(scan.Candidates);
            var cleanup = await recovery.CleanupAsync(workspaceId);

            Assert.Equal(WorkspaceRecoveryState.StaleCleanupOnly, candidate.State);
            Assert.False(candidate.CanRecover);
            Assert.True(candidate.CanCleanup);
            Assert.True(cleanup.Succeeded, cleanup.DiagnosticCode);
            Assert.False(Directory.Exists(workspaceRoot));
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Invalid_marker_is_reported_unsafe_and_cannot_be_recovered_or_cleaned()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            paths.EnsureDirectoriesExist();
            var workspaceId = new string('c', 32);
            var root = Path.Combine(paths.WorkspacesDirectory, workspaceId);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".workspace.lock"), "version=999\n");
            await using var recovery = new SecureWorkspaceService(paths, new PathSecurity());

            var candidate = Assert.Single((await recovery.DetectAsync()).Candidates);
            var recover = await recovery.RecoverAsync(workspaceId);
            var cleanup = await recovery.CleanupAsync(workspaceId);

            Assert.Equal(WorkspaceRecoveryState.Unsafe, candidate.State);
            Assert.False(candidate.CanRecover);
            Assert.False(candidate.CanCleanup);
            Assert.Equal(WorkspaceRecoveryActionFailureReason.UnsafeWorkspace, recover.FailureReason);
            Assert.Equal(WorkspaceRecoveryActionFailureReason.UnsafeWorkspace, cleanup.FailureReason);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Active_lock_wins_over_pid_or_timestamp_and_blocks_cleanup_race()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            await using var owner = new SecureWorkspaceService(paths, new PathSecurity());
            await using var active = await owner.CreateAsync();
            await using var observer = new SecureWorkspaceService(paths, new PathSecurity());

            var candidate = Assert.Single((await observer.DetectAsync()).Candidates);
            var cleanup = await observer.CleanupAsync(active.Id);

            Assert.Equal(WorkspaceRecoveryState.ActiveOrInaccessible, candidate.State);
            Assert.False(candidate.CanCleanup);
            Assert.Equal(WorkspaceRecoveryActionFailureReason.ActiveOrInaccessible, cleanup.FailureReason);
            Assert.True(Directory.Exists(active.Paths.RootDirectory));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Cancelled_discovery_does_not_publish_partial_inventory()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var paths = new AppPaths(testRoot);
            await using var recovery = new SecureWorkspaceService(paths, new PathSecurity());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await recovery.DetectAsync(cancellation.Token);

            Assert.False(result.Succeeded);
            Assert.True(result.Cancelled);
            Assert.Empty(result.Candidates);
            Assert.Empty(recovery.DetectedCandidates);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task<string> CreateRetainedWorkspaceAsync(AppPaths paths)
    {
        await using var creator = new SecureWorkspaceService(paths, new PathSecurity());
        var workspace = await creator.CreateAsync();
        creator.Retain(workspace);
        var id = workspace.Id;
        await workspace.DisposeAsync();
        return id;
    }

    private static Task WriteSessionMarkerAsync(string workspaceRoot) => File.WriteAllTextAsync(
        Path.Combine(workspaceRoot, ".workspace.lock"),
        $"version=1\nsessionId={new string('d', 32)}\nprocessId=123\ncreatedUtc={DateTimeOffset.UtcNow:O}\n");

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string CreateTestRoot()
    {
        return Path.Combine(Path.GetTempPath(), "AuditionModStudio.Tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string testRoot)
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
