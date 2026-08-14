using System.Security.Cryptography;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ProjectBuildServiceTests
{
    [Fact]
    public async Task Build_saves_validates_packs_temporary_copy_verifies_hashes_and_promotes_output()
    {
        await using var context = new Context();
        var sourceHash = await HashAsync(context.SourceArchivePath);
        var phases = new List<ProjectBuildPhase>();

        var result = await context.Service.BuildAsync(
            new(context.Project, context.ProjectWorkspace),
            new CallbackProgress<ProjectBuildProgress>(item => phases.Add(item.Phase)));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(2, context.Store.SaveCount);
        Assert.Equal(1, context.Validator.CallCount);
        Assert.Equal(1, context.ArchiveService.PackCount);
        Assert.Equal(sourceHash, await HashAsync(context.SourceArchivePath));
        Assert.Equal("packed-archive", await File.ReadAllTextAsync(context.OutputPath));
        Assert.Equal(await HashAsync(context.OutputPath), result.OutputSha256!.Value.Value);
        Assert.Equal(ProjectBuildStatus.Succeeded, result.Project!.BuildState.Status);
        Assert.Equal("BuildOutput/Output/015.ab", result.OutputArchiveRelativePath!.Value.Value);
        Assert.Contains(ProjectBuildPhase.Preparing, phases);
        Assert.Contains(ProjectBuildPhase.Validating, phases);
        Assert.Contains(ProjectBuildPhase.Packing, phases);
        Assert.Contains(ProjectBuildPhase.Verifying, phases);
        Assert.Equal(ProjectBuildPhase.Completed, phases[^1]);
        Assert.True(context.BuildWorkspaceService.LastWorkspaceDisposed);
        Assert.False(Directory.Exists(context.BuildWorkspaceService.LastWorkspaceRoot));
    }

    [Fact]
    public async Task Validation_failure_stops_before_temporary_workspace_and_pack()
    {
        await using var context = new Context(validationCanBuild: false);

        var result = await context.Service.BuildAsync(new(context.Project, context.ProjectWorkspace));

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectBuildFailureReason.ValidationFailed, result.FailureReason);
        Assert.Equal(1, context.Store.SaveCount);
        Assert.Equal(0, context.BuildWorkspaceService.CreateCount);
        Assert.Equal(0, context.ArchiveService.PackCount);
        Assert.False(File.Exists(context.OutputPath));
    }

    [Fact]
    public async Task Pack_failure_does_not_promote_output_and_cleans_temporary_workspace()
    {
        await using var context = new Context(packSucceeds: false);

        var result = await context.Service.BuildAsync(new(context.Project, context.ProjectWorkspace));

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectBuildFailureReason.PackFailed, result.FailureReason);
        Assert.False(File.Exists(context.OutputPath));
        Assert.True(context.BuildWorkspaceService.LastWorkspaceDisposed);
    }

    [Fact]
    public async Task Modified_project_working_archive_is_rejected_before_pack()
    {
        await using var context = new Context();
        await File.AppendAllTextAsync(context.SourceArchivePath, "tampered");

        var result = await context.Service.BuildAsync(new(context.Project, context.ProjectWorkspace));

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectBuildFailureReason.WorkspacePreparationFailed, result.FailureReason);
        Assert.Equal(0, context.ArchiveService.PackCount);
        Assert.False(File.Exists(context.OutputPath));
    }

    [Fact]
    public async Task Final_save_failure_restores_previous_output_bytes()
    {
        await using var context = new Context(finalSaveSucceeds: false);
        Directory.CreateDirectory(Path.GetDirectoryName(context.OutputPath)!);
        await File.WriteAllTextAsync(context.OutputPath, "previous-output");

        var result = await context.Service.BuildAsync(new(context.Project, context.ProjectWorkspace));

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectBuildFailureReason.SaveFailed, result.FailureReason);
        Assert.Equal("previous-output", await File.ReadAllTextAsync(context.OutputPath));
        Assert.Equal(ProjectBuildStatus.NotBuilt, context.Project.BuildState.Status);
    }

    [Fact]
    public async Task Exception_after_promotion_still_restores_previous_output_bytes()
    {
        await using var context = new Context(finalSaveThrows: true);
        Directory.CreateDirectory(Path.GetDirectoryName(context.OutputPath)!);
        await File.WriteAllTextAsync(context.OutputPath, "previous-output");

        var result = await context.Service.BuildAsync(new(context.Project, context.ProjectWorkspace));

        Assert.False(result.Succeeded);
        Assert.Equal("previous-output", await File.ReadAllTextAsync(context.OutputPath));
    }

    [Fact]
    public async Task Cancelled_pack_returns_cancelled_and_never_promotes_output()
    {
        await using var context = new Context(packCancelled: true);

        var result = await context.Service.BuildAsync(new(context.Project, context.ProjectWorkspace));

        Assert.True(result.Cancelled);
        Assert.Equal(ProjectBuildPhase.Cancelled, result.FinalPhase);
        Assert.False(File.Exists(context.OutputPath));
        Assert.True(context.BuildWorkspaceService.LastWorkspaceDisposed);
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly string _root;

        public Context(
            bool validationCanBuild = true,
            bool packSucceeds = true,
            bool packCancelled = false,
            bool finalSaveSucceeds = true,
            bool finalSaveThrows = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "ProjectBuildServiceTests", Guid.NewGuid().ToString("N"));
            var projectRoot = Path.Combine(_root, "Project");
            var projectPaths = CreatePaths(projectRoot);
            CreateDirectories(projectPaths);
            var secure = new TestSecureWorkspace(
                "0123456789abcdef0123456789abcdef", projectPaths, deleteOnDispose: false);
            var template = Template();
            var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            ProjectWorkspace = new TestProjectWorkspace(secure, template, projectId);
            SourceArchivePath = Path.Combine(projectPaths.WorkingDirectory, "015.ab");
            File.WriteAllText(SourceArchivePath, "working-archive");
            var texturePath = Path.Combine(projectPaths.ExtractedDirectory, "015", "Texture", "logo.dds");
            Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
            File.WriteAllText(texturePath, "dds");
            OutputPath = Path.Combine(projectPaths.BuildOutputDirectory, "Output", "015.ab");
            var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
            Project = AuditionProject.Create(
                1, projectId, "Project", new GameId("audition"), new ModId("login_mod"),
                ProjectWorkspace.Descriptor.ArchiveTemplate.Identity,
                new(ProjectWorkspace.Descriptor.WorkspaceId, new("Working/015.ab"), new("Extracted/015")),
                [], [], [], new(0, 0, null, []),
                new(ProjectBuildStatus.NotBuilt, null, null, null), now, now).Project!;

            Store = new SequenceStore(finalSaveSucceeds, finalSaveThrows);
            Validator = new StubValidator(validationCanBuild);
            BuildWorkspaceService = new StubWorkspaceService(Path.Combine(_root, "BuildWorkspaces"));
            ArchiveService = new StubArchiveService(packSucceeds, packCancelled);
            Service = new(Store, Validator, BuildWorkspaceService, ArchiveService, new PathSecurity(),
                ProjectBuildOptions.Default, new FixedTimeProvider());
        }

        public AuditionProject Project { get; }
        public TestProjectWorkspace ProjectWorkspace { get; }
        public string SourceArchivePath { get; }
        public string OutputPath { get; }
        public SequenceStore Store { get; }
        public StubValidator Validator { get; }
        public StubWorkspaceService BuildWorkspaceService { get; }
        public StubArchiveService ArchiveService { get; }
        public ProjectBuildService Service { get; }

        public ValueTask DisposeAsync()
        {
            Service.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceStore(bool finalSaveSucceeds, bool finalSaveThrows) : IAuditionProjectStore
    {
        public int SaveCount { get; private set; }
        public AuditionProject? LastSavedProject { get; private set; }

        public Task<AuditionProjectStoreResult> SaveAsync(
            AuditionProject project,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            LastSavedProject = project;
            if (SaveCount == 2 && finalSaveThrows)
            {
                throw new IOException("Test persistence exception.");
            }
            return Task.FromResult(SaveCount == 2 && !finalSaveSucceeds
                ? AuditionProjectStoreResult.Failure(AuditionProjectStoreFailureReason.IoFailure, "TEST_SAVE_FAILED")
                : AuditionProjectStoreResult.Success("project.audproj"));
        }

        public Task<AuditionProjectStoreResult> DeleteAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AuditionProjectLoadResult> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubValidator(bool canBuild) : IProjectValidator
    {
        public int CallCount { get; private set; }
        public Task<ProjectValidationResult> ValidateAsync(
            ProjectValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ProjectValidationResult.Success(canBuild
                ? []
                : [new(ProjectValidationSeverity.Error, ProjectValidationIssueKind.MissingFile, "TEST_INVALID")]));
        }
    }

    private sealed class StubArchiveService(bool packSucceeds, bool packCancelled) : IAuditionArchiveService
    {
        public int PackCount { get; private set; }
        public Task<ArchiveExtractResult> ExtractAsync(ArchiveExtractRequest request, IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<ArchivePackResult> PackAsync(
            ArchivePackRequest request,
            IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            PackCount++;
            Assert.True(File.Exists(Path.Combine(
                request.Workspace.SecureWorkspace.Paths.ExtractedDirectory, "015", "Texture", "logo.dds")));
            if (packCancelled)
            {
                return new(new(ArchiveOperation.Pack, ArchiveOperationState.Cancelled, false, 0,
                    ArchiveFailureReason.Cancelled, null, []));
            }
            if (!packSucceeds)
            {
                return new(new(ArchiveOperation.Pack, ArchiveOperationState.Failed, false, 0,
                    ArchiveFailureReason.RunnerFailed, null, []));
            }

            var path = Path.Combine(request.Workspace.SecureWorkspace.Paths.WorkingDirectory,
                request.Workspace.WorkingArchiveRelativePath);
            await File.WriteAllTextAsync(path, "packed-archive", cancellationToken);
            progress?.Report(new(ArchiveOperation.Pack, ArchiveOperationState.Packing, 1));
            return new(new(ArchiveOperation.Pack, ArchiveOperationState.Completed, true, 1,
                ArchiveFailureReason.None, request.Workspace.WorkingArchiveRelativePath, []));
        }
    }

    private sealed class StubWorkspaceService(string root) : ISecureWorkspaceService
    {
        public int CreateCount { get; private set; }
        public bool LastWorkspaceDisposed { get; private set; }
        public string LastWorkspaceRoot { get; private set; } = string.Empty;

        public ValueTask<ISecureWorkspace> CreateAsync(CancellationToken cancellationToken = default)
        {
            CreateCount++;
            LastWorkspaceRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var paths = CreatePaths(LastWorkspaceRoot);
            CreateDirectories(paths);
            return ValueTask.FromResult<ISecureWorkspace>(new TestSecureWorkspace(
                Guid.NewGuid().ToString("N"), paths, deleteOnDispose: true,
                () => LastWorkspaceDisposed = true));
        }

        public Task<int> CleanupAbandonedAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    public sealed class TestProjectWorkspace : IProjectArchiveWorkspace
    {
        public TestProjectWorkspace(ISecureWorkspace secure, AuditionArchiveTemplate template, Guid projectId)
        {
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = DateTimeOffset.UtcNow;
            Descriptor = new(1, projectId, "Project", secure.Id,
                new(template.TemplateId.Value, template.TemplateVersion!.Value.Value,
                    template.ExpectedSha256!.Value.Value, template.EngineType, template.RegionProfileId,
                    template.CompatibleGameBuild!.Value.Value),
                "Working/015.ab", "Extracted/015", "BuildOutput", null,
                ".project-archive-workspace.json", HashText("working-archive"), now, now,
                ProjectArchiveWorkspaceState.Ready);
        }
        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSecureWorkspace(
        string id,
        SecureWorkspacePaths paths,
        bool deleteOnDispose,
        Action? disposed = null) : ISecureWorkspace
    {
        public string Id { get; } = id;
        public SecureWorkspacePaths Paths { get; } = paths;
        public string ResolveRelativePath(string relativePath)
        {
            var path = Path.GetFullPath(Path.Combine(Paths.RootDirectory, relativePath));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Paths.RootDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Path escaped workspace.", nameof(relativePath));
            }
            return path;
        }

        public ValueTask DisposeAsync()
        {
            if (deleteOnDispose && Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
            disposed?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 12, 13, 0, 0, TimeSpan.Zero);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static AuditionArchiveTemplate Template() => new(
        "archive-015", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
        "audition_vn", "015", "1", new string('A', 64), "audition-vn-2026");

    private static SecureWorkspacePaths CreatePaths(string root) => new(
        root, Path.Combine(root, "Working"), Path.Combine(root, "Extracted"), Path.Combine(root, "BuildOutput"));

    private static void CreateDirectories(SecureWorkspacePaths paths)
    {
        Directory.CreateDirectory(paths.WorkingDirectory);
        Directory.CreateDirectory(paths.ExtractedDirectory);
        Directory.CreateDirectory(paths.BuildOutputDirectory);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
