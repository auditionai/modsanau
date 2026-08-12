using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Infrastructure.Paths;

namespace Archives.Tests;

public sealed class ArchiveToolIntegrityPolicyTests
{
    [Fact]
    public async Task Approved_filename_and_hash_are_accepted_in_unicode_space_path()
    {
        using var context = IntegrityTestContext.Create("Sàn Aubiz công cụ");

        var result = await context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            context.ToolPath,
            context.RootDirectory);

        Assert.True(result.Approved);
        Assert.Equal(ArchiveToolIntegrityFailureReason.None, result.FailureReason);
        Assert.Equal(context.ExpectedHash, result.ActualSha256);
    }

    [Fact]
    public async Task Modified_byte_is_rejected_with_hash_mismatch()
    {
        using var context = IntegrityTestContext.Create();
        await File.AppendAllTextAsync(context.ToolPath, "modified");

        var result = await context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            context.ToolPath,
            context.RootDirectory);

        Assert.False(result.Approved);
        Assert.Equal(ArchiveToolIntegrityFailureReason.HashMismatch, result.FailureReason);
    }

    [Fact]
    public async Task Missing_executable_is_rejected_structurally()
    {
        using var context = IntegrityTestContext.Create();
        File.Delete(context.ToolPath);

        var result = await context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            context.ToolPath,
            context.RootDirectory);

        Assert.Equal(ArchiveToolIntegrityFailureReason.ExecutableMissing, result.FailureReason);
    }

    [Fact]
    public async Task Wrong_hash_manifest_rejects_right_filename()
    {
        using var context = IntegrityTestContext.Create(expectedHash: new string('0', 64));

        var result = await context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            context.ToolPath,
            context.RootDirectory);

        Assert.Equal(ArchiveToolIntegrityFailureReason.HashMismatch, result.FailureReason);
    }

    [Fact]
    public async Task Right_hash_with_different_filename_is_rejected_by_manifest_policy()
    {
        using var context = IntegrityTestContext.Create();
        var renamed = Path.Combine(context.RootDirectory, "renamed.exe");
        File.Move(context.ToolPath, renamed);

        var result = await context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            renamed,
            context.RootDirectory);

        Assert.Equal(ArchiveToolIntegrityFailureReason.FilenameMismatch, result.FailureReason);
    }

    [Fact]
    public async Task Unapproved_tool_id_is_rejected()
    {
        using var context = IntegrityTestContext.Create();

        var result = await context.Policy.VerifyAsync(
            "unknown_tool",
            context.ToolPath,
            context.RootDirectory);

        Assert.Equal(ArchiveToolIntegrityFailureReason.UnapprovedTool, result.FailureReason);
    }

    [Fact]
    public async Task Executable_outside_trusted_root_is_rejected()
    {
        using var context = IntegrityTestContext.Create();
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var outside = Path.Combine(outsideRoot, "acv.exe");
        File.Copy(context.ToolPath, outside);
        try
        {
            var result = await context.Policy.VerifyAsync(
                ArchiveToolIds.AcvTool5,
                outside,
                context.RootDirectory);

            Assert.Equal(ArchiveToolIntegrityFailureReason.OutsideTrustedLocation, result.FailureReason);
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_manifest_hash_is_rejected_without_hashing_file()
    {
        using var context = IntegrityTestContext.Create(expectedHash: "not-a-sha256");

        var result = await context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            context.ToolPath,
            context.RootDirectory);

        Assert.Equal(ArchiveToolIntegrityFailureReason.InvalidManifest, result.FailureReason);
        Assert.Null(result.ActualSha256);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_hashing()
    {
        using var context = IntegrityTestContext.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            context.ToolPath,
            context.RootDirectory,
            cancellation.Token));
    }

    [Fact]
    public async Task Missing_version_resource_does_not_override_matching_sha256()
    {
        using var context = IntegrityTestContext.Create(expectedVersion: "expected-but-optional");

        var result = await context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            context.ToolPath,
            context.RootDirectory);

        Assert.True(result.Approved);
        Assert.True(string.IsNullOrEmpty(result.ObservedFileVersion));
    }

    [Fact]
    public async Task Unexpected_dll_beside_standalone_acv_is_rejected()
    {
        using var context = IntegrityTestContext.Create();
        await File.WriteAllTextAsync(Path.Combine(context.RootDirectory, "malicious.dll"), "not trusted");

        var result = await context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            context.ToolPath,
            context.RootDirectory);

        Assert.False(result.Approved);
        Assert.Equal(ArchiveToolIntegrityFailureReason.UnexpectedCompanion, result.FailureReason);
    }

    [Fact(Skip = "Requires Windows symbolic-link creation privilege; run manually in an elevated test process.")]
    public async Task Executable_symbolic_link_is_rejected()
    {
        using var context = IntegrityTestContext.Create();
        var target = Path.Combine(context.RootDirectory, "target.bin");
        File.Move(context.ToolPath, target);
        File.CreateSymbolicLink(context.ToolPath, target);

        var result = await context.Policy.VerifyAsync(
            ArchiveToolIds.AcvTool5,
            context.ToolPath,
            context.RootDirectory);

        Assert.Equal(ArchiveToolIntegrityFailureReason.ReparsePointRejected, result.FailureReason);
    }

    private sealed class IntegrityTestContext : IDisposable
    {
        private IntegrityTestContext(
            string rootDirectory,
            string? configuredExpectedHash,
            string? expectedVersion)
        {
            RootDirectory = rootDirectory;
            Directory.CreateDirectory(rootDirectory);
            ToolPath = Path.Combine(rootDirectory, "acv.exe");
            File.WriteAllBytes(ToolPath, [0x01, 0x02, 0x03, 0x04]);
            ExpectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ToolPath)));
            var manifest = new TrustedArchiveToolManifest(
            [
                new ArchiveToolDescriptor(
                    ArchiveToolIds.AcvTool5,
                    "acv.exe",
                    configuredExpectedHash ?? ExpectedHash,
                    expectedVersion,
                    IsApproved: true),
            ]);
            Policy = new ArchiveToolIntegrityPolicy(new PathSecurity(), manifest);
        }

        public string RootDirectory { get; }

        public string ToolPath { get; }

        public string ExpectedHash { get; }

        public ArchiveToolIntegrityPolicy Policy { get; }

        public static IntegrityTestContext Create(
            string? folderName = null,
            string? expectedHash = null,
            string? expectedVersion = null) => new(
            Path.Combine(
                Path.GetTempPath(),
                "ArchiveToolIntegrityTests",
                folderName ?? Guid.NewGuid().ToString("N")),
            expectedHash,
            expectedVersion);

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
