using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ProjectLoadServiceTests
{
    [Fact]
    public async Task Valid_workspace_and_cache_load_without_extract_or_scan()
    {
        var context = new Context(workspaceAvailable: true, cacheValid: true);

        var result = await context.Service.LoadAsync(context.Project.ProjectId);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.False(result.WorkspaceReextracted);
        Assert.False(result.MetadataCacheRecovered);
        Assert.False(context.Archive.Called);
        Assert.False(context.Scan.Called);
        Assert.False(context.Store.Saved);
        Assert.False(context.Entitlement.Called);
        Assert.False(context.Acquisition.Called);
    }

    [Fact]
    public async Task Missing_cache_is_rebuilt_without_reextracting_valid_workspace()
    {
        var context = new Context(workspaceAvailable: true, cacheValid: false);

        var result = await context.Service.LoadAsync(context.Project.ProjectId);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(result.MetadataCacheRecovered);
        Assert.False(result.WorkspaceReextracted);
        Assert.True(context.Scan.Called);
        Assert.True(context.Cache.Stored);
        Assert.False(context.Archive.Called);
    }

    [Fact]
    public async Task Missing_workspace_reextracts_exact_template_and_preserves_project_id()
    {
        var context = new Context(workspaceAvailable: false, cacheValid: false);

        var result = await context.Service.LoadAsync(context.Project.ProjectId);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(result.WorkspaceReextracted);
        Assert.True(context.Entitlement.Called);
        Assert.True(context.Acquisition.Called);
        Assert.True(context.Archive.Called);
        Assert.True(context.Store.Saved);
        Assert.Equal(context.Project.ProjectId, context.Workspaces.Request!.ProjectId);
        Assert.Equal(context.Project.TemplateIdentity, result.Project!.TemplateIdentity);
    }

    [Fact]
    public async Task Reextracted_workspace_rebuilds_cache_even_when_old_cache_is_structurally_valid()
    {
        var context = new Context(workspaceAvailable: false, cacheValid: true);

        var result = await context.Service.LoadAsync(context.Project.ProjectId);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(result.WorkspaceReextracted);
        Assert.True(result.MetadataCacheRecovered);
        Assert.True(context.Scan.Called);
        Assert.True(context.Cache.Stored);
    }

    [Fact]
    public async Task Missing_exact_template_fails_without_workspace_or_current_version_fallback()
    {
        var context = new Context(workspaceAvailable: true, cacheValid: true, includeExactTemplate: false);

        var result = await context.Service.LoadAsync(context.Project.ProjectId);

        Assert.Equal(ProjectLoadFailureReason.TemplateMissing, result.FailureReason);
        Assert.False(context.Recovery.Called);
        Assert.False(context.Archive.Called);
    }

    private sealed class Context
    {
        public Context(bool workspaceAvailable, bool cacheValid, bool includeExactTemplate = true)
        {
            Template = new(
                "archive-015", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
                "audition_vn", "015", "1", new string('A', 64), "audition-vn-2026");
            Workspace = new StubProjectWorkspace(Template);
            Project = CreateProject(Template);
            Store = new(Project);
            Recovery = new(workspaceAvailable ? Workspace : null);
            Entitlement = new();
            Acquisition = new();
            Workspaces = new(Workspace);
            Archive = new();
            Scan = new();
            Cache = new(cacheValid);
            var catalog = new StubTemplateCatalog(includeExactTemplate ? Template : null);
            Service = new(
                Store,
                catalog,
                Recovery,
                Entitlement,
                Acquisition,
                new StubRegionResolver(),
                Workspaces,
                Archive,
                Scan,
                Cache,
                new FixedTimeProvider());
        }

        public AuditionArchiveTemplate Template { get; }
        public AuditionProject Project { get; }
        public StubProjectWorkspace Workspace { get; }
        public StubStore Store { get; }
        public StubRecovery Recovery { get; }
        public StubEntitlement Entitlement { get; }
        public StubAcquisition Acquisition { get; }
        public StubWorkspaceService Workspaces { get; }
        public StubArchiveService Archive { get; }
        public StubSmartScan Scan { get; }
        public StubCache Cache { get; }
        public ProjectLoadService Service { get; }
    }

    private sealed class StubTemplateCatalog(AuditionArchiveTemplate? template) : ITemplateVersionCatalog
    {
        public bool TryGetExact(
            TemplateId templateId,
            TemplateVersion version,
            [NotNullWhen(true)] out AuditionArchiveTemplate? exact)
        {
            exact = template;
            return exact is not null;
        }

        public bool TryGetCurrent(TemplateId templateId, [NotNullWhen(true)] out AuditionArchiveTemplate? current)
        {
            current = template;
            return current is not null;
        }

        public TemplateResolutionResult Resolve(ArchiveTemplateReference snapshot) =>
            new(template is null ? TemplateResolutionStatus.TemplateMissing : TemplateResolutionStatus.ExactMatch,
                template, template);
    }

    private static AuditionProject CreateProject(AuditionArchiveTemplate template)
    {
        var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        return AuditionProject.Create(
            1,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Project",
            new("audition"),
            new("login_mod"),
            new(template.TemplateId, template.TemplateVersion!.Value,
                template.ExpectedSha256!.Value, template.CompatibleGameBuild!.Value),
            new("0123456789abcdef0123456789abcdef", new("Working/015.ab"), new("Extracted/015")),
            [], [], [],
            new(0, 0, null, []),
            new(ProjectBuildStatus.NotBuilt, null, null, null),
            now,
            now).Project!;
    }

    private sealed class StubStore(AuditionProject project) : IAuditionProjectStore
    {
        public bool Saved { get; private set; }
        public Task<AuditionProjectLoadResult> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditionProjectLoadResult.Success(project));
        public Task<AuditionProjectStoreResult> SaveAsync(AuditionProject value, CancellationToken cancellationToken = default)
        {
            Saved = true;
            return Task.FromResult(AuditionProjectStoreResult.Success("project.audproj"));
        }
        public Task<AuditionProjectStoreResult> DeleteAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditionProjectStoreResult.Success("project.audproj"));
    }

    private sealed class StubRecovery(IProjectArchiveWorkspace? workspace) : IProjectArchiveWorkspaceRecoveryService
    {
        public bool Called { get; private set; }
        public Task<ProjectArchiveWorkspaceRecoveryResult> TryRecoverAsync(
            AuditionProject project,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(workspace is null
                ? ProjectArchiveWorkspaceRecoveryResult.Unavailable("MISSING")
                : ProjectArchiveWorkspaceRecoveryResult.Success(workspace));
        }
    }

    private sealed class StubEntitlement : ITemplateEntitlementService
    {
        public bool Called { get; private set; }
        public Task<TemplateEntitlementResult> CheckAsync(TemplateIdentity identity, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new TemplateEntitlementResult(TemplateEntitlementStatus.NotRequired, null));
        }
    }

    private sealed class StubAcquisition : IProjectTemplateAcquisitionService
    {
        public bool Called { get; private set; }
        public Task<ProjectTemplateAcquisitionResult> AcquireAsync(
            AuditionArchiveTemplate template,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(ProjectTemplateAcquisitionResult.Success(new("C:\\trusted")));
        }
    }

    private sealed class StubRegionResolver : IGameRegionProfileResolver
    {
        public bool TryResolve(string regionProfileId, out GameRegionProfile profile)
        {
            profile = GameRegionProfile.AuditionVietnam;
            return true;
        }
    }

    private sealed class StubWorkspaceService(StubProjectWorkspace workspace) : IProjectArchiveWorkspaceService
    {
        public ProjectArchiveWorkspaceCreateRequest? Request { get; private set; }
        public Task<ProjectArchiveWorkspaceCreateResult> CreateAsync(
            ProjectArchiveWorkspaceCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
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
        public Task<ArchiveExtractResult> ExtractAsync(
            ArchiveExtractRequest request,
            IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new ArchiveExtractResult(new(
                ArchiveOperation.Extract, ArchiveOperationState.Completed, true, 1,
                ArchiveFailureReason.None, null, [])));
        }
        public Task<ArchivePackResult> PackAsync(
            ArchivePackRequest request,
            IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubSmartScan : ISmartModScanService
    {
        public bool Called { get; private set; }
        public Task<SmartModScanResult> ScanAsync(
            SmartModScanRequest request,
            IProgress<SmartModScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(SmartModScanResult.Success(new ArchiveAssetCatalog([], 0, 0), [], []));
        }
    }

    private sealed class StubCache(bool valid) : IProjectMetadataCache
    {
        public bool Stored { get; private set; }
        public Task<ProjectMetadataCacheValidationResult> ValidateAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectMetadataCacheValidationResult(
                valid ? ProjectMetadataCacheValidationStatus.Valid : ProjectMetadataCacheValidationStatus.Missing,
                null));
        public Task<ProjectMetadataCacheResult> StoreAsync(
            Guid projectId,
            IEnumerable<ProjectTextureMetadataSnapshot> textures,
            CancellationToken cancellationToken = default)
        {
            Stored = true;
            return Task.FromResult(ProjectMetadataCacheResult.Success());
        }
        public Task<ProjectMetadataCacheResult> DeleteAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectMetadataCacheResult.Success());
    }

    private sealed class StubProjectWorkspace : IProjectArchiveWorkspace
    {
        public StubProjectWorkspace(AuditionArchiveTemplate template)
        {
            var secure = new StubSecureWorkspace();
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
            Descriptor = new(
                1, Guid.Parse("11111111-1111-1111-1111-111111111111"), "Project", secure.Id,
                new(template.TemplateId.Value, template.TemplateVersion!.Value.Value,
                    template.ExpectedSha256!.Value.Value, template.EngineType, template.RegionProfileId,
                    template.CompatibleGameBuild!.Value.Value),
                "Working/015.ab", "Extracted/015", "BuildOutput", null,
                ".project-archive-workspace.json", template.ExpectedSha256.Value.Value,
                now, now, ProjectArchiveWorkspaceState.Ready);
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
        public SecureWorkspacePaths Paths { get; } = new(
            "C:\\root", "C:\\root\\Working", "C:\\root\\Extracted", "C:\\root\\BuildOutput");
        public string ResolveRelativePath(string relativePath) => Path.Combine(Paths.RootDirectory, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
    }
}
