using System.Security.Cryptography;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Imaging;

namespace Imaging.Tests;

public sealed class ImageResizeServiceTests
{
    private static readonly ImageSourceMetadata SourceMetadata = new(
        ImageSourceFormat.Png,
        1,
        1,
        ImageSourceOrientation.Normal,
        OrientationNormalized: true,
        HasIccProfile: false);

    [Fact]
    public async Task Stretch_and_free_aspect_produce_exact_dimensions_and_preserve_channels()
    {
        var source = Image(2, 2,
            Pixel(255, 0, 0), Pixel(0, 255, 0),
            Pixel(0, 0, 255), Pixel(255, 255, 255));
        var service = CreateService();

        var stretch = await service.ResizeAsync(Request(
            source, 4, 6, ImageResizeMode.Stretch, ImageInterpolationMode.NearestNeighbor));
        var free = await service.ResizeAsync(Request(
            source, 3, 5, ImageResizeMode.FreeAspect, ImageInterpolationMode.NearestNeighbor));

        AssertImageShape(stretch, 4, 6);
        AssertImageShape(free, 3, 5);
        AssertPixel(stretch.Image!, 0, 0, 255, 0, 0, 255);
        AssertPixel(stretch.Image!, 3, 0, 0, 255, 0, 255);
        AssertPixel(stretch.Image!, 0, 5, 0, 0, 255, 255);
    }

