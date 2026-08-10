namespace AuditionModStudio.Core.Dds;

public enum DdsMetadataFailureReason
{
    None,
    FileMissing,
    InvalidMagic,
    TruncatedHeader,
    InvalidHeaderSize,
    InvalidPixelFormatHeader,
    InvalidDx10Header,
    InvalidDimensions,
    CorruptHeader,
    Cancelled,
    IoError
}
