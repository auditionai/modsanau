using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ProjectResetServiceTests
{
    [Fact]
    public async Task Reset_texture_restores_pristine_content_and_clears_only_texture_edit_state()
    {
        var context = new Context();

        var result = await context.Service.ResetTextureAsync(
            context.Project, context.Current, new("Texture/logo.dds"));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Empty(result.Project!.EditedTextures);
        Assert.Empty(result.Project.ImageAssets);
        Assert.Equal(ProjectBuildStatus.Dirty, result.Project.BuildState.Status);
        Assert.Equal(2, result.Project.EditState.CurrentRevision);
        Assert.True(context.Restore.Transaction.Committed);
        Assert.False(context.Restore.Transaction.RolledBack);
        Assert.True(context.Store.Saved);
        Assert.True(context.Cache.Stored);
        Assert.True(context.Fresh.Disposed);
        Assert.False(context.Current.Removed);
    }

    [Fact]
    public async Task Reset_texture_rolls_back_file_when_project_save_fails()
    {
        var context = new Context();
        context.Store.Succeed = false;

        var result = await context.Service.ResetTextureAsync(
            context.Project, context.Current, new("Texture/logo.dds"));

        Assert.Equal(ProjectResetFailureReason.ProjectSaveFailed, result.FailureReason);
        Assert.True(context.Restore.Transaction.RolledBack);
        Assert.False(context.Restore.Transaction.Committed);
        Assert.False(context.Cache.Stored);
    }

    [Fact]
    public async Task Reset_unedited_texture_stops_before_template_acquisition()
    {
        var context = new Context(project: CreateProject(includeEdit: false));

        var result = await context.Service.ResetTextureAsync(
            context.Project, context.Current, new("Texture/logo.dds"));

        Assert.Equal(ProjectResetFailureReason.TextureNotEdited, result.FailureReason);
        Assert.False(context.Acquisition.Called);
    }

    [Fact]
    public async Task Reset_project_commits_fresh_workspace_and_removes_only_previous_workspace()
    {
        var context = new Context();

        var result = await context.Service.ResetProjectAsync(context.Project, context.Current);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Same(context.Fresh, result.Workspace);
        Assert.Equal(context.Project.ProjectId, result.Project!.ProjectId);
        Assert.Equal(context.Project.TemplateIdentity, result.Project.TemplateIdentity);
        Assert.Empty(result.Project.EditedTextures);
        Assert.Empty(result.Project.ImageAssets);
        Assert.Empty(result.Project.AiAssets);
        Assert.Equal(ProjectBuildStatus.NotBuilt, result.Project.BuildState.Status);
        Assert.True(context.Fresh.Retained);
        Assert.True(context.Current.Removed);
        Assert.False(context.Fresh.Removed);
        Assert.True(context.Store.Saved);
    }

    [Fact]
    public async Task Reset_project_extract_failure_disposes_fresh_workspace_without_saving()
    {
        var context = new Context();
        context.Archive.Succeed = false;

        var result = await context.Service.ResetProjectAsync(context.Project, context.Current);

        Assert.Equal(ProjectResetFailureReason.ArchiveExtractionFailed, result.FailureReason);
        Assert.True(context.Fresh.Disposed);
        Assert.False(context.Store.Saved);
        Assert.False(context.Current.Removed);
    }

    [Fact]
    public async Task Reset_project_save_failure_removes_fresh_workspace_and_preserves_current_workspace()
    {
        var context = new Context();
        context.Store.Succeed = false;

        var result = await context.Service.ResetProjectAsync(context.Project, context.Current);

        Assert.Equal(ProjectResetFailureReason.ProjectSaveFailed, result.FailureReason);
        Assert.True(context.Fresh.Removed);
        Assert.False(context.Current.Removed);
    }

    [Fact]
    public async Task Post_commit_cache_and_old_cleanup_failures_are_explicit_recovery_flags()
    {
        var context = new Context();
        context.Cache.Succeed = false;
        context.Workspaces.FailCurrentRemoval = true;

        var result = await context.Service.ResetProjectAsync(context.Project, context.Current);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(result.MetadataCacheRecoveryRequired);
        Assert.True(result.PreviousWorkspaceCleanupPending);
        Assert.True(context.Fresh.Retained);
        Assert.False(context.Fresh.Removed);
    }

    [Fact]
    public async Task Missing_exact_template_never_acquires_or_mutates_workspace()
    {
        var context = new Context(templateAvailable: false);

        var result = await context.Service.ResetProjectAsync(context.Project, context.Current);

        Assert.Equal(ProjectResetFailureReason.TemplateMissing, result.FailureReason);
        Assert.False(context.Acquisition.Called);
        Assert.False(context.Workspaces.Created);
    }

    private sealed class Context
    {
        public Context(
            AuditionProject? project = null,
            bool templateAvailable = true)
        {
            Template = new(
                "archive-015", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
                "audition_vn", "015", "1", new string('C', 64), "audition-vn-2026");
            Project = project ?? CreateProject();
            Current = new StubWorkspace(Template, Project.ProjectId, Project.Workspace.WorkspaceId);
            Fresh = new StubWorkspace(Template, Project.ProjectId, "fedcba9876543210fedcba9876543210");
            Acquisition = new();
            Workspaces = new(Fresh);
            Archive = new();
            Scan = new();
            Restore = new();
            Store = new();
            Cache = new();
            Service = new(
                new StubTemplateCatalog(templateAvailable ? Template : null),
                new StubEntitlement(),
                Acquisition,
                new StubRegionResolver(),
                Workspaces,
                Archive,
                Scan,
                Restore,
                Store,
                Cache,
                new FixedTimeProvider());
        }

        public AuditionArchiveTemplate Template { get; }
        public AuditionProject Project { get; }
        public StubWorkspace Current { get; }
        public StubWorkspace Fresh { get; }
        public StubAcquisition Acquisition { get; }
        public StubWorkspaceService Workspaces { get; }
        public StubArchive Archive { get; }
        public StubScan Scan { get; }
        public StubRestore Restore { get; }
        public StubStore Store { get; }
        public StubCache Cache { get; }
        public ProjectResetService Service { get; }
    }

    private static AuditionProject CreateProject(bool includeEdit = true)
    {
        var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var asset = new ProjectAssetRecord(
            new("image_asset"), new("Assets/logo.png"), new(new string('B', 64)));
        var edit = new ProjectEditedTextureRecord(
            new("Texture/logo.dds"), new(new string('A', 64)), new(new string('B', 64)), asset.Id, 1);
        return AuditionProject.Create(
            1,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Project",
            new("audition"),
            new("login_mod"),
            new(new("archive-015"), new("1"), new(new string('C', 64)), new("audition-vn-2026")),
            new("0123456789abcdef0123456789abcdef", new("Working/015.ab"), new("Extracted/015")),
            includeEdit ? [edit] : [],
            includeEdit ? [asset] : [],
            [],
            new(includeEdit ? 1 : 0, 0, includeEdit ? new ModRelativePath("Texture/logo.dds") : null, []),
            new(ProjectBuildStatus.Dirty, null, null, null),
            now,
            now).Project!;
    }

    private sealed class StubTemplateCatalog(AuditionArchiveTemplate? template) : ITemplateVersionCatalog
    {
        public bool TryGetExact(TemplateId id, TemplateVersion version,
            [NotNullWhen(true)] out AuditionArchiveTemplate? exact)
        {
            exact = template;
            return exact is not null;
        }
        public bool TryGetCurrent(TemplateId id, [NotNullWhen(true)] out AuditionArchiveTemplate? current)
        {
            current = template;
            return current is not null;
        }
        public TemplateResolutionResult Resolve(ArchiveTemplateReference snapshot) =>
            new(TemplateResolutionStatus.ExactMatch, template, template);
    }

    private sealed class StubEntitlement : ITemplateEntitlementService
    {
        public Task<TemplateEntitlementResult> CheckAsync(TemplateIdentity identity, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TemplateEntitlementResult(TemplateEntitlementStatus.NotRequired, null));
    }

    private sealed class StubAcquisition : IProjectTemplateAcquisitionService
    {
        public bool Called { get; private set; }
        public Task<ProjectTemplateAcquisitionResult> AcquireAsync(AuditionArchiveTemplate template,
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

    private sealed class StubWorkspaceService(StubWorkspace fresh) : IProjectArchiveWorkspaceService,
        IProjectArchiveWorkspaceRetentionService, IProjectArchiveWorkspaceRemovalService
    {
        public bool Created { get; private set; }
        public bool FailCurrentRemoval { get; set; }
        public Task<ProjectArchiveWorkspaceCreateResult> CreateAsync(
            ProjectArchiveWorkspaceCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Created = true;
            return Task.FromResult(ProjectArchiveWorkspaceCreateResult.Success(fresh));
        }
        public Task<ProjectArchiveWorkspaceValidationResult> ValidateAsync(
            IProjectArchiveWorkspace value, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectArchiveWorkspaceValidationResult.Valid());
        public void Retain(IProjectArchiveWorkspace workspace) => ((StubWorkspace)workspace).Retained = true;
        public Task<bool> RemoveAsync(IProjectArchiveWorkspace workspace, CancellationToken cancellationToken = default)
        {
            if (FailCurrentRemoval && !ReferenceEquals(workspace, fresh))
            {
                return Task.FromResult(false);
            }

            ((StubWorkspace)workspace).Removed = true;
            return Task.FromResult(true);
        }
    }

    private sealed class StubArchive : IAuditionArchiveService
    {
        public bool Succeed { get; set; } = true;
        public Task<ArchiveExtractResult> ExtractAsync(
            ArchiveExtractRequest request, IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ArchiveExtractResult(new(
                ArchiveOperation.Extract,
                Succeed ? ArchiveOperationState.Completed : ArchiveOperationState.Failed,
                Succeed, Succeed ? 1 : 0,
                Succeed ? ArchiveFailureReason.None : ArchiveFailureReason.RunnerFailed,
                null, [])));
        public Task<ArchivePackResult> PackAsync(
            ArchivePackRequest request, IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubScan : ISmartModScanService
    {
        public Task<SmartModScanResult> ScanAsync(
            SmartModScanRequest request, IProgress<SmartModScanProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SmartModScanResult.Success(new ArchiveAssetCatalog([], 0, 0), [], []));
    }

    private sealed class StubRestore : IProjectTextureRestoreService
    {
        public StubTransaction Transaction { get; } = new();
        public Task<ProjectTextureRestoreResult> BeginRestoreAsync(
            IProjectArchiveWorkspace targetWorkspace, IProjectArchiveWorkspace pristineWorkspace,
            ModRelativePath texturePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectTextureRestoreResult.Success(Transaction));
    }

    private sealed class StubTransaction : IProjectTextureRestoreTransaction
    {
        public bool CleanupPending => false;
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Committed = true;
            return Task.CompletedTask;
        }
        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RolledBack = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubStore : IAuditionProjectStore
    {
        public bool Succeed { get; set; } = true;
        public bool Saved { get; private set; }
        public Task<AuditionProjectStoreResult> SaveAsync(AuditionProject project, CancellationToken cancellationToken = default)
        {
            Saved = true;
            return Task.FromResult(Succeed
                ? AuditionProjectStoreResult.Success("project.audproj")
                : AuditionProjectStoreResult.Failure(AuditionProjectStoreFailureReason.IoFailure, "SAVE_FAILED"));
        }
        public Task<AuditionProjectStoreResult> DeleteAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditionProjectStoreResult.Success("project.audproj"));
        public Task<AuditionProjectLoadResult> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubCache : IProjectMetadataCache
    {
        public bool Succeed { get; set; } = true;
        public bool Stored { get; private set; }
        public Task<ProjectMetadataCacheResult> StoreAsync(Guid projectId,
            IEnumerable<ProjectTextureMetadataSnapshot> textures, CancellationToken cancellationToken = default)
        {
            Stored = true;
            return Task.FromResult(Succeed
                ? ProjectMetadataCacheResult.Success()
                : ProjectMetadataCacheResult.Failure(ProjectMetadataCacheFailureReason.IoFailure, "CACHE_FAILED"));
        }
        public Task<ProjectMetadataCacheResult> DeleteAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectMetadataCacheResult.Success());
        public Task<ProjectMetadataCacheValidationResult> ValidateAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubWorkspace : IProjectArchiveWorkspace
    {
        public StubWorkspace(AuditionArchiveTemplate template, Guid projectId, string workspaceId)
        {
            var secure = new StubSecureWorkspace(workspaceId);
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
            Descriptor = new(
                1, projectId, "Project", workspaceId,
                new(template.TemplateId.Value, template.TemplateVersion!.Value.Value,
                    template.ExpectedSha256!.Value.Value, template.EngineType, template.RegionProfileId,
                    template.CompatibleGameBuild!.Value.Value),
                "Working/015.ab", "Extracted/015", "BuildOutput", null,
                ".project-archive-workspace.json", template.ExpectedSha256.Value.Value,
                now, now, ProjectArchiveWorkspaceState.Ready);
        }
        public bool Disposed { get; private set; }
        public bool Retained { get; set; }
        public bool Removed { get; set; }
        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubSecureWorkspace(string id) : ISecureWorkspace
    {
        public string Id { get; } = id;
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
