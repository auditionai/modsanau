using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Workspaces;

namespace Archives.Tests;

public sealed class AcvTool5ArchiveEngineTests
{
    [Fact]
    public async Task Provisioning_occurs_before_keydat_and_runner()
    {
        var events = new List<string>();
        var context = TestContext.Create(events: events);

        var result = await context.Engine.ExtractAsync(context.ExtractRequest, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["provision", "keydat", "runner"], events);
    }

    [Fact]
    public async Task Integrity_failure_prevents_runner_launch()
    {
        var exception = new ArchiveToolIntegrityException(new(
            false,
            ArchiveToolIntegrityFailureReason.HashMismatch,
            ArchiveToolIds.AcvTool5,
            "EXPECTED",
            "ACTUAL",
            null));
        var context = TestContext.Create(provisioningException: exception);

        var result = await context.Engine.ExtractAsync(context.ExtractRequest, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ArchiveFailureReason.ToolIntegrityFailed, result.FailureReason);
        Assert.False(context.Runner.Launched);
    }

    [Fact]
    public async Task Provisioning_failure_prevents_runner_launch()
    {
        var context = TestContext.Create(provisioningException: new IOException("controlled failure"));

        var result = await context.Engine.ExtractAsync(context.ExtractRequest, null, CancellationToken.None);

        Assert.Equal(ArchiveFailureReason.ToolProvisioningFailed, result.FailureReason);
        Assert.False(context.Runner.Launched);
    }

    [Fact]
    public async Task Invalid_keydat_prevents_runner_launch()
    {
        var context = TestContext.Create(keydatStatus: KeydatStatus.Invalid);

        var result = await context.Engine.ExtractAsync(context.ExtractRequest, null, CancellationToken.None);

        Assert.Equal(ArchiveFailureReason.KeydatInvalid, result.FailureReason);
        Assert.False(context.Runner.Launched);
    }

