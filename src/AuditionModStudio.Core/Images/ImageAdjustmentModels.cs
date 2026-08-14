namespace AuditionModStudio.Core.Images;

public sealed record ImageAdjustmentSettings(
    double Brightness = 0,
    double Contrast = 0,
    double Exposure = 0,
    double Saturation = 0,
    double Vibrance = 0,
    double Hue = 0,
    double Temperature = 0,
    double Tint = 0,
    double Highlights = 0,
    double Shadows = 0,
    double Gamma = 1,
    double Sharpen = 0,
    double Blur = 0,
    double Opacity = 1)
{
    public const double MinimumUnitAdjustment = -1;
    public const double MaximumUnitAdjustment = 1;
    public const double MinimumExposure = -5;
    public const double MaximumExposure = 5;
    public const double MinimumHue = -180;
    public const double MaximumHue = 180;
    public const double MinimumGamma = 0.1;
    public const double MaximumGamma = 10;
    public const double MinimumSharpen = 0;
    public const double MaximumSharpen = 1;
    public const double MinimumBlur = 0;
    public const double MaximumBlur = 20;
    public const double MinimumOpacity = 0;
    public const double MaximumOpacity = 1;

    public bool IsNeutral =>
        Brightness == 0
        && Contrast == 0
        && Exposure == 0
        && Saturation == 0
        && Vibrance == 0
        && Hue == 0
        && Temperature == 0
        && Tint == 0
        && Highlights == 0
        && Shadows == 0
        && Gamma == 1
        && Sharpen == 0
        && Blur == 0
        && Opacity == 1;
}

public sealed record ImageAdjustmentRequest(
    InternalImage Source,
    ImageAdjustmentSettings Settings);

public enum ImageAdjustmentFailureReason
{
    None,
    InvalidRequest,
    InvalidSettings,
    ResourceLimitExceeded,
    AdjustmentFailed,
    Cancelled
}

public sealed record ImageAdjustmentResult(
    bool Succeeded,
    bool Cancelled,
    ImageAdjustmentFailureReason FailureReason,
    string? DiagnosticCode,
    InternalImage? Image)
{
    public static ImageAdjustmentResult Success(InternalImage image) =>
        new(true, false, ImageAdjustmentFailureReason.None, null, image);

    public static ImageAdjustmentResult Failure(
        ImageAdjustmentFailureReason reason,
        string diagnosticCode) =>
        new(false, reason == ImageAdjustmentFailureReason.Cancelled, reason, diagnosticCode, null);
}
