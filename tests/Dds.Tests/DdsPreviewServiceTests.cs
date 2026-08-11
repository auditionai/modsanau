using System.Buffers.Binary;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Dds;
using AuditionModStudio.Infrastructure.Paths;

namespace Dds.Tests;

public sealed class DdsPreviewServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Audition DDS Preview Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Missing_dds_returns_structured_source_missing()
    {
        var context = CreateContext(CreateSuccessfulDecode);

        var result = await context.Service.CreateAsync(new(context.Workspace, @"Extracted\missing.dds"));

        Assert.False(result.Succeeded);
        Assert.Equal(DdsPreviewFailureReason.SourceMissing, result.FailureReason);
        Assert.Null(result.Image);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_or_malformed_dds_is_rejected_before_decoder(bool malformedHeader)
    {
        var launches = 0;
        var context = CreateContext((request, _) =>
        {
            launches++;
            return CreateSuccessfulDecode(request, CancellationToken.None);
        });
        var relative = @"Extracted\bad.dds";
        var bytes = malformedHeader ? SyntheticDds.Legacy(headerSize: 123) : [1, 2, 3, 4];
        await File.WriteAllBytesAsync(context.Workspace.ResolveRelativePath(relative), bytes);

        var result = await context.Service.CreateAsync(new(context.Workspace, relative));

        Assert.Equal(DdsPreviewFailureReason.InvalidDds, result.FailureReason);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Unknown_format_is_rejected_before_decoder()
    {
        var launches = 0;
        var context = CreateContext((request, _) =>
        {
            launches++;
            return CreateSuccessfulDecode(request, CancellationToken.None);
        });
        var relative = @"Extracted\unknown.dds";
        await File.WriteAllBytesAsync(
            context.Workspace.ResolveRelativePath(relative),
            SyntheticDds.Legacy("NOPE"));

        var result = await context.Service.CreateAsync(new(context.Workspace, relative));

        Assert.Equal(DdsPreviewFailureReason.UnsupportedFormat, result.FailureReason);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Malicious_dimensions_are_rejected_before_decoder_allocation()
    {
        var launches = 0;
        var context = CreateContext((request, _) =>
        {
            launches++;
            return CreateSuccessfulDecode(request, CancellationToken.None);
        });
        var relative = @"Extracted\huge.dds";
        await File.WriteAllBytesAsync(
            context.Workspace.ResolveRelativePath(relative),
            SyntheticDds.Legacy(width: uint.MaxValue, height: uint.MaxValue));

        var result = await context.Service.CreateAsync(new(context.Workspace, relative));

        Assert.Equal(DdsPreviewFailureReason.ResourceLimitExceeded, result.FailureReason);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Successful_preview_returns_immutable_png_and_cleans_temp_output()
    {
        var context = CreateContext(CreateSuccessfulDecode);
        var relative = @"Extracted\thư mục có dấu\texture sample.dds";
        Directory.CreateDirectory(Path.GetDirectoryName(context.Workspace.ResolveRelativePath(relative))!);
        await File.WriteAllBytesAsync(
            context.Workspace.ResolveRelativePath(relative),
            CreateDdsWithPayload(width: 3, height: 5));

        var result = await context.Service.CreateAsync(new(context.Workspace, relative));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(3, result.Image!.Width);
        Assert.Equal(5, result.Image.Height);
        Assert.Equal(DdsPreviewImage.PngMediaType, result.Image.MediaType);
        Assert.False(result.Image.EncodedPng.IsEmpty);
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Workspace.Paths.BuildOutputDirectory));
    }

    [Fact]
    public async Task Same_filename_in_different_folders_uses_isolated_concurrent_outputs()
    {
        var outputDirectories = new List<string>();
        var sync = new object();
        var context = CreateContext(async (request, cancellationToken) =>
        {
            lock (sync)
            {
                outputDirectories.Add(request.OutputSubdirectory);
            }

            await Task.Yield();
            return await CreateSuccessfulDecode(request, cancellationToken);
        });
        foreach (var relative in new[] { @"Extracted\a\same.dds", @"Extracted\b\same.dds" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(context.Workspace.ResolveRelativePath(relative))!);
            await File.WriteAllBytesAsync(context.Workspace.ResolveRelativePath(relative), CreateDdsWithPayload());
        }

        var results = await Task.WhenAll(
            context.Service.CreateAsync(new(context.Workspace, @"Extracted\a\same.dds")),
            context.Service.CreateAsync(new(context.Workspace, @"Extracted\b\same.dds")));

        Assert.All(results, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        Assert.Equal(2, outputDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Workspace.Paths.BuildOutputDirectory));
    }

    [Theory]
    [InlineData(DirectXTexEvaluationFailureReason.Cancelled, DdsPreviewFailureReason.Cancelled, true)]
    [InlineData(DirectXTexEvaluationFailureReason.TimedOut, DdsPreviewFailureReason.TimedOut, false)]
    [InlineData(DirectXTexEvaluationFailureReason.ToolMissing, DdsPreviewFailureReason.DecoderUnavailable, false)]
    [InlineData(DirectXTexEvaluationFailureReason.ToolIntegrityMismatch, DdsPreviewFailureReason.DecoderIntegrityFailed, false)]
    public async Task Decoder_terminal_states_are_mapped_without_raw_diagnostics(
        DirectXTexEvaluationFailureReason decoderReason,
        DdsPreviewFailureReason previewReason,
        bool cancelled)
    {
        var context = CreateContext((_, _) => Task.FromResult(new DirectXTexEvaluationResult(
            false,
            decoderReason == DirectXTexEvaluationFailureReason.Cancelled
                ? DirectXTexEvaluationState.Cancelled
                : DirectXTexEvaluationState.Failed,
            decoderReason,
            "RAW_TOOL_CODE",
            null,
            null,
            null,
            null,
            null,
            "proprietary stdout",
            "proprietary stderr",
            false)));
        var relative = @"Extracted\valid.dds";
        await File.WriteAllBytesAsync(context.Workspace.ResolveRelativePath(relative), CreateDdsWithPayload());

        var result = await context.Service.CreateAsync(new(context.Workspace, relative));

        Assert.Equal(previewReason, result.FailureReason);
        Assert.Equal(cancelled, result.Cancelled);
        Assert.DoesNotContain("proprietary", result.DiagnosticCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
        var service = new DdsPreviewService(
            new DdsMetadataReader(),
            new StubHarness(run),
            new PathSecurity(),
            new DdsPreviewServiceOptions(
                Path.Combine(_root, "approved", "texconv.exe"),
                TimeSpan.FromSeconds(5),
                DdsPreviewResourcePolicy.Default));
        return new(workspace, service);
    }

    private static async Task<DirectXTexEvaluationResult> CreateSuccessfulDecode(
        DirectXTexEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var sourcePath = request.Workspace.ResolveRelativePath(request.InputRelativePath);
        var metadata = (await new DdsMetadataReader().ReadAsync(sourcePath, cancellationToken)).Metadata!;
        var outputDirectory = Path.Combine(
            request.Workspace.Paths.BuildOutputDirectory,
            request.OutputSubdirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".png");
        await File.WriteAllBytesAsync(outputPath, CreatePngHeader(metadata.Width, metadata.Height), cancellationToken);
        return new(
            true,
            DirectXTexEvaluationState.Completed,
            DirectXTexEvaluationFailureReason.None,
            null,
            0,
            Path.GetRelativePath(request.Workspace.Paths.RootDirectory, outputPath),
            metadata.Width,
            metadata.Height,
            null,
            string.Empty,
            string.Empty,
            false);
    }

    private static byte[] CreateDdsWithPayload(uint width = 4, uint height = 4)
    {
        var bytes = SyntheticDds.Legacy(width: width, height: height);
        var blockWidth = (width + 3) / 4;
        var blockHeight = (height + 3) / 4;
        Array.Resize(ref bytes, checked(128 + (int)(blockWidth * blockHeight * 16)));
        return bytes;
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), checked((uint)height));
        return bytes;
    }

    private sealed record TestContext(TestWorkspace Workspace, DdsPreviewService Service);

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

        public string ResolveRelativePath(string relativePath) =>
            new PathSecurity().ResolvePathWithinRoot(Paths.RootDirectory, relativePath);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
