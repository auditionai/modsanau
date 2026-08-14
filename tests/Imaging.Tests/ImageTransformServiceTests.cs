using AuditionModStudio.Core.Images;
using AuditionModStudio.Imaging;

namespace Imaging.Tests;

public sealed class ImageTransformServiceTests
{
    private static readonly ImageSourceMetadata SourceMetadata = new(
        ImageSourceFormat.Png,
        1,
        1,
        ImageSourceOrientation.Normal,
        OrientationNormalized: true,
        HasIccProfile: false);

    [Fact]
    public void Create_uses_full_normalized_crop_and_identity_transform()
    {
        var source = Image(7, 5);

        var result = CreateService().Create(source);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        var state = result.Value!;
        Assert.Equal(new NormalizedImageRectangle(0, 0, 1, 1), state.Crop);
        Assert.Equal(1, state.Zoom);
        Assert.Equal(new ViewportVector(0, 0), state.Pan);
        Assert.Equal(ImageScale.Identity, state.Scale);
        Assert.Equal(ImageQuarterTurn.None, state.Rotation);
        Assert.False(state.FlipHorizontal);
        Assert.False(state.FlipVertical);
        Assert.Equal(new ImagePixelVector(0, 0), state.Translation);
    }

    [Fact]
    public void Normalized_center_crop_converts_to_deterministic_half_open_pixel_rectangle()
    {
        var service = CreateService();
        var state = State(6000, 1801);
        var cropped = service.SetCrop(state, new(0.25, 0.25, 0.5, 0.5));

        var pixels = service.ToPixelCrop(cropped.Value!);

        Assert.True(pixels.Succeeded, pixels.DiagnosticCode);
        Assert.Equal(new ImageCropRectangle(1500, 450, 3000, 901), pixels.Value);
    }

    [Fact]
    public void Pixel_normalized_pixel_roundtrip_contains_original_odd_rectangle()
    {
        var service = CreateService();
        var state = State(137, 511);
        var original = new ImageCropRectangle(17, 23, 71, 203);
        var normalized = new NormalizedImageRectangle(
            (double)original.X / state.ImageWidth,
            (double)original.Y / state.ImageHeight,
            (double)original.Width / state.ImageWidth,
            (double)original.Height / state.ImageHeight);

        var updated = service.SetCrop(state, normalized);
        var roundtrip = service.ToPixelCrop(updated.Value!);

        Assert.Equal(original, roundtrip.Value);
        Assert.Equal(normalized.X, updated.Value!.Crop.X, 12);
        Assert.Equal(normalized.Y, updated.Value.Crop.Y, 12);
    }

    [Theory]
    [InlineData(1, 1, 1.0)]
    [InlineData(4, 3, 4.0 / 3.0)]
    [InlineData(16, 9, 16.0 / 9.0)]
    [InlineData(9, 16, 9.0 / 16.0)]
    [InlineData(2.39, 1, 2.39)]
    public void Crop_supports_preset_and_custom_pixel_aspect_ratios(
        double ratioWidth,
        double ratioHeight,
        double expected)
    {
        var service = CreateService();
        var state = State(4000, 3000);

        var result = service.SetCrop(
            state,
            new(0, 0, 1, 1),
            new(ratioWidth, ratioHeight));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        var crop = result.Value!.Crop;
        var actual = crop.Width * state.ImageWidth / (crop.Height * state.ImageHeight);
        Assert.Equal(expected, actual, 10);
        Assert.InRange(crop.X, 0, 1);
        Assert.InRange(crop.Y, 0, 1);
        Assert.True(crop.Right <= 1);
        Assert.True(crop.Bottom <= 1);
    }

    [Theory]
    [InlineData(CropAnchor.TopLeft, 0.1, 0.2)]
    [InlineData(CropAnchor.TopRight, 0.3, 0.2)]
    [InlineData(CropAnchor.BottomLeft, 0.1, 0.2)]
    [InlineData(CropAnchor.BottomRight, 0.3, 0.2)]
    public void Aspect_crop_preserves_requested_corner_anchor(
        CropAnchor anchor,
        double expectedX,
        double expectedY)
    {
        var result = CreateService().SetCrop(
            State(1000, 1000),
            new(0.1, 0.2, 0.8, 0.6),
            new(1, 1),
            anchor);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(expectedX, result.Value!.Crop.X, 10);
        Assert.Equal(expectedY, result.Value.Crop.Y, 10);
        Assert.Equal(0.6, result.Value.Crop.Width, 10);
        Assert.Equal(0.6, result.Value.Crop.Height, 10);
    }

