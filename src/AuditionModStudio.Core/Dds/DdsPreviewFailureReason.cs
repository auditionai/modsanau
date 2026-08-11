namespace AuditionModStudio.Core.Dds;

public enum DdsPreviewFailureReason
{
    None,
    SourceMissing,
    InvalidDds,
    UnsupportedFormat,
    ResourceLimitExceeded,
    DecoderUnavailable,
    DecoderIntegrityFailed,
    DecodeFailed,
    InvalidOutput,
    Cancelled,
    TimedOut
}
