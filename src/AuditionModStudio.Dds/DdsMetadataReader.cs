using System.Buffers.Binary;
using System.Text;
using AuditionModStudio.Core.Dds;

namespace AuditionModStudio.Dds;

public sealed class DdsMetadataReader : IDdsMetadataReader
{
    private const int LegacyHeaderLength = 128;
    private const int Dx10HeaderLength = 148;
    private const uint DdsMagic = 0x20534444;
    private const uint FourCcFlag = 0x4;
    private const uint RgbFlag = 0x40;
    private const uint AlphaPixelsFlag = 0x1;
    private const uint CubemapCaps2 = 0x200;
    private const uint VolumeCaps2 = 0x200000;
    private const uint TextureCubeMiscFlag = 0x4;

    public async Task<DdsMetadataReadResult> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(DdsMetadataFailureReason.Cancelled, "DDS_READ_CANCELLED");
        }

        if (!File.Exists(path))
        {
            return Failure(DdsMetadataFailureReason.FileMissing, "DDS_FILE_MISSING");
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var fileLength = stream.Length;
            var bytesToRead = (int)Math.Min(fileLength, Dx10HeaderLength);
            var header = new byte[Dx10HeaderLength];
            var bytesRead = 0;

            while (bytesRead < bytesToRead)
            {
                var count = await stream.ReadAsync(
                    header.AsMemory(bytesRead, bytesToRead - bytesRead),
                    cancellationToken);
                if (count == 0)
                {
                    break;
                }

                bytesRead = checked(bytesRead + count);
            }

            return Parse(header.AsSpan(0, bytesRead), fileLength);
        }
        catch (OperationCanceledException)
        {
            return Failure(DdsMetadataFailureReason.Cancelled, "DDS_READ_CANCELLED");
        }
        catch (IOException)
        {
            return Failure(DdsMetadataFailureReason.IoError, "DDS_IO_ERROR");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(DdsMetadataFailureReason.IoError, "DDS_ACCESS_DENIED");
        }
    }

    private static DdsMetadataReadResult Parse(ReadOnlySpan<byte> header, long fileLength)
    {
        if (header.Length < sizeof(uint))
        {
            return Failure(DdsMetadataFailureReason.TruncatedHeader, "DDS_TRUNCATED_MAGIC");
        }

        if (ReadUInt32(header, 0) != DdsMagic)
        {
            return Failure(DdsMetadataFailureReason.InvalidMagic, "DDS_INVALID_MAGIC");
        }

        if (header.Length < LegacyHeaderLength)
        {
            return Failure(DdsMetadataFailureReason.TruncatedHeader, "DDS_TRUNCATED_LEGACY_HEADER");
        }

        if (ReadUInt32(header, 4) != 124)
        {
            return Failure(DdsMetadataFailureReason.InvalidHeaderSize, "DDS_INVALID_HEADER_SIZE");
        }

        if (ReadUInt32(header, 76) != 32)
        {
            return Failure(
                DdsMetadataFailureReason.InvalidPixelFormatHeader,
                "DDS_INVALID_PIXEL_FORMAT_SIZE");
        }

        var width = ReadUInt32(header, 16);
        var height = ReadUInt32(header, 12);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return Failure(DdsMetadataFailureReason.InvalidDimensions, "DDS_INVALID_DIMENSIONS");
        }

        var pixelFormatFlags = ReadUInt32(header, 80);
        var fourCcValue = ReadUInt32(header, 84);
        var hasFourCc = (pixelFormatFlags & FourCcFlag) != 0;
        var fourCc = hasFourCc ? ToFourCc(fourCcValue) : null;
        var isDx10 = hasFourCc && fourCcValue == MakeFourCc("DX10");
        var pixelDataOffset = isDx10 ? Dx10HeaderLength : LegacyHeaderLength;

        if (fileLength < pixelDataOffset)
        {
            return Failure(
                isDx10 ? DdsMetadataFailureReason.InvalidDx10Header : DdsMetadataFailureReason.TruncatedHeader,
                isDx10 ? "DDS_TRUNCATED_DX10_HEADER" : "DDS_TRUNCATED_LEGACY_HEADER");
        }

        if (isDx10 && header.Length < Dx10HeaderLength)
        {
            return Failure(DdsMetadataFailureReason.InvalidDx10Header, "DDS_TRUNCATED_DX10_HEADER");
        }

        var caps2 = ReadUInt32(header, 112);
        var declaredMips = ReadUInt32(header, 28);
        var rawDepth = ReadUInt32(header, 24);
        if (rawDepth > int.MaxValue)
        {
            return Failure(DdsMetadataFailureReason.CorruptHeader, "DDS_INVALID_DEPTH");
        }

        var depth = rawDepth == 0 ? null : (int?)rawDepth;
        var formatInfo = isDx10
            ? ParseDx10Format(ReadUInt32(header, 128))
            : ParseLegacyFormat(header, pixelFormatFlags, fourCcValue, hasFourCc);

        var resourceDimension = isDx10
            ? ParseResourceDimension(ReadUInt32(header, 132))
            : (caps2 & VolumeCaps2) != 0
                ? DdsResourceDimension.Texture3D
                : DdsResourceDimension.Texture2D;
        var miscFlag = isDx10 ? ReadUInt32(header, 136) : 0;
        var arraySize = isDx10 ? ReadUInt32(header, 140) : 1;
        if (isDx10 && arraySize == 0)
        {
            return Failure(DdsMetadataFailureReason.InvalidDx10Header, "DDS_INVALID_DX10_ARRAY_SIZE");
        }

        var isCubemap = (caps2 & CubemapCaps2) != 0 || (miscFlag & TextureCubeMiscFlag) != 0;
        var metadata = new DdsMetadata(
            checked((int)width),
            checked((int)height),
            depth,
            declaredMips,
            declaredMips == 0 ? 1u : declaredMips,
            formatInfo.Format,
            formatInfo.Support,
            fourCc,
            isDx10 ? ReadUInt32(header, 128) : null,
            isDx10 ? DdsHeaderType.Dx10 : DdsHeaderType.Legacy,
            formatInfo.SupportsAlpha,
            formatInfo.HasAlphaChannel,
            formatInfo.AlphaMode,
            formatInfo.ColorSpace,
            resourceDimension,
            isCubemap,
            arraySize,
            fileLength,
            pixelDataOffset);

        return DdsMetadataReadResult.Success(metadata);
    }

    private static FormatInfo ParseLegacyFormat(
        ReadOnlySpan<byte> header,
        uint pixelFormatFlags,
        uint fourCc,
        bool hasFourCc)
    {
        if (hasFourCc)
        {
            if (fourCc == MakeFourCc("DXT1"))
            {
                return new(DdsFormat.BC1, DdsFormatSupport.Known, true, false, DdsAlphaMode.PossibleOneBit, DdsColorSpace.Unknown);
            }

            if (fourCc == MakeFourCc("DXT3"))
            {
                return Alpha(DdsFormat.BC2, DdsAlphaMode.Explicit, DdsColorSpace.Unknown);
            }

            if (fourCc == MakeFourCc("DXT5"))
            {
                return Alpha(DdsFormat.BC3, DdsAlphaMode.Interpolated, DdsColorSpace.Unknown);
            }

            if (fourCc is var value &&
                (value == MakeFourCc("ATI1") || value == MakeFourCc("BC4U") || value == MakeFourCc("BC4S")))
            {
                return Opaque(DdsFormat.BC4, DdsColorSpace.Unknown);
            }

            if (fourCc is var secondValue &&
                (secondValue == MakeFourCc("ATI2") || secondValue == MakeFourCc("BC5U") || secondValue == MakeFourCc("BC5S")))
            {
                return Opaque(DdsFormat.BC5, DdsColorSpace.Unknown);
            }

            return new(DdsFormat.Unknown, DdsFormatSupport.Unknown, false, false, DdsAlphaMode.Unknown, DdsColorSpace.Unknown);
        }

        if ((pixelFormatFlags & RgbFlag) == 0)
        {
            return new(DdsFormat.Unknown, DdsFormatSupport.Unsupported, false, false, DdsAlphaMode.Unknown, DdsColorSpace.Unknown);
        }

        var bits = ReadUInt32(header, 88);
        var redMask = ReadUInt32(header, 92);
        var greenMask = ReadUInt32(header, 96);
        var blueMask = ReadUInt32(header, 100);
        var alphaMask = ReadUInt32(header, 104);
        var hasAlpha = (pixelFormatFlags & AlphaPixelsFlag) != 0 && alphaMask != 0;
        var format = bits == 32 && redMask == 0x000000ff && greenMask == 0x0000ff00 && blueMask == 0x00ff0000
            ? DdsFormat.Rgba8
            : bits == 32 && redMask == 0x00ff0000 && greenMask == 0x0000ff00 && blueMask == 0x000000ff
                ? DdsFormat.Bgra8
                : DdsFormat.Uncompressed;

        return new(
            format,
            format == DdsFormat.Uncompressed ? DdsFormatSupport.Unsupported : DdsFormatSupport.Known,
            hasAlpha,
            hasAlpha,
            hasAlpha ? DdsAlphaMode.Channel : DdsAlphaMode.None,
            DdsColorSpace.Unknown);
    }

    private static FormatInfo ParseDx10Format(uint dxgiFormat) => dxgiFormat switch
    {
        28 => Alpha(DdsFormat.Rgba8, DdsAlphaMode.Channel, DdsColorSpace.Linear),
        29 => Alpha(DdsFormat.Rgba8, DdsAlphaMode.Channel, DdsColorSpace.Srgb),
        71 => Bc1(DdsColorSpace.Linear),
        72 => Bc1(DdsColorSpace.Srgb),
        74 => Alpha(DdsFormat.BC2, DdsAlphaMode.Explicit, DdsColorSpace.Linear),
        75 => Alpha(DdsFormat.BC2, DdsAlphaMode.Explicit, DdsColorSpace.Srgb),
        77 => Alpha(DdsFormat.BC3, DdsAlphaMode.Interpolated, DdsColorSpace.Linear),
        78 => Alpha(DdsFormat.BC3, DdsAlphaMode.Interpolated, DdsColorSpace.Srgb),
        80 or 81 => Opaque(DdsFormat.BC4, DdsColorSpace.Linear),
        83 or 84 => Opaque(DdsFormat.BC5, DdsColorSpace.Linear),
        87 => Alpha(DdsFormat.Bgra8, DdsAlphaMode.Channel, DdsColorSpace.Linear),
        91 => Alpha(DdsFormat.Bgra8, DdsAlphaMode.Channel, DdsColorSpace.Srgb),
        95 or 96 => Opaque(DdsFormat.BC6H, DdsColorSpace.Linear),
        98 => Alpha(DdsFormat.BC7, DdsAlphaMode.Channel, DdsColorSpace.Linear),
        99 => Alpha(DdsFormat.BC7, DdsAlphaMode.Channel, DdsColorSpace.Srgb),
        _ => new(DdsFormat.Unknown, DdsFormatSupport.Unknown, false, false, DdsAlphaMode.Unknown, DdsColorSpace.Unknown)
    };

    private static DdsResourceDimension ParseResourceDimension(uint value) => value switch
    {
        2 => DdsResourceDimension.Texture1D,
        3 => DdsResourceDimension.Texture2D,
        4 => DdsResourceDimension.Texture3D,
        _ => DdsResourceDimension.Unknown
    };

    private static FormatInfo Bc1(DdsColorSpace colorSpace) =>
        new(DdsFormat.BC1, DdsFormatSupport.Known, true, false, DdsAlphaMode.PossibleOneBit, colorSpace);

    private static FormatInfo Alpha(DdsFormat format, DdsAlphaMode alphaMode, DdsColorSpace colorSpace) =>
        new(format, DdsFormatSupport.Known, true, true, alphaMode, colorSpace);

    private static FormatInfo Opaque(DdsFormat format, DdsColorSpace colorSpace) =>
        new(format, DdsFormatSupport.Known, false, false, DdsAlphaMode.None, colorSpace);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

    private static uint MakeFourCc(string value) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(value));

    private static string ToFourCc(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return Encoding.ASCII.GetString(bytes);
    }

    private static DdsMetadataReadResult Failure(DdsMetadataFailureReason reason, string errorCode) =>
        DdsMetadataReadResult.Failure(reason, errorCode);

    private sealed record FormatInfo(
        DdsFormat Format,
        DdsFormatSupport Support,
        bool SupportsAlpha,
        bool HasAlphaChannel,
        DdsAlphaMode AlphaMode,
        DdsColorSpace ColorSpace);
}
