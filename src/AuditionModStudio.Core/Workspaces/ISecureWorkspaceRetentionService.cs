namespace AuditionModStudio.Core.Workspaces;

public interface ISecureWorkspaceRetentionService
{
    void Retain(ISecureWorkspace workspace);
}
