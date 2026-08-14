using AuditionModStudio.Core.AI;

namespace AuditionModStudio.AI;

public sealed class UnavailableAiService : IAiService
{
    public Task<AiImageResult> GenerateAsync(
        AiGenerateRequest request,
        IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);

    public Task<AiImageResult> EditAsync(
        AiEditRequest request,
        IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);

    public Task<AiImageResult> InpaintAsync(
        AiInpaintRequest request,
        IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);

    public Task<AiImageResult> OutpaintAsync(
        AiOutpaintRequest request,
        IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);

    public Task<AiImageResult> RemoveObjectAsync(
        AiRemoveObjectRequest request,
        IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);

    public Task<AiImageResult> ReplaceObjectAsync(
        AiReplaceObjectRequest request,
        IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);

    public Task<AiImageResult> UpscaleAsync(
        AiUpscaleRequest request,
        IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);

    private static Task<AiImageResult> UnavailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(cancellationToken.IsCancellationRequested
            ? AiImageResult.CancelledResult()
            : AiImageResult.Failure(AiServiceFailureReason.Unavailable, "AI_TRUSTED_BACKEND_UNAVAILABLE"));
}
