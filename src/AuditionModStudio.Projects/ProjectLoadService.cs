using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class ProjectLoadService(
    IAuditionProjectStore projectStore,
    ITemplateVersionCatalog templateCatalog,
    IProjectArchiveWorkspaceRecoveryService workspaceRecovery,
    ITemplateEntitlementService entitlementService,
    IProjectTemplateAcquisitionService templateAcquisition,
    IGameRegionProfileResolver regionProfiles,
    IProjectArchiveWorkspaceService workspaceService,
    IAuditionArchiveService archiveService,
    ISmartModScanService smartScanService,
    IProjectMetadataCache metadataCache,
    TimeProvider? timeProvider = null) : IProjectLoadService
{
    private const int TotalSteps = 8;
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(3);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProjectLoadResult> LoadAsync(
        Guid projectId,
        IProgress<ProjectLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, ProjectLoadPhase.LoadingProject, 0);
        var loaded = await projectStore.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!loaded.Succeeded || loaded.Project is null)
        {
            return Failure(MapLoadFailure(loaded.FailureReason), loaded.DiagnosticCode ?? "PROJECT_LOAD_FAILED");
        }

        var project = loaded.Project;
        Report(progress, ProjectLoadPhase.ResolvingTemplate, 1);
        if (!templateCatalog.TryGetExact(
                project.TemplateIdentity.TemplateId,
                project.TemplateIdentity.Version,
                out var template))
        {
            return Failure(ProjectLoadFailureReason.TemplateMissing, "PROJECT_LOAD_TEMPLATE_MISSING");
        }

        if (template.ExpectedSha256 != project.TemplateIdentity.Sha256
            || template.CompatibleGameBuild != project.TemplateIdentity.CompatibleGameBuild)
        {
            return Failure(ProjectLoadFailureReason.TemplateMismatch, "PROJECT_LOAD_TEMPLATE_MISMATCH");
        }

        IProjectArchiveWorkspace? ownedWorkspace = null;
        try
        {
            Report(progress, ProjectLoadPhase.ValidatingWorkspace, 2);
            var recovered = await workspaceRecovery.TryRecoverAsync(project, cancellationToken).ConfigureAwait(false);
            if (recovered.Recovered && recovered.Workspace is not null)
            {
                ownedWorkspace = recovered.Workspace;
                var existingResult = await CompleteWithWorkspaceAsync(
                    project,
                    recovered.Workspace,
                    workspaceReextracted: false,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                ownedWorkspace = null;
                return existingResult;
            }

            Report(progress, ProjectLoadPhase.RecoveringWorkspace, 3);
            var entitlement = await entitlementService.CheckAsync(project.TemplateIdentity, cancellationToken)
                .ConfigureAwait(false);
            if (entitlement.Status == TemplateEntitlementStatus.Denied)
            {
                return Failure(ProjectLoadFailureReason.EntitlementDenied,
                    entitlement.DiagnosticCode ?? "PROJECT_LOAD_ENTITLEMENT_DENIED");
            }

            if (entitlement.Status == TemplateEntitlementStatus.Unavailable
                || !Enum.IsDefined(entitlement.Status))
            {
                return Failure(ProjectLoadFailureReason.EntitlementUnavailable,
                    entitlement.DiagnosticCode ?? "PROJECT_LOAD_ENTITLEMENT_UNAVAILABLE");
            }

            var acquired = await templateAcquisition.AcquireAsync(template, cancellationToken).ConfigureAwait(false);
            if (!acquired.Succeeded || acquired.Source is null)
            {
                return Failure(ProjectLoadFailureReason.TemplateAcquisitionFailed,
                    acquired.DiagnosticCode ?? "PROJECT_LOAD_TEMPLATE_ACQUIRE_FAILED");
            }

            if (!regionProfiles.TryResolve(template.RegionProfileId, out _))
            {
                return Failure(ProjectLoadFailureReason.RegionProfileMissing, "PROJECT_LOAD_REGION_MISSING");
            }

            var created = await workspaceService.CreateAsync(
                new(project.Name, template, acquired.Source, project.ProjectId),
                cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded || created.Workspace is null)
            {
                return Failure(ProjectLoadFailureReason.WorkspaceRecoveryFailed,
                    created.DiagnosticCode ?? "PROJECT_LOAD_WORKSPACE_CREATE_FAILED");
            }

            var workspace = created.Workspace;
            ownedWorkspace = workspace;
            var extract = await archiveService.ExtractAsync(
                new(template, workspace.ArchiveWorkspace, acquired.Source, ExtractTimeout),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!extract.Command.Succeeded)
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
                ownedWorkspace = null;
                return Failure(ProjectLoadFailureReason.ArchiveExtractionFailed, "PROJECT_LOAD_REEXTRACT_FAILED");
            }

            var updated = CopyWithWorkspace(project, workspace);
            if (!updated.Succeeded)
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
                ownedWorkspace = null;
                return Failure(ProjectLoadFailureReason.ProjectInvalid, "PROJECT_LOAD_RECOVERED_MODEL_INVALID");
            }

            var completed = await CompleteWithWorkspaceAsync(
                updated.Project!,
                workspace,
                workspaceReextracted: true,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!completed.Succeeded)
            {
                ownedWorkspace = null;
                return completed;
            }

            Report(progress, ProjectLoadPhase.SavingRecoveredProject, 6);
            var saved = await projectStore.SaveAsync(completed.Project!, cancellationToken).ConfigureAwait(false);
            if (!saved.Succeeded)
            {
                await completed.Workspace!.DisposeAsync().ConfigureAwait(false);
                ownedWorkspace = null;
                return Failure(ProjectLoadFailureReason.ProjectSaveFailed,
                    saved.DiagnosticCode ?? "PROJECT_LOAD_RECOVERED_SAVE_FAILED");
            }

            if (workspaceService is IProjectArchiveWorkspaceRetentionService retention)
            {
                retention.Retain(completed.Workspace!);
            }

            Report(progress, ProjectLoadPhase.Completed, TotalSteps);
            ownedWorkspace = null;
            return completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (ownedWorkspace is not null)
            {
                await ownedWorkspace.DisposeAsync().ConfigureAwait(false);
            }

            return Failure(ProjectLoadFailureReason.Cancelled, "PROJECT_LOAD_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            if (ownedWorkspace is not null)
            {
                await ownedWorkspace.DisposeAsync().ConfigureAwait(false);
            }

            return Failure(ProjectLoadFailureReason.WorkspaceRecoveryFailed, "PROJECT_LOAD_RECOVERY_FAILED");
        }
    }

    private async Task<ProjectLoadResult> CompleteWithWorkspaceAsync(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        bool workspaceReextracted,
        IProgress<ProjectLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, ProjectLoadPhase.ValidatingMetadataCache, 4);
        var cache = await metadataCache.ValidateAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        if (cache.Status == ProjectMetadataCacheValidationStatus.Cancelled)
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
            return Failure(ProjectLoadFailureReason.Cancelled, cache.DiagnosticCode ?? "PROJECT_LOAD_CANCELLED");
        }

        var cacheRecovered = false;
        if (workspaceReextracted || !cache.IsValid)
        {
            Report(progress, ProjectLoadPhase.RecoveringMetadataCache, 5);
            var scan = await smartScanService.ScanAsync(
                new(project.GameId, project.ModId, workspace),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!scan.Succeeded)
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
                return Failure(
                    scan.Cancelled ? ProjectLoadFailureReason.Cancelled : ProjectLoadFailureReason.MetadataRecoveryFailed,
                    scan.DiagnosticCode ?? "PROJECT_LOAD_METADATA_SCAN_FAILED");
            }

            var metadata = scan.Groups.SelectMany(group => group.Textures).Select(texture =>
                new ProjectTextureMetadataSnapshot(
                    new(texture.Asset.RelativePath),
                    new(texture.Asset.Sha256),
                    texture.Metadata,
                    texture.ManifestResolution.Slot?.Id));
            var stored = await metadataCache.StoreAsync(project.ProjectId, metadata, cancellationToken)
                .ConfigureAwait(false);
            if (!stored.Succeeded)
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
                return Failure(ProjectLoadFailureReason.MetadataRecoveryFailed,
                    stored.DiagnosticCode ?? "PROJECT_LOAD_METADATA_SAVE_FAILED");
            }

            cacheRecovered = true;
        }

        Report(progress, ProjectLoadPhase.Completed, TotalSteps);
        return ProjectLoadResult.Success(project, workspace, cacheRecovered, workspaceReextracted);
    }

    private AuditionProjectCreateResult CopyWithWorkspace(
        AuditionProject project,
        IProjectArchiveWorkspace workspace) => AuditionProject.Create(
        project.SchemaVersion,
        project.ProjectId,
        project.Name,
        project.GameId,
        project.ModId,
        project.TemplateIdentity,
        new(
            workspace.Descriptor.WorkspaceId,
            new(workspace.Descriptor.WorkingArchiveRelativePath),
            new(workspace.Descriptor.ExtractedDirectoryRelativePath)),
        project.EditedTextures,
        project.ImageAssets,
        project.AiAssets,
        project.EditState,
        project.BuildState,
        project.CreatedAt,
        _timeProvider.GetUtcNow());

    private static ProjectLoadFailureReason MapLoadFailure(AuditionProjectLoadFailureReason reason) => reason switch
    {
        AuditionProjectLoadFailureReason.Missing => ProjectLoadFailureReason.ProjectMissing,
        AuditionProjectLoadFailureReason.Corrupt => ProjectLoadFailureReason.ProjectCorrupt,
        AuditionProjectLoadFailureReason.Cancelled => ProjectLoadFailureReason.Cancelled,
        _ => ProjectLoadFailureReason.ProjectInvalid
    };

    private static ProjectLoadResult Failure(ProjectLoadFailureReason reason, string code) =>
        ProjectLoadResult.Failure(reason, code);

    private static void Report(IProgress<ProjectLoadProgress>? progress, ProjectLoadPhase phase, int completed) =>
        progress?.Report(new(phase, completed, TotalSteps));
}
