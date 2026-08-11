namespace AuditionModStudio.Core.Dds;

public interface IDirectXTexEvaluationHarness
{
    Task<DirectXTexEvaluationResult> RunAsync(
        DirectXTexEvaluationRequest request,
        IProgress<DirectXTexEvaluationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
