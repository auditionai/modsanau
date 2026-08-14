using System.Security.Cryptography;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Dds;

namespace Dds.Tests;

public sealed class DdsMetadataReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "Audition Mod Studio DDS Tests",
        Guid.NewGuid().ToString("N"));
    private readonly DdsMetadataReader _reader = new();

    public DdsMetadataReaderTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("DXT1", DdsFormat.BC1)]
    [InlineData("DXT3", DdsFormat.BC2)]
    [InlineData("DXT5", DdsFormat.BC3)]
    [InlineData("ATI1", DdsFormat.BC4)]
    [InlineData("BC4U", DdsFormat.BC4)]
    [InlineData("BC4S", DdsFormat.BC4)]
    [InlineData("ATI2", DdsFormat.BC5)]
    [InlineData("BC5U", DdsFormat.BC5)]
    [InlineData("BC5S", DdsFormat.BC5)]
    public async Task Known_legacy_fourcc_maps_to_semantic_format(string fourCc, DdsFormat expected)
    {
        var result = await ReadAsync(SyntheticDds.Legacy(fourCc));

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Metadata!.Format);
        Assert.Equal(fourCc, result.Metadata.FourCC);
        Assert.Equal(DdsFormatSupport.Known, result.Metadata.FormatSupport);
    }

    [Theory]
    [InlineData(98u, DdsFormat.BC7, DdsColorSpace.Linear)]
    [InlineData(99u, DdsFormat.BC7, DdsColorSpace.Srgb)]
    [InlineData(95u, DdsFormat.BC6H, DdsColorSpace.Linear)]
    [InlineData(96u, DdsFormat.BC6H, DdsColorSpace.Linear)]
    public async Task Dx10_formats_are_recognized(uint dxgiFormat, DdsFormat format, DdsColorSpace colorSpace)
    {
        var result = await ReadAsync(SyntheticDds.Dx10(dxgiFormat));

        Assert.True(result.IsSuccess);
        Assert.Equal(DdsHeaderType.Dx10, result.Metadata!.HeaderType);
        Assert.Equal(dxgiFormat, result.Metadata.DxgiFormat);
        Assert.Equal(format, result.Metadata.Format);
        Assert.Equal(colorSpace, result.Metadata.ColorSpace);
        Assert.Equal(148, result.Metadata.PixelDataOffset);
    }

    [Theory]
    [InlineData(6000u, 1801u)]
    [InlineData(137u, 512u)]
    public async Task Unusual_non_power_of_two_dimensions_are_preserved(uint width, uint height)
    {
        var result = await ReadAsync(SyntheticDds.Legacy(width: width, height: height));

        Assert.True(result.IsSuccess);
        Assert.Equal((int)width, result.Metadata!.Width);
        Assert.Equal((int)height, result.Metadata.Height);
    }

    [Fact]
    public async Task Invalid_magic_is_rejected_before_header_parsing()
    {
        var bytes = SyntheticDds.Legacy();
        bytes[0] = 0;

        var result = await ReadAsync(bytes);

        AssertFailure(result, DdsMetadataFailureReason.InvalidMagic);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(127)]
    public async Task Truncated_magic_or_header_is_structured_failure(int length)
    {
        var result = await ReadAsync(SyntheticDds.Legacy().AsSpan(0, length).ToArray());

        AssertFailure(result, DdsMetadataFailureReason.TruncatedHeader);
    }

    [Fact]
    public async Task Wrong_header_size_is_rejected()
    {
        var result = await ReadAsync(SyntheticDds.Legacy(headerSize: 123));

        AssertFailure(result, DdsMetadataFailureReason.InvalidHeaderSize);
    }

    [Fact]
    public async Task Wrong_pixel_format_size_is_rejected()
    {
        var result = await ReadAsync(SyntheticDds.Legacy(pixelFormatSize: 31));

        AssertFailure(result, DdsMetadataFailureReason.InvalidPixelFormatHeader);
    }

    [Fact]
    public async Task Mipmap_declared_and_effective_semantics_are_explicit()
    {
        var declared = await ReadAsync(SyntheticDds.Legacy(mipMapCount: 7));
        var implicitBase = await ReadAsync(SyntheticDds.Legacy(mipMapCount: 0));

        Assert.Equal(7u, declared.Metadata!.DeclaredMipMapCount);
        Assert.Equal(7u, declared.Metadata.EffectiveMipLevelCount);
        Assert.Equal(0u, implicitBase.Metadata!.DeclaredMipMapCount);
        Assert.Equal(1u, implicitBase.Metadata.EffectiveMipLevelCount);
    }

    [Theory]
    [InlineData("DXT1", true, false, DdsAlphaMode.PossibleOneBit)]
    [InlineData("DXT3", true, true, DdsAlphaMode.Explicit)]
    [InlineData("DXT5", true, true, DdsAlphaMode.Interpolated)]
    public async Task Alpha_metadata_describes_capability_not_pixel_usage(
        string fourCc,
        bool supportsAlpha,
        bool hasAlphaChannel,
        DdsAlphaMode alphaMode)
    {
        var result = await ReadAsync(SyntheticDds.Legacy(fourCc));

        Assert.Equal(supportsAlpha, result.Metadata!.SupportsAlpha);
        Assert.Equal(hasAlphaChannel, result.Metadata.HasAlphaChannel);
        Assert.Equal(alphaMode, result.Metadata.AlphaMode);
    }

    [Fact]
    public async Task Uncompressed_a_mask_is_reported_as_alpha_channel()
    {
        var result = await ReadAsync(SyntheticDds.Legacy(uncompressed: true, alphaMask: 0xff000000));

        Assert.True(result.IsSuccess);
        Assert.Equal(DdsFormat.Rgba8, result.Metadata!.Format);
        Assert.True(result.Metadata.HasAlphaChannel);
        Assert.Equal(DdsAlphaMode.Channel, result.Metadata.AlphaMode);
    }

    [Fact]
    public async Task Legacy_offset_and_file_length_are_recorded()
    {
        var bytes = SyntheticDds.Legacy();
        var result = await ReadAsync(bytes);

        Assert.Equal(128, result.Metadata!.PixelDataOffset);
        Assert.Equal(bytes.LongLength, result.Metadata.FileLength);
    }

    [Fact]
    public async Task Unknown_fourcc_returns_safe_unknown_metadata()
    {
        var result = await ReadAsync(SyntheticDds.Legacy("ZZZZ"));

        Assert.True(result.IsSuccess);
        Assert.Equal(DdsFormat.Unknown, result.Metadata!.Format);
        Assert.Equal(DdsFormatSupport.Unknown, result.Metadata.FormatSupport);
    }

    [Theory]
    [InlineData(0u, 64u)]
    [InlineData(64u, 0u)]
    [InlineData(uint.MaxValue, 64u)]
    public async Task Invalid_dimensions_are_rejected_without_allocating_from_them(uint width, uint height)
    {
        var result = await ReadAsync(SyntheticDds.Legacy(width: width, height: height));

        AssertFailure(result, DdsMetadataFailureReason.InvalidDimensions);
    }

    [Fact]
    public async Task Cancellation_returns_structured_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var path = Write(SyntheticDds.Legacy(), "cancel.dds");

        var result = await _reader.ReadAsync(path, cancellation.Token);

        AssertFailure(result, DdsMetadataFailureReason.Cancelled);
    }

    [Fact]
    public async Task Unicode_and_space_path_is_supported_and_source_is_not_modified()
    {
        var bytes = SyntheticDds.Legacy();
        var path = Write(bytes, "ảnh texture có dấu.dds");
        byte[] before;
        await using (var beforeStream = File.OpenRead(path))
        {
            before = await SHA256.HashDataAsync(beforeStream);
        }

        var result = await _reader.ReadAsync(path);
        byte[] after;
        await using (var afterStream = File.OpenRead(path))
        {
            after = await SHA256.HashDataAsync(afterStream);
        }

        Assert.True(result.IsSuccess);
        Assert.Equal(before, after);
        Assert.Equal(bytes.LongLength, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Missing_file_returns_structured_failure()
    {
        var result = await _reader.ReadAsync(Path.Combine(_directory, "missing.dds"));

        AssertFailure(result, DdsMetadataFailureReason.FileMissing);
    }

    [Fact]
    public async Task Truncated_dx10_header_is_rejected()
    {
        var bytes = SyntheticDds.Dx10(98);
        Array.Resize(ref bytes, 147);

        var result = await ReadAsync(bytes);

        AssertFailure(result, DdsMetadataFailureReason.InvalidDx10Header);
    }

    [Fact]
    public async Task Invalid_dx10_array_size_is_rejected()
    {
        var result = await ReadAsync(SyntheticDds.Dx10(98, arraySize: 0));

        AssertFailure(result, DdsMetadataFailureReason.InvalidDx10Header);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<DdsMetadataReadResult> ReadAsync(byte[] bytes) =>
        await _reader.ReadAsync(Write(bytes, $"{Guid.NewGuid():N}.dds"));

    private string Write(byte[] bytes, string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AssertFailure(
        DdsMetadataReadResult result,
        DdsMetadataFailureReason expectedReason)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Metadata);
        Assert.Equal(expectedReason, result.FailureReason);
        Assert.NotNull(result.ErrorCode);
    }
}
