using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Core.Assets;

public interface IArchiveAssetScanner
{
    Task<ArchiveAssetScanResult> ScanAsync(
        IProjectArchiveWorkspace workspace,
        IProgress<ArchiveAssetScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
