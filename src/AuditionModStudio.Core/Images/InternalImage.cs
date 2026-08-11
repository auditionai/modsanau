using System.Collections.Immutable;

namespace AuditionModStudio.Core.Images;

public enum InternalImagePixelFormat
{
    Rgba8Straight
}

public enum InternalImageColorSpace
{
    Srgb
}

public sealed class InternalImage
{
    public InternalImage(
        int width,
        int height,
        int stride,
        ReadOnlySpan<byte> pixels,
        ImageSourceMetadata sourceMetadata)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(sourceMetadata);

        var expectedStride = checked(width * 4);
        var expectedLength = checked(expectedStride * height);
        if (stride != expectedStride)
        {
            throw new ArgumentException("Internal RGBA8 rows must be tightly packed.", nameof(stride));
        }

        if (pixels.Length != expectedLength)
        {
            throw new ArgumentException("Pixel buffer length does not match image dimensions.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Stride = stride;
        Pixels = ImmutableArray.Create(pixels.ToArray());
        SourceMetadata = sourceMetadata;
        HasAlpha = HasNonOpaqueAlpha(Pixels.AsSpan());
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public InternalImagePixelFormat PixelFormat => InternalImagePixelFormat.Rgba8Straight;

    public InternalImageColorSpace ColorSpace => InternalImageColorSpace.Srgb;

    public ImmutableArray<byte> Pixels { get; }

    public bool HasAlpha { get; }

    public ImageSourceMetadata SourceMetadata { get; }

    private static bool HasNonOpaqueAlpha(ReadOnlySpan<byte> pixels)
    {
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != byte.MaxValue)
            {
                return true;
            }
        }

        return false;
    }
}
