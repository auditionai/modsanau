using AuditionModStudio.Core.AI;

namespace AuditionModStudio.AI;

public sealed class UnavailableAiStudioService : IAiStudioService
{
    public Task<AiStudioQuoteResult> GetQuoteAsync(
        AiStudioOperation operation,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStudioQuoteResult(false,
            cancellationToken.IsCancellationRequested ? "AI_STUDIO_CANCELLED" : "AI_STUDIO_OFFLINE", null));

    public Task<AiStudioHistoryResult> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        UnavailableAsync(cancellationToken);

    public Task<AiStudioHistoryResult> CancelJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);

    public Task<AiStudioExecutionResult> ExecuteAsync(AiStudioExecutionRequest request,
        IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStudioExecutionResult(false, cancellationToken.IsCancellationRequested,
            cancellationToken.IsCancellationRequested ? "AI_STUDIO_CANCELLED" : "AI_STUDIO_OFFLINE", null));

    private static Task<AiStudioHistoryResult> UnavailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AiStudioHistoryResult(false,
            cancellationToken.IsCancellationRequested ? "AI_STUDIO_CANCELLED" : "AI_STUDIO_OFFLINE", []));
}
