using AuditionModStudio.Core.Images;

namespace AuditionModStudio.Imaging;

public sealed class ImageAdjustmentService : IImageAdjustmentService
{
    private const double RedLuminance = 0.2126;
    private const double GreenLuminance = 0.7152;
    private const double BlueLuminance = 0.0722;
    private readonly ImageImportResourcePolicy _resourcePolicy;

    public ImageAdjustmentService(ImageImportResourcePolicy resourcePolicy)
    {
        _resourcePolicy = resourcePolicy ?? throw new ArgumentNullException(nameof(resourcePolicy));
    }

    public Task<ImageAdjustmentResult> AdjustAsync(
        ImageAdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.Source is null || request.Settings is null)
        {
            return Task.FromResult(ImageAdjustmentResult.Failure(
                ImageAdjustmentFailureReason.InvalidRequest,
                "IMAGE_ADJUSTMENT_INVALID_REQUEST"));
        }

        var validation = Validate(request);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        if (request.Settings.IsNeutral)
        {
            return Task.FromResult(ImageAdjustmentResult.Success(request.Source));
        }

        return Task.Run(() => Adjust(request, cancellationToken), CancellationToken.None);
    }

    private static ImageAdjustmentResult Adjust(
        ImageAdjustmentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = request.Source;
            var settings = request.Settings;
            var pixels = source.Pixels.ToArray();
            ApplyColorAdjustments(pixels, source.Width, source.Height, settings, cancellationToken);

            if (settings.Sharpen > 0)
            {
                ApplySharpen(pixels, source.Width, source.Height, settings.Sharpen, cancellationToken);
            }

            if (settings.Blur > 0)
            {
                ApplyBlur(pixels, source.Width, source.Height, settings.Blur, cancellationToken);
            }

            if (settings.Opacity < 1)
            {
                ApplyOpacity(pixels, source.Width, source.Height, settings.Opacity, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ImageAdjustmentResult.Success(new InternalImage(
                source.Width,
                source.Height,
                source.Stride,
                pixels,
                source.SourceMetadata));
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or OutOfMemoryException)
        {
            return ImageAdjustmentResult.Failure(
                ImageAdjustmentFailureReason.AdjustmentFailed,
                "IMAGE_ADJUSTMENT_FAILED");
        }
    }

    private ImageAdjustmentResult? Validate(ImageAdjustmentRequest request)
    {
        var source = request.Source;
        try
        {
            var pixelCount = checked((long)source.Width * source.Height);
            var byteCount = checked(pixelCount * 4);
            if (source.Width > _resourcePolicy.MaximumDimension
                || source.Height > _resourcePolicy.MaximumDimension
                || pixelCount > _resourcePolicy.MaximumPixelCount
                || byteCount > _resourcePolicy.MaximumDecodedBytes)
            {
                return ImageAdjustmentResult.Failure(
                    ImageAdjustmentFailureReason.ResourceLimitExceeded,
                    "IMAGE_ADJUSTMENT_RESOURCE_LIMIT_EXCEEDED");
            }
        }
        catch (OverflowException)
        {
            return ImageAdjustmentResult.Failure(
                ImageAdjustmentFailureReason.ResourceLimitExceeded,
                "IMAGE_ADJUSTMENT_RESOURCE_OVERFLOW");
        }

        var settings = request.Settings;
        if (!InUnitRange(settings.Brightness)
            || !InUnitRange(settings.Contrast)
            || !InRange(settings.Exposure, ImageAdjustmentSettings.MinimumExposure, ImageAdjustmentSettings.MaximumExposure)
            || !InUnitRange(settings.Saturation)
            || !InUnitRange(settings.Vibrance)
            || !InRange(settings.Hue, ImageAdjustmentSettings.MinimumHue, ImageAdjustmentSettings.MaximumHue)
            || !InUnitRange(settings.Temperature)
            || !InUnitRange(settings.Tint)
            || !InUnitRange(settings.Highlights)
            || !InUnitRange(settings.Shadows)
            || !InRange(settings.Gamma, ImageAdjustmentSettings.MinimumGamma, ImageAdjustmentSettings.MaximumGamma)
            || !InRange(settings.Sharpen, ImageAdjustmentSettings.MinimumSharpen, ImageAdjustmentSettings.MaximumSharpen)
            || !InRange(settings.Blur, ImageAdjustmentSettings.MinimumBlur, ImageAdjustmentSettings.MaximumBlur)
            || !InRange(settings.Opacity, ImageAdjustmentSettings.MinimumOpacity, ImageAdjustmentSettings.MaximumOpacity))
        {
            return ImageAdjustmentResult.Failure(
                ImageAdjustmentFailureReason.InvalidSettings,
                "IMAGE_ADJUSTMENT_INVALID_SETTINGS");
        }

        return null;
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static bool InUnitRange(double value) =>
        InRange(
            value,
            ImageAdjustmentSettings.MinimumUnitAdjustment,
            ImageAdjustmentSettings.MaximumUnitAdjustment);

    private static void ApplyColorAdjustments(
        byte[] pixels,
        int width,
        int height,
        ImageAdjustmentSettings settings,
        CancellationToken cancellationToken)
    {
        var exposureFactor = Math.Pow(2, settings.Exposure);
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = checked(y * width * 4);
            for (var x = 0; x < width; x++)
            {
                var index = row + (x * 4);
                var red = (double)pixels[index];
                var green = (double)pixels[index + 1];
                var blue = (double)pixels[index + 2];

                // The order below is the public deterministic PLAN 23 pipeline.
                red += settings.Brightness * 255;
                green += settings.Brightness * 255;
                blue += settings.Brightness * 255;

                var contrastFactor = 1 + settings.Contrast;
                red = ((red - 127.5) * contrastFactor) + 127.5;
                green = ((green - 127.5) * contrastFactor) + 127.5;
                blue = ((blue - 127.5) * contrastFactor) + 127.5;

                red *= exposureFactor;
                green *= exposureFactor;
                blue *= exposureFactor;

                ApplySaturation(ref red, ref green, ref blue, 1 + settings.Saturation);
                var chroma = (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue))) / 255;
                ApplySaturation(ref red, ref green, ref blue, 1 + (settings.Vibrance * (1 - Math.Clamp(chroma, 0, 1))));
                ApplyHue(ref red, ref green, ref blue, settings.Hue);

                red += settings.Temperature * 32;
                blue -= settings.Temperature * 32;
                green += settings.Tint * 32;
                red -= settings.Tint * 16;
                blue -= settings.Tint * 16;

                var luminance = Luminance(red, green, blue) / 255;
                var highlightOffset = settings.Highlights * 255 * Math.Pow(Math.Clamp(luminance, 0, 1), 2);
                var shadowOffset = settings.Shadows * 255 * Math.Pow(1 - Math.Clamp(luminance, 0, 1), 2);
                red += highlightOffset + shadowOffset;
                green += highlightOffset + shadowOffset;
                blue += highlightOffset + shadowOffset;

                red = ApplyGamma(red, settings.Gamma);
                green = ApplyGamma(green, settings.Gamma);
                blue = ApplyGamma(blue, settings.Gamma);

                pixels[index] = Quantize(red);
                pixels[index + 1] = Quantize(green);
                pixels[index + 2] = Quantize(blue);
            }
        }
    }

    private static void ApplySaturation(ref double red, ref double green, ref double blue, double factor)
    {
        var luminance = Luminance(red, green, blue);
        red = luminance + ((red - luminance) * factor);
        green = luminance + ((green - luminance) * factor);
        blue = luminance + ((blue - luminance) * factor);
    }

    private static void ApplyHue(ref double red, ref double green, ref double blue, double degrees)
    {
        if (degrees == 0)
        {
            return;
        }

        var radians = degrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var nextRed = ((0.213 + (cosine * 0.787) - (sine * 0.213)) * red)
            + ((0.715 - (cosine * 0.715) - (sine * 0.715)) * green)
            + ((0.072 - (cosine * 0.072) + (sine * 0.928)) * blue);
        var nextGreen = ((0.213 - (cosine * 0.213) + (sine * 0.143)) * red)
            + ((0.715 + (cosine * 0.285) + (sine * 0.140)) * green)
            + ((0.072 - (cosine * 0.072) - (sine * 0.283)) * blue);
        var nextBlue = ((0.213 - (cosine * 0.213) - (sine * 0.787)) * red)
            + ((0.715 - (cosine * 0.715) + (sine * 0.715)) * green)
            + ((0.072 + (cosine * 0.928) + (sine * 0.072)) * blue);
        red = nextRed;
        green = nextGreen;
        blue = nextBlue;
    }

    private static double ApplyGamma(double channel, double gamma) =>
        255 * Math.Pow(Math.Clamp(channel, 0, 255) / 255, 1 / gamma);

    private static double Luminance(double red, double green, double blue) =>
        (red * RedLuminance) + (green * GreenLuminance) + (blue * BlueLuminance);

    private static void ApplySharpen(
        byte[] pixels,
        int width,
        int height,
        double amount,
        CancellationToken cancellationToken)
    {
        var source = (byte[])pixels.Clone();
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var index = ((y * width) + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var average = (
                        ReadChannel(source, width, height, x - 1, y, channel)
                        + ReadChannel(source, width, height, x + 1, y, channel)
                        + ReadChannel(source, width, height, x, y - 1, channel)
                        + ReadChannel(source, width, height, x, y + 1, channel)) / 4.0;
                    pixels[index + channel] = Quantize(source[index + channel] + (amount * (source[index + channel] - average)));
                }
            }
        }
    }

    private static byte ReadChannel(byte[] pixels, int width, int height, int x, int y, int channel)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        return pixels[((y * width) + x) * 4 + channel];
    }

    private static void ApplyBlur(
        byte[] pixels,
        int width,
        int height,
        double amount,
        CancellationToken cancellationToken)
    {
        var radius = (int)Math.Ceiling(amount);
        var blend = amount / radius;
        var original = (byte[])pixels.Clone();
        var horizontal = new byte[pixels.Length];
        BoxBlurHorizontal(original, horizontal, width, height, radius, cancellationToken);
        BoxBlurVertical(horizontal, pixels, original, width, height, radius, blend, cancellationToken);
    }

    private static void BoxBlurHorizontal(
        byte[] source,
        byte[] destination,
        int width,
        int height,
        int radius,
        CancellationToken cancellationToken)
    {
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var channel = 0; channel < 3; channel++)
            {
                var left = 0;
                var right = Math.Min(width - 1, radius);
                var sum = 0;
                for (var sample = left; sample <= right; sample++)
                {
                    sum += source[((y * width) + sample) * 4 + channel];
                }

                for (var x = 0; x < width; x++)
                {
                    destination[((y * width) + x) * 4 + channel] = Quantize((double)sum / (right - left + 1));
                    var nextLeft = Math.Max(0, (x + 1) - radius);
                    var nextRight = Math.Min(width - 1, (x + 1) + radius);
                    if (nextLeft > left)
                    {
                        sum -= source[((y * width) + left) * 4 + channel];
                    }

                    if (nextRight > right)
                    {
                        sum += source[((y * width) + nextRight) * 4 + channel];
                    }

                    left = nextLeft;
                    right = nextRight;
                }
            }

            for (var x = 0; x < width; x++)
            {
                var alphaIndex = ((y * width) + x) * 4 + 3;
                destination[alphaIndex] = source[alphaIndex];
            }
        }
    }

    private static void BoxBlurVertical(
        byte[] source,
        byte[] destination,
        byte[] original,
        int width,
        int height,
        int radius,
        double blend,
        CancellationToken cancellationToken)
    {
        for (var x = 0; x < width; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var channel = 0; channel < 3; channel++)
            {
                var top = 0;
                var bottom = Math.Min(height - 1, radius);
                var sum = 0;
                for (var sample = top; sample <= bottom; sample++)
                {
                    sum += source[((sample * width) + x) * 4 + channel];
                }

                for (var y = 0; y < height; y++)
                {
                    var destinationIndex = ((y * width) + x) * 4;
                    var blurred = (double)sum / (bottom - top + 1);
                    destination[destinationIndex + channel] = Quantize(
                        original[destinationIndex + channel]
                        + ((blurred - original[destinationIndex + channel]) * blend));
                    var nextTop = Math.Max(0, (y + 1) - radius);
                    var nextBottom = Math.Min(height - 1, (y + 1) + radius);
                    if (nextTop > top)
                    {
                        sum -= source[((top * width) + x) * 4 + channel];
                    }

                    if (nextBottom > bottom)
                    {
                        sum += source[((nextBottom * width) + x) * 4 + channel];
                    }

                    top = nextTop;
                    bottom = nextBottom;
                }
            }

            for (var y = 0; y < height; y++)
            {
                var destinationIndex = ((y * width) + x) * 4;
                destination[destinationIndex + 3] = original[destinationIndex + 3];
            }
        }
    }

    private static void ApplyOpacity(
        byte[] pixels,
        int width,
        int height,
        double opacity,
        CancellationToken cancellationToken)
    {
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = checked(y * width * 4);
            for (var x = 0; x < width; x++)
            {
                var alphaIndex = row + (x * 4) + 3;
                pixels[alphaIndex] = Quantize(pixels[alphaIndex] * opacity);
            }
        }
    }

    private static byte Quantize(double value) =>
        (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), byte.MinValue, byte.MaxValue);

    private static ImageAdjustmentResult Cancelled() =>
        ImageAdjustmentResult.Failure(
            ImageAdjustmentFailureReason.Cancelled,
            "IMAGE_ADJUSTMENT_CANCELLED");
}
