using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Archives;

public interface IArchiveToolProvisioningService
{
    Task<ArchiveToolProvisioningResult> ProvisionAsync(
        string toolId,
        string trustedSourceRoot,
        string sourceRelativePath,
        ISecureWorkspace workspace,
        CancellationToken cancellationToken = default);
}
