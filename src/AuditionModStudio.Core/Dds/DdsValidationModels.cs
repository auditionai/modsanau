using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Core.Dds;

public enum DdsValidationFailureReason
{
    None,
    TargetMissing,
    CandidateMissing,
    InvalidTargetDds,
    InvalidCandidateDds,
    UnsupportedTargetProfile,
    MetadataMismatch,
    Cancelled
}

public sealed record DdsValidationRequest(
    ISecureWorkspace Workspace,
    string TargetRelativePath,
    string CandidateRelativePath);

public sealed record DdsValidationResult(
    bool Succeeded,
    bool Cancelled,
    DdsValidationFailureReason FailureReason,
    string? DiagnosticCode,
    DdsMetadata? TargetMetadata,
    DdsMetadata? CandidateMetadata,
    DdsMetadataMatchReport? MatchReport)
{
    public static DdsValidationResult Success(
        DdsMetadata targetMetadata,
        DdsMetadata candidateMetadata,
        DdsMetadataMatchReport report) =>
        new(true, false, DdsValidationFailureReason.None, null, targetMetadata, candidateMetadata, report);

    public static DdsValidationResult Failure(
        DdsValidationFailureReason reason,
        string diagnosticCode,
        DdsMetadata? targetMetadata = null,
        DdsMetadata? candidateMetadata = null,
        DdsMetadataMatchReport? report = null) =>
        new(false, reason == DdsValidationFailureReason.Cancelled, reason, diagnosticCode, targetMetadata, candidateMetadata, report);
}
