using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ProjectCreationServiceTests
{
    [Fact]
    public async Task Complete_workflow_saves_exact_project_after_extract_scan_and_cache()
    {
        var context = new Context();
        var progress = new ProgressCollector();

        var result = await context.Service.CreateAsync(Request(), progress);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Same(context.Workspace, result.Workspace);
        Assert.Equal(new GameId("audition"), result.Project!.GameId);
        Assert.Equal("1", result.Project.TemplateIdentity.Version.Value);
        Assert.True(context.Archive.Called);
        Assert.True(context.Scan.Called);
        Assert.True(context.Cache.Stored);
        Assert.True(context.Store.Saved);
        Assert.False(context.Workspace.Disposed);
        Assert.Equal(ProjectCreationPhase.Completed, progress.Items[^1].Phase);
    }

    [Theory]
    [InlineData(false, true, ProjectCreationFailureReason.UnknownGame)]
    [InlineData(true, false, ProjectCreationFailureReason.UnknownMod)]
    public async Task Unknown_selection_stops_before_entitlement(
        bool gameExists,
        bool modExists,
        ProjectCreationFailureReason expected)
    {
        var context = new Context(gameExists, modExists);

        var result = await context.Service.CreateAsync(Request());

        Assert.Equal(expected, result.FailureReason);
        Assert.False(context.Entitlement.Called);
    }

    [Theory]
    [InlineData(TemplateEntitlementStatus.Denied, ProjectCreationFailureReason.EntitlementDenied)]
    [InlineData(TemplateEntitlementStatus.Unavailable, ProjectCreationFailureReason.EntitlementUnavailable)]
    public async Task Entitlement_is_checked_before_template_acquisition(
        TemplateEntitlementStatus status,
        ProjectCreationFailureReason expected)
    {
        var context = new Context();
        context.Entitlement.Status = status;

        var result = await context.Service.CreateAsync(Request());

        Assert.Equal(expected, result.FailureReason);
        Assert.False(context.Acquisition.Called);
    }

    [Fact]
    public async Task Template_acquisition_failure_does_not_allocate_workspace()
    {
        var context = new Context();
        context.Acquisition.Succeed = false;

        var result = await context.Service.CreateAsync(Request());

        Assert.Equal(ProjectCreationFailureReason.TemplateAcquisitionFailed, result.FailureReason);
        Assert.False(context.WorkspaceService.Called);
    }

    [Fact]
    public async Task Missing_region_fails_before_workspace_creation()
    {
        var context = new Context();
        context.Region.Exists = false;

        var result = await context.Service.CreateAsync(Request());

        Assert.Equal(ProjectCreationFailureReason.RegionProfileMissing, result.FailureReason);
        Assert.False(context.WorkspaceService.Called);
    }

    [Fact]
    public async Task Extract_failure_rolls_back_workspace_and_never_scans_or_saves()
    {
        var context = new Context();
        context.Archive.Succeed = false;

        var result = await context.Service.CreateAsync(Request());

        Assert.Equal(ProjectCreationFailureReason.ArchiveExtractionFailed, result.FailureReason);
        Assert.True(context.Workspace.Disposed);
        Assert.False(context.Scan.Called);
        Assert.False(context.Store.Saved);
    }

    [Fact]
    public async Task Scan_failure_rolls_back_without_caching_partial_metadata()
    {
        var context = new Context();
        context.Scan.Succeed = false;

        var result = await context.Service.CreateAsync(Request());

        Assert.Equal(ProjectCreationFailureReason.TextureScanFailed, result.FailureReason);
        Assert.True(context.Workspace.Disposed);
        Assert.False(context.Cache.Stored);
        Assert.False(context.Store.Saved);
    }

    [Fact]
    public async Task Metadata_failure_rolls_back_and_never_saves_project()
    {
        var context = new Context();
        context.Cache.Succeed = false;

        var result = await context.Service.CreateAsync(Request());

        Assert.Equal(ProjectCreationFailureReason.MetadataCacheFailed, result.FailureReason);
        Assert.True(context.Workspace.Disposed);
        Assert.False(context.Store.Saved);
    }

    [Fact]
    public async Task Save_failure_deletes_cache_and_disposes_workspace()
    {
        var context = new Context();
        context.Store.Succeed = false;

        var result = await context.Service.CreateAsync(Request());

        Assert.Equal(ProjectCreationFailureReason.ProjectSaveFailed, result.FailureReason);
        Assert.True(context.Cache.Deleted);
        Assert.True(context.Store.Deleted);
        Assert.True(context.Workspace.Disposed);
    }

    [Fact]
    public async Task Cancellation_is_structured_and_rolls_back()
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        context.Archive.Cancel = cancellation;

        var result = await context.Service.CreateAsync(Request(), cancellationToken: cancellation.Token);

        Assert.Equal(ProjectCreationFailureReason.Cancelled, result.FailureReason);
        Assert.True(context.Workspace.Disposed);
    }

    private static ProjectCreationRequest Request() => new(new("audition"), new("login_mod"), "Dự án Login");

    private sealed class Context
    {
        public Context(bool gameExists = true, bool modExists = true)
        {
            Template = new(
                "archive-015", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
                "audition_vn", "015", "1", new string('A', 64), "audition-vn-2026");
            var mod = new ModDefinition(
                new("login_mod"), new("audition"), "Login", new("interface"), new("covers/login.png"),
                "Description", Template, ModKeydatStrategy.ReuseOrGenerate, new("Data/015.ab"), "Compatible");
            Workspace = new StubProjectWorkspace(Template);
            Entitlement = new();
            Acquisition = new();
            Region = new();
            WorkspaceService = new(Workspace);
            Archive = new();
            Scan = new();
            Cache = new();
            Store = new();
            Service = new ProjectCreationService(
                new StubGameCatalog(gameExists),
                new StubModCatalog(modExists ? mod : null),
                Region,
                Entitlement,
                Acquisition,
                WorkspaceService,
                Archive,
                Scan,
                Cache,
                Store,
                new FixedTimeProvider());
        }

        public AuditionArchiveTemplate Template { get; }
        public StubProjectWorkspace Workspace { get; }
        public StubEntitlement Entitlement { get; }
        public StubAcquisition Acquisition { get; }
        public StubRegionResolver Region { get; }
        public StubWorkspaceService WorkspaceService { get; }
        public StubArchiveService Archive { get; }
        public StubSmartScan Scan { get; }
        public StubCache Cache { get; }
        public StubStore Store { get; }
        public ProjectCreationService Service { get; }
    }

    private sealed class StubGameCatalog(bool exists) : IGameCatalog
    {
        public ImmutableArray<GameDefinition> GetGames() => exists ? [new(new("audition"), "Audition")] : [];
        public bool TryGetGame(GameId gameId, [NotNullWhen(true)] out GameDefinition? game)
        {
            game = exists ? new(new("audition"), "Audition") : null;
            return game is not null;
        }
    }

    private sealed class StubModCatalog(ModDefinition? mod) : IModCatalog
    {
        public ImmutableArray<ModDefinition> GetMods(GameId gameId) => mod is null ? [] : [mod];
        public bool TryGetMod(GameId gameId, ModId modId, [NotNullWhen(true)] out ModDefinition? definition)
        {
            definition = mod;
            return definition is not null;
        }
    }

    private sealed class StubEntitlement : ITemplateEntitlementService
    {
        public bool Called { get; private set; }
        public TemplateEntitlementStatus Status { get; set; } = TemplateEntitlementStatus.NotRequired;
        public Task<TemplateEntitlementResult> CheckAsync(TemplateIdentity identity, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new TemplateEntitlementResult(Status, null));
        }
    }

    private sealed class StubAcquisition : IProjectTemplateAcquisitionService
    {
        public bool Called { get; private set; }
        public bool Succeed { get; set; } = true;
        public Task<ProjectTemplateAcquisitionResult> AcquireAsync(
            AuditionArchiveTemplate template,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(Succeed
                ? ProjectTemplateAcquisitionResult.Success(new("C:\\trusted"))
                : ProjectTemplateAcquisitionResult.Failure("ACQUIRE_FAILED"));
        }
    }

    private sealed class StubRegionResolver : IGameRegionProfileResolver
    {
        public bool Exists { get; set; } = true;
        public bool TryResolve(string regionProfileId, out GameRegionProfile profile)
        {
            profile = GameRegionProfile.AuditionVietnam;
            return Exists;
        }
    }

    private sealed class StubWorkspaceService(StubProjectWorkspace workspace) : IProjectArchiveWorkspaceService
    {
        public bool Called { get; private set; }
        public Task<ProjectArchiveWorkspaceCreateResult> CreateAsync(
            ProjectArchiveWorkspaceCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(ProjectArchiveWorkspaceCreateResult.Success(workspace));
        }

        public Task<ProjectArchiveWorkspaceValidationResult> ValidateAsync(
            IProjectArchiveWorkspace value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectArchiveWorkspaceValidationResult.Valid());
    }

    private sealed class StubArchiveService : IAuditionArchiveService
    {
        public bool Called { get; private set; }
        public bool Succeed { get; set; } = true;
        public CancellationTokenSource? Cancel { get; set; }

        public Task<ArchiveExtractResult> ExtractAsync(
            ArchiveExtractRequest request,
            IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            Cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ArchiveExtractResult(new(
                ArchiveOperation.Extract,
                Succeed ? ArchiveOperationState.Completed : ArchiveOperationState.Failed,
                Succeed,
                Succeed ? 1 : 0,
                Succeed ? ArchiveFailureReason.None : ArchiveFailureReason.RunnerFailed,
                null,
                [])));
        }

        public Task<ArchivePackResult> PackAsync(
            ArchivePackRequest request,
            IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubSmartScan : ISmartModScanService
    {
        public bool Called { get; private set; }
        public bool Succeed { get; set; } = true;
        public Task<SmartModScanResult> ScanAsync(
            SmartModScanRequest request,
            IProgress<SmartModScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(Succeed
                ? SmartModScanResult.Success(new ArchiveAssetCatalog([], 0, 0), [], [])
                : SmartModScanResult.Failure(SmartModScanFailureReason.AssetScanFailed, "SCAN_FAILED"));
        }
    }

    private sealed class StubCache : IProjectMetadataCache
    {
        public bool Succeed { get; set; } = true;
        public bool Stored { get; private set; }
        public bool Deleted { get; private set; }

        public Task<ProjectMetadataCacheValidationResult> ValidateAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectMetadataCacheValidationResult(
                ProjectMetadataCacheValidationStatus.Valid,
                null));

        public Task<ProjectMetadataCacheResult> StoreAsync(
            Guid projectId,
            IEnumerable<ProjectTextureMetadataSnapshot> textures,
            CancellationToken cancellationToken = default)
        {
            Stored = true;
            return Task.FromResult(Succeed
                ? ProjectMetadataCacheResult.Success()
                : ProjectMetadataCacheResult.Failure(ProjectMetadataCacheFailureReason.IoFailure, "CACHE_FAILED"));
        }

        public Task<ProjectMetadataCacheResult> DeleteAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            Deleted = true;
            return Task.FromResult(ProjectMetadataCacheResult.Success());
        }
    }

    private sealed class StubStore : IAuditionProjectStore
    {
        public bool Succeed { get; set; } = true;
        public bool Saved { get; private set; }
        public bool Deleted { get; private set; }

        public Task<AuditionProjectLoadResult> LoadAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditionProjectLoadResult.Failure(
                AuditionProjectLoadFailureReason.Missing,
                "TEST_PROJECT_MISSING"));

        public Task<AuditionProjectStoreResult> SaveAsync(AuditionProject project, CancellationToken cancellationToken = default)
        {
            Saved = true;
            return Task.FromResult(Succeed
                ? AuditionProjectStoreResult.Success("project.audproj")
                : AuditionProjectStoreResult.Failure(AuditionProjectStoreFailureReason.IoFailure, "SAVE_FAILED"));
        }

        public Task<AuditionProjectStoreResult> DeleteAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            Deleted = true;
            return Task.FromResult(AuditionProjectStoreResult.Success("project.audproj"));
        }
    }

    private sealed class StubProjectWorkspace : IProjectArchiveWorkspace
    {
        public StubProjectWorkspace(AuditionArchiveTemplate template)
        {
            var secure = new StubSecureWorkspace();
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
            Descriptor = new(
                1,
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "Project",
                secure.Id,
                new(template.TemplateId.Value, template.TemplateVersion!.Value.Value, template.ExpectedSha256!.Value.Value,
                    template.EngineType, template.RegionProfileId, template.CompatibleGameBuild!.Value.Value),
                "Working/015.ab",
                "Extracted/015",
                "BuildOutput",
                null,
                ".project-archive-workspace.json",
                template.ExpectedSha256.Value.Value,
                now,
                now,
                ProjectArchiveWorkspaceState.Ready);
        }

        public bool Disposed { get; private set; }
        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubSecureWorkspace : ISecureWorkspace
    {
        public string Id => "0123456789abcdef0123456789abcdef";
        public SecureWorkspacePaths Paths { get; } = new("C:\\root", "C:\\root\\Working", "C:\\root\\Extracted", "C:\\root\\BuildOutput");
        public string ResolveRelativePath(string relativePath) => Path.Combine(Paths.RootDirectory, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class ProgressCollector : IProgress<ProjectCreationProgress>
    {
        public List<ProjectCreationProgress> Items { get; } = [];
        public void Report(ProjectCreationProgress value) => Items.Add(value);
    }
}
