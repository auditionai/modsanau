using System.Diagnostics;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Imaging;
using Xunit.Abstractions;

namespace Imaging.Tests;

public sealed class ImageAdjustmentServiceTests
{
    private readonly ImageAdjustmentService _service = new(ImageImportResourcePolicy.Default);
    private readonly ITestOutputHelper _output;

    public ImageAdjustmentServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Small_image_adjustment_reports_representative_timing()
    {
        var source = CreateSolidImage(256, 256, 30, 90, 180, 128);
        var stopwatch = Stopwatch.StartNew();

        var result = await _service.AdjustAsync(new(source, new(Brightness: 0.01)));

        stopwatch.Stop();
        _output.WriteLine($"256x256 brightness: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Neutral_settings_return_same_immutable_image()
    {
        var source = CreateImage(2, 1, [10, 20, 30, 40, 50, 60, 70, 80]);

        var result = await _service.AdjustAsync(new(source, new()));

        Assert.True(result.Succeeded);
        Assert.Same(source, result.Image);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 }, source.Pixels);
    }

    [Fact]
    public async Task Null_request_is_structured_failure()
    {
        var result = await _service.AdjustAsync(null!);

        Assert.Equal(ImageAdjustmentFailureReason.InvalidRequest, result.FailureReason);
    }

    [Fact]
    public async Task Resource_policy_is_enforced_before_output_allocation()
    {
        var service = new ImageAdjustmentService(new(1024, 16, 1, 4));
        var source = CreateImage(2, 1, [1, 2, 3, 4, 5, 6, 7, 8]);

        var result = await service.AdjustAsync(new(source, new(Brightness: 0.1)));

        Assert.Equal(ImageAdjustmentFailureReason.ResourceLimitExceeded, result.FailureReason);
        Assert.Null(result.Image);
    }

    [Theory]
    [InlineData(0.25, 100, 164)]
    [InlineData(-0.25, 100, 36)]
    public async Task Brightness_is_additive(double amount, byte input, byte expected)
    {
        var result = await AdjustPixel(input, input, input, 255, new(Brightness: amount));

        Assert.Equal(new byte[] { expected, expected, expected, 255 }, result.Image!.Pixels);
    }

    [Theory]
    [InlineData(0.5, 100, 86)]
    [InlineData(-0.5, 100, 114)]
    public async Task Contrast_uses_127_5_midpoint(double amount, byte input, byte expected)
    {
        var result = await AdjustPixel(input, input, input, 255, new(Contrast: amount));

        Assert.Equal(expected, result.Image!.Pixels[0]);
    }

    [Theory]
    [InlineData(1, 64, 128)]
    [InlineData(-1, 64, 32)]
    public async Task Exposure_uses_ev_factor(double amount, byte input, byte expected)
    {
        var result = await AdjustPixel(input, input, input, 255, new(Exposure: amount));

        Assert.Equal(expected, result.Image!.Pixels[0]);
    }

    [Fact]
    public async Task Saturation_minus_one_produces_documented_luminance()
    {
        var result = await AdjustPixel(255, 0, 0, 128, new(Saturation: -1));

        Assert.Equal(new byte[] { 54, 54, 54, 128 }, result.Image!.Pixels);
    }

    [Fact]
    public async Task Saturation_increase_changes_chroma_without_swapping_channels()
    {
        var result = await AdjustPixel(180, 100, 80, 255, new(Saturation: 0.5));

        Assert.True(result.Image!.Pixels[0] > 180);
        Assert.True(result.Image.Pixels[2] < 80);
    }

    [Fact]
    public async Task Vibrance_is_not_equivalent_to_saturation()
    {
        var source = CreateImage(1, 1, [180, 100, 80, 255]);

        var vibrance = await _service.AdjustAsync(new(source, new(Vibrance: 0.5)));
        var saturation = await _service.AdjustAsync(new(source, new(Saturation: 0.5)));

        Assert.NotEqual(saturation.Image!.Pixels, vibrance.Image!.Pixels);
    }

    [Fact]
    public async Task Hue_rotates_rgb_only()
    {
        var result = await AdjustPixel(255, 0, 0, 64, new(Hue: 120));

        Assert.NotEqual((byte)255, result.Image!.Pixels[0]);
        Assert.Equal((byte)64, result.Image.Pixels[3]);
    }

