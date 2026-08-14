using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Dds;
using AuditionModStudio.Imaging;
using AuditionModStudio.Infrastructure.Paths;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace IntegrationTests;

[Collection(RealAcvTool5Collection.Name)]
public sealed class Plan98DdsCompatibilityMatrixTests(ITestOutputHelper output)
{
    private const string ExpectedTexconvSha256 =
        "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06";
    private const string MatrixStart = "<!-- MATRIX:START -->";
    private const string MatrixEnd = "<!-- MATRIX:END -->";

    [Fact]
    [Trait("Category", "Integration")]
    public void Matrix_definition_is_deterministic_typed_and_documentation_cannot_drift()
    {
        var cases = DdsCompatibilityMatrixEvidence.Cases;
        Assert.NotEmpty(cases);
        Assert.Equal(cases.Count, cases.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, item =>
        {
            Assert.Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$", item.CaseId);
            Assert.DoesNotContain('|', item.Reason);
            if (item.Overall == DdsCompatibilityClassification.Supported)
            {
                Assert.All(
                    new[]
                    {
                        item.MetadataRead,
                        item.PreviewImport,
                        item.Encode,
                        item.MatchValidate,
                        item.ReDecode,
                        item.ArchiveRoundtrip,
                    },
                    stage => Assert.Equal(DdsCompatibilityClassification.Supported, stage));
            }
            else if (item.Overall == DdsCompatibilityClassification.Unsupported)
            {
                Assert.Contains(
                    DdsCompatibilityClassification.Unsupported,
                    new[] { item.MetadataRead, item.PreviewImport, item.Encode, item.MatchValidate, item.ReDecode, item.ArchiveRoundtrip });
            }
            else if (item.Overall == DdsCompatibilityClassification.Invalid)
            {
                Assert.Equal(DdsCompatibilityClassification.Invalid, item.MetadataRead);
            }
            else
            {
                Assert.Contains(
                    DdsCompatibilityClassification.NotVerified,
                    new[] { item.MetadataRead, item.PreviewImport, item.Encode, item.MatchValidate, item.ReDecode, item.ArchiveRoundtrip });
            }
        });

        var document = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "DDS_FILE_PIPELINE_COMPATIBILITY_MATRIX.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = document.IndexOf(MatrixStart, StringComparison.Ordinal);
        var end = document.IndexOf(MatrixEnd, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var actual = document[(start + MatrixStart.Length)..end].Trim('\n') + "\n";
        Assert.Equal(DdsCompatibilityMatrixEvidence.RenderMarkdownTable(), actual);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    [Trait("Fixture", "RequiresDirectXTex")]
    [InlineData("synthetic-dx10-bc1-srgb", DdsFormat.BC1, DdsColorSpace.Srgb, DdsTargetAlphaSemantics.Binary, 7, 5, 3)]
    [InlineData("synthetic-dx10-bc3-linear", DdsFormat.BC3, DdsColorSpace.Linear, DdsTargetAlphaSemantics.Full, 9, 7, 4)]
    [InlineData("synthetic-dx10-rgba8-srgb", DdsFormat.Rgba8, DdsColorSpace.Srgb, DdsTargetAlphaSemantics.Full, 13, 9, 4)]
    [InlineData("synthetic-dx10-bgra8-linear", DdsFormat.Bgra8, DdsColorSpace.Linear, DdsTargetAlphaSemantics.Full, 17, 11, 1)]
    public async Task Synthetic_dx10_case_completes_metadata_preview_import_encode_match_validate_and_redecode(
        string caseId,
        DdsFormat format,
        DdsColorSpace colorSpace,
        DdsTargetAlphaSemantics alpha,
        int width,
        int height,
        int mipCount)
    {
        var texconv = RequireApprovedTexconv();
        var root = Path.Combine(Path.GetTempPath(), "Audition PLAN 98 ma trận DDS có dấu", caseId, Guid.NewGuid().ToString("N"));
        await using var workspace = new TestWorkspace(root);
        var pathSecurity = new PathSecurity();
        var metadataReader = new DdsMetadataReader();
        var harness = new DirectXTexEvaluationHarness(
            pathSecurity,
            metadataReader,
            DirectXTexEvaluationToolCatalog.May2026X64);
        var policy = DdsPreviewResourcePolicy.Default;
        var encoder = new DdsEncoder(
            metadataReader,
            harness,
            pathSecurity,
            new(texconv, TimeSpan.FromMinutes(2), policy));
        var preview = new DdsPreviewService(
            metadataReader,
            harness,
            pathSecurity,
            new(texconv, TimeSpan.FromMinutes(2), policy));
        var validator = new DdsValidationService(metadataReader, pathSecurity);
        var matcher = new DdsMatchOriginalService(metadataReader, encoder, validator);
        var import = new ImageImportService(ImageImportResourcePolicy.Default);
        var stopwatch = Stopwatch.StartNew();

        var settings = new DdsTargetSettings(
            width,
            height,
            format,
            mipCount,
            DdsHeaderType.Dx10,
            colorSpace,
            alpha);
        var source = await encoder.EncodeAsync(new(
            workspace,
            CreatePattern(width, height, alpha),
            settings,
            $"targets/{caseId}.dds"));
        Assert.True(source.Succeeded, source.DiagnosticCode);
        Assert.NotNull(source.Metadata);
        AssertTechnicalIdentity(settings, source.Metadata!);

        var sourcePreview = await preview.CreateAsync(new(workspace, source.OutputRelativePath!));
        Assert.True(sourcePreview.Succeeded, sourcePreview.DiagnosticCode);
        var imported = await import.ImportMemoryAsync(new(sourcePreview.Image!.EncodedPng.ToArray()));
        Assert.True(imported.Succeeded, imported.DiagnosticCode);
        Assert.Equal(width, imported.Image!.Width);
        Assert.Equal(height, imported.Image.Height);

        var matched = await matcher.MatchAsync(new(
            workspace,
            source.OutputRelativePath!,
            DdsRgbaImage.Create(imported.Image),
            $"matched/{caseId}.dds"));
        Assert.True(matched.Succeeded, matched.DiagnosticCode);
        Assert.True(matched.MatchReport!.OverallMatch);
        AssertTechnicalIdentity(settings, matched.OutputMetadata!);

        var validation = await validator.ValidateAsync(new(
            workspace,
            source.OutputRelativePath!,
            matched.OutputRelativePath!));
        Assert.True(validation.Succeeded, validation.DiagnosticCode);
        Assert.True(validation.MatchReport!.OverallMatch);

        var redecoded = await preview.CreateAsync(new(workspace, matched.OutputRelativePath!));
        Assert.True(redecoded.Succeeded, redecoded.DiagnosticCode);
        Assert.Equal(width, redecoded.Image!.Width);
        Assert.Equal(height, redecoded.Image.Height);
        var reread = await metadataReader.ReadAsync(workspace.ResolveRelativePath(matched.OutputRelativePath!));
        Assert.True(reread.IsSuccess, reread.ErrorCode);
        AssertTechnicalIdentity(settings, reread.Metadata!);

        stopwatch.Stop();
        output.WriteLine(
            "PLAN 98 DDS MATRIX: case={0}; format={1}; header=DX10; color={2}; dimensions={3}x{4}; mips={5}; alpha={6}; dds-pipeline=SUPPORTED; archive=NOT VERIFIED; elapsed={7:F3}s",
            caseId,
            format,
            colorSpace,
            width,
            height,
            mipCount,
            alpha,
            stopwatch.Elapsed.TotalSeconds);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Malformed_and_unsupported_matrix_boundaries_are_typed_and_fail_closed()
    {
        var root = Path.Combine(Path.GetTempPath(), "Audition PLAN 98 invalid DDS", Guid.NewGuid().ToString("N"));
        await using var workspace = new TestWorkspace(root);
        var reader = new DdsMetadataReader();
        var matcher = new DdsMatchOriginalService(reader, new NeverEncoder(), new NeverValidator());
        var cases = new[]
        {
            ("bad-magic.dds", "FAIL"u8.ToArray(), DdsMetadataFailureReason.InvalidMagic),
            ("truncated.dds", DdsBytes(64, 64, "DXT5")[..64], DdsMetadataFailureReason.TruncatedHeader),
            ("zero-width.dds", DdsBytes(0, 64, "DXT5"), DdsMetadataFailureReason.InvalidDimensions),
            ("truncated-dx10.dds", DdsBytes(8, 8, "DX10"), DdsMetadataFailureReason.InvalidDx10Header),
            ("invalid-array.dds", Dx10Bytes(8, 8, 28, 3, 0), DdsMetadataFailureReason.InvalidDx10Header),
        };
        foreach (var item in cases)
        {
            var path = workspace.ResolveRelativePath($"Working/{item.Item1}");
            await File.WriteAllBytesAsync(path, item.Item2);
            var result = await reader.ReadAsync(path);
            Assert.False(result.IsSuccess);
            Assert.Equal(item.Item3, result.FailureReason);
        }

        var recognizedFormats = new[]
        {
            ("bc2-legacy.dds", DdsBytes(8, 8, "DXT3"), DdsFormat.BC2, DdsHeaderType.Legacy),
            ("bc2-dx10.dds", Dx10Bytes(8, 8, 74, 3, 1), DdsFormat.BC2, DdsHeaderType.Dx10),
            ("bc4-legacy.dds", DdsBytes(8, 8, "ATI1"), DdsFormat.BC4, DdsHeaderType.Legacy),
            ("bc4-dx10.dds", Dx10Bytes(8, 8, 80, 3, 1), DdsFormat.BC4, DdsHeaderType.Dx10),
            ("bc5-legacy.dds", DdsBytes(8, 8, "ATI2"), DdsFormat.BC5, DdsHeaderType.Legacy),
            ("bc5-dx10.dds", Dx10Bytes(8, 8, 83, 3, 1), DdsFormat.BC5, DdsHeaderType.Dx10),
            ("bc6h-dx10.dds", Dx10Bytes(8, 8, 95, 3, 1), DdsFormat.BC6H, DdsHeaderType.Dx10),
            ("bc7-dx10.dds", Dx10Bytes(8, 8, 98, 3, 1), DdsFormat.BC7, DdsHeaderType.Dx10),
        };
        foreach (var item in recognizedFormats)
        {
            var path = workspace.ResolveRelativePath($"Working/{item.Item1}");
            await File.WriteAllBytesAsync(path, item.Item2);
            var result = await reader.ReadAsync(path);
            Assert.True(result.IsSuccess, result.ErrorCode);
            Assert.Equal(item.Item3, result.Metadata!.Format);
            Assert.Equal(item.Item4, result.Metadata.HeaderType);
            Assert.Equal(DdsFormatSupport.Known, result.Metadata.FormatSupport);
        }

        var bitmaskPath = workspace.ResolveRelativePath("Working/unsupported-bitmask.dds");
        await File.WriteAllBytesAsync(bitmaskPath, LegacyBitmaskBytes(8, 8));
        var bitmask = await reader.ReadAsync(bitmaskPath);
        Assert.True(bitmask.IsSuccess, bitmask.ErrorCode);
        Assert.Equal(DdsFormat.Uncompressed, bitmask.Metadata!.Format);
        Assert.Equal(DdsFormatSupport.Unsupported, bitmask.Metadata.FormatSupport);

        var unsupportedProfiles = new[]
        {
            Metadata(DdsFormat.BC2),
            Metadata(DdsFormat.BC4),
            Metadata(DdsFormat.BC5),
            Metadata(DdsFormat.BC6H, DdsHeaderType.Dx10),
            Metadata(DdsFormat.BC7, DdsHeaderType.Dx10),
        };
        Assert.All(unsupportedProfiles, metadata =>
        {
            var result = matcher.DeriveProfile(metadata);
            Assert.Equal(DdsMatchOriginalFailureReason.UnsupportedTargetFormat, result.FailureReason);
        });
        var resourceCases = new[]
        {
            ("texture1d.dds", Dx10Bytes(8, 8, 28, 2, 1), DdsResourceDimension.Texture1D, false, 1u),
            ("texture3d.dds", Dx10Bytes(8, 8, 28, 4, 1), DdsResourceDimension.Texture3D, false, 1u),
            ("array.dds", Dx10Bytes(8, 8, 28, 3, 2), DdsResourceDimension.Texture2D, false, 2u),
            ("cubemap.dds", Dx10Bytes(8, 8, 28, 3, 1, 4), DdsResourceDimension.Texture2D, true, 1u),
        };
        var preview = new DdsPreviewService(
            reader,
            new NeverEvaluationHarness(),
            new PathSecurity(),
            new(Path.GetFullPath("never-texconv.exe"), TimeSpan.FromMinutes(1), DdsPreviewResourcePolicy.Default));
        foreach (var item in resourceCases)
        {
            var relative = $"Working/{item.Item1}";
            await File.WriteAllBytesAsync(workspace.ResolveRelativePath(relative), item.Item2);
            var read = await reader.ReadAsync(workspace.ResolveRelativePath(relative));
            Assert.True(read.IsSuccess, read.ErrorCode);
            Assert.Equal(item.Item3, read.Metadata!.ResourceDimension);
            Assert.Equal(item.Item4, read.Metadata.IsCubemap);
            Assert.Equal(item.Item5, read.Metadata.ArraySize);
            var profile = matcher.DeriveProfile(read.Metadata);
            Assert.Equal(DdsMatchOriginalFailureReason.UnsupportedResourceType, profile.FailureReason);
            var previewResult = await preview.CreateAsync(new(workspace, relative));
            Assert.Equal(DdsPreviewFailureReason.UnsupportedFormat, previewResult.FailureReason);
        }

        var encoder = new DdsEncoder(
            reader,
            new NeverEvaluationHarness(),
            new PathSecurity(),
            new(Path.GetFullPath("never-texconv.exe"), TimeSpan.FromMinutes(1), DdsPreviewResourcePolicy.Default));
        foreach (var format in new[] { DdsFormat.BC2, DdsFormat.BC4, DdsFormat.BC5, DdsFormat.BC6H, DdsFormat.BC7 })
        {
            var encoded = await encoder.EncodeAsync(new(
                workspace,
                CreatePattern(8, 8, DdsTargetAlphaSemantics.Full),
                new(
                    8,
                    8,
                    format,
                    1,
                    format is DdsFormat.BC6H or DdsFormat.BC7 ? DdsHeaderType.Dx10 : DdsHeaderType.Legacy,
                    DdsColorSpace.Linear,
                    DdsTargetAlphaSemantics.Full),
                $"unsupported/{format}.dds"));
            Assert.Equal(DdsEncodeFailureReason.UnsupportedTargetFormat, encoded.FailureReason);
        }

        var excessiveMips = matcher.DeriveProfile(Metadata(DdsFormat.BC3) with
        {
            DeclaredMipMapCount = 99,
            EffectiveMipLevelCount = 99,
        });
        Assert.Equal(DdsMatchOriginalFailureReason.InvalidMipProfile, excessiveMips.FailureReason);
        Assert.Empty(Directory.EnumerateFiles(workspace.Paths.ExtractedDirectory, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(workspace.Paths.BuildOutputDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    [Trait("Fixture", "RequiresDirectXTex")]
    public async Task Truncated_payload_is_metadata_readable_but_rejected_before_native_decode()
    {
        var texconv = RequireApprovedTexconv();
        var root = Path.Combine(Path.GetTempPath(), "Audition PLAN 98 payload hỏng", Guid.NewGuid().ToString("N"));
        await using var workspace = new TestWorkspace(root);
        var relative = "Working/truncated-payload.dds";
        await File.WriteAllBytesAsync(workspace.ResolveRelativePath(relative), DdsBytes(64, 64, "DXT5"));
        var reader = new DdsMetadataReader();
        var metadata = await reader.ReadAsync(workspace.ResolveRelativePath(relative));
        Assert.True(metadata.IsSuccess, metadata.ErrorCode);

        var pathSecurity = new PathSecurity();
        var harness = new DirectXTexEvaluationHarness(
            pathSecurity,
            reader,
            DirectXTexEvaluationToolCatalog.May2026X64);
        var decode = await harness.RunAsync(new(
            DirectXTexEvaluationOperation.DecodeToPng,
            workspace,
            texconv,
            relative,
            "direct-truncated-payload",
            null,
            null,
            1,
            false,
            TimeSpan.FromMinutes(1)));
        Assert.False(decode.Succeeded);
        Assert.Equal(DirectXTexEvaluationFailureReason.InvalidInput, decode.FailureReason);
        Assert.Equal("DIRECTXTEX_DDS_PAYLOAD_REJECTED", decode.ErrorCode);

        var preview = new DdsPreviewService(
            reader,
            harness,
            pathSecurity,
            new(texconv, TimeSpan.FromMinutes(1), DdsPreviewResourcePolicy.Default));
        var result = await preview.CreateAsync(new(workspace, relative));
        Assert.False(result.Succeeded);
        Assert.Equal(DdsPreviewFailureReason.DecodeFailed, result.FailureReason);
        Assert.Equal("DDS_PREVIEW_DECODE_FAILED", result.DiagnosticCode);
        Assert.Empty(Directory.EnumerateFiles(workspace.Paths.BuildOutputDirectory, "*", SearchOption.AllDirectories));
    }

    private static string RequireApprovedTexconv()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("PLAN 98 DirectXTex matrix requires Windows.");
        }

        var path = Environment.GetEnvironmentVariable("AUDITION_DIRECTXTEX_TEXCONV_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw SkipException.ForSkip("PLAN 98 approved texconv is unavailable.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(ExpectedTexconvSha256, hash);
        return Path.GetFullPath(path);
    }

    private static DdsRgbaImage CreatePattern(int width, int height, DdsTargetAlphaSemantics alpha)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = checked((y * width + x) * 4);
                pixels[offset] = checked((byte)(17 + x * 13 % 239));
                pixels[offset + 1] = checked((byte)(23 + y * 11 % 229));
                pixels[offset + 2] = checked((byte)(31 + (x + y) * 7 % 223));
                pixels[offset + 3] = alpha switch
                {
                    DdsTargetAlphaSemantics.Opaque => 255,
                    DdsTargetAlphaSemantics.Binary => (x + y) % 2 == 0 ? (byte)0 : (byte)255,
                    _ => checked((byte)(32 + (x * 19 + y * 29) % 224)),
                };
            }
        }

        return DdsRgbaImage.Create(width, height, checked(width * 4), pixels);
    }

    private static void AssertTechnicalIdentity(DdsTargetSettings expected, DdsMetadata actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal((uint)expected.MipLevelCount, actual.EffectiveMipLevelCount);
        Assert.Equal(expected.HeaderType, actual.HeaderType);
        Assert.Equal(expected.ColorSpace, actual.ColorSpace);
        Assert.Equal(DdsResourceDimension.Texture2D, actual.ResourceDimension);
        Assert.False(actual.IsCubemap);
        Assert.Equal(1u, actual.ArraySize);
    }

    private static byte[] DdsBytes(uint width, uint height, string fourCc)
    {
        var bytes = new byte[129];
        Write(bytes, 0, FourCc("DDS "));
        Write(bytes, 4, 124);
        Write(bytes, 8, 0x0002100f);
        Write(bytes, 12, height);
        Write(bytes, 16, width);
        Write(bytes, 28, 1);
        Write(bytes, 76, 32);
        Write(bytes, 80, 0x4);
        Write(bytes, 84, FourCc(fourCc));
        Write(bytes, 108, 0x1000);
        return bytes;
    }

    private static byte[] Dx10Bytes(
        uint width,
        uint height,
        uint format,
        uint dimension,
        uint arraySize,
        uint miscFlag = 0)
    {
        var bytes = DdsBytes(width, height, "DX10");
        Array.Resize(ref bytes, 149);
        Write(bytes, 128, format);
        Write(bytes, 132, dimension);
        Write(bytes, 136, miscFlag);
        Write(bytes, 140, arraySize);
        return bytes;
    }

    private static byte[] LegacyBitmaskBytes(uint width, uint height)
    {
        var bytes = DdsBytes(width, height, "NONE");
        Write(bytes, 80, 0x41);
        Write(bytes, 88, 16);
        Write(bytes, 92, 0x0000f800);
        Write(bytes, 96, 0x000007e0);
        Write(bytes, 100, 0x0000001f);
        Write(bytes, 104, 0);
        return bytes;
    }

    private static DdsMetadata Metadata(DdsFormat format, DdsHeaderType header = DdsHeaderType.Legacy) => new(
        8,
        8,
        null,
        1,
        1,
        format,
        DdsFormatSupport.Known,
        header == DdsHeaderType.Legacy ? format switch
        {
            DdsFormat.BC2 => "DXT3",
            DdsFormat.BC4 => "ATI1",
            DdsFormat.BC5 => "ATI2",
            _ => null,
        } : "DX10",
        header == DdsHeaderType.Dx10 ? 98u : null,
        header,
        format is DdsFormat.BC2 or DdsFormat.BC7,
        format is DdsFormat.BC2 or DdsFormat.BC7,
        format == DdsFormat.BC2 ? DdsAlphaMode.Explicit : format == DdsFormat.BC7 ? DdsAlphaMode.Channel : DdsAlphaMode.None,
        header == DdsHeaderType.Dx10 ? DdsColorSpace.Linear : DdsColorSpace.Unknown,
        DdsResourceDimension.Texture2D,
        false,
        1,
        256,
        header == DdsHeaderType.Legacy ? 128 : 148);

    private static uint FourCc(string value) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(value));
    private static void Write(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class TestWorkspace : ISecureWorkspace
    {
        public TestWorkspace(string root)
        {
            Id = Guid.NewGuid().ToString("N");
            Paths = new(
                root,
                Path.Combine(root, "Working"),
                Path.Combine(root, "Extracted"),
                Path.Combine(root, "BuildOutput"));
            Directory.CreateDirectory(Paths.WorkingDirectory);
            Directory.CreateDirectory(Paths.ExtractedDirectory);
            Directory.CreateDirectory(Paths.BuildOutputDirectory);
        }

        public string Id { get; }
        public SecureWorkspacePaths Paths { get; }
        public string ResolveRelativePath(string relativePath) =>
            new PathSecurity().ResolvePathWithinRoot(Paths.RootDirectory, relativePath);
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverEncoder : IDdsEncoder
    {
        public Task<DdsEncodeResult> EncodeAsync(DdsEncodeRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unsupported profiles must be rejected before encode.");
    }

    private sealed class NeverValidator : IDdsValidationService
    {
        public Task<DdsValidationResult> ValidateAsync(DdsValidationRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unsupported profiles must be rejected before validation.");
    }

    private sealed class NeverEvaluationHarness : IDirectXTexEvaluationHarness
    {
        public Task<DirectXTexEvaluationResult> RunAsync(
            DirectXTexEvaluationRequest request,
            IProgress<DirectXTexEvaluationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unsupported settings must be rejected before native tool execution.");
    }
}
