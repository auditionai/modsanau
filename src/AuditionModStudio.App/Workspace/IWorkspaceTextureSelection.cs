using AuditionModStudio.Core.Images;

namespace AuditionModStudio.App.Workspace;

public interface IWorkspaceTextureSelection
{
    WorkspaceTextureItem? SelectedTexture { get; }

    Task<InternalImage?> LoadSelectedImageAsync(CancellationToken cancellationToken = default);

    bool CancelSelectedImageLoading();
}
