using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class UnavailableTemplateEntitlementService : ITemplateEntitlementService
{
    public Task<TemplateEntitlementResult> CheckAsync(
        TemplateIdentity identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TemplateEntitlementResult(
            TemplateEntitlementStatus.Unavailable,
            "PROJECT_TEMPLATE_ENTITLEMENT_PROVIDER_UNAVAILABLE"));
}

public sealed class UnavailableProjectTemplateAcquisitionService : IProjectTemplateAcquisitionService
{
    public Task<ProjectTemplateAcquisitionResult> AcquireAsync(
        AuditionArchiveTemplate template,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ProjectTemplateAcquisitionResult.Failure(
            "PROJECT_TEMPLATE_ACQUISITION_PROVIDER_UNAVAILABLE"));
}

public sealed class ProjectCreationService(
    IGameCatalog gameCatalog,
    IModCatalog modCatalog,
    IGameRegionProfileResolver regionProfiles,
    ITemplateEntitlementService entitlementService,
    IProjectTemplateAcquisitionService templateAcquisition,
    IProjectArchiveWorkspaceService workspaceService,
    IAuditionArchiveService archiveService,
    ISmartModScanService smartScanService,
    IProjectMetadataCache metadataCache,
    IAuditionProjectStore projectStore,
    TimeProvider? timeProvider = null) : IProjectCreationService
{
    private const int TotalSteps = 10;
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(3);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProjectCreationResult> CreateAsync(
        ProjectCreationRequest request,
        IProgress<ProjectCreationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Report(progress, ProjectCreationPhase.ValidatingSelection, 0);
        if (!request.GameId.IsValid || !request.ModId.IsValid
            || string.IsNullOrWhiteSpace(request.ProjectName)
            || request.ProjectName.Length > AuditionProject.MaximumNameLength
            || request.ProjectName.Any(char.IsControl))
        {
            return Failure(ProjectCreationFailureReason.InvalidRequest, "PROJECT_CREATE_REQUEST_INVALID");
        }

        if (!gameCatalog.TryGetGame(request.GameId, out _))
        {
            return Failure(ProjectCreationFailureReason.UnknownGame, "PROJECT_CREATE_GAME_UNKNOWN");
        }

        if (!modCatalog.TryGetMod(request.GameId, request.ModId, out var mod))
        {
            return Failure(ProjectCreationFailureReason.UnknownMod, "PROJECT_CREATE_MOD_UNKNOWN");
        }

        if (mod.ArchiveTemplate.Identity is not { IsValid: true } identity)
        {
            return Failure(ProjectCreationFailureReason.TemplateAcquisitionFailed, "PROJECT_CREATE_TEMPLATE_INVALID");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, ProjectCreationPhase.CheckingEntitlement, 1);
            var entitlement = await entitlementService.CheckAsync(identity, cancellationToken).ConfigureAwait(false);
            if (entitlement.Status is TemplateEntitlementStatus.Denied)
            {
                return Failure(ProjectCreationFailureReason.EntitlementDenied,
                    entitlement.DiagnosticCode ?? "PROJECT_CREATE_ENTITLEMENT_DENIED");
            }

            if (entitlement.Status is TemplateEntitlementStatus.Unavailable
                || !Enum.IsDefined(entitlement.Status))
            {
                return Failure(ProjectCreationFailureReason.EntitlementUnavailable,
                    entitlement.DiagnosticCode ?? "PROJECT_CREATE_ENTITLEMENT_UNAVAILABLE");
            }

            Report(progress, ProjectCreationPhase.AcquiringTemplate, 2);
            var acquired = await templateAcquisition.AcquireAsync(mod.ArchiveTemplate, cancellationToken)
                .ConfigureAwait(false);
            if (!acquired.Succeeded || acquired.Source is null)
            {
                return Failure(ProjectCreationFailureReason.TemplateAcquisitionFailed,
                    acquired.DiagnosticCode ?? "PROJECT_CREATE_TEMPLATE_ACQUIRE_FAILED");
            }

            if (!regionProfiles.TryResolve(mod.ArchiveTemplate.RegionProfileId, out _))
            {
                return Failure(ProjectCreationFailureReason.RegionProfileMissing, "PROJECT_CREATE_REGION_MISSING");
            }

            Report(progress, ProjectCreationPhase.CreatingWorkspace, 3);
            var workspaceResult = await workspaceService.CreateAsync(
                new(request.ProjectName, mod.ArchiveTemplate, acquired.Source),
                cancellationToken).ConfigureAwait(false);
            if (!workspaceResult.Succeeded || workspaceResult.Workspace is null)
            {
                return Failure(ProjectCreationFailureReason.WorkspaceCreationFailed,
                    workspaceResult.DiagnosticCode ?? "PROJECT_CREATE_WORKSPACE_FAILED");
            }

            var workspace = workspaceResult.Workspace;
            var cacheStored = false;
            try
            {
                Report(progress, ProjectCreationPhase.PreparingKeydat, 4);
                if (mod.KeydatStrategy != ModKeydatStrategy.ReuseOrGenerate)
                {
                    return await FailAndRollbackAsync(
                        ProjectCreationFailureReason.KeydatPolicyInvalid,
                        "PROJECT_CREATE_KEYDAT_POLICY_INVALID",
                        workspace,
                        workspace.Descriptor.ProjectId,
                        cacheStored).ConfigureAwait(false);
                }

                Report(progress, ProjectCreationPhase.ExtractingArchive, 5);
                var extracted = await archiveService.ExtractAsync(
                    new(mod.ArchiveTemplate, workspace.ArchiveWorkspace, acquired.Source, ExtractTimeout),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!extracted.Command.Succeeded)
                {
                    return await FailAndRollbackAsync(
                        ProjectCreationFailureReason.ArchiveExtractionFailed,
                        "PROJECT_CREATE_EXTRACT_FAILED",
                        workspace,
                        workspace.Descriptor.ProjectId,
                        cacheStored).ConfigureAwait(false);
                }

                Report(progress, ProjectCreationPhase.ScanningTextures, 6);
                var scan = await smartScanService.ScanAsync(
                    new(request.GameId, request.ModId, workspace),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!scan.Succeeded)
                {
                    return await FailAndRollbackAsync(
                        scan.Cancelled ? ProjectCreationFailureReason.Cancelled : ProjectCreationFailureReason.TextureScanFailed,
                        scan.DiagnosticCode ?? "PROJECT_CREATE_SCAN_FAILED",
                        workspace,
                        workspace.Descriptor.ProjectId,
                        cacheStored).ConfigureAwait(false);
                }

                var metadata = scan.Groups
                    .SelectMany(group => group.Textures)
                    .Select(texture => new ProjectTextureMetadataSnapshot(
                        new(texture.Asset.RelativePath),
                        new(texture.Asset.Sha256),
                        texture.Metadata,
                        texture.ManifestResolution.Slot?.Id))
                    .ToArray();

                Report(progress, ProjectCreationPhase.CachingMetadata, 7);
                var cached = await metadataCache.StoreAsync(
                    workspace.Descriptor.ProjectId,
                    metadata,
                    cancellationToken).ConfigureAwait(false);
                if (!cached.Succeeded)
                {
                    return await FailAndRollbackAsync(
                        cached.FailureReason == ProjectMetadataCacheFailureReason.Cancelled
                            ? ProjectCreationFailureReason.Cancelled
                            : ProjectCreationFailureReason.MetadataCacheFailed,
                        cached.DiagnosticCode ?? "PROJECT_CREATE_METADATA_FAILED",
                        workspace,
                        workspace.Descriptor.ProjectId,
                        cacheStored).ConfigureAwait(false);
                }

                cacheStored = true;
                var now = _timeProvider.GetUtcNow();
                var model = AuditionProject.Create(
                    AuditionProject.CurrentSchemaVersion,
                    workspace.Descriptor.ProjectId,
                    request.ProjectName,
                    request.GameId,
                    request.ModId,
                    identity,
                    new(
                        workspace.Descriptor.WorkspaceId,
                        new(workspace.Descriptor.WorkingArchiveRelativePath),
                        new(workspace.Descriptor.ExtractedDirectoryRelativePath)),
                    [],
                    [],
                    [],
                    new(0, 0, null, []),
                    new(ProjectBuildStatus.NotBuilt, null, null, null),
                    now,
                    now);
                if (!model.Succeeded)
                {
                    return await FailAndRollbackAsync(
                        ProjectCreationFailureReason.ProjectSaveFailed,
                        "PROJECT_CREATE_MODEL_INVALID",
                        workspace,
                        workspace.Descriptor.ProjectId,
                        cacheStored).ConfigureAwait(false);
                }

                Report(progress, ProjectCreationPhase.SavingProject, 8);
                var saved = await projectStore.SaveAsync(model.Project!, cancellationToken).ConfigureAwait(false);
                if (!saved.Succeeded)
                {
                    return await FailAndRollbackAsync(
                        saved.FailureReason == AuditionProjectStoreFailureReason.Cancelled
                            ? ProjectCreationFailureReason.Cancelled
                            : ProjectCreationFailureReason.ProjectSaveFailed,
                        saved.DiagnosticCode ?? "PROJECT_CREATE_SAVE_FAILED",
                        workspace,
                        workspace.Descriptor.ProjectId,
                        cacheStored).ConfigureAwait(false);
                }

                Report(progress, ProjectCreationPhase.Completed, TotalSteps);
                return ProjectCreationResult.Success(model.Project!, workspace);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await FailAndRollbackAsync(
                    ProjectCreationFailureReason.Cancelled,
                    "PROJECT_CREATE_CANCELLED",
                    workspace,
                    workspace.Descriptor.ProjectId,
                    cacheStored).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                                              or InvalidDataException
                                              or InvalidOperationException
                                              or UnauthorizedAccessException
                                              or ArgumentException)
            {
                return await FailAndRollbackAsync(
                    ProjectCreationFailureReason.ArchiveExtractionFailed,
                    "PROJECT_CREATE_OPERATION_FAILED",
                    workspace,
                    workspace.Descriptor.ProjectId,
                    cacheStored).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(ProjectCreationFailureReason.Cancelled, "PROJECT_CREATE_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException
                                          or ArgumentException)
        {
            return Failure(ProjectCreationFailureReason.TemplateAcquisitionFailed, "PROJECT_CREATE_PREREQUISITE_FAILED");
        }
    }

    private async Task<ProjectCreationResult> FailAndRollbackAsync(
        ProjectCreationFailureReason reason,
        string code,
        IProjectArchiveWorkspace workspace,
        Guid projectId,
        bool cacheStored)
    {
        var rollbackFailed = false;
        if (cacheStored)
        {
            var cacheDelete = await metadataCache.DeleteAsync(projectId, CancellationToken.None).ConfigureAwait(false);
            rollbackFailed = !cacheDelete.Succeeded;
        }

        var projectDelete = await projectStore.DeleteAsync(projectId, CancellationToken.None).ConfigureAwait(false);
        rollbackFailed |= !projectDelete.Succeeded;
        try
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            rollbackFailed = true;
        }

        return rollbackFailed
            ? Failure(ProjectCreationFailureReason.RollbackFailed, "PROJECT_CREATE_ROLLBACK_FAILED")
            : Failure(reason, code);
    }

    private static ProjectCreationResult Failure(ProjectCreationFailureReason reason, string code) =>
        ProjectCreationResult.Failure(reason, code);

    private static void Report(
        IProgress<ProjectCreationProgress>? progress,
        ProjectCreationPhase phase,
        int completedSteps) => progress?.Report(new(phase, completedSteps, TotalSteps));
}