    [Fact]
    public async Task Temperature_warms_red_and_cools_blue()
    {
        var result = await AdjustPixel(100, 100, 100, 255, new(Temperature: 1));

        Assert.Equal(new byte[] { 132, 100, 68, 255 }, result.Image!.Pixels);
    }

    [Fact]
    public async Task Tint_increases_green_and_reduces_red_blue()
    {
        var result = await AdjustPixel(100, 100, 100, 255, new(Tint: 1));

        Assert.Equal(new byte[] { 84, 132, 84, 255 }, result.Image!.Pixels);
    }

    [Fact]
    public async Task Highlights_affect_white_more_than_black()
    {
        var source = CreateImage(2, 1, [0, 0, 0, 255, 200, 200, 200, 255]);

        var result = await _service.AdjustAsync(new(source, new(Highlights: 0.25)));

        Assert.Equal((byte)0, result.Image!.Pixels[0]);
        Assert.True(result.Image.Pixels[4] > 200);
    }

    [Fact]
    public async Task Shadows_affect_black_more_than_white()
    {
        var source = CreateImage(2, 1, [20, 20, 20, 255, 255, 255, 255, 255]);

        var result = await _service.AdjustAsync(new(source, new(Shadows: 0.25)));

        Assert.True(result.Image!.Pixels[0] > 20);
        Assert.Equal((byte)255, result.Image.Pixels[4]);
    }

    [Fact]
    public async Task Gamma_is_an_arbitrary_channel_adjustment_not_srgb_conversion()
    {
        var result = await AdjustPixel(64, 64, 64, 255, new(Gamma: 2));

        Assert.Equal((byte)128, result.Image!.Pixels[0]);
    }

    [Fact]
    public async Task Sharpen_changes_rgb_but_preserves_dimensions_and_alpha()
    {
        var source = CreateImage(3, 1, [0, 0, 0, 10, 100, 100, 100, 64, 0, 0, 0, 200]);

        var result = await _service.AdjustAsync(new(source, new(Sharpen: 1)));

        Assert.Equal(3, result.Image!.Width);
        Assert.True(result.Image.Pixels[4] > 100);
        Assert.Equal(new byte[] { 10, 64, 200 }, AlphaBytes(result.Image));
    }

    [Fact]
    public async Task Blur_changes_rgb_but_preserves_dimensions_and_alpha()
    {
        var source = CreateImage(3, 1, [0, 0, 0, 0, 255, 255, 255, 128, 0, 0, 0, 255]);

        var result = await _service.AdjustAsync(new(source, new(Blur: 1)));

        Assert.Equal(3, result.Image!.Width);
        Assert.InRange(result.Image.Pixels[4], (byte)84, (byte)86);
        Assert.Equal(new byte[] { 0, 128, 255 }, AlphaBytes(result.Image));
    }

    [Fact]
    public async Task Opacity_changes_alpha_only()
    {
        var result = await AdjustPixel(10, 20, 30, 255, new(Opacity: 0.5));

        Assert.Equal(new byte[] { 10, 20, 30, 128 }, result.Image!.Pixels);
    }

    public static TheoryData<ImageAdjustmentSettings> AlphaPreservingSettings => new()
    {
        new(Brightness: 0.2),
        new(Contrast: 0.2),
        new(Exposure: 0.5),
        new(Saturation: 0.2),
        new(Vibrance: 0.2),
        new(Hue: 45),
        new(Temperature: 0.2),
        new(Tint: 0.2),
        new(Highlights: 0.2),
        new(Shadows: 0.2),
        new(Gamma: 1.2),
        new(Sharpen: 0.5),
        new(Blur: 0.5)
    };

    [Theory]
    [MemberData(nameof(AlphaPreservingSettings))]
    public async Task Every_rgb_adjustment_preserves_mixed_alpha_exactly(ImageAdjustmentSettings settings)
    {
        var source = CreateImage(5, 1,
        [
            10, 20, 30, 0,
            40, 50, 60, 64,
            70, 80, 90, 128,
            100, 110, 120, 192,
            130, 140, 150, 255
        ]);

        var result = await _service.AdjustAsync(new(source, settings));

        Assert.Equal(new byte[] { 0, 64, 128, 192, 255 }, AlphaBytes(result.Image!));
    }

