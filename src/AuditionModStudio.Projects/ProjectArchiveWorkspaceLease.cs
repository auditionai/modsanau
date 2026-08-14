using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Projects;

internal sealed class ProjectArchiveWorkspaceLease(
    ISecureWorkspace secureWorkspace,
    ProjectArchiveWorkspaceDescriptor descriptor,
    ArchiveWorkspace archiveWorkspace) : IProjectArchiveWorkspace
{
    private ISecureWorkspace? _secureWorkspace = secureWorkspace;

    public ProjectArchiveWorkspaceDescriptor Descriptor { get; } = descriptor;

    public ArchiveWorkspace ArchiveWorkspace { get; } = archiveWorkspace;

    public async ValueTask DisposeAsync()
    {
        var workspace = Interlocked.Exchange(ref _secureWorkspace, null);
        if (workspace is not null)
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
        }
    }
}
