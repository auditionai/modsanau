using System.Collections.Concurrent;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;
using AuditionModStudio.Infrastructure.Tasks;
using AuditionModStudio.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace Projects.Tests;

public sealed class BatchBuildExportServiceTests
{
    [Fact]
    public async Task Batch_uses_deterministic_names_preserves_order_and_bounds_concurrency()
    {
        await using var context = await Context.CreateAsync(maximumConcurrency: 2);
        var jobs = Enumerable.Range(1, 4).Select(index => context.Job(ProjectId(index))).ToArray();
        var observed = new ConcurrentQueue<BatchBuildExportJobProgress>();

        var result = await context.Service.ExecuteAsync(
            new(jobs, BatchDestinationConflictPolicy.RejectDuplicates),
            new CallbackProgress<BatchBuildExportJobProgress>(observed.Enqueue));

        Assert.True(result.Accepted);
        Assert.Equal(4, result.SucceededCount);
        Assert.Equal(jobs.Select(job => job.JobId), result.Jobs.Select(job => job.JobId));
        Assert.All(result.Jobs, item => Assert.Equal(
            $"project-{jobs[item.JobIndex].Project.ProjectId:N}.ab", item.OutputFileName));
        Assert.Equal(2, context.BuildService.MaximumActiveCalls);
        Assert.All(context.TaskManager.GetSnapshots(), snapshot =>
            Assert.Equal(BackgroundTaskKind.Build, snapshot.Kind));
        Assert.Contains(observed, item => item.State == BatchBuildExportJobState.Validating);
        Assert.Contains(observed, item => item.State == BatchBuildExportJobState.Building);
        Assert.Contains(observed, item => item.State == BatchBuildExportJobState.Exporting);
        Assert.Contains(observed, item => item.State == BatchBuildExportJobState.Succeeded);
    }

    [Fact]
    public async Task Duplicate_destination_is_rejected_before_any_build()
    {
        await using var context = await Context.CreateAsync();
        var projectId = ProjectId(1);
        var jobs = new[] { context.Job(projectId), context.Job(projectId) };

        var result = await context.Service.ExecuteAsync(
            new(jobs, BatchDestinationConflictPolicy.RejectDuplicates));

        Assert.Equal(0, result.SucceededCount);
        Assert.All(result.Jobs, item =>
            Assert.Equal(BatchBuildExportFailureReason.DestinationConflict, item.FailureReason));
        Assert.Equal(0, context.BuildService.CallCount);
        Assert.Equal(0, context.ExportService.CallCount);
    }

