namespace AuditionModStudio.Core.Workspaces;

public interface ISecureWorkspaceService
{
    ValueTask<ISecureWorkspace> CreateAsync(CancellationToken cancellationToken = default);

    Task<int> CleanupAbandonedAsync(CancellationToken cancellationToken = default);
}
