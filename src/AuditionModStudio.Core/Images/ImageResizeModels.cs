namespace AuditionModStudio.Core.Images;

public enum ImageResizeMode
{
    Stretch,
    Fit,
    Fill,
    KeepAspect,
    FreeAspect,
    ManualCrop,
    CanvasResize,
    TransparentPadding
}

public enum ImageResizeAlignment
{
    Center,
    Top,
    Bottom,
    Left,
    Right
}

public enum ImageInterpolationMode
{
    NearestNeighbor,
    Linear
}

public sealed record ImageCropRectangle(int X, int Y, int Width, int Height);

public sealed record ImageResizeOptions(
    ImageResizeMode Mode,
    ImageInterpolationMode Interpolation = ImageInterpolationMode.Linear,
    ImageResizeAlignment Alignment = ImageResizeAlignment.Center,
    ImageCropRectangle? CropRectangle = null);

public sealed record ImageResizeRequest(
    InternalImage Source,
    int TargetWidth,
    int TargetHeight,
    ImageResizeOptions Options);

public enum ImageResizeFailureReason
{
    None,
    InvalidSource,
    InvalidTargetDimensions,
    ResourceLimitExceeded,
    InvalidOptions,
    UnsupportedMode,
    ResizeFailed,
    Cancelled
}

public sealed record ImageResizeResult(
    bool Succeeded,
    bool Cancelled,
    ImageResizeFailureReason FailureReason,
    string? DiagnosticCode,
    InternalImage? Image)
{
    public static ImageResizeResult Success(InternalImage image) =>
        new(true, false, ImageResizeFailureReason.None, null, image);

    public static ImageResizeResult Failure(ImageResizeFailureReason reason, string diagnosticCode) =>
        new(false, reason == ImageResizeFailureReason.Cancelled, reason, diagnosticCode, null);
}
