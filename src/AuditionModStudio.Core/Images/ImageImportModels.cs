namespace AuditionModStudio.Core.Images;

public enum ImageSourceFormat
{
    Png,
    Jpeg,
    WebP,
    Bmp
}

public enum ImageSourceOrientation
{
    Unspecified,
    Normal,
    FlipHorizontal,
    Rotate180,
    FlipVertical,
    Transpose,
    Rotate90,
    Transverse,
    Rotate270
}

public sealed record ImageSourceMetadata(
    ImageSourceFormat Format,
    int OriginalWidth,
    int OriginalHeight,
    ImageSourceOrientation OriginalOrientation,
    bool OrientationNormalized,
    bool HasIccProfile);

public enum ImageImportFailureReason
{
    None,
    SourceMissing,
    AccessDenied,
    InvalidPath,
    UnsupportedFormat,
    InvalidImage,
    ResourceLimitExceeded,
    DecodeFailed,
    Cancelled
}

public sealed record ImageImportRequest(string SourcePath);

public sealed record ImageImportResult(
    bool Succeeded,
    bool Cancelled,
    ImageImportFailureReason FailureReason,
    string? DiagnosticCode,
    InternalImage? Image)
{
    public static ImageImportResult Success(InternalImage image) =>
        new(true, false, ImageImportFailureReason.None, null, image);

    public static ImageImportResult Failure(ImageImportFailureReason reason, string diagnosticCode) =>
        new(false, reason == ImageImportFailureReason.Cancelled, reason, diagnosticCode, null);
}

public sealed record ImageImportResourcePolicy(
    long MaximumSourceBytes,
    int MaximumDimension,
    long MaximumPixelCount,
    long MaximumDecodedBytes)
{
    public static ImageImportResourcePolicy Default { get; } = new(
        MaximumSourceBytes: 512L * 1024 * 1024,
        MaximumDimension: 16_384,
        MaximumPixelCount: 100_000_000,
        MaximumDecodedBytes: 512L * 1024 * 1024);
}
