using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Dds;
using AuditionModStudio.Infrastructure.Paths;

namespace Dds.Tests;

public sealed class DdsMatchOriginalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Audition Match Original Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(DdsFormat.BC1)]
    [InlineData(DdsFormat.BC3)]
    [InlineData(DdsFormat.Rgba8)]
    [InlineData(DdsFormat.Bgra8)]
    public void Derive_profile_preserves_real_format_legacy_dimensions_and_mips(DdsFormat format)
    {
        var service = CreateService((_, _) => throw new InvalidOperationException()).Service;
        var metadata = Metadata(format, width: 137, height: 512, declaredMips: 4, effectiveMips: 4);

        var result = service.DeriveProfile(metadata);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        var settings = result.Profile!.TargetSettings;
        Assert.Equal(format, settings.Format);
        Assert.Equal(137, settings.Width);
        Assert.Equal(512, settings.Height);
        Assert.Equal(4, settings.MipLevelCount);
        Assert.Equal(DdsHeaderType.Legacy, settings.HeaderType);
        Assert.Equal(DdsColorSpace.Linear, settings.ColorSpace);
        Assert.Equal(DdsColorSpace.Unknown, result.Profile.OriginalColorSpace);
        Assert.False(result.Profile.ColorSpaceIsMeaningful);
    }

    [Fact]
    public void Declared_zero_uses_effective_single_base_mip()
    {
        var service = CreateService((_, _) => throw new InvalidOperationException()).Service;
        var result = service.DeriveProfile(Metadata(DdsFormat.BC3, declaredMips: 0, effectiveMips: 1));
        Assert.True(result.Succeeded);
        Assert.Equal(0u, result.Profile!.OriginalDeclaredMipMapCount);
        Assert.Equal(1, result.Profile.TargetSettings.MipLevelCount);
    }

    [Fact]
    public void Dx10_srgb_profile_is_preserved_when_encoder_supports_it()
    {
        var service = CreateService((_, _) => throw new InvalidOperationException()).Service;
        var result = service.DeriveProfile(Metadata(
            DdsFormat.Rgba8,
            header: DdsHeaderType.Dx10,
            colorSpace: DdsColorSpace.Srgb));
        Assert.True(result.Succeeded);
        Assert.True(result.Profile!.ColorSpaceIsMeaningful);
        Assert.Equal(DdsHeaderType.Dx10, result.Profile.TargetSettings.HeaderType);
        Assert.Equal(DdsColorSpace.Srgb, result.Profile.TargetSettings.ColorSpace);
    }

    [Fact]
    public void Unsupported_format_and_resource_are_never_downgraded()
    {
        var service = CreateService((_, _) => throw new InvalidOperationException()).Service;
        var bc7 = service.DeriveProfile(Metadata(DdsFormat.BC7, header: DdsHeaderType.Dx10));
        var cubemap = service.DeriveProfile(Metadata(DdsFormat.BC3) with { IsCubemap = true });
        Assert.Equal(DdsMatchOriginalFailureReason.UnsupportedTargetFormat, bc7.FailureReason);
        Assert.Equal(DdsMatchOriginalFailureReason.UnsupportedResourceType, cubemap.FailureReason);
    }

    [Fact]
    public async Task Dimension_mismatch_and_bc1_semitransparent_alpha_are_rejected_before_encode()
    {
        var launches = 0;
        var context = CreateService((_, _) =>
        {
            launches++;
            throw new InvalidOperationException();
        });
        await WriteTarget(context.Workspace, @"Extracted\target.dds", "DXT1", 4, 4);
        var mismatch = await context.Service.MatchAsync(new(
            context.Workspace,
            @"Extracted\target.dds",
            SolidImage(8, 4, 255),
            "mismatch.dds"));
        var alpha = await context.Service.MatchAsync(new(
            context.Workspace,
            @"Extracted\target.dds",
            SolidImage(4, 4, 64),
            "alpha.dds"));
        Assert.Equal(DdsMatchOriginalFailureReason.DimensionMismatch, mismatch.FailureReason);
        Assert.Equal(DdsMatchOriginalFailureReason.AlphaIncompatible, alpha.FailureReason);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Post_validation_builds_strict_report_and_detects_mismatch()
    {
        var context = CreateService(async (request, cancellationToken) =>
            await WriteEncodedOutput(request, "DXT1", cancellationToken));
        await WriteTarget(context.Workspace, @"Extracted\target.dds", "DXT5", 4, 4);

        var result = await context.Service.MatchAsync(new(
            context.Workspace,
            @"Extracted\target.dds",
            SolidImage(4, 4, 255),
            "matched.dds"));

        Assert.Equal(DdsMatchOriginalFailureReason.PostValidationFailed, result.FailureReason);
        Assert.NotNull(result.MatchReport);
        Assert.False(result.MatchReport!.FormatMatches);
        Assert.False(result.MatchReport.OverallMatch);
    }

    [Fact]
    public async Task Successful_match_returns_overall_strict_report()
    {
        var context = CreateService(async (request, cancellationToken) =>
            await WriteEncodedOutput(request, "DXT5", cancellationToken));
        await WriteTarget(context.Workspace, @"Extracted\thư mục\target file.dds", "DXT5", 4, 4);

        var result = await context.Service.MatchAsync(new(
            context.Workspace,
            @"Extracted\thư mục\target file.dds",
            SolidImage(4, 4, 255),
            @"kết quả\matched file.dds"));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(result.MatchReport!.OverallMatch);
        Assert.Equal(DdsFormat.BC3, result.OutputMetadata!.Format);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TestContext CreateService(
        Func<DdsEncodeRequest, CancellationToken, Task<DdsEncodeResult>> encode)
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
        var metadataReader = new DdsMetadataReader();
        var pathSecurity = new PathSecurity();
        var service = new DdsMatchOriginalService(
            metadataReader,
            new StubEncoder(encode),
            new DdsValidationService(metadataReader, pathSecurity));
        return new(workspace, service);
    }

    private static DdsMetadata Metadata(
        DdsFormat format,
        int width = 64,
        int height = 64,
        uint declaredMips = 1,
        uint effectiveMips = 1,
        DdsHeaderType header = DdsHeaderType.Legacy,
        DdsColorSpace colorSpace = DdsColorSpace.Unknown) => new(
            width,
            height,
            null,
            declaredMips,
            effectiveMips,
            format,
            DdsFormatSupport.Known,
            format == DdsFormat.BC1 ? "DXT1" : format == DdsFormat.BC3 ? "DXT5" : null,
            header == DdsHeaderType.Dx10 ? 28u : null,
            header,
            format is DdsFormat.BC1 or DdsFormat.BC3 or DdsFormat.Rgba8 or DdsFormat.Bgra8,
            format is DdsFormat.BC3 or DdsFormat.Rgba8 or DdsFormat.Bgra8,
            format == DdsFormat.BC1 ? DdsAlphaMode.PossibleOneBit : DdsAlphaMode.Channel,
            colorSpace,
            DdsResourceDimension.Texture2D,
            false,
            1,
            1024,
            header == DdsHeaderType.Legacy ? 128 : 148);

    private static async Task WriteTarget(
        TestWorkspace workspace,
        string relativePath,
        string fourCc,
        uint width,
        uint height)
    {
        var path = workspace.ResolveRelativePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = SyntheticDds.Legacy(fourCc, width, height);
        var blockBytes = fourCc == "DXT1" ? 8 : 16;
        Array.Resize(ref bytes, checked(128 + (int)(((width + 3) / 4) * ((height + 3) / 4) * blockBytes)));
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async Task<DdsEncodeResult> WriteEncodedOutput(
        DdsEncodeRequest request,
        string fourCc,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(request.Workspace.Paths.BuildOutputDirectory, request.OutputRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = SyntheticDds.Legacy(fourCc, (uint)request.Image.Width, (uint)request.Image.Height);
        var blockBytes = fourCc == "DXT1" ? 8 : 16;
        Array.Resize(ref bytes, 128 + Math.Max(1, (request.Image.Width + 3) / 4) * Math.Max(1, (request.Image.Height + 3) / 4) * blockBytes);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        var metadata = (await new DdsMetadataReader().ReadAsync(path, cancellationToken)).Metadata!;
        return DdsEncodeResult.Success(
            Path.GetRelativePath(request.Workspace.Paths.RootDirectory, path),
            metadata);
    }

    private static DdsRgbaImage SolidImage(int width, int height, byte alpha)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
            pixels[index + 3] = alpha;
        }

        return DdsRgbaImage.Create(width, height, width * 4, pixels);
    }

    private sealed record TestContext(TestWorkspace Workspace, DdsMatchOriginalService Service);

    private sealed class StubEncoder(Func<DdsEncodeRequest, CancellationToken, Task<DdsEncodeResult>> encode) : IDdsEncoder
    {
        public Task<DdsEncodeResult> EncodeAsync(
            DdsEncodeRequest request,
            CancellationToken cancellationToken = default) => encode(request, cancellationToken);
    }

    private sealed class TestWorkspace(SecureWorkspacePaths paths) : ISecureWorkspace
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SecureWorkspacePaths Paths { get; } = paths;
        public string ResolveRelativePath(string relativePath) => new PathSecurity().ResolvePathWithinRoot(Paths.RootDirectory, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