    [Theory]
    [InlineData(KeydatStatus.Missing)]
    [InlineData(KeydatStatus.PresentUnverified)]
    public async Task Missing_or_existing_keydat_is_delegated_to_runner(KeydatStatus status)
    {
        var context = TestContext.Create(keydatStatus: status);

        var result = await context.Engine.ExtractAsync(context.ExtractRequest, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(context.Runner.Launched);
        Assert.Equal(GameRegionProfile.AuditionVietnam, context.Runner.Request!.RegionProfile);
    }

    [Fact]
    public async Task Extract_progress_is_mapped_to_semantic_progress()
    {
        var context = TestContext.Create();
        var observed = new List<ArchiveProgress>();

        await context.Engine.ExtractAsync(
            context.ExtractRequest,
            new InlineTestProgress<ArchiveProgress>(observed.Add),
            CancellationToken.None);

        Assert.Contains(observed, item =>
            item.State == ArchiveOperationState.ProvisioningTool && item.Operation == ArchiveOperation.Extract);
        Assert.Contains(observed, item => item.State == ArchiveOperationState.PreparingKeydat);
        Assert.Contains(observed, item =>
            item.State == ArchiveOperationState.Extracting
            && item.CurrentItemRelativePath == @"015\texture\file.dds"
            && item.ProcessedItemCount == 1);
    }

    [Fact]
    public async Task Pack_progress_is_mapped_to_semantic_progress()
    {
        var context = TestContext.Create(operation: AcvTool5Operation.Pack);
        var observed = new List<ArchiveProgress>();

        var result = await context.Engine.PackAsync(
            context.PackRequest,
            new InlineTestProgress<ArchiveProgress>(observed.Add),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(observed, item =>
            item.State == ArchiveOperationState.Packing
            && item.Operation == ArchiveOperation.Pack);
    }

    [Fact]
    public async Task Cancellation_token_is_propagated_to_runner_and_mapped()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = TestContext.Create(
            runnerResult: CreateRunnerResult(AcvTool5Operation.Extract, AcvTool5RunnerState.Cancelled, false));

        var result = await context.Engine.ExtractAsync(
            context.ExtractRequest,
            null,
            cancellation.Token);

        Assert.Equal(ArchiveOperationState.Cancelled, result.FinalState);
        Assert.Equal(ArchiveFailureReason.Cancelled, result.FailureReason);
        Assert.True(context.Provisioning.CancellationObserved);
    }

    [Fact]
    public async Task Timeout_is_distinct_from_failure()
    {
        var context = TestContext.Create(
            runnerResult: CreateRunnerResult(AcvTool5Operation.Extract, AcvTool5RunnerState.TimedOut, false));

        var result = await context.Engine.ExtractAsync(context.ExtractRequest, null, CancellationToken.None);

        Assert.Equal(ArchiveOperationState.TimedOut, result.FinalState);
        Assert.Equal(ArchiveFailureReason.Timeout, result.FailureReason);
    }

    [Fact]
    public async Task Runner_nonzero_failure_is_structured()
    {
        var context = TestContext.Create(
            runnerResult: CreateRunnerResult(AcvTool5Operation.Extract, AcvTool5RunnerState.Failed, false, exitCode: 7));

        var result = await context.Engine.ExtractAsync(context.ExtractRequest, null, CancellationToken.None);

        Assert.Equal(ArchiveFailureReason.RunnerFailed, result.FailureReason);
    }

    [Fact]
    public async Task Artifact_verification_failure_is_structured()
    {
        var context = TestContext.Create(
            runnerResult: CreateRunnerResult(AcvTool5Operation.Extract, AcvTool5RunnerState.Failed, false, exitCode: 0));

        var result = await context.Engine.ExtractAsync(context.ExtractRequest, null, CancellationToken.None);

        Assert.Equal(ArchiveFailureReason.ArtifactValidationFailed, result.FailureReason);
    }

    [Fact]
    public async Task Unsupported_region_prevents_runner_launch()
    {
        var context = TestContext.Create(resolveRegion: false);

        var result = await context.Engine.ExtractAsync(context.ExtractRequest, null, CancellationToken.None);

        Assert.Equal(ArchiveFailureReason.InvalidArchive, result.FailureReason);
        Assert.False(context.Runner.Launched);
    }

    private static AcvTool5RunResult CreateRunnerResult(
        AcvTool5Operation operation,
        AcvTool5RunnerState state,
        bool succeeded,
        int exitCode = 0)
    {
        var progressState = operation == AcvTool5Operation.Extract
            ? AcvTool5RunnerState.Extracting
            : AcvTool5RunnerState.Packing;
        return new(
            operation,
            state,
            succeeded,
            exitCode,
            string.Empty,
            string.Empty,
            false,
            false,
            KeydatStatus.Missing,
            KeydatStatus.PresentUnverified,
            true,
            [new(operation, progressState, @"015\texture\file.dds", 1)],
            succeeded ? [] : ["Controlled runner diagnostic."]);
    }

    private sealed class TestContext
    {
        private TestContext(
            KeydatStatus keydatStatus,
            Exception? provisioningException,
            AcvTool5RunResult? runnerResult,
            AcvTool5Operation operation,
            bool resolveRegion,
            List<string>? events)
        {
            Workspace = new TestWorkspace();
            var template = new AuditionArchiveTemplate(
                "sample",
                "015.ab",
                "015.ab",
                ArchiveEngineType.AcvTool5,
                resolveRegion ? "audition_vn" : "unsupported_region",
                "015");
            var archiveWorkspace = ArchiveWorkspace.Create(Workspace, template);
            ExtractRequest = new(template, archiveWorkspace, new PristineArchiveSource(Path.GetTempPath()), TimeSpan.FromSeconds(5));
            PackRequest = new(template, archiveWorkspace, TimeSpan.FromSeconds(5));
            Provisioning = new FakeProvisioningService(provisioningException, events);
            Runner = new FakeRunner(
                runnerResult ?? CreateRunnerResult(operation, AcvTool5RunnerState.Completed, true),
                events);
            Engine = new(
                Provisioning,
                new FakeKeydatService(keydatStatus, events),
                Runner,
                new GameRegionProfileCatalog(),
                new(Path.GetTempPath(), "acv.exe"));
        }

        public TestWorkspace Workspace { get; }

        public ArchiveExtractRequest ExtractRequest { get; }

        public ArchivePackRequest PackRequest { get; }

        public FakeProvisioningService Provisioning { get; }

        public FakeRunner Runner { get; }

        public AcvTool5ArchiveEngine Engine { get; }

        public static TestContext Create(
            KeydatStatus keydatStatus = KeydatStatus.Missing,
            Exception? provisioningException = null,
            AcvTool5RunResult? runnerResult = null,
            AcvTool5Operation operation = AcvTool5Operation.Extract,
            bool resolveRegion = true,
            List<string>? events = null) => new(
                keydatStatus,
                provisioningException,
                runnerResult,
                operation,
                resolveRegion,
                events);
    }

    private sealed class FakeProvisioningService(Exception? exception, List<string>? events) : IArchiveToolProvisioningService
    {
        public bool CancellationObserved { get; private set; }

        public Task<ArchiveToolProvisioningResult> ProvisionAsync(
            string toolId,
            string trustedSourceRoot,
            string sourceRelativePath,
            ISecureWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            events?.Add("provision");
            CancellationObserved = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            if (exception is not null)
            {
                return Task.FromException<ArchiveToolProvisioningResult>(exception);
            }

            return Task.FromResult(new ArchiveToolProvisioningResult(
                toolId,
                Path.Combine(workspace.Paths.WorkingDirectory, "acv.exe"),
                new string('A', 64),
                null,
                false));
        }
    }

    private sealed class FakeKeydatService(KeydatStatus status, List<string>? events) : IKeydatService
    {
        public KeydatDescriptor Describe(ISecureWorkspace workspace, string archiveRelativePath)
        {
            events?.Add("keydat");
            var archive = Path.Combine(workspace.Paths.WorkingDirectory, archiveRelativePath);
            return new(archive, Path.ChangeExtension(archive, ".keydat"), status, null, null);
        }

        public Task<KeydatDescriptor> CopyToWorkspaceAsync(
            ISecureWorkspace workspace,
            string archiveRelativePath,
            string trustedSourceRoot,
            string sourceRelativePath,
            string? expectedSha256 = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeRunner(AcvTool5RunResult result, List<string>? events) : IArchiveToolRunner
    {
        public bool Launched { get; private set; }

        public AcvTool5RunRequest? Request { get; private set; }

        public Task<AcvTool5RunResult> RunAsync(
            AcvTool5RunRequest request,
            IProgress<AcvTool5Progress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            events?.Add("runner");
            Launched = true;
            Request = request;
            foreach (var item in result.Progress)
            {
                progress?.Report(item);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class TestWorkspace : ISecureWorkspace
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AcvEngineTests", Guid.NewGuid().ToString("N"));

        public TestWorkspace()
        {
            Paths = new(
                _root,
                Path.Combine(_root, "Working"),
                Path.Combine(_root, "Extracted"),
                Path.Combine(_root, "BuildOutput"));
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public SecureWorkspacePaths Paths { get; }

        public string ResolveRelativePath(string relativePath) => Path.Combine(_root, relativePath);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineTestProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
