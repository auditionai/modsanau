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
