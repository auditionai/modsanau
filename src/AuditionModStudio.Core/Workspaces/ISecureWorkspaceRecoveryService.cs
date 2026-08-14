namespace AuditionModStudio.Core.Workspaces;

public interface ISecureWorkspaceRecoveryService : ISecureWorkspaceRetentionService
{
    ValueTask<ISecureWorkspace?> TryOpenExistingAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);
}
