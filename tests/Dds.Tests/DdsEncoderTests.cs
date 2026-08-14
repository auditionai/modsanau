using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Dds;
using AuditionModStudio.Infrastructure.Paths;

namespace Dds.Tests;

public sealed class DdsEncoderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Audition Encoder Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(DdsFormat.BC1, "DXT1")]
    [InlineData(DdsFormat.BC3, "DXT5")]
    [InlineData(DdsFormat.Rgba8, null)]
    [InlineData(DdsFormat.Bgra8, null)]
    public async Task Explicit_format_is_encoded_and_atomically_promoted(DdsFormat format, string? fourCc)
    {
        DirectXTexEvaluationRequest? captured = null;
        var context = CreateContext(async (request, cancellationToken) =>
        {
            captured = request;
            return await WriteSuccessfulOutput(request, cancellationToken);
        });
        var alpha = format == DdsFormat.BC1 ? DdsTargetAlphaSemantics.Opaque : DdsTargetAlphaSemantics.Full;

        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            SolidImage(137, 512, 255),
            new(137, 512, format, 4, DdsHeaderType.Legacy, DdsColorSpace.Linear, alpha),
            $@"encoded output\{format}.dds"));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(format, result.Metadata!.Format);
        Assert.Equal(137, result.Metadata.Width);
        Assert.Equal(512, result.Metadata.Height);
        Assert.Equal(4u, result.Metadata.EffectiveMipLevelCount);
        Assert.Equal(DdsHeaderType.Legacy, result.Metadata.HeaderType);
        Assert.Equal(fourCc, result.Metadata.FourCC);
        Assert.Equal(format, captured!.TargetFormat);
        Assert.True(File.Exists(context.Workspace.ResolveRelativePath(result.OutputRelativePath!)));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(context.Workspace.Paths.BuildOutputDirectory),
            path => Path.GetFileName(path).StartsWith("DdsEncode-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dimension_mismatch_is_rejected_before_native_encoder()
    {
        var launches = 0;
        var context = CreateContext((_, _) =>
        {
            launches++;
            throw new InvalidOperationException();
        });

        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            SolidImage(4, 4, 255),
            Settings(8, 4),
            "mismatch.dds"));

        Assert.Equal(DdsEncodeFailureReason.DimensionMismatch, result.FailureReason);
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Invalid_mip_count_is_rejected(int mipCount)
    {
        var context = CreateContext((_, _) => throw new InvalidOperationException());
        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            SolidImage(64, 64, 255),
            Settings(64, 64) with { MipLevelCount = mipCount },
            "invalid-mips.dds"));
        Assert.Equal(DdsEncodeFailureReason.InvalidMipCount, result.FailureReason);
    }

    [Fact]
    public async Task Full_alpha_bc1_is_rejected_instead_of_claiming_lossless_alpha()
    {
        var context = CreateContext((_, _) => throw new InvalidOperationException());
        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            SolidImage(4, 4, 64),
            Settings(4, 4) with { Format = DdsFormat.BC1, AlphaSemantics = DdsTargetAlphaSemantics.Full },
            "bc1.dds"));
        Assert.Equal(DdsEncodeFailureReason.UnsupportedTargetFormat, result.FailureReason);
    }

    [Fact]
    public async Task Invalid_stride_and_huge_dimensions_are_rejected_without_allocation()
    {
        var context = CreateContext((_, _) => throw new InvalidOperationException());
        var invalid = new DdsRgbaImage(
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            DdsImagePixelFormat.Rgba8,
            []);
        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            invalid,
            Settings(int.MaxValue, int.MaxValue),
            "huge.dds"));
        Assert.Equal(DdsEncodeFailureReason.ResourceLimitExceeded, result.FailureReason);
    }

    [Theory]
    [InlineData(@"..\escape.dds")]
    [InlineData("not-dds.png")]
    public async Task Unsafe_output_path_is_rejected(string output)
    {
        var context = CreateContext((_, _) => throw new InvalidOperationException());
        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            SolidImage(4, 4, 255),
            Settings(4, 4),
            output));
        Assert.Equal(DdsEncodeFailureReason.InvalidOutputPath, result.FailureReason);
    }

    [Fact]
    public async Task Output_metadata_mismatch_is_not_promoted()
    {
        var context = CreateContext(async (request, cancellationToken) =>
        {
            var changed = request with { TargetFormat = DdsFormat.BC1 };
            var result = await WriteSuccessfulOutput(changed, cancellationToken);
            return result with { OutputDdsMetadata = null };
        });
        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            SolidImage(4, 4, 255),
            Settings(4, 4),
            "must-not-exist.dds"));
        Assert.Equal(DdsEncodeFailureReason.OutputValidationFailed, result.FailureReason);
        Assert.False(File.Exists(Path.Combine(context.Workspace.Paths.BuildOutputDirectory, "must-not-exist.dds")));
    }

    [Fact]
    public async Task Existing_output_is_never_overwritten()
    {
        var context = CreateContext((_, _) => throw new InvalidOperationException());
        var output = Path.Combine(context.Workspace.Paths.BuildOutputDirectory, "existing.dds");
        await File.WriteAllBytesAsync(output, [1, 2, 3]);
        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            SolidImage(4, 4, 255),
            Settings(4, 4),
            "existing.dds"));
        Assert.Equal(DdsEncodeFailureReason.OutputExists, result.FailureReason);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task Pre_cancelled_encode_returns_cancelled_and_does_not_launch()
    {
        var launches = 0;
        var context = CreateContext((_, _) =>
        {
            launches++;
            throw new InvalidOperationException();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            SolidImage(4, 4, 255),
            Settings(4, 4),
            "cancelled.dds"), cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(DdsEncodeFailureReason.Cancelled, result.FailureReason);
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData(DirectXTexEvaluationFailureReason.Cancelled, DdsEncodeFailureReason.Cancelled)]
    [InlineData(DirectXTexEvaluationFailureReason.TimedOut, DdsEncodeFailureReason.TimedOut)]
    [InlineData(DirectXTexEvaluationFailureReason.ToolIntegrityMismatch, DdsEncodeFailureReason.EncoderIntegrityFailed)]
    public async Task Encoder_failures_are_structured(
        DirectXTexEvaluationFailureReason toolReason,
        DdsEncodeFailureReason expected)
    {
        var context = CreateContext((_, _) => Task.FromResult(new DirectXTexEvaluationResult(
            false,
            DirectXTexEvaluationState.Failed,
            toolReason,
            "RAW",
            null,
            null,
            null,
            null,
            null,
            "raw stdout",
            "raw stderr",
            false)));
        var result = await context.Encoder.EncodeAsync(new(
            context.Workspace,
            SolidImage(4, 4, 255),
            Settings(4, 4),
            "result.dds"));
        Assert.Equal(expected, result.FailureReason);
        Assert.DoesNotContain("raw", result.DiagnosticCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TestContext CreateContext(
        Func<DirectXTexEvaluationRequest, CancellationToken, Task<DirectXTexEvaluationResult>> run)
    {
        var paths = new SecureWorkspacePaths(
            _root,
            Path.Combine(_root, "Working"),
            Path.Combine(_root, "Extracted"),
            Path.Combine(_root, "BuildOutput"));
        Directory.CreateDirectory(paths.WorkingDirectory);
        Directory.CreateDirectory(paths.ExtractedDirectory);
        Directory.CreateDirectory(paths.BuildOutputDirectory);
        var workspace = new TestWorkspace(paths);
        var encoder = new DdsEncoder(
            new DdsMetadataReader(),
            new StubHarness(run),
            new PathSecurity(),
            new DdsEncoderOptions(Path.Combine(_root, "texconv.exe"), TimeSpan.FromSeconds(5), DdsPreviewResourcePolicy.Default));
        return new(workspace, encoder);
    }

    private static async Task<DirectXTexEvaluationResult> WriteSuccessfulOutput(
        DirectXTexEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(request.Workspace.Paths.BuildOutputDirectory, request.OutputSubdirectory);
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, "input.dds");
        var bytes = CreateDds(request);
        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
        var metadata = (await new DdsMetadataReader().ReadAsync(outputPath, cancellationToken)).Metadata!;
        return new(
            true,
            DirectXTexEvaluationState.Completed,
            DirectXTexEvaluationFailureReason.None,
            null,
            0,
            Path.GetRelativePath(request.Workspace.Paths.RootDirectory, outputPath),
            request.TargetWidth,
            request.TargetHeight,
            metadata,
            string.Empty,
            string.Empty,
            false);
    }

    private static byte[] CreateDds(DirectXTexEvaluationRequest request)
    {
        var width = checked((uint)request.TargetWidth!.Value);
        var height = checked((uint)request.TargetHeight!.Value);
        byte[] bytes;
        if (request.TargetFormat is DdsFormat.BC1 or DdsFormat.BC3)
        {
            bytes = SyntheticDds.Legacy(
                request.TargetFormat == DdsFormat.BC1 ? "DXT1" : "DXT5",
                width,
                height,
                checked((uint)request.MipLevelCount));
        }
        else
        {
            var bgra = request.TargetFormat == DdsFormat.Bgra8;
            bytes = SyntheticDds.Legacy(
                width: width,
                height: height,
                mipMapCount: checked((uint)request.MipLevelCount),
                uncompressed: true,
                alphaMask: 0xff000000,
                redMask: bgra ? 0x00ff0000u : 0x000000ffu,
                greenMask: 0x0000ff00,
                blueMask: bgra ? 0x000000ffu : 0x00ff0000u);
        }

        long payload = 0;
        var w = request.TargetWidth.Value;
        var h = request.TargetHeight.Value;
        for (var mip = 0; mip < request.MipLevelCount; mip++)
        {
            payload += request.TargetFormat switch
            {
                DdsFormat.BC1 => (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
                DdsFormat.BC3 => (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
                _ => (long)w * h * 4,
            };
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        Array.Resize(ref bytes, checked(128 + (int)payload));
        return bytes;
    }

    private static DdsRgbaImage SolidImage(int width, int height, byte alpha)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
            pixels[index + 3] = alpha;
        }

        return DdsRgbaImage.Create(width, height, checked(width * 4), pixels);
    }

    private static DdsTargetSettings Settings(int width, int height) =>
        new(width, height, DdsFormat.BC3, 1, DdsHeaderType.Legacy, DdsColorSpace.Linear, DdsTargetAlphaSemantics.Full);

    private sealed record TestContext(TestWorkspace Workspace, DdsEncoder Encoder);

    private sealed class StubHarness(
        Func<DirectXTexEvaluationRequest, CancellationToken, Task<DirectXTexEvaluationResult>> run)
        : IDirectXTexEvaluationHarness
    {
        public Task<DirectXTexEvaluationResult> RunAsync(
            DirectXTexEvaluationRequest request,
            IProgress<DirectXTexEvaluationProgress>? progress = null,
            CancellationToken cancellationToken = default) => run(request, cancellationToken);
    }

    private sealed class TestWorkspace(SecureWorkspacePaths paths) : ISecureWorkspace
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SecureWorkspacePaths Paths { get; } = paths;
        public string ResolveRelativePath(string relativePath) => new PathSecurity().ResolvePathWithinRoot(Paths.RootDirectory, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
