using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public interface IProjectArchiveWorkspaceManifestStore
{
    Task WriteAsync(
        string workspaceRoot,
        ProjectArchiveWorkspaceDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task<ProjectArchiveWorkspaceDescriptor> ReadAsync(
        string workspaceRoot,
        string manifestRelativePath,
        CancellationToken cancellationToken = default);
}
