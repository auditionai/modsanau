namespace AuditionModStudio.Core.Images;

public readonly record struct NormalizedImagePoint(double X, double Y);

public readonly record struct NormalizedImageRectangle(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

public readonly record struct ImagePixelPoint(double X, double Y);

public readonly record struct ViewportPoint(double X, double Y);

public readonly record struct ViewportVector(double X, double Y);

public readonly record struct ImagePixelVector(double X, double Y);

public readonly record struct ViewportSize(double Width, double Height);

public readonly record struct ImageScale(double X, double Y)
{
    public static ImageScale Identity => new(1, 1);
}

public readonly record struct CropAspectRatio(double Width, double Height)
{
    public double Value => Width / Height;
}

public enum CropAnchor
{
    Center,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum ImageQuarterTurn
{
    None,
    Clockwise90,
    Clockwise180,
    Clockwise270
}

public sealed record ImageTransformConstraints(
    double MinimumZoom,
    double MaximumZoom,
    double MinimumCropWidthPixels,
    double MinimumCropHeightPixels)
{
    public static ImageTransformConstraints Default { get; } = new(
        MinimumZoom: 0.1,
        MaximumZoom: 32,
        MinimumCropWidthPixels: 1,
        MinimumCropHeightPixels: 1);
}

public sealed record InteractiveImageTransformState(
    int ImageWidth,
    int ImageHeight,
    NormalizedImageRectangle Crop,
    double Zoom,
    ViewportVector Pan,
    ImageScale Scale,
    ImageQuarterTurn Rotation,
    bool FlipHorizontal,
    bool FlipVertical,
    ImagePixelVector Translation,
    ImageTransformConstraints Constraints);

public enum ImageTransformFailureReason
{
    None,
    InvalidImage,
    InvalidCrop,
    InvalidAspectRatio,
    InvalidZoom,
    InvalidTransform,
    InvalidViewport,
    InvalidCoordinate,
    ImageMismatch,
    ConversionOverflow
}

public sealed record ImageTransformResult<T>(
    bool Succeeded,
    ImageTransformFailureReason FailureReason,
    string? DiagnosticCode,
    T? Value)
{
    public static ImageTransformResult<T> Success(T value) =>
        new(true, ImageTransformFailureReason.None, null, value);

    public static ImageTransformResult<T> Failure(
        ImageTransformFailureReason reason,
        string diagnosticCode) => new(false, reason, diagnosticCode, default);
}
