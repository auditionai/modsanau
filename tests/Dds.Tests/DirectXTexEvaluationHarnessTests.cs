using System.Security.Cryptography;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Dds;
using AuditionModStudio.Infrastructure.Paths;

namespace Dds.Tests;

public sealed class DirectXTexEvaluationHarnessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Audition DDS Evaluation Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Tool_hash_mismatch_is_rejected_before_process_launch()
    {
        var context = CreateContext();
        var input = context.Workspace.ResolveRelativePath(@"Extracted\valid.dds");
        await File.WriteAllBytesAsync(input, CreateValidBc3Dds());
        var request = context.CreateRequest(DirectXTexEvaluationOperation.DecodeToPng);

        var rejectedHarness = new DirectXTexEvaluationHarness(
            new PathSecurity(),
            new DdsMetadataReader(),
            context.Tool with { Sha256 = new string('0', 64) });

        var result = await rejectedHarness.RunAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal(DirectXTexEvaluationFailureReason.ToolIntegrityMismatch, result.FailureReason);
        Assert.False(Directory.Exists(Path.Combine(context.Workspace.Paths.WorkingDirectory, "DirectXTexEvaluation")));
    }

    [Fact]
    public async Task Approved_but_non_executable_test_file_returns_structured_start_failure()
    {
        var context = CreateContext();
        var input = context.Workspace.ResolveRelativePath(@"Extracted\valid.dds");
        await File.WriteAllBytesAsync(input, CreateValidBc3Dds());

        var result = await context.Harness.RunAsync(
            context.CreateRequest(DirectXTexEvaluationOperation.DecodeToPng));

        Assert.False(result.Succeeded);
        Assert.Equal(DirectXTexEvaluationFailureReason.ProcessStartFailed, result.FailureReason);
        Assert.Equal("DIRECTXTEX_PROCESS_START_FAILED", result.ErrorCode);
    }

    [Fact]
    public async Task Malformed_dds_is_rejected_before_native_process()
    {
        var context = CreateContext();
        var input = context.Workspace.ResolveRelativePath(@"Extracted\bad.dds");
        await File.WriteAllBytesAsync(input, [1, 2, 3, 4]);

        var result = await context.Harness.RunAsync(
            context.CreateRequest(DirectXTexEvaluationOperation.DecodeToPng) with
            {
                InputRelativePath = @"Extracted\bad.dds",
            });

        Assert.Equal(DirectXTexEvaluationFailureReason.InvalidInput, result.FailureReason);
        Assert.False(Directory.Exists(Path.Combine(context.Workspace.Paths.WorkingDirectory, "DirectXTexEvaluation")));
    }

    [Fact]
    public async Task Malicious_dds_dimensions_are_rejected_by_pixel_policy()
    {
        var context = CreateContext();
        var input = context.Workspace.ResolveRelativePath(@"Extracted\large.dds");
        await File.WriteAllBytesAsync(input, SyntheticDds.Legacy(width: 20_000, height: 20_000));

        var result = await context.Harness.RunAsync(
            context.CreateRequest(DirectXTexEvaluationOperation.DecodeToPng) with
            {
                InputRelativePath = @"Extracted\large.dds",
            });

        Assert.Equal(DirectXTexEvaluationFailureReason.InputTooLarge, result.FailureReason);
        Assert.Equal("DIRECTXTEX_INPUT_PIXEL_LIMIT_EXCEEDED", result.ErrorCode);
    }

    [Fact]
    public async Task Truncated_known_dds_payload_is_rejected_before_native_process()
    {
        var context = CreateContext();
        var input = context.Workspace.ResolveRelativePath(@"Extracted\truncated.dds");
        await File.WriteAllBytesAsync(input, SyntheticDds.Legacy());

        var result = await context.Harness.RunAsync(
            context.CreateRequest(DirectXTexEvaluationOperation.DecodeToPng) with
            {
                InputRelativePath = @"Extracted\truncated.dds",
            });

        Assert.Equal(DirectXTexEvaluationFailureReason.InvalidInput, result.FailureReason);
        Assert.Equal("DIRECTXTEX_DDS_PAYLOAD_REJECTED", result.ErrorCode);
    }

    [Fact]
    public async Task Malformed_png_is_rejected_before_native_process()
    {
        var context = CreateContext();
        var input = context.Workspace.ResolveRelativePath(@"Extracted\bad.png");
        await File.WriteAllBytesAsync(input, new byte[24]);

        var result = await context.Harness.RunAsync(
            context.CreateRequest(DirectXTexEvaluationOperation.EncodeBc3) with
            {
                InputRelativePath = @"Extracted\bad.png",
                TargetWidth = 137,
                TargetHeight = 512,
            });

        Assert.Equal(DirectXTexEvaluationFailureReason.InvalidInput, result.FailureReason);
    }

    [Theory]
    [InlineData(0, 512, 1)]
    [InlineData(137, 0, 1)]
    [InlineData(16385, 1, 1)]
    [InlineData(137, 512, 11)]
    public async Task Unsafe_encode_settings_are_rejected(int width, int height, int mips)
    {
        var context = CreateContext();

        var result = await context.Harness.RunAsync(
            context.CreateRequest(DirectXTexEvaluationOperation.EncodeBc3) with
            {
                TargetWidth = width,
                TargetHeight = height,
                MipLevelCount = mips,
            });

        Assert.Equal(DirectXTexEvaluationFailureReason.InvalidRequest, result.FailureReason);
        Assert.Equal("DIRECTXTEX_INVALID_ENCODE_SETTINGS", result.ErrorCode);
    }

    [Fact]
    public async Task Pre_cancelled_operation_returns_structured_cancellation()
    {
        var context = CreateContext();
        var input = context.Workspace.ResolveRelativePath(@"Extracted\valid.dds");
        await File.WriteAllBytesAsync(input, CreateValidBc3Dds());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await context.Harness.RunAsync(
            context.CreateRequest(DirectXTexEvaluationOperation.DecodeToPng),
            cancellationToken: cancellation.Token);

        Assert.Equal(DirectXTexEvaluationState.Cancelled, result.FinalState);
        Assert.Equal(DirectXTexEvaluationFailureReason.Cancelled, result.FailureReason);
    }

    [Fact]
    public void Evaluation_catalog_pins_official_may_2026_x64_release()
    {
        var descriptor = DirectXTexEvaluationToolCatalog.May2026X64;

        Assert.Equal("texconv.exe", descriptor.FileName);
        Assert.Equal("2026.5.8.1", descriptor.Version);
        Assert.Equal("DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06", descriptor.Sha256);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static byte[] CreateValidBc3Dds()
    {
        var bytes = SyntheticDds.Legacy();
        Array.Resize(ref bytes, 128 + 64 / 4 * (32 / 4) * 16);
        return bytes;
    }

    private TestContext CreateContext()
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
        var toolDirectory = Path.Combine(_root, "Tool Source");
        Directory.CreateDirectory(toolDirectory);
        var toolPath = Path.Combine(toolDirectory, "texconv.exe");
        File.WriteAllBytes(toolPath, [0x4d, 0x5a, 0x00, 0x00]);
        var toolHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(toolPath)));
        var tool = new DirectXTexEvaluationToolDescriptor("fake_texconv", "texconv.exe", "test", toolHash);
        var harness = new DirectXTexEvaluationHarness(new PathSecurity(), new DdsMetadataReader(), tool);
        return new(workspace, harness, toolPath, tool);
    }

    private sealed record TestContext(
        TestWorkspace Workspace,
        DirectXTexEvaluationHarness Harness,
        string ToolPath,
        DirectXTexEvaluationToolDescriptor Tool)
    {
        public DirectXTexEvaluationRequest CreateRequest(DirectXTexEvaluationOperation operation) => new(
            operation,
            Workspace,
            ToolPath,
            operation == DirectXTexEvaluationOperation.DecodeToPng
                ? @"Extracted\valid.dds"
                : @"Extracted\valid.png",
            $"result-{Guid.NewGuid():N}",
            operation == DirectXTexEvaluationOperation.EncodeBc3 ? 137 : null,
            operation == DirectXTexEvaluationOperation.EncodeBc3 ? 512 : null,
            1,
            true,
            TimeSpan.FromSeconds(10));
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
