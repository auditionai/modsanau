using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Core.Dds;

public enum DdsMatchOriginalFailureReason
{
    None,
    TargetMissing,
    InvalidTargetDds,
    UnsupportedTargetFormat,
    UnsupportedHeaderType,
    UnsupportedResourceType,
    DimensionMismatch,
    AlphaIncompatible,
    InvalidMipProfile,
    ColorSpaceIncompatible,
    ResourceLimitExceeded,
    EncodeFailed,
    PostValidationFailed,
    Cancelled,
    TimedOut
}

public sealed record DdsMatchOriginalProfile(
    DdsTargetSettings TargetSettings,
    DdsColorSpace OriginalColorSpace,
    bool ColorSpaceIsMeaningful,
    uint OriginalDeclaredMipMapCount,
    DdsResourceDimension ResourceDimension,
    bool IsCubemap,
    uint ArraySize);

public sealed record DdsMatchOriginalProfileResult(
    bool Succeeded,
    DdsMatchOriginalFailureReason FailureReason,
    string? DiagnosticCode,
    DdsMatchOriginalProfile? Profile)
{
    public static DdsMatchOriginalProfileResult Success(DdsMatchOriginalProfile profile) =>
        new(true, DdsMatchOriginalFailureReason.None, null, profile);

    public static DdsMatchOriginalProfileResult Failure(
        DdsMatchOriginalFailureReason reason,
        string diagnosticCode) => new(false, reason, diagnosticCode, null);
}

public sealed record DdsMetadataMatchReport(
    bool FormatMatches,
    bool DimensionsMatch,
    bool MipCountMatches,
    bool HeaderMatches,
    bool ColorSpaceMatches,
    bool ResourceTypeMatches)
{
    public bool OverallMatch =>
        FormatMatches
        && DimensionsMatch
        && MipCountMatches
        && HeaderMatches
        && ColorSpaceMatches
        && ResourceTypeMatches;
}

public sealed record DdsMatchOriginalRequest(
    ISecureWorkspace Workspace,
    string TargetRelativePath,
    DdsRgbaImage ReplacementImage,
    string OutputRelativePath);

public sealed record DdsMatchOriginalResult(
    bool Succeeded,
    bool Cancelled,
    DdsMatchOriginalFailureReason FailureReason,
    string? DiagnosticCode,
    DdsMatchOriginalProfile? Profile,
    DdsMetadata? OutputMetadata,
    DdsMetadataMatchReport? MatchReport,
    string? OutputRelativePath)
{
    public static DdsMatchOriginalResult Success(
        DdsMatchOriginalProfile profile,
        DdsMetadata outputMetadata,
        DdsMetadataMatchReport report,
        string outputRelativePath) =>
        new(true, false, DdsMatchOriginalFailureReason.None, null, profile, outputMetadata, report, outputRelativePath);

    public static DdsMatchOriginalResult Failure(
        DdsMatchOriginalFailureReason reason,
        string diagnosticCode,
        DdsMatchOriginalProfile? profile = null,
        DdsMetadata? outputMetadata = null,
        DdsMetadataMatchReport? report = null) =>
        new(false, reason == DdsMatchOriginalFailureReason.Cancelled, reason, diagnosticCode, profile, outputMetadata, report, null);
}
