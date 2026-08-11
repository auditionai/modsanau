namespace AuditionModStudio.Core.Workspaces;

public interface ISecureWorkspaceRemovalService
{
    Task<bool> RemoveAsync(ISecureWorkspace workspace, CancellationToken cancellationToken = default);
}
