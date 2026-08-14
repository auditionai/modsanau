using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class TextureBatchBuildSummaryServiceTests
{
    [Fact]
    public async Task Approved_replacements_produce_one_build_and_deterministic_summary()
    {
        var context = new Context();
        var outcomes = new[]
        {
            Outcome("texture/c.dds", TextureBatchOutcomeStatus.Skipped, "SKIPPED_BY_POLICY"),
            Outcome("texture/a.dds", TextureBatchOutcomeStatus.Changed, "REPLACED"),
            Outcome("texture/b.dds", TextureBatchOutcomeStatus.Failed, "ENCODE_FAILED"),
        };

        var result = await context.Service.BuildAsync(new(context.Project, context.Workspace, outcomes));

        Assert.True(result.Succeeded);
        Assert.Equal(1, context.Build.CallCount);
        Assert.Equal(1, result.Summary!.ChangedCount);
        Assert.Equal(1, result.Summary.FailedCount);
        Assert.Equal(1, result.Summary.SkippedCount);
        Assert.Equal(["texture/a.dds", "texture/b.dds", "texture/c.dds"],
            result.Summary.Textures.Select(item => item.RelativePath.Value));
        Assert.Same(context.Build.Result, result.Build);
    }

    [Fact]
    public async Task Changed_set_must_exactly_match_project_source_of_truth()
    {
        var context = new Context();

        var result = await context.Service.BuildAsync(new(
            context.Project,
            context.Workspace,
            [Outcome("texture/other.dds", TextureBatchOutcomeStatus.Changed, "REPLACED")]));

        Assert.False(result.Succeeded);
        Assert.Equal(TextureBatchBuildFailureReason.OutcomeMismatch, result.FailureReason);
        Assert.Equal(0, context.Build.CallCount);
    }

    [Fact]
    public async Task Path_comparison_is_normalized_and_case_insensitive()
    {
        var context = new Context();

        var result = await context.Service.BuildAsync(new(
            context.Project,
            context.Workspace,
            [Outcome("TEXTURE\\A.DDS", TextureBatchOutcomeStatus.Changed, "REPLACED")]));

        Assert.True(result.Succeeded);
        Assert.Equal(1, context.Build.CallCount);
    }

    [Fact]
    public async Task Duplicate_texture_identity_is_rejected_before_build()
    {
        var context = new Context();

        var result = await context.Service.BuildAsync(new(
            context.Project,
            context.Workspace,
            [
                Outcome("texture/a.dds", TextureBatchOutcomeStatus.Changed, "REPLACED"),
                Outcome("TEXTURE\\A.DDS", TextureBatchOutcomeStatus.Skipped, "SKIPPED"),
            ]));

        Assert.Equal(TextureBatchBuildFailureReason.InvalidRequest, result.FailureReason);
        Assert.Equal("TEXTURE_BATCH_BUILD_OUTCOME_DUPLICATE", result.DiagnosticCode);
        Assert.Equal(0, context.Build.CallCount);
    }

    [Fact]
    public async Task No_approved_change_returns_summary_without_building()
    {
        var context = new Context(withEditedTexture: false);

        var result = await context.Service.BuildAsync(new(
            context.Project,
            context.Workspace,
            [Outcome("texture/a.dds", TextureBatchOutcomeStatus.Skipped, "NOT_APPROVED")]));

        Assert.Equal(TextureBatchBuildFailureReason.NoApprovedChanges, result.FailureReason);
        Assert.Equal(0, result.Summary!.ChangedCount);
        Assert.Equal(1, result.Summary.SkippedCount);
        Assert.Equal(0, context.Build.CallCount);
    }

    [Fact]
    public async Task Build_failure_is_preserved_with_texture_summary()
    {
        var context = new Context();
        context.Build.Result = ProjectBuildResult.Failure(
            ProjectBuildFailureReason.ValidationFailed,
            "PROJECT_BUILD_VALIDATION_FAILED");

        var result = await context.Service.BuildAsync(new(
            context.Project,
            context.Workspace,
            [Outcome("texture/a.dds", TextureBatchOutcomeStatus.Changed, "REPLACED")]));

        Assert.Equal(TextureBatchBuildFailureReason.BuildFailed, result.FailureReason);
        Assert.Equal(ProjectBuildFailureReason.ValidationFailed, result.Build!.FailureReason);
        Assert.Equal(1, result.Summary!.ChangedCount);
        Assert.Equal(1, context.Build.CallCount);
    }

    [Fact]
    public async Task Build_cancellation_never_publishes_success()
    {
        var context = new Context();
        context.Build.Result = ProjectBuildResult.CancelledResult();

        var result = await context.Service.BuildAsync(new(
            context.Project,
            context.Workspace,
            [Outcome("texture/a.dds", TextureBatchOutcomeStatus.Changed, "REPLACED")]));

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Equal(TextureBatchBuildFailureReason.Cancelled, result.FailureReason);
        Assert.Equal(1, context.Build.CallCount);
    }

    private static TextureBatchOutcome Outcome(
        string path,
        TextureBatchOutcomeStatus status,
        string code) => new(new(path), status, code);

    private sealed class Context
    {
        public Context(bool withEditedTexture = true)
        {
            Workspace = new StubWorkspace();
            var hash = new Sha256Digest(new string('A', 64));
            var edits = withEditedTexture
                ? new[] { new ProjectEditedTextureRecord(new("texture/a.dds"), hash, new(new string('B', 64)), new("asset"), 1) }
                : [];
            var assets = withEditedTexture
                ? new[] { new ProjectAssetRecord(new("asset"), new("BuildOutput/EditAssets/asset.dds"), new(new string('B', 64))) }
                : [];
            var now = DateTimeOffset.UtcNow;
            Project = AuditionProject.Create(
                AuditionProject.CurrentSchemaVersion,
                Workspace.Descriptor.ProjectId,
                "Batch summary",
                new("audition"),
                new("pointer"),
                Workspace.Descriptor.ArchiveTemplate.Identity,
                new(Workspace.Descriptor.WorkspaceId, new("Working/015.ab"), new("Extracted/015")),
                edits,
                assets,
                [],
                new(withEditedTexture ? 1 : 0, withEditedTexture ? 1 : 0,
                    withEditedTexture ? new("texture/a.dds") : null, []),
                new(ProjectBuildStatus.Dirty, null, null, null),
                now,
                now).Project!;
            Build = new(Project);
            Service = new(Build);
        }

        public AuditionProject Project { get; }
        public StubWorkspace Workspace { get; }
        public StubBuildService Build { get; }
        public TextureBatchBuildSummaryService Service { get; }
    }

    private sealed class StubBuildService(AuditionProject project) : IProjectBuildService
    {
        public int CallCount { get; private set; }
        public ProjectBuildResult Result { get; set; } = ProjectBuildResult.Success(
            project,
            new("BuildOutput/Output/015.ab"),
            new(new string('C', 64)),
            ProjectValidationResult.Success([]));

        public Task<ProjectBuildResult> BuildAsync(
            ProjectBuildRequest request,
            IProgress<ProjectBuildProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubWorkspace : IProjectArchiveWorkspace
    {
        public StubWorkspace()
        {
            var root = Path.Combine(Path.GetTempPath(), "TextureBatchBuildSummaryTests");
            var secure = new StubSecureWorkspace(root);
            var template = new AuditionArchiveTemplate(
                "archive", "015.ab", "015.ab", ArchiveEngineType.AcvTool5,
                "audition_vn", "015", "1", new string('D', 64), "build");
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = DateTimeOffset.UtcNow;
            Descriptor = new(
                1,
                Guid.NewGuid(),
                "Batch summary",
                secure.Id,
                new("archive", "1", new string('D', 64), ArchiveEngineType.AcvTool5,
                    "audition_vn", "build"),
                "Working/015.ab",
                "Extracted/015",
                "BuildOutput",
                null,
                ".manifest.json",
                new string('D', 64),
                now,
                now,
                ProjectArchiveWorkspaceState.Ready);
        }

        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSecureWorkspace(string root) : ISecureWorkspace
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SecureWorkspacePaths Paths { get; } = new(
            root,
            Path.Combine(root, "Working"),
            Path.Combine(root, "Extracted"),
            Path.Combine(root, "BuildOutput"));
        public string ResolveRelativePath(string relativePath) => Path.Combine(root, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
