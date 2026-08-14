using System.Collections.Immutable;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Core.Dds;

public enum DdsImagePixelFormat
{
    Rgba8
}

public enum DdsTargetAlphaSemantics
{
    Opaque,
    Binary,
    Full
}

public enum DdsEncodeFailureReason
{
    None,
    InvalidInputImage,
    DimensionMismatch,
    UnsupportedTargetFormat,
    InvalidMipCount,
    ResourceLimitExceeded,
    InvalidOutputPath,
    OutputExists,
    EncoderUnavailable,
    EncoderIntegrityFailed,
    EncodeFailed,
    OutputMissing,
    OutputValidationFailed,
    Cancelled,
    TimedOut
}

public sealed record DdsRgbaImage(
    int Width,
    int Height,
    int Stride,
    DdsImagePixelFormat PixelFormat,
    ImmutableArray<byte> Pixels)
{
    public static DdsRgbaImage Create(int width, int height, int stride, ReadOnlySpan<byte> pixels) =>
        new(width, height, stride, DdsImagePixelFormat.Rgba8, ImmutableArray.Create(pixels.ToArray()));

    public static DdsRgbaImage Create(InternalImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new(
            image.Width,
            image.Height,
            image.Stride,
            DdsImagePixelFormat.Rgba8,
            image.Pixels);
    }
}

public sealed record DdsTargetSettings(
    int Width,
    int Height,
    DdsFormat Format,
    int MipLevelCount,
    DdsHeaderType HeaderType,
    DdsColorSpace ColorSpace,
    DdsTargetAlphaSemantics AlphaSemantics);

public sealed record DdsEncodeRequest(
    ISecureWorkspace Workspace,
    DdsRgbaImage Image,
    DdsTargetSettings Settings,
    string OutputRelativePath);

public sealed record DdsEncodeResult(
    bool Succeeded,
    bool Cancelled,
    DdsEncodeFailureReason FailureReason,
    string? DiagnosticCode,
    string? OutputRelativePath,
    DdsMetadata? Metadata)
{
    public static DdsEncodeResult Success(string outputRelativePath, DdsMetadata metadata) =>
        new(true, false, DdsEncodeFailureReason.None, null, outputRelativePath, metadata);

    public static DdsEncodeResult Failure(
        DdsEncodeFailureReason reason,
        string diagnosticCode,
        DdsMetadata? metadata = null) =>
        new(false, reason == DdsEncodeFailureReason.Cancelled, reason, diagnosticCode, null, metadata);
}
