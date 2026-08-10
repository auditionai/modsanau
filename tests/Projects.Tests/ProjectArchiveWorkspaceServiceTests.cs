using System.Security.Cryptography;
using System.Text.Json;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ProjectArchiveWorkspaceServiceTests
{
    [Fact]
    public async Task Create_project_archive_workspace_succeeds_and_validates()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);

        var validation = await context.Service.ValidateAsync(workspace);

        Assert.True(validation.IsValid);
        Assert.Equal(ProjectArchiveWorkspaceState.Ready, workspace.Descriptor.State);
        Assert.NotEqual(Guid.Empty, workspace.Descriptor.ProjectId);
    }

    [Fact]
    public async Task Working_archive_is_verified_copy_and_pristine_source_is_unchanged()
    {
        await using var context = TestContext.Create();
        var sourceHashBefore = await ComputeHashAsync(context.SourcePath);
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);
        var workingPath = GetWorkingArchivePath(workspace);

        Assert.Equal(sourceHashBefore, await ComputeHashAsync(context.SourcePath));
        Assert.Equal(sourceHashBefore, await ComputeHashAsync(workingPath));
        Assert.Equal(sourceHashBefore, workspace.Descriptor.ArchiveTemplate.SourceSha256);
        Assert.Equal(sourceHashBefore, workspace.Descriptor.WorkingArchiveSha256);
    }

    [Theory]
    [InlineData("015.ab")]
    [InlineData("021.acv")]
    [InlineData("archive.future")]
    public async Task Working_archive_preserves_exact_filename_for_any_extension(string fileName)
    {
        await using var context = TestContext.Create(fileName: fileName);
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);

        Assert.Equal(fileName, Path.GetFileName(GetWorkingArchivePath(workspace)));
    }

    [Fact]
    public async Task Same_template_creates_isolated_working_paths_for_two_projects()
    {
        await using var context = TestContext.Create();
        var results = await Task.WhenAll(context.CreateAsync(), context.CreateAsync());
        await using var first = AssertWorkspace(results[0]);
        await using var second = AssertWorkspace(results[1]);

        Assert.NotEqual(first.Descriptor.ProjectId, second.Descriptor.ProjectId);
        Assert.NotEqual(first.Descriptor.WorkspaceId, second.Descriptor.WorkspaceId);
        Assert.NotEqual(GetWorkingArchivePath(first), GetWorkingArchivePath(second));
    }

    [Theory]
    [InlineData("Mod Login đẹp nhất của tôi ../")]
    [InlineData(@"..\Windows")]
    [InlineData("folder/name")]
    public async Task Unsafe_project_display_name_is_rejected_without_allocating_workspace(string displayName)
    {
        await using var context = TestContext.Create(displayName: displayName);

        var result = await context.CreateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectArchiveWorkspaceFailureReason.InvalidRequest, result.FailureReason);
        Assert.Empty(Directory.EnumerateDirectories(context.AppPaths.WorkspacesDirectory));
    }

    [Theory]
    [InlineData("Sàn Aubiz 015")]
    [InlineData("Dự án có khoảng trắng")]
    public async Task Unicode_and_spaces_in_display_name_are_metadata_only(string displayName)
    {
        await using var context = TestContext.Create(displayName: displayName, rootName: "Thư mục kiểm thử có khoảng trắng");
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);

        Assert.Equal(displayName, workspace.Descriptor.DisplayName);
        Assert.DoesNotContain(displayName, workspace.ArchiveWorkspace.SecureWorkspace.Paths.RootDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traversal_in_pristine_source_metadata_is_rejected()
    {
        await using var context = TestContext.Create(sourceRelativePath: @"..\015.ab");

        var result = await context.CreateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectArchiveWorkspaceFailureReason.SourceMissing, result.FailureReason);
    }

    [Fact]
    public async Task Pristine_source_is_outside_writable_workspace_and_survives_cleanup()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        var workspace = AssertWorkspace(result);
        var workspaceRoot = workspace.ArchiveWorkspace.SecureWorkspace.Paths.RootDirectory;

        Assert.False(context.SourcePath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase));
        await workspace.DisposeAsync();

        Assert.False(Directory.Exists(workspaceRoot));
        Assert.True(File.Exists(context.SourcePath));
    }

    [Fact]
    public async Task Extracted_and_build_output_directories_are_separate_and_managed()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);
        var paths = workspace.ArchiveWorkspace.SecureWorkspace.Paths;
        var extractedTarget = Path.Combine(paths.ExtractedDirectory, workspace.ArchiveWorkspace.ExtractDirectoryRelativePath);

        Assert.True(Directory.Exists(extractedTarget));
        Assert.True(Directory.Exists(paths.BuildOutputDirectory));
        Assert.NotEqual(paths.WorkingDirectory, paths.ExtractedDirectory);
        Assert.NotEqual(paths.WorkingDirectory, paths.BuildOutputDirectory);
        Assert.NotEqual(paths.ExtractedDirectory, paths.BuildOutputDirectory);
    }

    [Fact]
    public async Task Keydat_path_is_not_invented_before_keydat_service_runs()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);

        Assert.Null(workspace.Descriptor.WorkingKeydatRelativePath);
        Assert.Empty(Directory.EnumerateFiles(
            workspace.ArchiveWorkspace.SecureWorkspace.Paths.WorkingDirectory,
            "*.keydat"));
    }

    [Fact]
    public async Task Missing_source_returns_structured_failure()
    {
        await using var context = TestContext.Create();
        File.Delete(context.SourcePath);

        var result = await context.CreateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectArchiveWorkspaceFailureReason.SourceMissing, result.FailureReason);
    }

    [Fact]
    public async Task Expected_source_hash_mismatch_returns_structured_failure()
    {
        await using var context = TestContext.Create(expectedHash: new string('A', 64));

        var result = await context.CreateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectArchiveWorkspaceFailureReason.SourceHashMismatch, result.FailureReason);
        Assert.Empty(Directory.EnumerateDirectories(context.AppPaths.WorkspacesDirectory));
    }

    [Fact]
    public async Task Cancelled_creation_returns_structured_result_without_partial_workspace()
    {
        await using var context = TestContext.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await context.Service.CreateAsync(context.Request, cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectArchiveWorkspaceFailureReason.Cancelled, result.FailureReason);
        Assert.Empty(Directory.EnumerateDirectories(context.AppPaths.WorkspacesDirectory));
    }

    [Fact]
    public async Task Manifest_failure_cleans_partial_workspace_and_never_returns_ready()
    {
        await using var context = TestContext.Create(failManifestWrite: true);

        var result = await context.CreateAsync();

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Equal(ProjectArchiveWorkspaceFailureReason.ManifestWriteFailed, result.FailureReason);
        Assert.Empty(Directory.EnumerateDirectories(context.AppPaths.WorkspacesDirectory));
        Assert.True(File.Exists(context.SourcePath));
    }

    [Fact]
    public async Task Manifest_is_written_atomically_with_schema_and_relative_paths()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);
        var root = workspace.ArchiveWorkspace.SecureWorkspace.Paths.RootDirectory;
        var manifestPath = Path.Combine(root, workspace.Descriptor.ManifestRelativePath);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));

        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ready", json.RootElement.GetProperty("state").GetString());
        Assert.False(Path.IsPathRooted(json.RootElement.GetProperty("workingArchiveRelativePath").GetString()));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Corrupt_manifest_is_reported_without_silent_repair()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);
        var manifestPath = Path.Combine(
            workspace.ArchiveWorkspace.SecureWorkspace.Paths.RootDirectory,
            workspace.Descriptor.ManifestRelativePath);
        await File.WriteAllTextAsync(manifestPath, "{ corrupt json");

        var validation = await context.Service.ValidateAsync(workspace);

        Assert.False(validation.IsValid);
        Assert.Equal(ProjectArchiveWorkspaceValidationStatus.ManifestCorrupt, validation.Status);
    }

    [Fact]
    public async Task Missing_working_archive_is_reported()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);
        File.Delete(GetWorkingArchivePath(workspace));

        var validation = await context.Service.ValidateAsync(workspace);

        Assert.Equal(ProjectArchiveWorkspaceValidationStatus.WorkingArchiveMissing, validation.Status);
    }

    [Fact]
    public async Task Missing_extracted_directory_is_reported()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);
        Directory.Delete(Path.Combine(
            workspace.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
            workspace.ArchiveWorkspace.ExtractDirectoryRelativePath));

        var validation = await context.Service.ValidateAsync(workspace);

        Assert.Equal(ProjectArchiveWorkspaceValidationStatus.ExtractedDirectoryMissing, validation.Status);
    }

    [Fact]
    public async Task Missing_build_output_directory_is_reported()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);
        Directory.Delete(workspace.ArchiveWorkspace.SecureWorkspace.Paths.BuildOutputDirectory);

        var validation = await context.Service.ValidateAsync(workspace);

        Assert.Equal(ProjectArchiveWorkspaceValidationStatus.BuildOutputDirectoryMissing, validation.Status);
    }

    [Fact]
    public async Task Modified_working_archive_hash_is_reported()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);
        await File.AppendAllTextAsync(GetWorkingArchivePath(workspace), "modified");

        var validation = await context.Service.ValidateAsync(workspace);

        Assert.Equal(ProjectArchiveWorkspaceValidationStatus.WorkingArchiveHashMismatch, validation.Status);
    }

    [Fact]
    public async Task Concurrent_creation_has_no_workspace_collision()
    {
        await using var context = TestContext.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => context.CreateAsync()));
        var workspaces = results.Select(AssertWorkspace).ToArray();
        try
        {
            Assert.Equal(12, workspaces.Select(item => item.Descriptor.WorkspaceId).Distinct().Count());
            Assert.Equal(12, workspaces.Select(GetWorkingArchivePath).Distinct().Count());
        }
        finally
        {
            foreach (var workspace in workspaces)
            {
                await workspace.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Cleaning_project_A_does_not_affect_project_B()
    {
        await using var context = TestContext.Create();
        var first = AssertWorkspace(await context.CreateAsync());
        await using var second = AssertWorkspace(await context.CreateAsync());
        var firstRoot = first.ArchiveWorkspace.SecureWorkspace.Paths.RootDirectory;
        var secondRoot = second.ArchiveWorkspace.SecureWorkspace.Paths.RootDirectory;

        await first.DisposeAsync();

        Assert.False(Directory.Exists(firstRoot));
        Assert.True(Directory.Exists(secondRoot));
        Assert.True((await context.Service.ValidateAsync(second)).IsValid);
    }

    [Fact]
    public async Task Descriptor_keeps_exact_template_version_engine_and_region()
    {
        await using var context = TestContext.Create(templateVersion: "1.0.7");
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);

        Assert.Equal("template_015", workspace.Descriptor.ArchiveTemplate.TemplateId);
        Assert.Equal("1.0.7", workspace.Descriptor.ArchiveTemplate.TemplateVersion);
        Assert.Equal(ArchiveEngineType.AcvTool5, workspace.Descriptor.ArchiveTemplate.EngineType);
        Assert.Equal("audition_vn", workspace.Descriptor.ArchiveTemplate.RegionProfileId);
    }

    [Fact]
    public async Task Workspace_exposes_archive_context_ready_for_archive_service()
    {
        await using var context = TestContext.Create();
        var result = await context.CreateAsync();
        await using var workspace = AssertWorkspace(result);

        Assert.Equal(context.Template.FileName, workspace.ArchiveWorkspace.WorkingArchiveRelativePath);
        Assert.Equal(context.Template.ExpectedExtractFolderName, workspace.ArchiveWorkspace.ExtractDirectoryRelativePath);
        Assert.True(File.Exists(Path.Combine(
            workspace.ArchiveWorkspace.SecureWorkspace.Paths.WorkingDirectory,
            workspace.ArchiveWorkspace.WorkingArchiveRelativePath)));
    }

    private static IProjectArchiveWorkspace AssertWorkspace(ProjectArchiveWorkspaceCreateResult result)
    {
        Assert.True(result.Succeeded, result.DiagnosticCode);
        return Assert.IsAssignableFrom<IProjectArchiveWorkspace>(result.Workspace);
    }

    private static string GetWorkingArchivePath(IProjectArchiveWorkspace workspace) => Path.Combine(
        workspace.ArchiveWorkspace.SecureWorkspace.Paths.WorkingDirectory,
        workspace.ArchiveWorkspace.WorkingArchiveRelativePath);

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly SecureWorkspaceService _secureWorkspaceService;

        private TestContext(
            string fileName,
            string displayName,
            string? expectedHash,
            string sourceRelativePath,
            string? templateVersion,
            string? rootName,
            bool failManifestWrite)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                rootName ?? "ProjectArchiveWorkspaceTests",
                Guid.NewGuid().ToString("N"));
            AppPaths = new TestAppPaths(Root);
            AppPaths.EnsureDirectoriesExist();
            var pathSecurity = new PathSecurity();
            _secureWorkspaceService = new(AppPaths, pathSecurity);

            SourceRoot = Path.Combine(Root, "PristineSource");
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(SourceRoot, "templates")).FullName;
            SourcePath = Path.Combine(sourceDirectory, fileName);
            File.WriteAllBytes(SourcePath, [1, 2, 3, 4, 5]);
            Template = new(
                "template_015",
                fileName,
                sourceRelativePath,
                ArchiveEngineType.AcvTool5,
                "audition_vn",
                "extracted assets",
                templateVersion,
                expectedHash);
            Request = new(displayName, Template, new PristineArchiveSource(SourceRoot));
            IProjectArchiveWorkspaceManifestStore manifestStore = failManifestWrite
                ? new FailingManifestStore()
                : new ProjectArchiveWorkspaceManifestStore(pathSecurity);
            Service = new(
                AppPaths,
                pathSecurity,
                _secureWorkspaceService,
                manifestStore);
        }

        public string Root { get; }

        public TestAppPaths AppPaths { get; }

        public string SourceRoot { get; }

        public string SourcePath { get; }

        public AuditionArchiveTemplate Template { get; }

        public ProjectArchiveWorkspaceCreateRequest Request { get; }

        public ProjectArchiveWorkspaceService Service { get; }

        public static TestContext Create(
            string fileName = "015.ab",
            string displayName = "Project 015",
            string? expectedHash = null,
            string? sourceRelativePath = null,
            string? templateVersion = "1.0",
            string? rootName = null,
            bool failManifestWrite = false) => new(
                fileName,
                displayName,
                expectedHash,
                sourceRelativePath ?? Path.Combine("templates", fileName),
                templateVersion,
                rootName,
                failManifestWrite);

        public Task<ProjectArchiveWorkspaceCreateResult> CreateAsync() => Service.CreateAsync(Request);

        public async ValueTask DisposeAsync()
        {
            await _secureWorkspaceService.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FailingManifestStore : IProjectArchiveWorkspaceManifestStore
    {
        public Task WriteAsync(
            string workspaceRoot,
            ProjectArchiveWorkspaceDescriptor descriptor,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Controlled manifest write failure."));

        public Task<ProjectArchiveWorkspaceDescriptor> ReadAsync(
            string workspaceRoot,
            string manifestRelativePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestAppPaths : IAppPaths
    {
        public TestAppPaths(string root)
        {
            RootDirectory = root;
            LogsDirectory = Path.Combine(root, "Logs");
            CacheDirectory = Path.Combine(root, "Cache");
            ProjectsDirectory = Path.Combine(root, "Projects");
            TempDirectory = Path.Combine(root, "Temp");
            SettingsDirectory = Path.Combine(root, "Settings");
            DownloadsDirectory = Path.Combine(root, "Downloads");
            SecureTemplateCacheDirectory = Path.Combine(root, "SecureTemplateCache");
            BackupsDirectory = Path.Combine(root, "Backups");
            WorkspacesDirectory = Path.Combine(TempDirectory, "Workspaces");
            ManagedDirectories =
            [
                LogsDirectory,
                CacheDirectory,
                ProjectsDirectory,
                TempDirectory,
                SettingsDirectory,
                DownloadsDirectory,
                SecureTemplateCacheDirectory,
                BackupsDirectory,
                WorkspacesDirectory,
            ];
        }

        public string RootDirectory { get; }
        public string LogsDirectory { get; }
        public string CacheDirectory { get; }
        public string ProjectsDirectory { get; }
        public string TempDirectory { get; }
        public string SettingsDirectory { get; }
        public string DownloadsDirectory { get; }
        public string SecureTemplateCacheDirectory { get; }
        public string BackupsDirectory { get; }
        public string WorkspacesDirectory { get; }
        public IReadOnlyCollection<string> ManagedDirectories { get; }

        public void EnsureDirectoriesExist()
        {
            foreach (var directory in ManagedDirectories)
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
}
