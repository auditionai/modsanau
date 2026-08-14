using System.Buffers.Binary;
using System.Text;

namespace Dds.Tests;

internal static class SyntheticDds
{
    public static byte[] Legacy(
        string fourCc = "DXT5",
        uint width = 64,
        uint height = 32,
        uint mipMapCount = 1,
        uint headerSize = 124,
        uint pixelFormatSize = 32,
        bool uncompressed = false,
        uint alphaMask = 0,
        uint redMask = 0x000000ff,
        uint greenMask = 0x0000ff00,
        uint blueMask = 0x00ff0000)
    {
        var bytes = new byte[129];
        Write(bytes, 0, FourCc("DDS "));
        Write(bytes, 4, headerSize);
        Write(bytes, 8, 0x0002100f);
        Write(bytes, 12, height);
        Write(bytes, 16, width);
        Write(bytes, 28, mipMapCount);
        Write(bytes, 76, pixelFormatSize);
        Write(bytes, 80, uncompressed ? (alphaMask == 0 ? 0x40u : 0x41u) : 0x4u);
        Write(bytes, 84, uncompressed ? 0 : FourCc(fourCc));
        if (uncompressed)
        {
            Write(bytes, 88, 32);
            Write(bytes, 92, redMask);
            Write(bytes, 96, greenMask);
            Write(bytes, 100, blueMask);
            Write(bytes, 104, alphaMask);
        }

        Write(bytes, 108, 0x1000);
        return bytes;
    }

    public static byte[] Dx10(uint dxgiFormat, uint width = 64, uint height = 32, uint arraySize = 1)
    {
        var bytes = Legacy("DX10", width, height);
        Array.Resize(ref bytes, 149);
        Write(bytes, 128, dxgiFormat);
        Write(bytes, 132, 3);
        Write(bytes, 140, arraySize);
        return bytes;
    }

    public static uint FourCc(string value) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(value));

    private static void Write(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
