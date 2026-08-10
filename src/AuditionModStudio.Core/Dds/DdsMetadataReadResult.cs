namespace AuditionModStudio.Core.Dds;

public sealed record DdsMetadataReadResult(
    bool IsSuccess,
    DdsMetadata? Metadata,
    DdsMetadataFailureReason FailureReason,
    string? ErrorCode)
{
    public static DdsMetadataReadResult Success(DdsMetadata metadata) =>
        new(true, metadata, DdsMetadataFailureReason.None, null);

    public static DdsMetadataReadResult Failure(DdsMetadataFailureReason reason, string errorCode) =>
        new(false, null, reason, errorCode);
}
