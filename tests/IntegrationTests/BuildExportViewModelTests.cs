using System.Collections.Immutable;
using AuditionModStudio.App.Shell;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;

namespace IntegrationTests;

public sealed class BuildExportViewModelTests
{
    [Fact]
    public void Refresh_uses_project_archive_name_and_requires_destination()
    {
        var context = new Context();

        context.ViewModel.RefreshProject();

        Assert.Equal("015.ab", context.ViewModel.OutputFileName);
        Assert.Contains("NotBuilt", context.ViewModel.ProjectStatus, StringComparison.Ordinal);
        Assert.False(context.ViewModel.CanStart);

        context.ViewModel.OutputDirectory = context.OutputDirectory;

        Assert.True(context.ViewModel.CanStart);
    }

    [Fact]
    public async Task Start_builds_then_exports_through_one_typed_background_job()
    {
        var context = new Context();
        context.ViewModel.RefreshProject();
        context.ViewModel.OutputDirectory = context.OutputDirectory;

        await context.ViewModel.StartAsync();

        Assert.Equal(BackgroundTaskKind.Build, context.TaskManager.EnqueuedKind);
        Assert.Equal(1, context.BuildService.CallCount);
        Assert.Equal(1, context.ExportService.CallCount);
        Assert.Equal(ProjectBuildStatus.Succeeded, context.Session.Project!.BuildState.Status);
        Assert.Equal(Path.Combine(context.OutputDirectory, "015.ab"), context.ViewModel.FinalOutputPath);
        Assert.Equal(new string('B', 64), context.ViewModel.FinalSha256);
        Assert.False(context.ViewModel.IsBusy);
    }

