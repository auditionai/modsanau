using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Archives;

public interface IKeydatService
{
    KeydatDescriptor Describe(ISecureWorkspace workspace, string archiveRelativePath);

    Task<KeydatDescriptor> CopyToWorkspaceAsync(
        ISecureWorkspace workspace,
        string archiveRelativePath,
        string trustedSourceRoot,
        string sourceRelativePath,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default);
}
