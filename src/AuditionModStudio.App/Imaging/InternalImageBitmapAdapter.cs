using System.Runtime.InteropServices.WindowsRuntime;
using AuditionModStudio.Core.Images;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AuditionModStudio.App.Imaging;

public static class InternalImageBitmapAdapter
{
    public static WriteableBitmap CreateBitmap(InternalImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bitmap = new WriteableBitmap(source.Width, source.Height);
        var rgba = source.Pixels.AsSpan();
        var row = new byte[source.Stride];
        using var stream = bitmap.PixelBuffer.AsStream();
        for (var y = 0; y < source.Height; y++)
        {
            var sourceRow = rgba.Slice(y * source.Stride, source.Stride);
            for (var index = 0; index < sourceRow.Length; index += 4)
            {
                var alpha = sourceRow[index + 3];
                row[index] = Premultiply(sourceRow[index + 2], alpha);
                row[index + 1] = Premultiply(sourceRow[index + 1], alpha);
                row[index + 2] = Premultiply(sourceRow[index], alpha);
                row[index + 3] = alpha;
            }

            stream.Write(row, 0, row.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    private static byte Premultiply(byte channel, byte alpha) =>
        (byte)((channel * alpha + 127) / 255);
}
