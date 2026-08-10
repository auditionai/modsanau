using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;

namespace Archives.Tests;

public sealed class KeydatServiceTests
{
    [Theory]
    [InlineData("015.ab", "015.keydat")]
    [InlineData("021.acv", "021.keydat")]
    [InlineData("custom.ext", "custom.keydat")]
    [InlineData("archive.with.many.dots.bin", "archive.with.many.dots.keydat")]
    [InlineData("Sàn Audition Việt Nam.ab", "Sàn Audition Việt Nam.keydat")]
    public void Descriptor_derives_keydat_from_archive_basename(string archiveName, string expectedKeydatName)
    {
        using var context = TestWorkspaceContext.Create();
        context.CreateArchive(archiveName);

        var descriptor = context.Service.Describe(context.Workspace, archiveName);

        Assert.Equal(expectedKeydatName, Path.GetFileName(descriptor.KeydatPath));
        Assert.Equal(KeydatStatus.Missing, descriptor.Status);
    }

    [Fact]
    public void Existing_nonempty_keydat_is_present_but_not_claimed_valid()
    {
        using var context = TestWorkspaceContext.Create();
        context.CreateArchive("015.ab");
        File.WriteAllText(Path.Combine(context.WorkingDirectory, "015.keydat"), "unverified");

        var descriptor = context.Service.Describe(context.Workspace, "015.ab");

        Assert.Equal(KeydatStatus.PresentUnverified, descriptor.Status);
        Assert.Null(descriptor.Sha256);
    }

    [Fact]
    public void Empty_keydat_is_structurally_invalid()
    {
        using var context = TestWorkspaceContext.Create();
        context.CreateArchive("015.ab");
        File.WriteAllBytes(Path.Combine(context.WorkingDirectory, "015.keydat"), []);

        var descriptor = context.Service.Describe(context.Workspace, "015.ab");

        Assert.Equal(KeydatStatus.Invalid, descriptor.Status);
        Assert.Equal(0, descriptor.Length);
    }

    [Fact]
    public void Archive_outside_workspace_is_rejected()
    {
        using var context = TestWorkspaceContext.Create();

        Assert.Throws<ArgumentException>(() =>
            context.Service.Describe(context.Workspace, @"..\outside.ab"));
    }

    [Fact]
    public async Task Trusted_source_is_copied_once_and_source_hash_remains_unchanged()
    {
        using var context = TestWorkspaceContext.Create();
        context.CreateArchive("015.ab");
        var sourceRoot = context.CreateTrustedSource("source.keydat", "keydat-data");
        var sourcePath = Path.Combine(sourceRoot, "source.keydat");
        var sourceHash = await ComputeHashAsync(sourcePath);

        var first = await context.Service.CopyToWorkspaceAsync(
            context.Workspace,
            "015.ab",
            sourceRoot,
            "source.keydat",
            sourceHash);
        var second = await context.Service.CopyToWorkspaceAsync(
            context.Workspace,
            "015.ab",
            sourceRoot,
            "source.keydat",
            sourceHash);

        Assert.Equal(KeydatStatus.PresentUnverified, first.Status);
        Assert.Equal(first.KeydatPath, second.KeydatPath);
        Assert.Equal(sourceHash, first.Sha256);
        Assert.Equal(sourceHash, second.Sha256);
        Assert.Equal(sourceHash, await ComputeHashAsync(sourcePath));
        Assert.NotEqual(sourcePath, first.KeydatPath);
    }

    [Fact]
    public async Task Source_outside_trusted_root_is_rejected()
    {
        using var context = TestWorkspaceContext.Create();
        context.CreateArchive("015.ab");
        var sourceRoot = context.CreateTrustedSource("source.keydat", "keydat-data");

        await Assert.ThrowsAsync<ArgumentException>(() => context.Service.CopyToWorkspaceAsync(
            context.Workspace,
            "015.ab",
            sourceRoot,
            @"..\outside.keydat"));
    }

    [Fact]
    public async Task Empty_trusted_source_is_rejected_without_creating_workspace_keydat()
    {
        using var context = TestWorkspaceContext.Create();
        context.CreateArchive("015.ab");
        var sourceRoot = context.CreateTrustedSource("source.keydat", string.Empty);

        await Assert.ThrowsAsync<InvalidDataException>(() => context.Service.CopyToWorkspaceAsync(
            context.Workspace,
            "015.ab",
            sourceRoot,
            "source.keydat"));

        Assert.False(File.Exists(Path.Combine(context.WorkingDirectory, "015.keydat")));
    }

    [Fact]
    public async Task Two_workspaces_with_same_archive_basename_do_not_share_writable_keydat()
    {
        using var first = TestWorkspaceContext.Create();
        using var second = TestWorkspaceContext.Create();
        first.CreateArchive("015.ab");
        second.CreateArchive("015.ab");
        var sourceRoot = first.CreateTrustedSource("source.keydat", "shared-source");
        var sourceHash = await ComputeHashAsync(Path.Combine(sourceRoot, "source.keydat"));

        var copies = await Task.WhenAll(
            first.Service.CopyToWorkspaceAsync(
                first.Workspace,
                "015.ab",
                sourceRoot,
                "source.keydat",
                sourceHash),
            second.Service.CopyToWorkspaceAsync(
                second.Workspace,
                "015.ab",
                sourceRoot,
                "source.keydat",
                sourceHash));

        Assert.NotEqual(copies[0].KeydatPath, copies[1].KeydatPath);
        Assert.True(File.Exists(copies[0].KeydatPath));
        Assert.True(File.Exists(copies[1].KeydatPath));
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class TestWorkspaceContext : IDisposable
    {
        private TestWorkspaceContext(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            WorkingDirectory = Path.Combine(rootDirectory, "Working");
            Directory.CreateDirectory(WorkingDirectory);
            Workspace = new TestWorkspace(rootDirectory, WorkingDirectory);
            Service = new KeydatService(new PathSecurity());
        }

        public string RootDirectory { get; }

        public string WorkingDirectory { get; }

        public TestWorkspace Workspace { get; }

        public KeydatService Service { get; }

        public static TestWorkspaceContext Create() => new(
            Path.Combine(Path.GetTempPath(), "KeydatServiceTests", Guid.NewGuid().ToString("N")));

        public void CreateArchive(string relativePath)
        {
            var path = Path.Combine(WorkingDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0x01]);
        }

        public string CreateTrustedSource(string filename, string content)
        {
            var sourceRoot = Path.Combine(RootDirectory, "TrustedSource");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(Path.Combine(sourceRoot, filename), content);
            return sourceRoot;
        }

        public void Dispose()
        {
            Service.Dispose();
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }

    private sealed class TestWorkspace(string rootDirectory, string workingDirectory) : ISecureWorkspace
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public SecureWorkspacePaths Paths { get; } = new(
            rootDirectory,
            workingDirectory,
            Path.Combine(rootDirectory, "Extracted"),
            Path.Combine(rootDirectory, "BuildOutput"));

        public string ResolveRelativePath(string relativePath) => Path.Combine(workingDirectory, relativePath);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