    [Fact]
    public async Task Destination_collision_is_rejected_before_build_without_explicit_replace()
    {
        var context = new Context(ArchiveExportDestinationFailureReason.DestinationCollision);
        context.ViewModel.RefreshProject();
        context.ViewModel.OutputDirectory = context.OutputDirectory;

        await context.ViewModel.StartAsync();

        Assert.Equal(0, context.BuildService.CallCount);
        Assert.Equal(0, context.ExportService.CallCount);
        Assert.Contains("already exists", context.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_targets_the_manager_owned_background_task()
    {
        var project = CreateProject();
        var workspace = new TestWorkspace();
        var session = new TestSession(project, workspace);
        var manager = new ControlledTaskManager();
        var viewModel = new BuildExportViewModel(
            session,
            new TestBuildService(),
            new TestDestinationValidator(ArchiveExportDestinationFailureReason.None),
            new TestExportService(),
            manager);
        viewModel.RefreshProject();
        viewModel.OutputDirectory = Path.Combine("C:\\", "Exports");

        var running = viewModel.StartAsync();
        await manager.WaitUntilQueued.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.CanCancel);
        Assert.True(viewModel.Cancel());
        await running;

        Assert.Equal(manager.TaskId, manager.CancelledTaskId);
        Assert.Contains("cancelled", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void Xaml_contract_exposes_accessible_file_only_workflow_without_copy_logic()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "AuditionModStudio.App", "Workspace", "ProjectWorkspacePage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root, "src", "AuditionModStudio.App", "Workspace", "ProjectWorkspacePage.xaml.cs"));

        Assert.Contains("Build &amp; Export", xaml, StringComparison.Ordinal);
        Assert.Contains("Choose export folder", xaml, StringComparison.Ordinal);
        Assert.Contains("Archive filename", xaml, StringComparison.Ordinal);
        Assert.Contains("Replace an existing archive", xaml, StringComparison.Ordinal);
        Assert.Contains("Build and export progress", xaml, StringComparison.Ordinal);
        Assert.Contains("FinalSha256", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"44\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Launch Game", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Game Path", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File.Copy", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Application_session_updates_build_state_only_for_exact_active_project_and_workspace()
    {
        await using var session = new ApplicationProjectSession();
        var project = CreateProject();
        var workspace = new TestWorkspace();
        var completed = CompleteBuild(project);
        await session.ActivateAsync(project, workspace);

        var updated = await session.TryUpdateProjectAsync(project, workspace, completed);

        Assert.True(updated);
        Assert.Same(completed, session.Project);
        Assert.Equal(0, workspace.DisposeCount);
    }

    [Fact]
    public async Task Application_session_rejects_stale_build_completion_after_project_switch()
    {
        await using var session = new ApplicationProjectSession();
        var oldProject = CreateProject();
        var oldWorkspace = new TestWorkspace();
        var currentProject = CreateProject(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var currentWorkspace = new TestWorkspace();
        await session.ActivateAsync(oldProject, oldWorkspace);
        await session.ActivateAsync(currentProject, currentWorkspace);

        var updated = await session.TryUpdateProjectAsync(
            oldProject, oldWorkspace, CompleteBuild(oldProject));

        Assert.False(updated);
        Assert.Same(currentProject, session.Project);
        Assert.Same(currentWorkspace, session.Workspace);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class Context
    {
        public Context(ArchiveExportDestinationFailureReason destinationFailure = ArchiveExportDestinationFailureReason.None)
        {
            Project = CreateProject();
            Workspace = new TestWorkspace();
            Session = new TestSession(Project, Workspace);
            BuildService = new TestBuildService();
            DestinationValidator = new TestDestinationValidator(destinationFailure);
            ExportService = new TestExportService();
            TaskManager = new ImmediateTaskManager();
            ViewModel = new(Session, BuildService, DestinationValidator, ExportService, TaskManager);
        }

        public string OutputDirectory { get; } = Path.Combine("C:\\", "Exports Unicode");
        public AuditionProject Project { get; }
        public TestWorkspace Workspace { get; }
        public TestSession Session { get; }
        public TestBuildService BuildService { get; }
        public TestDestinationValidator DestinationValidator { get; }
        public TestExportService ExportService { get; }
        public ImmediateTaskManager TaskManager { get; }
        public BuildExportViewModel ViewModel { get; }
    }

    private static AuditionProject CreateProject(Guid? projectId = null)
    {
        var now = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);
        return AuditionProject.Create(
            AuditionProject.CurrentSchemaVersion,
            projectId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Pointer Project",
            new GameId("audition"),
            new ModId("pointer_mod"),
            new TemplateIdentity(
                new TemplateId("archive-015"),
                new TemplateVersion("1"),
                new TemplateSha256(new string('A', 64)),
                new CompatibleGameBuild("audition-vn-2026")),
            new ProjectWorkspaceReference(
                "0123456789abcdef0123456789abcdef",
                new ModRelativePath("Working/015.ab"),
                new ModRelativePath("Extracted/015")),
            [], [], [],
            new ProjectEditStateSnapshot(0, 0, null, []),
            new ProjectBuildStateSnapshot(ProjectBuildStatus.NotBuilt, null, null, null),
            now,
            now).Project!;
    }

    private static AuditionProject CompleteBuild(AuditionProject project)
    {
        var now = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
        return AuditionProject.Create(
            project.SchemaVersion, project.ProjectId, project.Name, project.GameId, project.ModId,
            project.TemplateIdentity, project.Workspace, project.EditedTextures, project.ImageAssets,
            project.AiAssets, project.EditState,
            new ProjectBuildStateSnapshot(
                ProjectBuildStatus.Succeeded,
                now,
                new ModRelativePath("BuildOutput/Output/015.ab"),
                new Sha256Digest(new string('B', 64))),
            project.CreatedAt,
            now).Project!;
    }

    private sealed class TestSession(
        AuditionProject project,
        IProjectArchiveWorkspace workspace) : IApplicationProjectSession
    {
        public AuditionProject? Project { get; private set; } = project;
        public IProjectArchiveWorkspace? Workspace { get; private set; } = workspace;

        public ValueTask ActivateAsync(AuditionProject nextProject, IProjectArchiveWorkspace nextWorkspace)
        {
            Project = nextProject;
            Workspace = nextWorkspace;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryUpdateProjectAsync(
            AuditionProject expectedProject,
            IProjectArchiveWorkspace expectedWorkspace,
            AuditionProject updatedProject)
        {
            if (!ReferenceEquals(Project, expectedProject) || !ReferenceEquals(Workspace, expectedWorkspace))
            {
                return ValueTask.FromResult(false);
            }

            Project = updatedProject;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class TestWorkspace : IProjectArchiveWorkspace
    {
        public ProjectArchiveWorkspaceDescriptor Descriptor => throw new NotSupportedException();
        public ArchiveWorkspace ArchiveWorkspace { get; } = new(null!, "015.ab", "015");
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestBuildService : IProjectBuildService
    {
        public int CallCount { get; private set; }

        public Task<ProjectBuildResult> BuildAsync(
            ProjectBuildRequest request,
            IProgress<ProjectBuildProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            progress?.Report(new(ProjectBuildPhase.Validating, 0, "test.validating"));
            progress?.Report(new(ProjectBuildPhase.Completed, 1, "test.completed"));
            var completed = CompleteBuild(request.Project);
            return Task.FromResult(ProjectBuildResult.Success(
                completed,
                new ModRelativePath("BuildOutput/Output/015.ab"),
                new Sha256Digest(new string('B', 64)),
                ProjectValidationResult.Success([])));
        }
    }

    private sealed class TestDestinationValidator(ArchiveExportDestinationFailureReason failure)
        : IArchiveExportDestinationValidator
    {
        public ArchiveExportDestinationResult Validate(ArchiveExportDestinationRequest request) =>
            failure == ArchiveExportDestinationFailureReason.None
                ? ArchiveExportDestinationResult.Success(new(
                    request.OutputDirectory,
                    request.OutputFileName,
                    Path.Combine(request.OutputDirectory, request.OutputFileName),
                    request.FileContract,
                    request.OverwritePolicy,
                    false))
                : ArchiveExportDestinationResult.Failure(failure, "test.destination_invalid");
    }

    private sealed class TestExportService : IArchiveExportService
    {
        public int CallCount { get; private set; }

        public Task<ArchiveExportResult> ExportAsync(
            ArchiveExportRequest request,
            IProgress<ArchiveExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            progress?.Report(new(ArchiveExportPhase.Copying, "test.copying"));
            progress?.Report(new(ArchiveExportPhase.Completed, "test.completed"));
            var contract = ArchiveExportFileContract.TryCreate("015.ab", out var value) ? value : default;
            var destination = new ArchiveExportDestination(
                request.OutputDirectory,
                request.OutputFileName,
                Path.Combine(request.OutputDirectory, request.OutputFileName),
                contract,
                request.OverwritePolicy,
                false);
            return Task.FromResult(ArchiveExportResult.Success(
                destination, 4096, new Sha256Digest(new string('B', 64))));
        }
    }

    private sealed class ImmediateTaskManager : IBackgroundTaskManager
    {
        private BackgroundTaskSnapshot? _snapshot;
        public BackgroundTaskKind EnqueuedKind { get; private set; }
        public event EventHandler<BackgroundTaskNotification>? Notification { add { } remove { } }

        public async ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(
            BackgroundTaskRequest request,
            CancellationToken cancellationToken = default)
        {
            EnqueuedKind = request.Kind;
            var id = new BackgroundTaskId(Guid.NewGuid());
            var result = await request.Operation(new Progress<BackgroundTaskProgress>(), cancellationToken);
            _snapshot = new(
                id,
                request.Kind,
                result.Succeeded ? BackgroundTaskState.Succeeded : BackgroundTaskState.Failed,
                null,
                result.DiagnosticCode,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            return BackgroundTaskEnqueueResult.Success(id);
        }

        public bool TryCancel(BackgroundTaskId taskId) => false;
        public bool TryGetSnapshot(BackgroundTaskId taskId, out BackgroundTaskSnapshot snapshot)
        {
            snapshot = _snapshot!;
            return _snapshot?.TaskId == taskId;
        }

        public ImmutableArray<BackgroundTaskSnapshot> GetSnapshots() => _snapshot is null ? [] : [_snapshot];
        public Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(
            BackgroundTaskId taskId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot?.TaskId == taskId ? _snapshot : null);
    }

    private sealed class ControlledTaskManager : IBackgroundTaskManager
    {
        private readonly TaskCompletionSource<BackgroundTaskSnapshot?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BackgroundTaskId TaskId { get; } = new(Guid.NewGuid());
        public BackgroundTaskId CancelledTaskId { get; private set; }
        public TaskCompletionSource WaitUntilQueued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<BackgroundTaskNotification>? Notification { add { } remove { } }

        public ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(
            BackgroundTaskRequest request,
            CancellationToken cancellationToken = default)
        {
            WaitUntilQueued.TrySetResult();
            return ValueTask.FromResult(BackgroundTaskEnqueueResult.Success(TaskId));
        }

        public bool TryCancel(BackgroundTaskId taskId)
        {
            CancelledTaskId = taskId;
            _completion.TrySetResult(new(
                TaskId,
                BackgroundTaskKind.Build,
                BackgroundTaskState.Cancelled,
                null,
                "test.cancelled",
                DateTimeOffset.UtcNow,
                null,
                DateTimeOffset.UtcNow));
            return taskId == TaskId;
        }

        public bool TryGetSnapshot(BackgroundTaskId taskId, out BackgroundTaskSnapshot snapshot)
        {
            snapshot = null!;
            return false;
        }

        public ImmutableArray<BackgroundTaskSnapshot> GetSnapshots() => [];

        public Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(
            BackgroundTaskId taskId,
            CancellationToken cancellationToken = default) => _completion.Task;
    }
}
