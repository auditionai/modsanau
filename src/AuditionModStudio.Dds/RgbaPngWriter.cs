using System.Buffers.Binary;
using System.IO.Compression;
using AuditionModStudio.Core.Dds;

namespace AuditionModStudio.Dds;

internal static class RgbaPngWriter
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task WriteAsync(
        string path,
        DdsRgbaImage image,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(Signature.ToArray(), cancellationToken).ConfigureAwait(false);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), checked((uint)image.Width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), checked((uint)image.Height));
        ihdr[8] = 8;
        ihdr[9] = 6;
        await WriteChunkAsync(output, "IHDR"u8.ToArray(), ihdr, cancellationToken).ConfigureAwait(false);

        await using var compressed = new MemoryStream();
        await using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var filter = new byte[] { 0 };
            for (var y = 0; y < image.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await zlib.WriteAsync(filter, cancellationToken).ConfigureAwait(false);
                var offset = checked(y * image.Stride);
                await zlib.WriteAsync(
                    image.Pixels.AsMemory().Slice(offset, checked(image.Width * 4)),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteChunkAsync(output, "IDAT"u8.ToArray(), compressed.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        await WriteChunkAsync(output, "IEND"u8.ToArray(), [], cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task WriteChunkAsync(
        Stream output,
        byte[] type,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        await output.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(type, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        BinaryPrimitives.WriteUInt32BigEndian(length, ComputeCrc32(type, data));
        await output.WriteAsync(length, cancellationToken).ConfigureAwait(false);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        Update(type);
        Update(data);
        return ~crc;

        void Update(ReadOnlySpan<byte> values)
        {
            foreach (var value in values)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
                }
            }
        }
    }
}