    [Fact]
    public async Task Fully_transparent_hidden_rgb_is_adjusted_deterministically()
    {
        var result = await AdjustPixel(10, 20, 30, 0, new(Brightness: 0.1));

        Assert.Equal(new byte[] { 36, 46, 56, 0 }, result.Image!.Pixels);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1.01)]
    [InlineData(-1.01)]
    public async Task Invalid_brightness_is_structured_failure(double value)
    {
        var result = await AdjustPixel(1, 2, 3, 4, new(Brightness: value));

        Assert.False(result.Succeeded);
        Assert.Equal(ImageAdjustmentFailureReason.InvalidSettings, result.FailureReason);
        Assert.Equal("IMAGE_ADJUSTMENT_INVALID_SETTINGS", result.DiagnosticCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.01)]
    public async Task Invalid_gamma_is_rejected(double value)
    {
        var result = await AdjustPixel(1, 2, 3, 4, new(Gamma: value));

        Assert.Equal(ImageAdjustmentFailureReason.InvalidSettings, result.FailureReason);
    }

    public static TheoryData<ImageAdjustmentSettings> ValidBoundarySettings => new()
    {
        new(Brightness: -1), new(Brightness: 1),
        new(Contrast: -1), new(Contrast: 1),
        new(Exposure: -5), new(Exposure: 5),
        new(Saturation: -1), new(Saturation: 1),
        new(Vibrance: -1), new(Vibrance: 1),
        new(Hue: -180), new(Hue: 180),
        new(Temperature: -1), new(Temperature: 1),
        new(Tint: -1), new(Tint: 1),
        new(Highlights: -1), new(Highlights: 1),
        new(Shadows: -1), new(Shadows: 1),
        new(Gamma: 0.1), new(Gamma: 10),
        new(Sharpen: 1), new(Blur: 20), new(Opacity: 0)
    };

    [Theory]
    [MemberData(nameof(ValidBoundarySettings))]
    public async Task Every_parameter_accepts_documented_boundary(ImageAdjustmentSettings settings)
    {
        var result = await AdjustPixel(30, 90, 180, 128, settings);

        Assert.True(result.Succeeded);
    }

    public static TheoryData<ImageAdjustmentSettings> InvalidOutOfRangeSettings => new()
    {
        new(Contrast: 1.01),
        new(Exposure: -5.01),
        new(Saturation: 1.01),
        new(Vibrance: -1.01),
        new(Hue: 180.01),
        new(Temperature: -1.01),
        new(Tint: 1.01),
        new(Highlights: -1.01),
        new(Shadows: 1.01),
        new(Gamma: 10.01),
        new(Sharpen: -0.01),
        new(Blur: 20.01),
        new(Opacity: -0.01)
    };

    [Theory]
    [MemberData(nameof(InvalidOutOfRangeSettings))]
    public async Task Every_parameter_rejects_values_outside_documented_range(ImageAdjustmentSettings settings)
    {
        var result = await AdjustPixel(30, 90, 180, 128, settings);

        Assert.False(result.Succeeded);
        Assert.Equal(ImageAdjustmentFailureReason.InvalidSettings, result.FailureReason);
    }

    [Fact]
    public async Task Extreme_values_clamp_without_overflow_or_wrap()
    {
        var high = await AdjustPixel(255, 254, 1, 255, new(Brightness: 1));
        var low = await AdjustPixel(0, 1, 255, 255, new(Brightness: -1));

        Assert.Equal(new byte[] { 255, 255, 255, 255 }, high.Image!.Pixels);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, low.Image!.Pixels);
    }

    [Theory]
    [InlineData(0.49, 0)]
    [InlineData(0.5, 1)]
    [InlineData(254.5, 255)]
    public async Task Quantization_uses_midpoint_away_from_zero(double input, byte expected)
    {
        var opacity = input / 255;
        var result = await AdjustPixel(1, 1, 1, 255, new(Opacity: opacity));

        Assert.Equal(expected, result.Image!.Pixels[3]);
    }

    [Fact]
    public async Task Combined_adjustments_use_fixed_brightness_then_contrast_order()
    {
        var result = await AdjustPixel(100, 100, 100, 255, new(Brightness: 0.1, Contrast: 0.5));

        Assert.Equal((byte)125, result.Image!.Pixels[0]);
    }

    [Fact]
    public async Task Source_is_not_mutated_and_output_is_new_for_non_neutral_settings()
    {
        var original = new byte[] { 10, 20, 30, 40 };
        var source = CreateImage(1, 1, original);

        var result = await _service.AdjustAsync(new(source, new(Brightness: 0.1)));

        Assert.NotSame(source, result.Image);
        Assert.Equal(original, source.Pixels);
        Assert.Equal(source.Width, result.Image!.Width);
        Assert.Equal(source.Height, result.Image.Height);
        Assert.Equal(source.Stride, result.Image.Stride);
    }

    [Fact]
    public async Task Repeated_and_concurrent_adjustments_are_deterministic()
    {
        var source = CreateSolidImage(128, 128, 30, 90, 180, 128);
        var request = new ImageAdjustmentRequest(source, new(Brightness: 0.1, Hue: 20, Blur: 0.5));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _service.AdjustAsync(request)));

        Assert.All(results, result => Assert.True(result.Succeeded));
        var expected = results[0].Image!.Pixels.ToArray();
        Assert.All(results.Skip(1), result => Assert.Equal(expected, result.Image!.Pixels.ToArray()));
    }

    [Fact]
    public async Task Cancellation_during_large_adjustment_returns_no_partial_image()
    {
        var source = CreateSolidImage(4000, 4000, 30, 90, 180, 128);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));

        var result = await _service.AdjustAsync(
            new(source, new(Brightness: 0.1, Blur: 1)),
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(ImageAdjustmentFailureReason.Cancelled, result.FailureReason);
        Assert.Null(result.Image);
    }

    [Fact]
    public async Task Real_fixture_dimensions_6000_by_1801_complete_with_simple_adjustment()
    {
        var source = CreateSolidImage(6000, 1801, 30, 90, 180, 128);
        var stopwatch = Stopwatch.StartNew();

        var result = await _service.AdjustAsync(new(source, new(Brightness: 0.01)));

        stopwatch.Stop();
        _output.WriteLine($"6000x1801 brightness: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(result.Succeeded);
        Assert.Equal((6000, 1801, 24000), (result.Image!.Width, result.Image.Height, result.Image.Stride));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Large_4000_by_4000_simple_adjustment_completes()
    {
        var source = CreateSolidImage(4000, 4000, 30, 90, 180, 128);
        var stopwatch = Stopwatch.StartNew();

        var result = await _service.AdjustAsync(new(source, new(Brightness: 0.01)));

        stopwatch.Stop();
        _output.WriteLine($"4000x4000 brightness: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(result.Succeeded);
        Assert.NotSame(source, result.Image);
    }

    [Fact]
    public async Task Adjustment_output_interoperates_with_dds_rgba_image()
    {
        var source = CreateImage(2, 1, [10, 20, 30, 40, 50, 60, 70, 80]);
        var result = await _service.AdjustAsync(new(source, new(Brightness: 0.1)));

        var ddsImage = DdsRgbaImage.Create(result.Image!);

        Assert.Equal(result.Image!.Width, ddsImage.Width);
        Assert.Equal(result.Image.Stride, ddsImage.Stride);
        Assert.Equal(result.Image.Pixels, ddsImage.Pixels);
    }

    [Fact]
    public async Task Red_green_blue_black_white_and_gray_channels_remain_in_rgba_order()
    {
        var source = CreateImage(6, 1,
        [
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            0, 0, 0, 255,
            255, 255, 255, 255,
            128, 128, 128, 255
        ]);

        var result = await _service.AdjustAsync(new(source, new(Gamma: 2)));

        Assert.Equal(new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            0, 0, 0, 255,
            255, 255, 255, 255,
            181, 181, 181, 255
        }, result.Image!.Pixels);
    }

    private async Task<ImageAdjustmentResult> AdjustPixel(
        byte red,
        byte green,
        byte blue,
        byte alpha,
        ImageAdjustmentSettings settings) =>
        await _service.AdjustAsync(new(CreateImage(1, 1, [red, green, blue, alpha]), settings));

    private static byte[] AlphaBytes(InternalImage image) =>
        Enumerable.Range(0, image.Width * image.Height)
            .Select(index => image.Pixels[(index * 4) + 3])
            .ToArray();

    private static InternalImage CreateSolidImage(
        int width,
        int height,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = red;
            pixels[index + 1] = green;
            pixels[index + 2] = blue;
            pixels[index + 3] = alpha;
        }

        return CreateImage(width, height, pixels);
    }

    private static InternalImage CreateImage(int width, int height, byte[] pixels) =>
        new(
            width,
            height,
            checked(width * 4),
            pixels,
            new ImageSourceMetadata(
                ImageSourceFormat.Png,
                width,
                height,
                ImageSourceOrientation.Normal,
                true,
                false));
}
