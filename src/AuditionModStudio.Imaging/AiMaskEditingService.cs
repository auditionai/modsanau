using AuditionModStudio.Core.Images;

namespace AuditionModStudio.Imaging;

public sealed class AiMaskEditingService : IAiMaskEditingService
{
    private const int MaximumStrokePoints = 4096;
    private const int MaximumStrokeStamps = 1_000_000;

    public AiMaskEditResult Create(InternalImage source)
    {
        if (source is null || (long)source.Width * source.Height > AiMask.MaximumPixels)
            return Failure("AI_MASK_SOURCE_INVALID");
        return Success(new(source.Width, source.Height, new byte[checked(source.Width * source.Height)]));
    }

    public AiMaskEditResult ApplyStroke(AiMask mask, AiMaskStroke stroke)
    {
        if (mask is null || stroke?.Points is null || !stroke.Brush.IsValid
            || stroke.Points.Count is <= 0 or > MaximumStrokePoints
            || !HasBoundedWork(mask, stroke)) return Failure("AI_MASK_STROKE_INVALID");
        var pixels = mask.Opacity.ToArray();
        var radius = stroke.Brush.Size / 2;
        for (var index = 0; index < stroke.Points.Count; index++)
        {
            var current = stroke.Points[index];
            if (index == 0)
            {
                Stamp(pixels, mask.Width, mask.Height, current.X, current.Y, radius, stroke.Brush);
                continue;
            }
            var previous = stroke.Points[index - 1];
            var dx = current.X - previous.X;
            var dy = current.Y - previous.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(0.5, radius / 4)));
            for (var step = 1; step <= steps; step++)
            {
                var t = (double)step / steps;
                Stamp(pixels, mask.Width, mask.Height,
                    previous.X + dx * t, previous.Y + dy * t, radius, stroke.Brush);
            }
        }
        return Success(new(mask.Width, mask.Height, pixels));
    }

    private static bool HasBoundedWork(AiMask mask, AiMaskStroke stroke)
    {
        var maximumMargin = stroke.Brush.Size;
        long stamps = 1;
        for (var index = 0; index < stroke.Points.Count; index++)
        {
            var point = stroke.Points[index];
            if (!point.IsValid || point.X < -maximumMargin || point.Y < -maximumMargin
                || point.X > mask.Width + maximumMargin || point.Y > mask.Height + maximumMargin)
                return false;
            if (index == 0) continue;
            var previous = stroke.Points[index - 1];
            var dx = point.X - previous.X;
            var dy = point.Y - previous.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(distance)) return false;
            stamps += Math.Max(1, (long)Math.Ceiling(distance / Math.Max(0.5, stroke.Brush.Size / 8)));
            if (stamps > MaximumStrokeStamps) return false;
        }
        return true;
    }

    public AiMaskEditResult Clear(AiMask mask) => mask is null
        ? Failure("AI_MASK_INVALID")
        : Success(new(mask.Width, mask.Height, new byte[mask.Opacity.Length]));

    public AiMaskEditResult Invert(AiMask mask)
    {
        if (mask is null) return Failure("AI_MASK_INVALID");
        var pixels = mask.Opacity.ToArray();
        for (var index = 0; index < pixels.Length; index++) pixels[index] = (byte)(255 - pixels[index]);
        return Success(new(mask.Width, mask.Height, pixels));
    }

    public async Task<InternalImage?> ComposeOverlayAsync(
        AiMask mask, CancellationToken cancellationToken = default)
    {
        if (mask is null) return null;
        var pixels = new byte[checked(mask.Width * mask.Height * 4)];
        for (var y = 0; y < mask.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < mask.Width; x++)
            {
                var maskIndex = y * mask.Width + x;
                var pixelIndex = maskIndex * 4;
                pixels[pixelIndex] = 255;
                pixels[pixelIndex + 1] = 0;
                pixels[pixelIndex + 2] = 255;
                pixels[pixelIndex + 3] = mask.Opacity[maskIndex];
            }
            if ((y & 63) == 63) await Task.Yield();
        }
        return new(mask.Width, mask.Height, mask.Width * 4, pixels,
            new(ImageSourceFormat.Png, mask.Width, mask.Height,
                ImageSourceOrientation.Normal, true, false));
    }

    private static void Stamp(
        byte[] pixels, int width, int height, double centerX, double centerY, double radius,
        AiMaskBrushSettings brush)
    {
        var minimumX = Math.Max(0, (int)Math.Floor(centerX - radius));
        var maximumX = Math.Min(width - 1, (int)Math.Ceiling(centerX + radius));
        var minimumY = Math.Max(0, (int)Math.Floor(centerY - radius));
        var maximumY = Math.Min(height - 1, (int)Math.Ceiling(centerY + radius));
        var hardRadius = radius * brush.Hardness;
        for (var y = minimumY; y <= maximumY; y++)
            for (var x = minimumX; x <= maximumX; x++)
            {
                var dx = x + 0.5 - centerX;
                var dy = y + 0.5 - centerY;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > radius) continue;
                var falloff = distance <= hardRadius || radius == hardRadius
                    ? 1
                    : 1 - (distance - hardRadius) / (radius - hardRadius);
                var coverage = Math.Clamp(falloff * brush.Opacity, 0, 1);
                var index = y * width + x;
                pixels[index] = brush.Mode == AiMaskBrushMode.Paint
                    ? (byte)Math.Round(pixels[index] + (255 - pixels[index]) * coverage,
                        MidpointRounding.AwayFromZero)
                    : (byte)Math.Round(pixels[index] * (1 - coverage), MidpointRounding.AwayFromZero);
            }
    }

    private static AiMaskEditResult Success(AiMask mask) => new(true, "AI_MASK_EDITED", mask);
    private static AiMaskEditResult Failure(string code) => new(false, code, null);
}