    [Fact]
    public void Interactive_crop_clamps_partial_bounds_but_rejects_zero_outside_and_below_minimum()
    {
        var constraints = new ImageTransformConstraints(0.5, 4, 10, 10);
        var state = State(100, 100, constraints);
        var service = CreateService();

        var clamped = service.SetCrop(state, new(-0.1, 0.2, 0.5, 0.5));
        var outside = service.SetCrop(state, new(2, 2, 1, 1));
        var tiny = service.SetCrop(state, new(0, 0, 0.05, 0.05));

        Assert.Equal(0, clamped.Value!.Crop.X, 12);
        Assert.Equal(0.2, clamped.Value.Crop.Y, 12);
        Assert.Equal(0.4, clamped.Value.Crop.Width, 12);
        Assert.Equal(0.5, clamped.Value.Crop.Height, 12);
        Assert.Equal(ImageTransformFailureReason.InvalidCrop, outside.FailureReason);
        Assert.Equal(ImageTransformFailureReason.InvalidCrop, tiny.FailureReason);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, double.PositiveInfinity, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, -1, 1)]
    public void Invalid_nonfinite_and_zero_crop_is_rejected(double x, double y, double width, double height)
    {
        var result = CreateService().SetCrop(State(100, 100), new(x, y, width, height));

        Assert.Equal(ImageTransformFailureReason.InvalidCrop, result.FailureReason);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.PositiveInfinity)]
    public void Invalid_aspect_ratio_is_rejected(double width, double height)
    {
        var result = CreateService().SetCrop(
            State(100, 100),
            new(0, 0, 1, 1),
            new(width, height));

        Assert.Equal(ImageTransformFailureReason.InvalidAspectRatio, result.FailureReason);
    }

    [Fact]
    public void Viewport_fit_mapping_accounts_for_letterbox_offsets()
    {
        var service = CreateService();
        var state = State(1920, 1080);
        var viewport = new ViewportSize(1000, 1000);

        var topLeft = service.MapImageToViewport(state, viewport, new(0, 0));
        var bottomRight = service.MapImageToViewport(state, viewport, new(1920, 1080));

        AssertPoint(topLeft.Value!, 0, 218.75, 0.001);
        AssertPoint(bottomRight.Value!, 1000, 781.25, 0.001);
    }

    [Theory]
    [InlineData(ImageQuarterTurn.None, false, false)]
    [InlineData(ImageQuarterTurn.Clockwise90, false, false)]
    [InlineData(ImageQuarterTurn.Clockwise180, true, false)]
    [InlineData(ImageQuarterTurn.Clockwise270, false, true)]
    public void Forward_inverse_mapping_roundtrips_scale_rotation_flip_translate_zoom_and_pan(
        ImageQuarterTurn rotation,
        bool flipHorizontal,
        bool flipVertical)
    {
        var service = CreateService();
        var transformed = service.SetImageTransform(
            State(137, 512),
            new(1.25, 0.75),
            rotation,
            flipHorizontal,
            flipVertical,
            new(13.5, -7.25));
        var viewportState = service.SetViewportTransform(
            transformed.Value!,
            2.5,
            new(31.25, -18.5));
        var point = new ImagePixelPoint(29.75, 301.125);

        var mapped = service.MapImageToViewport(viewportState.Value!, new(801, 603), point);
        var roundtrip = service.MapViewportToImage(
            viewportState.Value!,
            new(801, 603),
            mapped.Value!);

        Assert.Equal(point.X, roundtrip.Value!.X, 3);
        Assert.Equal(point.Y, roundtrip.Value.Y, 3);
    }

    [Fact]
    public void Zoom_constraints_and_nonfinite_pan_scale_translation_are_rejected()
    {
        var service = CreateService();
        var state = State(100, 100, new(0.5, 4, 1, 1));

        var below = service.SetViewportTransform(state, 0.49, new(0, 0));
        var above = service.SetViewportTransform(state, 4.01, new(0, 0));
        var pan = service.SetViewportTransform(state, 1, new(double.NaN, 0));
        var scale = service.SetImageTransform(
            state, new(0, 1), ImageQuarterTurn.None, false, false, new(0, 0));
        var translation = service.SetImageTransform(
            state, ImageScale.Identity, ImageQuarterTurn.None, false, false,
            new(double.PositiveInfinity, 0));

        Assert.Equal(ImageTransformFailureReason.InvalidZoom, below.FailureReason);
        Assert.Equal(ImageTransformFailureReason.InvalidZoom, above.FailureReason);
        Assert.Equal(ImageTransformFailureReason.InvalidZoom, pan.FailureReason);
        Assert.Equal(ImageTransformFailureReason.InvalidTransform, scale.FailureReason);
        Assert.Equal(ImageTransformFailureReason.InvalidTransform, translation.FailureReason);
    }

    [Fact]
    public void Reset_restores_all_non_destructive_state_to_identity()
    {
        var service = CreateService();
        var crop = service.SetCrop(State(200, 100), new(0.2, 0.1, 0.5, 0.7));
        var viewport = service.SetViewportTransform(crop.Value!, 3, new(9, -4));
        var transformed = service.SetImageTransform(
            viewport.Value!, new(2, 0.5), ImageQuarterTurn.Clockwise90, true, true, new(7, 8));

        var reset = service.Reset(transformed.Value!);

        Assert.Equal(new NormalizedImageRectangle(0, 0, 1, 1), reset.Value!.Crop);
        Assert.Equal(1, reset.Value.Zoom);
        Assert.Equal(new ViewportVector(0, 0), reset.Value.Pan);
        Assert.Equal(ImageScale.Identity, reset.Value.Scale);
        Assert.Equal(ImageQuarterTurn.None, reset.Value.Rotation);
        Assert.False(reset.Value.FlipHorizontal);
        Assert.False(reset.Value.FlipVertical);
        Assert.Equal(new ImagePixelVector(0, 0), reset.Value.Translation);
    }

    [Theory]
    [InlineData(6000, 1801)]
    [InlineData(4000, 4000)]
    [InlineData(137, 511)]
    public void Large_and_npot_geometry_is_constant_memory_and_deterministic(int width, int height)
    {
        var service = CreateService();
        var state = State(width, height);

        var first = service.SetCrop(state, new(0.123, 0.234, 0.456, 0.345));
        var second = service.SetCrop(state, new(0.123, 0.234, 0.456, 0.345));
        var firstPixels = service.ToPixelCrop(first.Value!);
        var secondPixels = service.ToPixelCrop(second.Value!);

        Assert.Equal(first.Value, second.Value);
        Assert.Equal(firstPixels.Value, secondPixels.Value);
    }

    [Fact]
    public async Task Geometry_state_does_not_copy_pixels_and_translates_to_plan21_manual_crop()
    {
        var source = Image(4, 2);
        var pixels = source.Pixels;
        var service = CreateService();
        var cropped = service.SetCrop(State(4, 2), new(0.5, 0, 0.5, 1));

        var request = service.ToManualCropResizeRequest(cropped.Value!, source, 3, 3);
        var resized = await new ImageResizeService(ImageImportResourcePolicy.Default)
            .ResizeAsync(request.Value!);

        Assert.True(request.Succeeded, request.DiagnosticCode);
        Assert.Equal(ImageResizeMode.ManualCrop, request.Value!.Options.Mode);
        Assert.Equal(new ImageCropRectangle(2, 0, 2, 2), request.Value.Options.CropRectangle);
        Assert.True(resized.Succeeded, resized.DiagnosticCode);
        Assert.Equal(3, resized.Image!.Width);
        Assert.Equal(3, resized.Image.Height);
        Assert.Equal(pixels, source.Pixels);
    }

    [Fact]
    public async Task Stateless_geometry_calculations_are_concurrent()
    {
        var service = CreateService();
        var state = State(6000, 1801);
        var tasks = Enumerable.Range(0, 32)
            .Select(index => Task.Run(() => service.SetCrop(
                state,
                new(index / 1000.0, 0.1, 0.5, 0.5),
                new(16, 9))))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        Assert.Equal(new NormalizedImageRectangle(0, 0, 1, 1), state.Crop);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    public void Invalid_viewport_is_rejected(double width, double height)
    {
        var result = CreateService().MapImageToViewport(
            State(10, 10),
            new(width, height),
            new(1, 1));

        Assert.Equal(ImageTransformFailureReason.InvalidViewport, result.FailureReason);
    }

    private static ImageTransformService CreateService() => new();

    private static InteractiveImageTransformState State(
        int width,
        int height,
        ImageTransformConstraints? constraints = null) => new(
            width,
            height,
            new(0, 0, 1, 1),
            1,
            new(0, 0),
            ImageScale.Identity,
            ImageQuarterTurn.None,
            false,
            false,
            new(0, 0),
            constraints ?? ImageTransformConstraints.Default);

    private static InternalImage Image(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = checked((byte)(index % 251));
            pixels[index + 3] = 255;
        }

        return new(width, height, checked(width * 4), pixels, SourceMetadata);
    }

    private static void AssertPoint(ViewportPoint point, double x, double y, double tolerance)
    {
        Assert.InRange(point.X, x - tolerance, x + tolerance);
        Assert.InRange(point.Y, y - tolerance, y + tolerance);
    }
}
