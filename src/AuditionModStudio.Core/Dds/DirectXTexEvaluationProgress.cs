namespace AuditionModStudio.Core.Dds;

public sealed record DirectXTexEvaluationProgress(
    DirectXTexEvaluationOperation Operation,
    DirectXTexEvaluationState State);
