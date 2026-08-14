using System.Collections.Immutable;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class ProjectResetService(
    ITemplateVersionCatalog templateCatalog,
    ITemplateEntitlementService entitlementService,
    IProjectTemplateAcquisitionService templateAcquisition,
    IGameRegionProfileResolver regionProfiles,
    IProjectArchiveWorkspaceService workspaceService,
    IAuditionArchiveService archiveService,
    ISmartModScanService smartScanService,
    IProjectTextureRestoreService textureRestoreService,
    IAuditionProjectStore projectStore,
    IProjectMetadataCache metadataCache,
    TimeProvider? timeProvider = null) : IProjectResetService
{
    private const int TotalSteps = 10;
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(3);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _resetGate = new(1, 1);

    public async Task<ProjectResetResult> ResetTextureAsync(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        ModRelativePath texturePath,
        IProgress<ProjectResetProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure(ProjectResetFailureReason.Cancelled, "PROJECT_RESET_TEXTURE_CANCELLED");
        }

        try
        {
            return await ResetTextureCoreAsync(project, workspace, texturePath, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _resetGate.Release();
        }
    }

    private async Task<ProjectResetResult> ResetTextureCoreAsync(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        ModRelativePath texturePath,
        IProgress<ProjectResetProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, ProjectResetPhase.ValidatingProject, 0);
        var validation = Validate(project, workspace);
        if (validation is not null || !texturePath.IsValid)
        {
            return validation ?? Failure(ProjectResetFailureReason.InvalidRequest, "PROJECT_RESET_TEXTURE_PATH_INVALID");
        }

        if (!project.EditedTextures.Any(item => SamePath(item.RelativePath.Value, texturePath.Value)))
        {
            return Failure(ProjectResetFailureReason.TextureNotEdited, "PROJECT_RESET_TEXTURE_NOT_EDITED");
        }

        var prepared = await PrepareFreshWorkspaceSafelyAsync(project, progress, cancellationToken).ConfigureAwait(false);
        if (!prepared.Succeeded || prepared.Workspace is null)
        {
            return prepared.Result!;
        }

        await using var pristineWorkspace = prepared.Workspace;
        IProjectTextureRestoreTransaction? transaction = null;
        try
        {
            Report(progress, ProjectResetPhase.RestoringTexture, 5);
            var restored = await textureRestoreService.BeginRestoreAsync(
                workspace, pristineWorkspace, texturePath, cancellationToken).ConfigureAwait(false);
            if (!restored.Succeeded || restored.Transaction is null)
            {
                return Failure(
                    cancellationToken.IsCancellationRequested
                        ? ProjectResetFailureReason.Cancelled
                        : ProjectResetFailureReason.TextureRestoreFailed,
                    restored.DiagnosticCode ?? "PROJECT_RESET_TEXTURE_RESTORE_FAILED");
            }

            transaction = restored.Transaction;
            Report(progress, ProjectResetPhase.ScanningTextures, 6);
            var scan = await smartScanService.ScanAsync(
                new(project.GameId, project.ModId, workspace),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!scan.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                transaction = null;
                return Failure(
                    scan.Cancelled ? ProjectResetFailureReason.Cancelled : ProjectResetFailureReason.TextureScanFailed,
                    scan.DiagnosticCode ?? "PROJECT_RESET_TEXTURE_SCAN_FAILED");
            }

            var updated = ResetTextureModel(project, texturePath);
            if (!updated.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                transaction = null;
                return Failure(ProjectResetFailureReason.ProjectModelInvalid, "PROJECT_RESET_TEXTURE_MODEL_INVALID");
            }

            Report(progress, ProjectResetPhase.SavingProject, 7);
            var saved = await projectStore.SaveAsync(updated.Project!, cancellationToken).ConfigureAwait(false);
            if (!saved.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                transaction = null;
                return Failure(
                    saved.FailureReason == AuditionProjectStoreFailureReason.Cancelled
                        ? ProjectResetFailureReason.Cancelled
                        : ProjectResetFailureReason.ProjectSaveFailed,
                    saved.DiagnosticCode ?? "PROJECT_RESET_TEXTURE_SAVE_FAILED");
            }

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            var temporaryCleanupPending = transaction.CleanupPending;
            transaction = null;
            Report(progress, ProjectResetPhase.UpdatingMetadataCache, 8);
            var cacheStored = await TryStoreCacheAsync(project.ProjectId, scan).ConfigureAwait(false);
            Report(progress, ProjectResetPhase.Completed, TotalSteps);
            return ProjectResetResult.Success(
                updated.Project!, workspace, !cacheStored, previousWorkspaceCleanupPending: false,
                temporaryCleanupPending);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return Failure(ProjectResetFailureReason.Cancelled, "PROJECT_RESET_TEXTURE_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return Failure(ProjectResetFailureReason.RollbackFailed, "PROJECT_RESET_TEXTURE_FAILED");
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<ProjectResetResult> ResetProjectAsync(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        IProgress<ProjectResetProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure(ProjectResetFailureReason.Cancelled, "PROJECT_RESET_CANCELLED");
        }

        try
        {
            return await ResetProjectCoreAsync(project, workspace, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resetGate.Release();
        }
    }

    private async Task<ProjectResetResult> ResetProjectCoreAsync(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        IProgress<ProjectResetProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, ProjectResetPhase.ValidatingProject, 0);
        var validation = Validate(project, workspace);
        if (validation is not null)
        {
            return validation;
        }

        var prepared = await PrepareFreshWorkspaceSafelyAsync(project, progress, cancellationToken).ConfigureAwait(false);
        if (!prepared.Succeeded || prepared.Workspace is null)
        {
            return prepared.Result!;
        }

        var freshWorkspace = prepared.Workspace;
        try
        {
            Report(progress, ProjectResetPhase.ScanningTextures, 6);
            var scan = await smartScanService.ScanAsync(
                new(project.GameId, project.ModId, freshWorkspace),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!scan.Succeeded)
            {
                await freshWorkspace.DisposeAsync().ConfigureAwait(false);
                return Failure(
                    scan.Cancelled ? ProjectResetFailureReason.Cancelled : ProjectResetFailureReason.TextureScanFailed,
                    scan.DiagnosticCode ?? "PROJECT_RESET_SCAN_FAILED");
            }

            var now = _timeProvider.GetUtcNow();
            var updated = AuditionProject.Create(
                project.SchemaVersion,
                project.ProjectId,
                project.Name,
                project.GameId,
                project.ModId,
                project.TemplateIdentity,
                new(
                    freshWorkspace.Descriptor.WorkspaceId,
                    new(freshWorkspace.Descriptor.WorkingArchiveRelativePath),
                    new(freshWorkspace.Descriptor.ExtractedDirectoryRelativePath)),
                [], [], [],
                new(0, 0, null, []),
                new(ProjectBuildStatus.NotBuilt, null, null, null),
                project.CreatedAt,
                now);
            if (!updated.Succeeded)
            {
                await freshWorkspace.DisposeAsync().ConfigureAwait(false);
                return Failure(ProjectResetFailureReason.ProjectModelInvalid, "PROJECT_RESET_MODEL_INVALID");
            }

            if (workspaceService is not IProjectArchiveWorkspaceRetentionService retention
                || workspaceService is not IProjectArchiveWorkspaceRemovalService removal)
            {
                await freshWorkspace.DisposeAsync().ConfigureAwait(false);
                return Failure(ProjectResetFailureReason.WorkspaceCreationFailed,
                    "PROJECT_RESET_WORKSPACE_LIFECYCLE_UNAVAILABLE");
            }

            retention.Retain(freshWorkspace);
            Report(progress, ProjectResetPhase.SavingProject, 7);
            var saved = await projectStore.SaveAsync(updated.Project!, cancellationToken).ConfigureAwait(false);
            if (!saved.Succeeded)
            {
                var removed = await TryRemoveAsync(removal, freshWorkspace).ConfigureAwait(false);
                return Failure(
                    removed ? ProjectResetFailureReason.ProjectSaveFailed : ProjectResetFailureReason.RollbackFailed,
                    removed
                        ? saved.DiagnosticCode ?? "PROJECT_RESET_SAVE_FAILED"
                        : "PROJECT_RESET_NEW_WORKSPACE_ROLLBACK_FAILED");
            }

            Report(progress, ProjectResetPhase.UpdatingMetadataCache, 8);
            var cacheStored = await TryStoreCacheAsync(project.ProjectId, scan).ConfigureAwait(false);
            Report(progress, ProjectResetPhase.RemovingPreviousWorkspace, 9);
            var oldRemoved = await TryRemoveAsync(removal, workspace).ConfigureAwait(false);
            Report(progress, ProjectResetPhase.Completed, TotalSteps);
            return ProjectResetResult.Success(updated.Project!, freshWorkspace, !cacheStored, !oldRemoved);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RemoveOrDisposeFreshAsync(freshWorkspace).ConfigureAwait(false);
            return Failure(ProjectResetFailureReason.Cancelled, "PROJECT_RESET_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            await RemoveOrDisposeFreshAsync(freshWorkspace).ConfigureAwait(false);
            return Failure(ProjectResetFailureReason.RollbackFailed, "PROJECT_RESET_FAILED");
        }
    }

    private async Task<PreparedWorkspace> PrepareFreshWorkspaceAsync(
        AuditionProject project,
        IProgress<ProjectResetProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, ProjectResetPhase.ResolvingTemplate, 1);
        if (!templateCatalog.TryGetExact(
                project.TemplateIdentity.TemplateId,
                project.TemplateIdentity.Version,
                out var template))
        {
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.TemplateMissing, "PROJECT_RESET_TEMPLATE_MISSING"));
        }

        if (template.ExpectedSha256 != project.TemplateIdentity.Sha256
            || template.CompatibleGameBuild != project.TemplateIdentity.CompatibleGameBuild)
        {
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.TemplateMismatch, "PROJECT_RESET_TEMPLATE_MISMATCH"));
        }

        Report(progress, ProjectResetPhase.AcquiringPristineTemplate, 2);
        var entitlement = await entitlementService.CheckAsync(project.TemplateIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (entitlement.Status is TemplateEntitlementStatus.Denied)
        {
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.EntitlementDenied,
                entitlement.DiagnosticCode ?? "PROJECT_RESET_ENTITLEMENT_DENIED"));
        }

        if (entitlement.Status is TemplateEntitlementStatus.Unavailable || !Enum.IsDefined(entitlement.Status))
        {
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.EntitlementUnavailable,
                entitlement.DiagnosticCode ?? "PROJECT_RESET_ENTITLEMENT_UNAVAILABLE"));
        }

        var acquired = await templateAcquisition.AcquireAsync(template, cancellationToken).ConfigureAwait(false);
        if (!acquired.Succeeded || acquired.Source is null)
        {
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.TemplateAcquisitionFailed,
                acquired.DiagnosticCode ?? "PROJECT_RESET_TEMPLATE_ACQUIRE_FAILED"));
        }

        if (!regionProfiles.TryResolve(template.RegionProfileId, out _))
        {
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.RegionProfileMissing, "PROJECT_RESET_REGION_MISSING"));
        }

        Report(progress, ProjectResetPhase.CreatingFreshWorkspace, 3);
        var created = await workspaceService.CreateAsync(
            new(project.Name, template, acquired.Source, project.ProjectId),
            cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded || created.Workspace is null)
        {
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.WorkspaceCreationFailed,
                created.DiagnosticCode ?? "PROJECT_RESET_WORKSPACE_CREATE_FAILED"));
        }

        Report(progress, ProjectResetPhase.ExtractingPristineArchive, 4);
        ArchiveExtractResult extract;
        try
        {
            extract = await archiveService.ExtractAsync(
                new(template, created.Workspace.ArchiveWorkspace, acquired.Source, ExtractTimeout),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await created.Workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (!extract.Command.Succeeded)
        {
            await created.Workspace.DisposeAsync().ConfigureAwait(false);
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.ArchiveExtractionFailed, "PROJECT_RESET_EXTRACT_FAILED"));
        }

        return PreparedWorkspace.Success(created.Workspace);
    }

    private async Task<PreparedWorkspace> PrepareFreshWorkspaceSafelyAsync(
        AuditionProject project,
        IProgress<ProjectResetProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PrepareFreshWorkspaceAsync(project, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.Cancelled, "PROJECT_RESET_CANCELLED"));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return PreparedWorkspace.Failure(Failure(
                ProjectResetFailureReason.WorkspaceCreationFailed, "PROJECT_RESET_PREPARE_FAILED"));
        }
    }

    private AuditionProjectCreateResult ResetTextureModel(
        AuditionProject project,
        ModRelativePath texturePath)
    {
        if (project.EditState.CurrentRevision == long.MaxValue)
        {
            return AuditionProjectCreateResult.Failure([
                new(AuditionProjectValidationFailureReason.InvalidEditState, "PROJECT_RESET_REVISION_OVERFLOW")]);
        }

        var edits = project.EditedTextures.Where(item => !SamePath(item.RelativePath.Value, texturePath.Value))
            .ToImmutableArray();
        var history = project.EditState.History
            .Where(item => !SamePath(item.TextureRelativePath.Value, texturePath.Value))
            .ToImmutableArray();
        var referenced = edits.Select(item => item.CurrentImageAssetId)
            .Concat(history.SelectMany(item => new[] { item.BeforeImageAssetId, item.AfterImageAssetId }))
            .ToHashSet();
        var images = project.ImageAssets.Where(item => referenced.Contains(item.Id)).ToImmutableArray();
        var ai = project.AiAssets.Where(item => referenced.Contains(item.Id)).ToImmutableArray();
        var revision = project.EditState.CurrentRevision + 1;
        return AuditionProject.Create(
            project.SchemaVersion, project.ProjectId, project.Name, project.GameId, project.ModId,
            project.TemplateIdentity, project.Workspace, edits, images, ai,
            new(
                revision,
                project.EditState.SavedRevision,
                project.EditState.ActiveTextureRelativePath is { } active
                && SamePath(active.Value, texturePath.Value) ? null : project.EditState.ActiveTextureRelativePath,
                history),
            new(ProjectBuildStatus.Dirty, null, null, null),
            project.CreatedAt,
            _timeProvider.GetUtcNow());
    }

    private static IEnumerable<ProjectTextureMetadataSnapshot> CreateMetadata(SmartModScanResult scan) =>
        scan.Groups.SelectMany(group => group.Textures).Select(texture => new ProjectTextureMetadataSnapshot(
            new(texture.Asset.RelativePath),
            new(texture.Asset.Sha256),
            texture.Metadata,
            texture.ManifestResolution.Slot?.Id));

    private async Task RemoveOrDisposeFreshAsync(IProjectArchiveWorkspace workspace)
    {
        if (workspaceService is IProjectArchiveWorkspaceRemovalService removal
            && await TryRemoveAsync(removal, workspace).ConfigureAwait(false))
        {
            return;
        }

        await workspace.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> TryStoreCacheAsync(Guid projectId, SmartModScanResult scan)
    {
        try
        {
            var result = await metadataCache.StoreAsync(
                projectId, CreateMetadata(scan), CancellationToken.None).ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> TryRemoveAsync(
        IProjectArchiveWorkspaceRemovalService removal,
        IProjectArchiveWorkspace workspace)
    {
        try
        {
            return await removal.RemoveAsync(workspace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return false;
        }
    }

    private static ProjectResetResult? Validate(
        AuditionProject? project,
        IProjectArchiveWorkspace? workspace)
    {
        if (project is null || workspace is null)
        {
            return Failure(ProjectResetFailureReason.InvalidRequest, "PROJECT_RESET_REQUEST_INVALID");
        }

        var descriptor = workspace.Descriptor;
        return descriptor.ProjectId == project.ProjectId
               && descriptor.ArchiveTemplate.Identity == project.TemplateIdentity
               && string.Equals(descriptor.WorkspaceId, project.Workspace.WorkspaceId, StringComparison.Ordinal)
               && string.Equals(descriptor.WorkingArchiveRelativePath.Replace('\\', '/'),
                   project.Workspace.WorkingArchiveRelativePath.Value, StringComparison.Ordinal)
               && string.Equals(descriptor.ExtractedDirectoryRelativePath.Replace('\\', '/'),
                   project.Workspace.ExtractedRootRelativePath.Value, StringComparison.Ordinal)
               && descriptor.State == ProjectArchiveWorkspaceState.Ready
            ? null
            : Failure(ProjectResetFailureReason.WorkspaceMismatch, "PROJECT_RESET_WORKSPACE_MISMATCH");
    }

    private async Task<bool> EnterGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _resetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static ProjectResetResult Failure(ProjectResetFailureReason reason, string code) =>
        ProjectResetResult.Failure(reason, code);

    private static void Report(IProgress<ProjectResetProgress>? progress, ProjectResetPhase phase, int completed) =>
        progress?.Report(new(phase, completed, TotalSteps));

    private sealed record PreparedWorkspace(
        bool Succeeded,
        IProjectArchiveWorkspace? Workspace,
        ProjectResetResult? Result)
    {
        public static PreparedWorkspace Success(IProjectArchiveWorkspace workspace) => new(true, workspace, null);
        public static PreparedWorkspace Failure(ProjectResetResult result) => new(false, null, result);
    }
}
