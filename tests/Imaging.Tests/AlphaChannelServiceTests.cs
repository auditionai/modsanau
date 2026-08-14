using System.Diagnostics;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Imaging;
using Xunit.Abstractions;

namespace Imaging.Tests;

public sealed class AlphaChannelServiceTests
{
    private readonly AlphaChannelService _service = new(ImageImportResourcePolicy.Default);
    private readonly ITestOutputHelper _output;

    public AlphaChannelServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task View_returns_opaque_grayscale_visualization_without_mutating_source()
    {
        var source = CreateImage(2, 1, [10, 20, 30, 0, 40, 50, 60, 128]);
        var original = source.Pixels.ToArray();

        var result = await _service.ProcessAsync(new(source, AlphaChannelOperation.View));

        Assert.True(result.Succeeded);
        Assert.Null(result.Channel);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 128, 128, 128, 255 }, result.Image!.Pixels);
        Assert.Equal(original, source.Pixels);
    }

    [Fact]
    public async Task Extract_returns_tightly_packed_immutable_channel()
    {
        var source = CreateImage(3, 1, [1, 2, 3, 0, 4, 5, 6, 127, 7, 8, 9, 255]);

        var result = await _service.ProcessAsync(new(source, AlphaChannelOperation.Extract));

        Assert.True(result.Succeeded);
        Assert.Null(result.Image);
        Assert.Equal(3, result.Channel!.Width);
        Assert.Equal(1, result.Channel.Height);
        Assert.Equal(3, result.Channel.Stride);
        Assert.Equal(new byte[] { 0, 127, 255 }, result.Channel.Values);
    }

    [Fact]
    public void Alpha_channel_data_defensively_copies_input()
    {
        byte[] values = [1, 2];

        var channel = new AlphaChannelData(2, 1, values);
        values[0] = 99;

        Assert.Equal(new byte[] { 1, 2 }, channel.Values);
    }

    [Fact]
    public async Task Extract_then_replace_roundtrip_preserves_exact_rgba()
    {
        var source = CreateImage(2, 2,
        [
            255, 0, 0, 0,
            0, 255, 0, 1,
            0, 0, 255, 254,
            255, 255, 255, 255
        ]);
        var extracted = await _service.ProcessAsync(new(source, AlphaChannelOperation.Extract));
        var opaque = CreateSolidImage(2, 2, 9, 8, 7, 255);

        var replaced = await _service.ProcessAsync(
            new(opaque, AlphaChannelOperation.Replace, extracted.Channel));

        Assert.True(replaced.Succeeded);
        Assert.Equal(new byte[]
        {
            9, 8, 7, 0,
            9, 8, 7, 1,
            9, 8, 7, 254,
            9, 8, 7, 255
        }, replaced.Image!.Pixels);
    }

    [Fact]
    public async Task Replace_preserves_rgb_and_hidden_rgb_when_alpha_becomes_zero()
    {
        var source = CreateImage(2, 1, [255, 0, 0, 200, 4, 5, 6, 100]);
        var channel = new AlphaChannelData(2, 1, [0, 255]);

        var result = await _service.ProcessAsync(
            new(source, AlphaChannelOperation.Replace, channel));

        Assert.Equal(new byte[] { 255, 0, 0, 0, 4, 5, 6, 255 }, result.Image!.Pixels);
    }

    [Theory]
    [InlineData(0, 255)]
    [InlineData(1, 254)]
    [InlineData(127, 128)]
    [InlineData(128, 127)]
    [InlineData(254, 1)]
    [InlineData(255, 0)]
    public async Task Invert_uses_255_minus_alpha_and_preserves_rgb(byte alpha, byte expected)
    {
        var source = CreateImage(1, 1, [100, 150, 200, alpha]);

        var result = await _service.ProcessAsync(new(source, AlphaChannelOperation.Invert));

        Assert.Equal(new byte[] { 100, 150, 200, expected }, result.Image!.Pixels);
    }

    [Theory]
    [InlineData(0, 128, 0)]
    [InlineData(127, 128, 0)]
    [InlineData(128, 128, 255)]
    [InlineData(255, 128, 255)]
    [InlineData(0, 0, 255)]
    [InlineData(254, 255, 0)]
    [InlineData(255, 255, 255)]
    public async Task Threshold_uses_greater_than_or_equal_boundary(
        byte alpha,
        int threshold,
        byte expected)
    {
        var source = CreateImage(1, 1, [11, 22, 33, alpha]);

        var result = await _service.ProcessAsync(
            new(source, AlphaChannelOperation.Threshold, Threshold: threshold));

        Assert.Equal(new byte[] { 11, 22, 33, expected }, result.Image!.Pixels);
    }

    [Fact]
    public async Task Threshold_preserves_hidden_rgb()
    {
        var source = CreateImage(1, 1, [255, 0, 0, 1]);

        var result = await _service.ProcessAsync(
            new(source, AlphaChannelOperation.Threshold, Threshold: 128));

        Assert.Equal(new byte[] { 255, 0, 0, 0 }, result.Image!.Pixels);
    }

    [Fact]
    public async Task No_change_replace_and_threshold_reuse_source_instance()
    {
        var source = CreateImage(2, 1, [1, 2, 3, 0, 4, 5, 6, 255]);
        var channel = new AlphaChannelData(2, 1, [0, 255]);

        var replaced = await _service.ProcessAsync(
            new(source, AlphaChannelOperation.Replace, channel));
        var thresholded = await _service.ProcessAsync(
            new(source, AlphaChannelOperation.Threshold, Threshold: 128));

        Assert.Same(source, replaced.Image);
        Assert.Same(source, thresholded.Image);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public async Task Invalid_threshold_is_structured_failure(int threshold)
    {
        var result = await _service.ProcessAsync(
            new(CreateSolidImage(1, 1, 0, 0, 0, 255), AlphaChannelOperation.Threshold, Threshold: threshold));

        Assert.False(result.Succeeded);
        Assert.Equal(AlphaChannelFailureReason.InvalidThreshold, result.FailureReason);
        Assert.Null(result.Image);
    }

    [Fact]
    public async Task Missing_threshold_is_structured_failure()
    {
        var result = await _service.ProcessAsync(new(
            CreateSolidImage(1, 1, 0, 0, 0, 255),
            AlphaChannelOperation.Threshold));

        Assert.Equal(AlphaChannelFailureReason.InvalidThreshold, result.FailureReason);
    }

    [Fact]
    public async Task Missing_or_mismatched_replacement_is_structured_failure()
    {
        var source = CreateSolidImage(2, 1, 0, 0, 0, 255);

        var missing = await _service.ProcessAsync(new(source, AlphaChannelOperation.Replace));
        var mismatch = await _service.ProcessAsync(new(
            source,
            AlphaChannelOperation.Replace,
            new AlphaChannelData(1, 1, [0])));

        Assert.Equal(AlphaChannelFailureReason.ReplacementDimensionMismatch, missing.FailureReason);
        Assert.Equal(AlphaChannelFailureReason.ReplacementDimensionMismatch, mismatch.FailureReason);
    }

    [Fact]
    public async Task Invalid_request_and_operation_are_structured_failures()
    {
        var nullResult = await _service.ProcessAsync(null!);
        var invalidOperation = await _service.ProcessAsync(new(
            CreateSolidImage(1, 1, 0, 0, 0, 255),
            (AlphaChannelOperation)999));

        Assert.Equal(AlphaChannelFailureReason.InvalidRequest, nullResult.FailureReason);
        Assert.Equal(AlphaChannelFailureReason.InvalidOperation, invalidOperation.FailureReason);
    }

    [Fact]
    public async Task Arguments_not_used_by_operation_are_rejected()
    {
        var source = CreateSolidImage(1, 1, 0, 0, 0, 255);

        var result = await _service.ProcessAsync(
            new(source, AlphaChannelOperation.Invert, Threshold: 10));

        Assert.Equal(AlphaChannelFailureReason.InvalidRequest, result.FailureReason);
    }

    [Fact]
    public async Task Resource_policy_is_enforced_before_processing()
    {
        var service = new AlphaChannelService(new(1024, 1, 1, 4));
        var source = CreateSolidImage(2, 1, 0, 0, 0, 255);

        var result = await service.ProcessAsync(new(source, AlphaChannelOperation.Invert));

        Assert.Equal(AlphaChannelFailureReason.ResourceLimitExceeded, result.FailureReason);
    }

    [Fact]
    public async Task Pre_cancelled_operation_returns_no_partial_output()
    {
        var source = CreateSolidImage(64, 64, 1, 2, 3, 4);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await _service.ProcessAsync(
            new(source, AlphaChannelOperation.Invert),
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(AlphaChannelFailureReason.Cancelled, result.FailureReason);
        Assert.Null(result.Image);
        Assert.Null(result.Channel);
    }

    [Fact]
    public async Task Cancellation_during_large_operation_returns_no_partial_output()
    {
        var source = CreateSolidImage(4000, 4000, 1, 2, 3, 4);
        using var cancellation = new CancellationTokenSource();

        var operation = _service.ProcessAsync(
            new(source, AlphaChannelOperation.Invert),
            cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));
        var result = await operation;

        Assert.True(result.Cancelled);
        Assert.Null(result.Image);
        Assert.Null(result.Channel);
    }

    [Fact]
    public async Task Concurrent_operations_are_independent_and_deterministic()
    {
        var source = CreateSolidImage(512, 512, 30, 60, 90, 100);
        var requests = Enumerable.Range(0, 8)
            .Select(_ => _service.ProcessAsync(new(source, AlphaChannelOperation.Invert)));

        var results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.True(result.Succeeded));
        var expected = results[0].Image!.Pixels.ToArray();
        Assert.All(results, result => Assert.Equal(expected, result.Image!.Pixels.ToArray()));
        Assert.Equal(100, source.Pixels[3]);
    }

    [Fact]
    public async Task Large_6000_by_1801_extract_and_invert_complete_with_timing()
    {
        var source = CreateSolidImage(6000, 1801, 10, 20, 30, 128);
        var stopwatch = Stopwatch.StartNew();

        var extracted = await _service.ProcessAsync(new(source, AlphaChannelOperation.Extract));
        var inverted = await _service.ProcessAsync(new(source, AlphaChannelOperation.Invert));

        stopwatch.Stop();
        _output.WriteLine($"6000x1801 extract+invert: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(extracted.Succeeded);
        Assert.True(inverted.Succeeded);
        Assert.Equal(6000 * 1801, extracted.Channel!.Values.Length);
        Assert.Equal(127, inverted.Image!.Pixels[3]);
    }

    [Fact]
    public async Task Large_4000_by_4000_threshold_completes_with_timing()
    {
        var source = CreateSolidImage(4000, 4000, 10, 20, 30, 127);
        var stopwatch = Stopwatch.StartNew();

        var result = await _service.ProcessAsync(
            new(source, AlphaChannelOperation.Threshold, Threshold: 128));

        stopwatch.Stop();
        _output.WriteLine($"4000x4000 threshold: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Image!.Pixels[3]);
        Assert.Equal(source.Stride, result.Image.Stride);
    }

    [Fact]
    public async Task Resize_and_adjustment_interoperate_with_alpha_result()
    {
        var source = CreateImage(2, 1, [100, 50, 25, 64, 10, 20, 30, 192]);
        var alpha = await _service.ProcessAsync(new(source, AlphaChannelOperation.Invert));
        var alphaImage = Assert.IsType<InternalImage>(alpha.Image);
        var resized = await new ImageResizeService(ImageImportResourcePolicy.Default).ResizeAsync(
            new(alphaImage, 4, 2, new(ImageResizeMode.Stretch, ImageInterpolationMode.NearestNeighbor)));
        var adjusted = await new ImageAdjustmentService(ImageImportResourcePolicy.Default).AdjustAsync(
            new(alphaImage, new(Brightness: 0.1)));

        Assert.True(resized.Succeeded);
        Assert.Equal(4, resized.Image!.Width);
        Assert.True(adjusted.Succeeded);
        Assert.Equal(alphaImage.Pixels[3], adjusted.Image!.Pixels[3]);
    }

    [Fact]
    public async Task Alpha_result_is_compatible_with_history_and_dds_boundary()
    {
        var source = CreateSolidImage(2, 2, 1, 2, 3, 10);
        var alpha = await _service.ProcessAsync(new(source, AlphaChannelOperation.Invert));
        var alphaImage = Assert.IsType<InternalImage>(alpha.Image);
        var initialState = CreateEditorState(source);
        var editedState = CreateEditorState(alphaImage);
        var history = new EditHistoryService().CreateSession(initialState);

        var pushed = history.Session!.Push(
            editedState,
            new EditOperationDescriptor(EditOperationKind.Alpha));
        var dds = DdsRgbaImage.Create(alphaImage);

        Assert.True(pushed.Succeeded);
        Assert.Same(alphaImage, pushed.State.Current.Image);
        Assert.Equal(EditOperationKind.Alpha, pushed.Entry!.Operation.Kind);
        Assert.Equal(alphaImage.Pixels, dds.Pixels);
        Assert.Equal(alphaImage.Stride, dds.Stride);
    }

    private static ImageEditorState CreateEditorState(InternalImage image) =>
        new(
            image,
            new InteractiveImageTransformState(
                image.Width,
                image.Height,
                new(0, 0, 1, 1),
                1,
                new(0, 0),
                ImageScale.Identity,
                ImageQuarterTurn.None,
                false,
                false,
                new(0, 0),
                ImageTransformConstraints.Default),
            new ImageAdjustmentSettings());

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
