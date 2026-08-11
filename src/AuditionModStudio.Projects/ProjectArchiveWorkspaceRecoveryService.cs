using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Projects;

public sealed class ProjectArchiveWorkspaceRecoveryService(
    ISecureWorkspaceRecoveryService secureWorkspaces,
    IProjectArchiveWorkspaceManifestStore manifestStore,
    IProjectArchiveWorkspaceService workspaceService) : IProjectArchiveWorkspaceRecoveryService
{
    public async Task<ProjectArchiveWorkspaceRecoveryResult> TryRecoverAsync(
        AuditionProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ISecureWorkspace? secure = null;
        try
        {
            secure = await secureWorkspaces.TryOpenExistingAsync(project.Workspace.WorkspaceId, cancellationToken)
                .ConfigureAwait(false);
            if (secure is null)
            {
                return ProjectArchiveWorkspaceRecoveryResult.Unavailable("PROJECT_WORKSPACE_MISSING");
            }

            var descriptor = await manifestStore.ReadAsync(
                secure.Paths.RootDirectory,
                ProjectArchiveWorkspaceService.ManifestFileName,
                cancellationToken).ConfigureAwait(false);
            if (!Matches(project, descriptor, secure.Id))
            {
                await secure.DisposeAsync().ConfigureAwait(false);
                return ProjectArchiveWorkspaceRecoveryResult.Unavailable("PROJECT_WORKSPACE_MANIFEST_MISMATCH");
            }

            var workingRelative = Path.GetRelativePath("Working", descriptor.WorkingArchiveRelativePath);
            var extractedRelative = Path.GetRelativePath("Extracted", descriptor.ExtractedDirectoryRelativePath);
            var workspace = new ProjectArchiveWorkspaceLease(
                secure,
                descriptor,
                new ArchiveWorkspace(secure, workingRelative, extractedRelative));
            secure = null;
            var validation = await workspaceService.ValidateAsync(workspace, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
                return ProjectArchiveWorkspaceRecoveryResult.Unavailable(
                    validation.DiagnosticCode ?? "PROJECT_WORKSPACE_INVALID");
            }

            secureWorkspaces.Retain(workspace.ArchiveWorkspace.SecureWorkspace);

            return ProjectArchiveWorkspaceRecoveryResult.Success(workspace);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (secure is not null)
            {
                await secure.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException
                                          or System.Text.Json.JsonException)
        {
            if (secure is not null)
            {
                await secure.DisposeAsync().ConfigureAwait(false);
            }

            return ProjectArchiveWorkspaceRecoveryResult.Unavailable("PROJECT_WORKSPACE_RECOVERY_FAILED");
        }
    }

    private static bool Matches(
        AuditionProject project,
        ProjectArchiveWorkspaceDescriptor descriptor,
        string workspaceId) =>
        descriptor.SchemaVersion == ProjectArchiveWorkspaceService.CurrentSchemaVersion
        && descriptor.ProjectId == project.ProjectId
        && string.Equals(descriptor.WorkspaceId, workspaceId, StringComparison.Ordinal)
        && descriptor.ArchiveTemplate.Identity == project.TemplateIdentity
        && string.Equals(
            descriptor.WorkingArchiveRelativePath.Replace('\\', '/'),
            project.Workspace.WorkingArchiveRelativePath.Value,
            StringComparison.Ordinal)
        && string.Equals(
            descriptor.ExtractedDirectoryRelativePath.Replace('\\', '/'),
            project.Workspace.ExtractedRootRelativePath.Value,
            StringComparison.Ordinal)
        && descriptor.State == ProjectArchiveWorkspaceState.Ready;
}
