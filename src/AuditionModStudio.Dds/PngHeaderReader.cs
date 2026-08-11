using System.Buffers.Binary;

namespace AuditionModStudio.Dds;

internal static class PngHeaderReader
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task<(int Width, int Height)?> ReadDimensionsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var header = new byte[24];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var offset = 0;
        while (offset < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return null;
            }

            offset = checked(offset + count);
        }

        if (!header.AsSpan(0, 8).SequenceEqual(Signature)
            || !header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return null;
        }

        return (checked((int)width), checked((int)height));
    }
}