    [Fact]
    public async Task Fit_preserves_aspect_adds_transparency_and_honors_top_alignment()
    {
        var source = Solid(2, 1, 255, 0, 0, 255);
        var service = CreateService();

        var center = await service.ResizeAsync(Request(
            source, 4, 4, ImageResizeMode.Fit, ImageResizeAlignment.Center,
            ImageInterpolationMode.NearestNeighbor));
        var top = await service.ResizeAsync(Request(
            source, 4, 4, ImageResizeMode.Fit, ImageResizeAlignment.Top,
            ImageInterpolationMode.NearestNeighbor));

        AssertImageShape(center, 4, 4);
        AssertPixel(center.Image!, 0, 0, 0, 0, 0, 0);
        AssertPixel(center.Image!, 1, 1, 255, 0, 0, 255);
        AssertPixel(center.Image!, 1, 3, 0, 0, 0, 0);
        AssertPixel(top.Image!, 1, 0, 255, 0, 0, 255);
        AssertPixel(top.Image!, 1, 3, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Fill_preserves_aspect_crops_and_honors_left_right_alignment()
    {
        var source = Image(2, 1, Pixel(255, 0, 0), Pixel(0, 0, 255));
        var service = CreateService();

        var left = await service.ResizeAsync(Request(
            source, 1, 1, ImageResizeMode.Fill, ImageResizeAlignment.Left,
            ImageInterpolationMode.NearestNeighbor));
        var right = await service.ResizeAsync(Request(
            source, 1, 1, ImageResizeMode.Fill, ImageResizeAlignment.Right,
            ImageInterpolationMode.NearestNeighbor));

        AssertPixel(left.Image!, 0, 0, 255, 0, 0, 255);
        AssertPixel(right.Image!, 0, 0, 0, 0, 255, 255);
    }

    [Fact]
    public async Task Keep_aspect_returns_fitted_dimensions_inside_requested_box()
    {
        var result = await CreateService().ResizeAsync(Request(
            Solid(16, 9, 10, 20, 30, 255),
            512,
            512,
            ImageResizeMode.KeepAspect));

        AssertImageShape(result, 512, 288);
    }

    [Fact]
    public async Task Manual_crop_uses_explicit_source_rectangle()
    {
        var source = Image(2, 1, Pixel(255, 0, 0), Pixel(0, 0, 255));
        var request = new ImageResizeRequest(
            source,
            3,
            2,
            new ImageResizeOptions(
                ImageResizeMode.ManualCrop,
                ImageInterpolationMode.NearestNeighbor,
                CropRectangle: new ImageCropRectangle(1, 0, 1, 1)));

        var result = await CreateService().ResizeAsync(request);

        AssertImageShape(result, 3, 2);
        AssertPixel(result.Image!, 0, 0, 0, 0, 255, 255);
        AssertPixel(result.Image!, 2, 1, 0, 0, 255, 255);
    }

    [Theory]
    [InlineData(ImageResizeAlignment.Center, 1, 1)]
    [InlineData(ImageResizeAlignment.Top, 1, 0)]
    [InlineData(ImageResizeAlignment.Bottom, 1, 2)]
    [InlineData(ImageResizeAlignment.Left, 0, 1)]
    [InlineData(ImageResizeAlignment.Right, 2, 1)]
    public async Task Canvas_resize_places_unscaled_source_at_each_required_alignment(
        ImageResizeAlignment alignment,
        int expectedX,
        int expectedY)
    {
        var result = await CreateService().ResizeAsync(Request(
            Solid(1, 1, 255, 0, 0, 255),
            3,
            3,
            ImageResizeMode.CanvasResize,
            alignment,
            ImageInterpolationMode.NearestNeighbor));

        AssertImageShape(result, 3, 3);
        AssertPixel(result.Image!, expectedX, expectedY, 255, 0, 0, 255);
        Assert.Equal(1, CountPixelsWithAlpha(result.Image!));
    }

    [Fact]
    public async Task Transparent_padding_never_scales_and_rejects_smaller_canvas()
    {
        var service = CreateService();
        var source = Solid(2, 1, 0, 255, 0, 255);

        var padded = await service.ResizeAsync(Request(
            source, 4, 3, ImageResizeMode.TransparentPadding, ImageResizeAlignment.Bottom,
            ImageInterpolationMode.NearestNeighbor));
        var rejected = await service.ResizeAsync(Request(
            source, 1, 1, ImageResizeMode.TransparentPadding));

        AssertImageShape(padded, 4, 3);
        Assert.Equal(2, CountPixelsWithAlpha(padded.Image!));
        AssertPixel(padded.Image!, 1, 2, 0, 255, 0, 255);
        Assert.Equal(ImageResizeFailureReason.InvalidTargetDimensions, rejected.FailureReason);
    }

    [Fact]
    public async Task Linear_filter_uses_premultiplied_alpha_without_black_halo()
    {
        var source = Image(2, 1, Pixel(255, 0, 0, 255), Pixel(0, 0, 0, 0));

        var result = await CreateService().ResizeAsync(Request(
            source, 3, 1, ImageResizeMode.Stretch, ImageInterpolationMode.Linear));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        var image = Assert.IsType<InternalImage>(result.Image);
        var middle = GetPixel(image, 1, 0);
        Assert.InRange(middle.R, (byte)245, byte.MaxValue);
        Assert.Equal(0, middle.G);
        Assert.Equal(0, middle.B);
        Assert.InRange(middle.A, (byte)100, (byte)200);
        Assert.Equal(InternalImagePixelFormat.Rgba8Straight, image.PixelFormat);
    }

    [Theory]
    [InlineData(6000, 1801, 378, 126)]
    [InlineData(378, 126, 137, 512)]
    [InlineData(137, 512, 64, 17)]
    [InlineData(64, 17, 137, 511)]
    [InlineData(4000, 4000, 200, 200)]
    public async Task Roadmap_and_large_npot_dimensions_resize_without_power_of_two_coercion(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        var result = await CreateService().ResizeAsync(Request(
            Solid(sourceWidth, sourceHeight, 25, 50, 75, 128),
            targetWidth,
            targetHeight,
            ImageResizeMode.Stretch));

        AssertImageShape(result, targetWidth, targetHeight);
        Assert.Equal(checked(targetWidth * targetHeight * 4), result.Image!.Pixels.Length);
        Assert.True(result.Image.HasAlpha);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(16_385, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public async Task Invalid_excessive_and_overflow_dimensions_fail_before_allocation(int width, int height)
    {
        var result = await CreateService().ResizeAsync(Request(
            Solid(1, 1, 1, 2, 3, 255),
            width,
            height,
            ImageResizeMode.Stretch));

        Assert.False(result.Succeeded);
        Assert.Contains(result.FailureReason, new[]
        {
            ImageResizeFailureReason.InvalidTargetDimensions,
            ImageResizeFailureReason.ResourceLimitExceeded
        });
    }

    [Fact]
    public async Task Invalid_crop_and_unknown_options_fail_structurally()
    {
        var source = Solid(2, 2, 1, 2, 3, 255);
        var service = CreateService();
        var crop = await service.ResizeAsync(new(
            source,
            1,
            1,
            new(ImageResizeMode.ManualCrop, CropRectangle: new(1, 1, 2, 2))));
        var mode = await service.ResizeAsync(Request(source, 1, 1, (ImageResizeMode)999));
        var interpolation = await service.ResizeAsync(new(
            source,
            1,
            1,
            new(ImageResizeMode.Stretch, (ImageInterpolationMode)999)));

        Assert.Equal(ImageResizeFailureReason.InvalidOptions, crop.FailureReason);
        Assert.Equal(ImageResizeFailureReason.UnsupportedMode, mode.FailureReason);
        Assert.Equal(ImageResizeFailureReason.InvalidOptions, interpolation.FailureReason);
    }

    [Fact]
    public async Task Source_is_immutable_same_size_is_reused_and_dds_interop_preserves_storage()
    {
        var source = Image(2, 1, Pixel(255, 0, 0), Pixel(0, 0, 255, 128));
        var sourceHash = Hash(source.Pixels.AsSpan());
        var service = CreateService();

        var resized = await service.ResizeAsync(Request(source, 4, 2, ImageResizeMode.Stretch));
        var sameSize = await service.ResizeAsync(Request(source, 2, 1, ImageResizeMode.Stretch));
        var dds = DdsRgbaImage.Create(resized.Image!);

        Assert.Equal(sourceHash, Hash(source.Pixels.AsSpan()));
        Assert.NotSame(source, resized.Image);
        Assert.Same(source, sameSize.Image);
        Assert.Equal(resized.Image!.Pixels, dds.Pixels);
        Assert.Equal(resized.Image.Stride, dds.Stride);
    }

    [Fact]
    public async Task Cancellation_concurrency_and_repeat_output_are_structured_and_deterministic()
    {
        var source = Solid(73, 129, 80, 120, 160, 200);
        var service = CreateService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await service.ResizeAsync(
            Request(source, 137, 511, ImageResizeMode.Stretch), cancellation.Token);
        var requests = Enumerable.Range(0, 4)
            .Select(_ => service.ResizeAsync(Request(source, 137, 511, ImageResizeMode.Stretch)))
            .ToArray();
        var results = await Task.WhenAll(requests);

        Assert.True(cancelled.Cancelled);
        Assert.Equal(ImageResizeFailureReason.Cancelled, cancelled.FailureReason);
        Assert.All(results, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        var firstHash = Hash(results[0].Image!.Pixels.AsSpan());
        Assert.All(results, result => Assert.Equal(firstHash, Hash(result.Image!.Pixels.AsSpan())));
    }

    private static ImageResizeService CreateService() => new(ImageImportResourcePolicy.Default);

    private static ImageResizeRequest Request(
        InternalImage source,
        int width,
        int height,
        ImageResizeMode mode,
        ImageInterpolationMode interpolation = ImageInterpolationMode.Linear) =>
        new(source, width, height, new(mode, interpolation));

    private static ImageResizeRequest Request(
        InternalImage source,
        int width,
        int height,
        ImageResizeMode mode,
        ImageResizeAlignment alignment,
        ImageInterpolationMode interpolation = ImageInterpolationMode.Linear) =>
        new(source, width, height, new(mode, interpolation, alignment));

    private static InternalImage Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = r;
            pixels[index + 1] = g;
            pixels[index + 2] = b;
            pixels[index + 3] = a;
        }

        return new InternalImage(width, height, checked(width * 4), pixels, SourceMetadata);
    }

    private static InternalImage Image(int width, int height, params byte[][] pixels) =>
        new(width, height, checked(width * 4), pixels.SelectMany(pixel => pixel).ToArray(), SourceMetadata);

    private static byte[] Pixel(byte r, byte g, byte b, byte a = 255) => [r, g, b, a];

    private static void AssertImageShape(ImageResizeResult result, int width, int height)
    {
        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(width, result.Image!.Width);
        Assert.Equal(height, result.Image.Height);
        Assert.Equal(checked(width * 4), result.Image.Stride);
        Assert.Equal(checked(width * height * 4), result.Image.Pixels.Length);
    }

    private static void AssertPixel(
        InternalImage image,
        int x,
        int y,
        byte r,
        byte g,
        byte b,
        byte a) => Assert.Equal(new Rgba(r, g, b, a), GetPixel(image, x, y));

    private static Rgba GetPixel(InternalImage image, int x, int y)
    {
        var offset = checked(y * image.Stride + x * 4);
        return new(
            image.Pixels[offset],
            image.Pixels[offset + 1],
            image.Pixels[offset + 2],
            image.Pixels[offset + 3]);
    }

    private static int CountPixelsWithAlpha(InternalImage image)
    {
        var count = 0;
        for (var index = 3; index < image.Pixels.Length; index += 4)
        {
            if (image.Pixels[index] != 0)
            {
                count++;
            }
        }

        return count;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);
}
