namespace AuditionModStudio.Core.Dds;

public sealed record DirectXTexEvaluationResult(
    bool Succeeded,
    DirectXTexEvaluationState FinalState,
    DirectXTexEvaluationFailureReason FailureReason,
    string? ErrorCode,
    int? ExitCode,
    string? OutputRelativePath,
    int? OutputWidth,
    int? OutputHeight,
    DdsMetadata? OutputDdsMetadata,
    string StandardOutput,
    string StandardError,
    bool DiagnosticsTruncated);
