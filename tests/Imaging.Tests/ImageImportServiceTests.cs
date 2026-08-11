using System.Security.Cryptography;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Imaging;
using SkiaSharp;

namespace Imaging.Tests;

public sealed class ImageImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Audition Image Import Tests",
        Guid.NewGuid().ToString("N"));

    public ImageImportServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Png_normalizes_exact_rgba_alpha_stride_and_owned_buffer()
    {
        var path = WriteEncoded("RGBA màu.png", SKEncodedImageFormat.Png, 2, 2, new[]
        {
            new SKColor(255, 0, 0, 255),
            new SKColor(0, 255, 0, 255),
            new SKColor(0, 0, 255, 255),
            new SKColor(20, 30, 40, 0)
        });
        var sourceHash = Hash(path);

        var result = await CreateService().ImportAsync(new(path));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        var image = result.Image!;
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(8, image.Stride);
        Assert.Equal(16, image.Pixels.Length);
        Assert.Equal(InternalImagePixelFormat.Rgba8Straight, image.PixelFormat);
        Assert.True(image.HasAlpha);
        Assert.Equal(ImageSourceFormat.Png, image.SourceMetadata.Format);
        Assert.Equal(new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            20, 30, 40, 0
        }, image.Pixels);
        Assert.Equal(sourceHash, Hash(path));

        var ddsImage = DdsRgbaImage.Create(image);
        Assert.Equal(image.Pixels, ddsImage.Pixels);
        Assert.Equal(DdsImagePixelFormat.Rgba8, ddsImage.PixelFormat);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, ImageSourceFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp, ImageSourceFormat.WebP)]
    public async Task Lossy_formats_decode_red_without_channel_swap_and_with_opaque_alpha(
        SKEncodedImageFormat encodedFormat,
        ImageSourceFormat expectedFormat)
    {
        var path = WriteEncoded($"solid {encodedFormat}.bin", encodedFormat, 8, 8, [new SKColor(240, 10, 5, 255)]);

        var result = await CreateService().ImportAsync(new(path));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(expectedFormat, result.Image!.SourceMetadata.Format);
        Assert.False(result.Image.HasAlpha);
        Assert.All(Enumerable.Range(0, 64), index =>
        {
            var offset = index * 4;
            Assert.InRange(result.Image.Pixels[offset], (byte)210, byte.MaxValue);
            Assert.InRange(result.Image.Pixels[offset + 1], (byte)0, (byte)35);
            Assert.InRange(result.Image.Pixels[offset + 2], (byte)0, (byte)35);
            Assert.Equal(byte.MaxValue, result.Image.Pixels[offset + 3]);
        });
    }

    [Fact]
    public async Task Bmp_rgb_is_normalized_to_rgba_with_opaque_alpha()
    {
        var path = Path.Combine(_root, "source.bmp");
        File.WriteAllBytes(path, CreateBmp24(3, 1, new[]
        {
            new SKColor(255, 0, 0),
            new SKColor(0, 255, 0),
            new SKColor(0, 0, 255)
        }));

        var result = await CreateService().ImportAsync(new(path));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(ImageSourceFormat.Bmp, result.Image!.SourceMetadata.Format);
        Assert.Equal(new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255
        }, result.Image.Pixels);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(137, 512)]
    [InlineData(6000, 1801)]
    [InlineData(4000, 4000)]
    public async Task Import_preserves_exact_npot_dimensions_without_resize(int width, int height)
    {
        var path = WriteEncoded($"{width} x {height}.png", SKEncodedImageFormat.Png, width, height, [SKColors.CornflowerBlue]);

        var result = await CreateService().ImportAsync(new(path));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(width, result.Image!.Width);
        Assert.Equal(height, result.Image.Height);
        Assert.Equal(checked(width * 4), result.Image.Stride);
        Assert.Equal(checked(width * height * 4), result.Image.Pixels.Length);
    }

    [Fact]
    public async Task Actual_signature_is_authoritative_over_extension()
    {
        var path = WriteEncoded("misleading.jpg", SKEncodedImageFormat.Png, 1, 1, [SKColors.Red]);

        var result = await CreateService().ImportAsync(new(path));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(ImageSourceFormat.Png, result.Image!.SourceMetadata.Format);
    }

    [Fact]
    public async Task Jpeg_exif_orientation_is_normalized_into_pixels_and_dimensions()
    {
        var jpeg = Encode(SKEncodedImageFormat.Jpeg, 2, 1, [SKColors.Red, SKColors.Blue]);
        var path = WriteBytes("oriented.jpg", AddExifOrientation(jpeg, orientation: 6));

        var result = await CreateService().ImportAsync(new(path));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(2, result.Image!.SourceMetadata.OriginalWidth);
        Assert.Equal(1, result.Image.SourceMetadata.OriginalHeight);
        Assert.Equal(ImageSourceOrientation.Rotate90, result.Image.SourceMetadata.OriginalOrientation);
        Assert.True(result.Image.SourceMetadata.OrientationNormalized);
        Assert.Equal(1, result.Image.Width);
        Assert.Equal(2, result.Image.Height);
    }

    [Fact]
    public async Task Missing_empty_random_and_truncated_sources_fail_structurally()
    {
        var service = CreateService();
        var missing = await service.ImportAsync(new(Path.Combine(_root, "missing.png")));
        var emptyPath = WriteBytes("empty.png", []);
        var empty = await service.ImportAsync(new(emptyPath));
        var random = await service.ImportAsync(new(WriteBytes("random.png", [1, 2, 3, 4, 5])));
        var png = Encode(SKEncodedImageFormat.Png, 2, 2, [SKColors.Red]);
        var truncatedPng = await service.ImportAsync(new(WriteBytes("truncated.png", png[..20])));
        var jpeg = Encode(SKEncodedImageFormat.Jpeg, 2, 2, [SKColors.Red]);
        var truncatedJpeg = await service.ImportAsync(new(WriteBytes("truncated.jpg", jpeg[..20])));

        Assert.Equal(ImageImportFailureReason.SourceMissing, missing.FailureReason);
        Assert.Equal(ImageImportFailureReason.InvalidImage, empty.FailureReason);
        Assert.Equal(ImageImportFailureReason.UnsupportedFormat, random.FailureReason);
        Assert.False(truncatedPng.Succeeded);
        Assert.Contains(truncatedPng.FailureReason, new[] { ImageImportFailureReason.InvalidImage, ImageImportFailureReason.DecodeFailed });
        Assert.False(truncatedJpeg.Succeeded);
        Assert.Contains(truncatedJpeg.FailureReason, new[] { ImageImportFailureReason.InvalidImage, ImageImportFailureReason.DecodeFailed });
    }

    [Fact]
    public async Task Resource_policy_rejects_decoded_dimensions_before_pixel_allocation()
    {
        var path = WriteEncoded("bomb-shaped.png", SKEncodedImageFormat.Png, 20, 20, [SKColors.Red]);
        var policy = new ImageImportResourcePolicy(1024 * 1024, 100, 100, 400);

        var result = await new ImageImportService(policy).ImportAsync(new(path));

        Assert.Equal(ImageImportFailureReason.ResourceLimitExceeded, result.FailureReason);
        Assert.Null(result.Image);
    }

    [Fact]
    public async Task Cancellation_is_structured_and_two_imports_are_independent()
    {
        var firstPath = WriteEncoded("first.png", SKEncodedImageFormat.Png, 4, 4, [SKColors.Red]);
        var secondPath = WriteEncoded("second.webp", SKEncodedImageFormat.Webp, 4, 4, [SKColors.Blue]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService();

        var cancelled = await service.ImportAsync(new(firstPath), cancellation.Token);
        var results = await Task.WhenAll(
            service.ImportAsync(new(firstPath)),
            service.ImportAsync(new(secondPath)));

        Assert.True(cancelled.Cancelled);
        Assert.Equal(ImageImportFailureReason.Cancelled, cancelled.FailureReason);
        Assert.All(results, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        Assert.Equal(ImageSourceFormat.Png, results[0].Image!.SourceMetadata.Format);
        Assert.Equal(ImageSourceFormat.WebP, results[1].Image!.SourceMetadata.Format);
    }

    [Fact]
    public void Internal_image_defensively_copies_input_and_validates_shape()
    {
        var pixels = new byte[] { 1, 2, 3, 4 };
        var metadata = new ImageSourceMetadata(
            ImageSourceFormat.Png,
            1,
            1,
            ImageSourceOrientation.Normal,
            true,
            false);
        var image = new InternalImage(1, 1, 4, pixels, metadata);
        pixels[0] = 99;

        Assert.Equal(1, image.Pixels[0]);
        Assert.Throws<ArgumentException>(() => new InternalImage(1, 1, 8, new byte[8], metadata));
        Assert.Throws<ArgumentException>(() => new InternalImage(1, 1, 4, new byte[3], metadata));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ImageImportService CreateService() => new(ImageImportResourcePolicy.Default);

    private string WriteEncoded(string fileName, SKEncodedImageFormat format, int width, int height, SKColor[] colors) =>
        WriteBytes(fileName, Encode(format, width, height, colors));

    private string WriteBytes(string fileName, byte[] bytes)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Encode(SKEncodedImageFormat format, int width, int height, SKColor[] colors)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, colors[(y * width + x) % colors.Length]);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        Assert.NotNull(data);
        return data.ToArray();
    }

    private static byte[] CreateBmp24(int width, int height, SKColor[] colors)
    {
        var rowSize = (width * 3 + 3) & ~3;
        var bytes = new byte[54 + rowSize * height];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(rowSize * height).CopyTo(bytes, 34);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = colors[(y * width + x) % colors.Length];
                var offset = 54 + (height - 1 - y) * rowSize + x * 3;
                bytes[offset] = color.Blue;
                bytes[offset + 1] = color.Green;
                bytes[offset + 2] = color.Red;
            }
        }

        return bytes;
    }

    private static byte[] AddExifOrientation(byte[] jpeg, ushort orientation)
    {
        Assert.True(jpeg.AsSpan(0, 2).SequenceEqual(new byte[] { 0xff, 0xd8 }));
        var payload = new byte[32];
        "Exif\0\0"u8.CopyTo(payload);
        payload[6] = (byte)'I';
        payload[7] = (byte)'I';
        BitConverter.GetBytes((ushort)42).CopyTo(payload, 8);
        BitConverter.GetBytes(8u).CopyTo(payload, 10);
        BitConverter.GetBytes((ushort)1).CopyTo(payload, 14);
        BitConverter.GetBytes((ushort)0x0112).CopyTo(payload, 16);
        BitConverter.GetBytes((ushort)3).CopyTo(payload, 18);
        BitConverter.GetBytes(1u).CopyTo(payload, 20);
        BitConverter.GetBytes(orientation).CopyTo(payload, 24);
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xff;
        segment[1] = 0xe1;
        segment[2] = 0;
        segment[3] = checked((byte)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        var result = new byte[jpeg.Length + segment.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);
        segment.CopyTo(result, 2);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + segment.Length));
        return result;
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
