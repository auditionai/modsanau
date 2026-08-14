using AuditionModStudio.Core.AI;

namespace AuditionModStudio.Cloud;

public sealed class UnavailableCloudPromptPresetService : ICloudPromptPresetService
{
    public Task<PromptPresetCollectionResult> ListOwnedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PromptPresetCollectionResult(false,
            cancellationToken.IsCancellationRequested ? "PROMPT_PRESET_CLOUD_CANCELLED" : "PROMPT_PRESET_CLOUD_UNAVAILABLE",
            [], []));
}
