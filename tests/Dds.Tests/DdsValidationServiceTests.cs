using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Dds;
using AuditionModStudio.Infrastructure.Paths;

namespace Dds.Tests;

public sealed class DdsValidationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Audition DDS Validation Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(DdsFormat.BC1)]
    [InlineData(DdsFormat.BC3)]
    [InlineData(DdsFormat.Rgba8)]
    [InlineData(DdsFormat.Bgra8)]
    public async Task Exact_supported_metadata_passes_without_mutating_files(DdsFormat format)
    {
        var metadata = Metadata(format, width: 6000, height: 1801);
        var context = CreateContext(metadata, metadata);
        var targetPath = context.Workspace.ResolveRelativePath(@"Extracted\texture\target.dds");
        var candidatePath = context.Workspace.ResolveRelativePath(@"BuildOutput\validated\candidate.dds");
        await WriteSentinel(targetPath, 0x11);
        await WriteSentinel(candidatePath, 0x22);
        var targetBefore = await File.ReadAllBytesAsync(targetPath);
        var candidateBefore = await File.ReadAllBytesAsync(candidatePath);

        var result = await context.Service.ValidateAsync(new(
            context.Workspace,
            @"Extracted\texture\target.dds",
            @"BuildOutput\validated\candidate.dds"));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(result.MatchReport!.OverallMatch);
        Assert.Equal(targetBefore, await File.ReadAllBytesAsync(targetPath));
        Assert.Equal(candidateBefore, await File.ReadAllBytesAsync(candidatePath));
    }

    [Fact]
    public async Task Every_structural_mismatch_is_reported_and_blocks_success()
    {
        var target = Metadata(DdsFormat.BC3, header: DdsHeaderType.Dx10, colorSpace: DdsColorSpace.Srgb, mips: 4);
        var candidates = new[]
        {
            target with { Format = DdsFormat.BC1 },
            target with { Width = target.Width + 1 },
            target with { EffectiveMipLevelCount = 3 },
            target with { HeaderType = DdsHeaderType.Legacy, ColorSpace = DdsColorSpace.Unknown },
            target with { ColorSpace = DdsColorSpace.Linear },
            target with { ArraySize = 2 },
        };

        foreach (var candidate in candidates)
        {
            var context = CreateContext(target, candidate);
            var result = await context.Service.ValidateAsync(new(
                context.Workspace,
                @"Extracted\target.dds",
                @"BuildOutput\candidate.dds"));
            Assert.False(result.Succeeded);
            Assert.Equal(DdsValidationFailureReason.MetadataMismatch, result.FailureReason);
            Assert.False(result.MatchReport!.OverallMatch);
        }
    }

    [Fact]
    public async Task Legacy_unknown_color_space_is_not_invented_or_compared_as_srgb()
    {
        var target = Metadata(DdsFormat.BC3, colorSpace: DdsColorSpace.Unknown);
        var candidate = target with { ColorSpace = DdsColorSpace.Linear };
        var context = CreateContext(target, candidate);

        var result = await context.Service.ValidateAsync(new(
            context.Workspace,
            @"Extracted\target.dds",
            @"BuildOutput\candidate.dds"));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(result.MatchReport!.ColorSpaceMatches);
    }

    [Theory]
    [InlineData(DdsFormat.BC7, false, 1u)]
    [InlineData(DdsFormat.BC3, true, 1u)]
    [InlineData(DdsFormat.BC3, false, 2u)]
    public async Task Unsupported_target_profiles_are_rejected_without_downgrade(
        DdsFormat format,
        bool isCubemap,
        uint arraySize)
    {
        var target = Metadata(format) with { IsCubemap = isCubemap, ArraySize = arraySize };
        var context = CreateContext(target, target);

        var result = await context.Service.ValidateAsync(new(
            context.Workspace,
            @"Extracted\target.dds",
            @"BuildOutput\candidate.dds"));

        Assert.Equal(DdsValidationFailureReason.UnsupportedTargetProfile, result.FailureReason);
        Assert.Equal("DDS_VALIDATION_UNSUPPORTED_TARGET_PROFILE", result.DiagnosticCode);
    }

    [Fact]
    public async Task Missing_invalid_cancelled_and_traversal_failures_are_structured()
    {
        var metadata = Metadata(DdsFormat.BC3);
        var missingReader = new StubReader((path, _) => Task.FromResult(
            path.Contains("target", StringComparison.Ordinal)
                ? DdsMetadataReadResult.Failure(DdsMetadataFailureReason.FileMissing, "missing")
                : DdsMetadataReadResult.Success(metadata)));
        var missing = CreateContext(missingReader);
        var missingResult = await missing.Service.ValidateAsync(new(
            missing.Workspace, @"Extracted\target.dds", @"BuildOutput\candidate.dds"));
        Assert.Equal(DdsValidationFailureReason.TargetMissing, missingResult.FailureReason);

        var cancelled = CreateContext(new StubReader((_, _) => Task.FromResult(
            DdsMetadataReadResult.Failure(DdsMetadataFailureReason.Cancelled, "cancelled"))));
        var cancelledResult = await cancelled.Service.ValidateAsync(new(
            cancelled.Workspace, @"Extracted\target.dds", @"BuildOutput\candidate.dds"));
        Assert.True(cancelledResult.Cancelled);

        var traversal = CreateContext(metadata, metadata);
        var traversalResult = await traversal.Service.ValidateAsync(new(
            traversal.Workspace, @"..\target.dds", @"BuildOutput\candidate.dds"));
        Assert.Equal(DdsValidationFailureReason.InvalidTargetDds, traversalResult.FailureReason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TestContext CreateContext(DdsMetadata target, DdsMetadata candidate) =>
        CreateContext(new StubReader((path, _) => Task.FromResult(
            DdsMetadataReadResult.Success(
                path.Contains("BuildOutput", StringComparison.OrdinalIgnoreCase) ? candidate : target))));

    private TestContext CreateContext(IDdsMetadataReader reader)
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
        return new(workspace, new DdsValidationService(reader, new PathSecurity()));
    }

    private static DdsMetadata Metadata(
        DdsFormat format,
        int width = 137,
        int height = 512,
        uint mips = 1,
        DdsHeaderType header = DdsHeaderType.Legacy,
        DdsColorSpace colorSpace = DdsColorSpace.Unknown) => new(
            width,
            height,
            null,
            mips,
            mips,
            format,
            DdsFormatSupport.Known,
            format == DdsFormat.BC1 ? "DXT1" : format == DdsFormat.BC3 ? "DXT5" : null,
            header == DdsHeaderType.Dx10 ? 29u : null,
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

    private static async Task WriteSentinel(string path, byte value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [value, value, value]);
    }

    private sealed record TestContext(TestWorkspace Workspace, DdsValidationService Service);

    private sealed class StubReader(
        Func<string, CancellationToken, Task<DdsMetadataReadResult>> read) : IDdsMetadataReader
    {
        public Task<DdsMetadataReadResult> ReadAsync(
            string path,
            CancellationToken cancellationToken = default) => read(path, cancellationToken);
    }

    private sealed class TestWorkspace(SecureWorkspacePaths paths) : ISecureWorkspace
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SecureWorkspacePaths Paths { get; } = paths;
        public string ResolveRelativePath(string relativePath) =>
            new PathSecurity().ResolvePathWithinRoot(Paths.RootDirectory, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
