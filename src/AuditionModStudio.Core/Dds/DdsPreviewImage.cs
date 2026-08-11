using System.Collections.Immutable;

namespace AuditionModStudio.Core.Dds;

public sealed record DdsPreviewImage
{
    public const string PngMediaType = "image/png";

    public DdsPreviewImage(int width, int height, IEnumerable<byte> encodedPng)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(encodedPng);

        Width = width;
        Height = height;
        EncodedPng = ImmutableArray.CreateRange(encodedPng);
        if (EncodedPng.IsEmpty)
        {
            throw new ArgumentException("Preview PNG must not be empty.", nameof(encodedPng));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public string MediaType => PngMediaType;

    public ImmutableArray<byte> EncodedPng { get; }
}