    [Fact]
    public async Task Explicit_conflict_policy_serializes_duplicate_replacements()
    {
        await using var context = await Context.CreateAsync(maximumConcurrency: 2);
        var projectId = ProjectId(1);
        var jobs = new[]
        {
            context.Job(projectId, ArchiveExportOverwritePolicy.ReplaceExisting),
            context.Job(projectId, ArchiveExportOverwritePolicy.ReplaceExisting)
        };

        var result = await context.Service.ExecuteAsync(
            new(jobs, BatchDestinationConflictPolicy.SerializeConflicts));

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, context.BuildService.MaximumActiveCalls);
        Assert.Equal(1, context.ExportService.MaximumActiveCalls);
    }

    [Fact]
    public async Task One_build_failure_does_not_stop_or_corrupt_other_jobs()
    {
        var failedProject = ProjectId(2);
        await using var context = await Context.CreateAsync(failedBuildProject: failedProject);
        var jobs = new[]
        {
            context.Job(ProjectId(1)),
            context.Job(failedProject),
            context.Job(ProjectId(3))
        };

        var result = await context.Service.ExecuteAsync(
            new(jobs, BatchDestinationConflictPolicy.RejectDuplicates));

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(BatchBuildExportFailureReason.BuildFailed, result.Jobs[1].FailureReason);
        Assert.Equal(2, context.ExportService.CallCount);
    }

    [Fact]
    public async Task Invalid_destination_fails_only_its_job_during_preflight()
    {
        await using var context = await Context.CreateAsync(invalidDirectory: "C:\\Rejected");
        var jobs = new[]
        {
            context.Job(ProjectId(1), outputDirectory: "C:\\Exports"),
            context.Job(ProjectId(2), outputDirectory: "C:\\Rejected")
        };

        var result = await context.Service.ExecuteAsync(
            new(jobs, BatchDestinationConflictPolicy.RejectDuplicates));

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(BatchBuildExportFailureReason.DestinationInvalid, result.Jobs[1].FailureReason);
        Assert.Equal(1, context.BuildService.CallCount);
    }

    [Fact]
    public async Task Batch_cancellation_cancels_manager_jobs_and_returns_typed_results()
    {
        await using var context = await Context.CreateAsync(buildDelay: TimeSpan.FromSeconds(30));
        var jobs = new[] { context.Job(ProjectId(1)), context.Job(ProjectId(2)) };
        using var cancellation = new CancellationTokenSource();

        var running = context.Service.ExecuteAsync(
            new(jobs, BatchDestinationConflictPolicy.RejectDuplicates),
            cancellationToken: cancellation.Token);
        await context.BuildService.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Cancelled);
        Assert.Equal(2, result.CancelledCount);
        Assert.All(result.Jobs, item => Assert.Equal(BatchBuildExportJobState.Cancelled, item.State));
        Assert.Equal(0, context.ExportService.CallCount);
    }

    [Fact]
    public async Task One_cancelled_job_does_not_cancel_other_jobs()
    {
        var cancelledProject = ProjectId(2);
        await using var context = await Context.CreateAsync(cancelledBuildProject: cancelledProject);
        var jobs = new[]
        {
            context.Job(ProjectId(1)),
            context.Job(cancelledProject),
            context.Job(ProjectId(3))
        };

        var result = await context.Service.ExecuteAsync(
            new(jobs, BatchDestinationConflictPolicy.RejectDuplicates));

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.CancelledCount);
        Assert.Equal(BatchBuildExportJobState.Cancelled, result.Jobs[1].State);
        Assert.Equal(2, context.ExportService.CallCount);
    }

    [Fact]
    public async Task Invalid_or_duplicate_job_identity_rejects_entire_batch()
    {
        await using var context = await Context.CreateAsync();
        var first = context.Job(ProjectId(1));
        var duplicate = context.Job(ProjectId(2)) with { JobId = first.JobId };

        var result = await context.Service.ExecuteAsync(
            new([first, duplicate], BatchDestinationConflictPolicy.RejectDuplicates));

        Assert.False(result.Accepted);
        Assert.Empty(result.Jobs);
        Assert.Equal(0, context.BuildService.CallCount);
    }

    [Fact]
    public async Task Pre_cancelled_batch_returns_typed_cancelled_jobs_without_enqueueing()
    {
        await using var context = await Context.CreateAsync();
        var jobs = new[] { context.Job(ProjectId(1)), context.Job(ProjectId(2)) };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await context.Service.ExecuteAsync(
            new(jobs, BatchDestinationConflictPolicy.RejectDuplicates),
            cancellationToken: cancellation.Token);

        Assert.True(result.Accepted);
        Assert.True(result.Cancelled);
        Assert.Equal(2, result.CancelledCount);
        Assert.Empty(context.TaskManager.GetSnapshots());
    }

    private static Guid ProjectId(int value) => Guid.Parse($"{value:D8}-1111-1111-1111-111111111111");

    private sealed class Context : IAsyncDisposable
    {
        private Context(
            BackgroundTaskManager taskManager,
            RecordingBuildService buildService,
            RecordingExportService exportService,
            BatchBuildExportService service)
        {
            TaskManager = taskManager;
            BuildService = buildService;
            ExportService = exportService;
            Service = service;
        }

        public BackgroundTaskManager TaskManager { get; }
        public RecordingBuildService BuildService { get; }
        public RecordingExportService ExportService { get; }
        public BatchBuildExportService Service { get; }

        public static async Task<Context> CreateAsync(
            int maximumConcurrency = 2,
            Guid? failedBuildProject = null,
            Guid? cancelledBuildProject = null,
            string? invalidDirectory = null,
            TimeSpan? buildDelay = null)
        {
            var manager = new BackgroundTaskManager(
                new(4, 64, 128),
                NullLogger<BackgroundTaskManager>.Instance);
            await manager.StartAsync(CancellationToken.None);
            var build = new RecordingBuildService(
                failedBuildProject,
                cancelledBuildProject,
                buildDelay ?? TimeSpan.FromMilliseconds(40));
            var export = new RecordingExportService();
            var validator = new RecordingDestinationValidator(invalidDirectory);
            var service = new BatchBuildExportService(
                validator,
                build,
                export,
                manager,
                new(maximumConcurrency, 64),
                NullLogger<BatchBuildExportService>.Instance);
            return new(manager, build, export, service);
        }

        public BatchBuildExportJobRequest Job(
            Guid projectId,
            ArchiveExportOverwritePolicy overwritePolicy = ArchiveExportOverwritePolicy.RejectExisting,
            string outputDirectory = "C:\\Exports")
        {
            var workspace = new TestWorkspace(projectId);
            var project = CreateProject(projectId, workspace);
            return new(Guid.NewGuid(), project, workspace, outputDirectory, overwritePolicy);
        }

        public async ValueTask DisposeAsync() => await TaskManager.DisposeAsync();
    }

    private static AuditionProject CreateProject(Guid projectId, IProjectArchiveWorkspace workspace)
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        return AuditionProject.Create(
            AuditionProject.CurrentSchemaVersion,
            projectId,
            $"Project {projectId:N}",
            new GameId("audition"),
            new ModId("pointer_mod"),
            workspace.Descriptor.ArchiveTemplate.Identity,
            new(
                workspace.Descriptor.WorkspaceId,
                new("Working/015.ab"),
                new("Extracted/015")),
            [], [], [],
            new(0, 0, null, []),
            new(ProjectBuildStatus.NotBuilt, null, null, null),
            now,
            now).Project!;
    }

    private static AuditionProject CompleteBuild(AuditionProject project)
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 5, 0, TimeSpan.Zero);
        return AuditionProject.Create(
            project.SchemaVersion,
            project.ProjectId,
            project.Name,
            project.GameId,
            project.ModId,
            project.TemplateIdentity,
            project.Workspace,
            project.EditedTextures,
            project.ImageAssets,
            project.AiAssets,
            project.EditState,
            new(
                ProjectBuildStatus.Succeeded,
                now,
                new("BuildOutput/Output/015.ab"),
                new(new string('A', 64))),
            project.CreatedAt,
            now).Project!;
    }

    private sealed class TestWorkspace : IProjectArchiveWorkspace
    {
        public TestWorkspace(Guid projectId)
        {
            var now = DateTimeOffset.UtcNow;
            Descriptor = new(
                1,
                projectId,
                "Project",
                Guid.NewGuid().ToString("N"),
                new(
                    "archive-015",
                    "1",
                    new string('A', 64),
                    ArchiveEngineType.AcvTool5,
                    "audition_vn",
                    "audition-vn-2026"),
                "Working/015.ab",
                "Extracted/015",
                "BuildOutput",
                null,
                ".project-archive-workspace.json",
                new string('B', 64),
                now,
                now,
                ProjectArchiveWorkspaceState.Ready);
        }

        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; } = new(null!, "015.ab", "015");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDestinationValidator(string? invalidDirectory)
        : IArchiveExportDestinationValidator
    {
        public ArchiveExportDestinationResult Validate(ArchiveExportDestinationRequest request)
        {
            if (string.Equals(request.OutputDirectory, invalidDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return ArchiveExportDestinationResult.Failure(
                    ArchiveExportDestinationFailureReason.DirectoryUnavailable,
                    "test.destination_invalid");
            }

            var directory = Path.GetFullPath(request.OutputDirectory);
            return ArchiveExportDestinationResult.Success(new(
                directory,
                request.OutputFileName,
                Path.Combine(directory, request.OutputFileName),
                request.FileContract,
                request.OverwritePolicy,
                false));
        }
    }

    private sealed class RecordingBuildService(
        Guid? failedProject,
        Guid? cancelledProject,
        TimeSpan delay) : IProjectBuildService
    {
        private int _activeCalls;
        private int _callCount;
        private int _maximumActiveCalls;

        public int CallCount => Volatile.Read(ref _callCount);
        public int MaximumActiveCalls => Volatile.Read(ref _maximumActiveCalls);
        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProjectBuildResult> BuildAsync(
            ProjectBuildRequest request,
            IProgress<ProjectBuildProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCalls);
            SetMaximum(ref _maximumActiveCalls, active);
            FirstCallStarted.TrySetResult();
            try
            {
                progress?.Report(new(ProjectBuildPhase.Validating, 0, "test.validating"));
                await Task.Delay(delay, cancellationToken);
                if (request.Project.ProjectId == failedProject)
                {
                    return ProjectBuildResult.Failure(
                        ProjectBuildFailureReason.ValidationFailed,
                        "test.build_failed");
                }

                if (request.Project.ProjectId == cancelledProject)
                {
                    return ProjectBuildResult.CancelledResult();
                }

                progress?.Report(new(ProjectBuildPhase.Packing, 1, "test.packing"));
                var project = CompleteBuild(request.Project);
                return ProjectBuildResult.Success(
                    project,
                    new("BuildOutput/Output/015.ab"),
                    new(new string('A', 64)),
                    ProjectValidationResult.Success([]));
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }
    }

    private sealed class RecordingExportService : IArchiveExportService
    {
        private int _activeCalls;
        private int _callCount;
        private int _maximumActiveCalls;

        public int CallCount => Volatile.Read(ref _callCount);
        public int MaximumActiveCalls => Volatile.Read(ref _maximumActiveCalls);

        public async Task<ArchiveExportResult> ExportAsync(
            ArchiveExportRequest request,
            IProgress<ArchiveExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCalls);
            SetMaximum(ref _maximumActiveCalls, active);
            try
            {
                progress?.Report(new(ArchiveExportPhase.Copying, "test.copying"));
                await Task.Delay(20, cancellationToken);
                ArchiveExportFileContract.TryCreate("015.ab", out var contract);
                var destination = new ArchiveExportDestination(
                    request.OutputDirectory,
                    request.OutputFileName,
                    Path.Combine(request.OutputDirectory, request.OutputFileName),
                    contract,
                    request.OverwritePolicy,
                    false);
                return ArchiveExportResult.Success(destination, 1024, new(new string('C', 64)));
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }
    }

    private static void SetMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
