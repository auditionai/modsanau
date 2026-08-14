using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Images;
using SkiaSharp;

namespace AuditionModStudio.Imaging;

public sealed class AiTransportImageEncoder : IAiTransportImageEncoder
{
    public Task<byte[]?> EncodePngAsync(InternalImage image, CancellationToken cancellationToken = default) =>
        Task.FromResult(Encode(image.Width, image.Height, image.Pixels.AsSpan(), cancellationToken));

    public Task<byte[]?> EncodeMaskPngAsync(AiMask mask, CancellationToken cancellationToken = default)
    {
        var pixels = new byte[checked(mask.Width * mask.Height * 4)];
        for (var index = 0; index < mask.Opacity.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = index * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = mask.Opacity[index];
            pixels[offset + 3] = 255;
        }
        return Task.FromResult(Encode(mask.Width, mask.Height, pixels, cancellationToken));
    }

    private static byte[]? Encode(int width, int height, ReadOnlySpan<byte> pixels,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var colorSpace = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul, colorSpace);
        using var bitmap = new SKBitmap(info);
        pixels.CopyTo(bitmap.GetPixelSpan());
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
