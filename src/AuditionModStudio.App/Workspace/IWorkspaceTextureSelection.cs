using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.App.Workspace;

public interface IWorkspaceTextureSelection
{
    WorkspaceTextureItem? SelectedTexture { get; }

    Task<InternalImage?> LoadSelectedImageAsync(CancellationToken cancellationToken = default);

    bool CancelSelectedImageLoading();

    Task RefreshAfterApplyAsync(
        ModRelativePath textureRelativePath,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
