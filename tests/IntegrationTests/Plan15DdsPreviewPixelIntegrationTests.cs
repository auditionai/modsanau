using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Dds;
using AuditionModStudio.Infrastructure.Paths;
using Xunit.Sdk;

namespace IntegrationTests;

public sealed class Plan15DdsPreviewPixelIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Audition PLAN 15 Pixel Preview",
        Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    public async Task Production_preview_preserves_rgba_bgra_bc_color_and_alpha()
    {
        var texconvPath = RequireApprovedTexconv();
        var workspace = CreateWorkspace();
        var service = CreateService(workspace, texconvPath);
        var fixtures = new[]
        {
            new PixelFixture(@"Extracted\rgba red.dds", CreateRgba(red: true, alpha: 255), 255, 0, 0, 255),
            new PixelFixture(@"Extracted\bgra red.dds", CreateBgra(red: true, alpha: 0), 255, 0, 0, 0),
            new PixelFixture(@"Extracted\bc1 red.dds", CreateBc1Red(), 255, 0, 0, 255),
            new PixelFixture(@"Extracted\bc3 red alpha.dds", CreateBc3Red(alpha: 64), 255, 0, 0, 64),
        };

        foreach (var fixture in fixtures)
        {
            var path = workspace.ResolveRelativePath(fixture.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, fixture.Bytes);
            var hashBefore = await ComputeHashAsync(path);

            var result = await service.CreateAsync(new(workspace, fixture.RelativePath));

            Assert.True(result.Succeeded, result.DiagnosticCode);
            var pixel = DecodeFirstPixel(result.Image!.EncodedPng.AsSpan());
            Assert.InRange(pixel.R, Math.Max(0, fixture.R - 3), Math.Min(255, fixture.R + 3));
            Assert.InRange(pixel.G, Math.Max(0, fixture.G - 3), Math.Min(255, fixture.G + 3));
            Assert.InRange(pixel.B, Math.Max(0, fixture.B - 3), Math.Min(255, fixture.B + 3));
            Assert.InRange(pixel.A, Math.Max(0, fixture.A - 3), Math.Min(255, fixture.A + 3));
            Assert.Equal(hashBefore, await ComputeHashAsync(path));
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Paths.BuildOutputDirectory));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    public async Task Production_preview_isolates_same_basename_concurrently()
    {
        var texconvPath = RequireApprovedTexconv();
        var workspace = CreateWorkspace();
        var service = CreateService(workspace, texconvPath);
        var first = @"Extracted\đường dẫn một\same.dds";
        var second = @"Extracted\path two\same.dds";
        foreach (var relative in new[] { first, second })
        {
            var path = workspace.ResolveRelativePath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, CreateRgba(red: true, alpha: 255));
        }

        var results = await Task.WhenAll(
            service.CreateAsync(new(workspace, first)),
            service.CreateAsync(new(workspace, second)));

        Assert.All(results, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Paths.BuildOutputDirectory));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    public async Task Production_encoder_roundtrips_all_real_formats_mips_alpha_and_channels()
    {
        var texconvPath = RequireApprovedTexconv();
        var workspace = CreateWorkspace();
        var preview = CreateService(workspace, texconvPath);
        var encoder = CreateEncoder(texconvPath);
        var cases = new[]
        {
            new EncodeCase(DdsFormat.BC1, 137, 512, 4, 255, DdsTargetAlphaSemantics.Opaque),
            new EncodeCase(DdsFormat.BC3, 4, 4, 1, 64, DdsTargetAlphaSemantics.Full),
            new EncodeCase(DdsFormat.Rgba8, 4, 4, 1, 64, DdsTargetAlphaSemantics.Full),
            new EncodeCase(DdsFormat.Bgra8, 4, 4, 1, 64, DdsTargetAlphaSemantics.Full),
        };

        foreach (var item in cases)
        {
            var result = await encoder.EncodeAsync(new(
                workspace,
                SolidRedImage(item.Width, item.Height, item.Alpha),
                new(
                    item.Width,
                    item.Height,
                    item.Format,
                    item.Mips,
                    DdsHeaderType.Legacy,
                    DdsColorSpace.Linear,
                    item.AlphaSemantics),
                $@"Encoded Output\{item.Format}.dds"));
            Assert.True(result.Succeeded, result.DiagnosticCode);
            Assert.Equal(item.Format, result.Metadata!.Format);
            Assert.Equal((uint)item.Mips, result.Metadata.EffectiveMipLevelCount);
            Assert.Equal(DdsHeaderType.Legacy, result.Metadata.HeaderType);

            var decoded = await preview.CreateAsync(new(workspace, result.OutputRelativePath!));
            Assert.True(decoded.Succeeded, decoded.DiagnosticCode);
            var pixel = DecodeFirstPixel(decoded.Image!.EncodedPng.AsSpan());
            Assert.InRange(pixel.R, (byte)250, byte.MaxValue);
            Assert.InRange(pixel.G, byte.MinValue, (byte)5);
            Assert.InRange(pixel.B, byte.MinValue, (byte)5);
            Assert.InRange(pixel.A, (byte)Math.Max(0, item.Alpha - 3), (byte)Math.Min(255, item.Alpha + 3));
        }

        var sharedImage = SolidRedImage(8, 8, 255);
        var sharedPixels = sharedImage.Pixels;
        var concurrentSettings = new DdsTargetSettings(
            8, 8, DdsFormat.Rgba8, 1, DdsHeaderType.Legacy, DdsColorSpace.Linear, DdsTargetAlphaSemantics.Opaque);
        var concurrent = await Task.WhenAll(
            encoder.EncodeAsync(new(workspace, sharedImage, concurrentSettings, @"đầu ra một\same.dds")),
            encoder.EncodeAsync(new(workspace, sharedImage, concurrentSettings, @"output two\same.dds")));
        Assert.All(concurrent, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        Assert.Equal(sharedPixels, sharedImage.Pixels);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    public async Task Production_encoder_preserves_6000x1801_and_supports_dx10_srgb()
    {
        var texconvPath = RequireApprovedTexconv();
        var workspace = CreateWorkspace();
        var encoder = CreateEncoder(texconvPath);
        var large = await encoder.EncodeAsync(new(
            workspace,
            SolidRedImage(6000, 1801, 255),
            new(6000, 1801, DdsFormat.BC3, 1, DdsHeaderType.Legacy, DdsColorSpace.Linear, DdsTargetAlphaSemantics.Full),
            "large 6000x1801.dds"));
        Assert.True(large.Succeeded, large.DiagnosticCode);
        Assert.Equal(6000, large.Metadata!.Width);
        Assert.Equal(1801, large.Metadata.Height);
        Assert.Equal(1u, large.Metadata.EffectiveMipLevelCount);

        var srgb = await encoder.EncodeAsync(new(
            workspace,
            SolidRedImage(256, 256, 255),
            new(256, 256, DdsFormat.Rgba8, 9, DdsHeaderType.Dx10, DdsColorSpace.Srgb, DdsTargetAlphaSemantics.Opaque),
            "dx10-srgb.dds"));
        Assert.True(srgb.Succeeded, srgb.DiagnosticCode);
        Assert.Equal(DdsHeaderType.Dx10, srgb.Metadata!.HeaderType);
        Assert.Equal(DdsColorSpace.Srgb, srgb.Metadata.ColorSpace);
        Assert.Equal(9u, srgb.Metadata.EffectiveMipLevelCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TestWorkspace CreateWorkspace()
    {
        var paths = new SecureWorkspacePaths(
            _root,
            Path.Combine(_root, "Working"),
            Path.Combine(_root, "Extracted"),
            Path.Combine(_root, "BuildOutput"));
        Directory.CreateDirectory(paths.WorkingDirectory);
        Directory.CreateDirectory(paths.ExtractedDirectory);
        Directory.CreateDirectory(paths.BuildOutputDirectory);
        return new(paths);
    }

    private static DdsPreviewService CreateService(TestWorkspace workspace, string texconvPath)
    {
        var pathSecurity = new PathSecurity();
        var metadataReader = new DdsMetadataReader();
        var harness = new DirectXTexEvaluationHarness(
            pathSecurity,
            metadataReader,
            DirectXTexEvaluationToolCatalog.May2026X64);
        return new(
            metadataReader,
            harness,
            pathSecurity,
            new DdsPreviewServiceOptions(
                texconvPath,
                TimeSpan.FromSeconds(30),
                DdsPreviewResourcePolicy.Default));
    }

    private static DdsEncoder CreateEncoder(string texconvPath)
    {
        var pathSecurity = new PathSecurity();
        var metadataReader = new DdsMetadataReader();
        var harness = new DirectXTexEvaluationHarness(
            pathSecurity,
            metadataReader,
            DirectXTexEvaluationToolCatalog.May2026X64);
        return new(
            metadataReader,
            harness,
            pathSecurity,
            new DdsEncoderOptions(texconvPath, TimeSpan.FromMinutes(2), DdsPreviewResourcePolicy.Default));
    }

    private static DdsRgbaImage SolidRedImage(int width, int height, byte alpha)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
            pixels[index + 3] = alpha;
        }

        return DdsRgbaImage.Create(width, height, checked(width * 4), pixels);
    }

    private static string RequireApprovedTexconv()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("PLAN 15 DirectXTex preview requires Windows.");
        }

        var path = Environment.GetEnvironmentVariable("AUDITION_DIRECTXTEX_TEXCONV_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw SkipException.ForSkip(
                "PLAN 15 preview requires approved texconv.exe via AUDITION_DIRECTXTEX_TEXCONV_PATH.");
        }

        return path;
    }

    private static byte[] CreateRgba(bool red, byte alpha) =>
        CreateUncompressed(
            rMask: 0x000000ff,
            gMask: 0x0000ff00,
            bMask: 0x00ff0000,
            aMask: 0xff000000,
            red ? new byte[] { 255, 0, 0, alpha } : new byte[] { 0, 0, 255, alpha });

    private static byte[] CreateBgra(bool red, byte alpha) =>
        CreateUncompressed(
            rMask: 0x00ff0000,
            gMask: 0x0000ff00,
            bMask: 0x000000ff,
            aMask: 0xff000000,
            red ? new byte[] { 0, 0, 255, alpha } : new byte[] { 255, 0, 0, alpha });

    private static byte[] CreateUncompressed(
        uint rMask,
        uint gMask,
        uint bMask,
        uint aMask,
        byte[] pixel)
    {
        const int width = 1;
        const int height = 1;
        var bytes = CreateHeader(width, height, pixel.Length, fourCc: null);
        Write(bytes, 80, 0x41);
        Write(bytes, 88, 32);
        Write(bytes, 92, rMask);
        Write(bytes, 96, gMask);
        Write(bytes, 100, bMask);
        Write(bytes, 104, aMask);
        pixel.CopyTo(bytes, 128);
        return bytes;
    }

    private static byte[] CreateBc1Red()
    {
        var payload = new byte[] { 0x00, 0xf8, 0x00, 0x00, 0, 0, 0, 0 };
        var bytes = CreateHeader(4, 4, payload.Length, "DXT1");
        payload.CopyTo(bytes, 128);
        return bytes;
    }

    private static byte[] CreateBc3Red(byte alpha)
    {
        var payload = new byte[16];
        payload[0] = alpha;
        payload[1] = alpha == 0 ? (byte)255 : (byte)0;
        payload[8] = 0x00;
        payload[9] = 0xf8;
        var bytes = CreateHeader(4, 4, payload.Length, "DXT5");
        payload.CopyTo(bytes, 128);
        return bytes;
    }

    private static byte[] CreateHeader(int width, int height, int payloadLength, string? fourCc)
    {
        var bytes = new byte[checked(128 + payloadLength)];
        Write(bytes, 0, 0x20534444);
        Write(bytes, 4, 124);
        Write(bytes, 8, fourCc is null ? 0x0000100fu : 0x00081007u);
        Write(bytes, 12, checked((uint)height));
        Write(bytes, 16, checked((uint)width));
        Write(bytes, 20, checked((uint)payloadLength));
        Write(bytes, 28, 1);
        Write(bytes, 76, 32);
        Write(bytes, 80, fourCc is null ? 0x41u : 0x4u);
        if (fourCc is not null)
        {
            Write(bytes, 84, BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(fourCc)));
        }

        Write(bytes, 108, 0x1000);
        return bytes;
    }

    private static Rgba DecodeFirstPixel(ReadOnlySpan<byte> png)
    {
        Assert.True(png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        var offset = 8;
        var idat = new MemoryStream();
        var width = 0;
        var colorType = 0;
        while (offset + 12 <= png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4)));
            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                Assert.Equal(8, data[8]);
                colorType = data[9];
                Assert.Equal(0, data[12]);
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset = checked(offset + 12 + length);
        }

        var bytesPerPixel = colorType == 6 ? 4 : colorType == 2 ? 3 : 0;
        Assert.True(bytesPerPixel > 0, $"Unsupported PNG color type {colorType}.");
        idat.Position = 0;
        using var decompressed = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            zlib.CopyTo(decompressed);
        }

        var scanline = decompressed.ToArray();
        Assert.True(scanline.Length >= checked(1 + width * bytesPerPixel));
        var filter = scanline[0];
        var pixel = scanline.AsSpan(1, bytesPerPixel).ToArray();
        Assert.InRange(filter, (byte)0, (byte)4); // The first pixel has zero left/up predictors.
        return new(pixel[0], pixel[1], pixel[2], bytesPerPixel == 4 ? pixel[3] : (byte)255);
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static void Write(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private sealed record PixelFixture(
        string RelativePath,
        byte[] Bytes,
        byte R,
        byte G,
        byte B,
        byte A);

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    private sealed record EncodeCase(
        DdsFormat Format,
        int Width,
        int Height,
        int Mips,
        byte Alpha,
        DdsTargetAlphaSemantics AlphaSemantics);

    private sealed class TestWorkspace(SecureWorkspacePaths paths) : ISecureWorkspace
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public SecureWorkspacePaths Paths { get; } = paths;

        public string ResolveRelativePath(string relativePath) =>
            new PathSecurity().ResolvePathWithinRoot(Paths.RootDirectory, relativePath);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
