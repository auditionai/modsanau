namespace AuditionModStudio.Core.Dds;

public sealed record DdsPreviewResult(
    bool Succeeded,
    bool Cancelled,
    DdsPreviewFailureReason FailureReason,
    string? DiagnosticCode,
    DdsMetadata? Metadata,
    DdsPreviewImage? Image)
{
    public static DdsPreviewResult Success(DdsMetadata metadata, DdsPreviewImage image) =>
        new(true, false, DdsPreviewFailureReason.None, null, metadata, image);

    public static DdsPreviewResult Failure(
        DdsPreviewFailureReason reason,
        string diagnosticCode,
        DdsMetadata? metadata = null) =>
        new(false, reason == DdsPreviewFailureReason.Cancelled, reason, diagnosticCode, metadata, null);
}
