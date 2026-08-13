using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;
using Xunit.Sdk;

namespace IntegrationTests;

[Collection(RealAcvTool5Collection.Name)]
public sealed class Plan89MalformedArchiveSecurityTests
{
    private const string ExpectedToolSha256 =
        "6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3";

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    [Trait("Fixture", "RequiresPrivateFixture")]
    public async Task Real_acv_malformed_archive_fails_structurally_without_escape_or_process_exception()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("PLAN 89 malformed archive gate requires Windows.");
        var repositoryRoot = FindRepositoryRoot();
        var sourceTool = Path.Combine(repositoryRoot, "acv.exe");
        if (!File.Exists(sourceTool))
            throw SkipException.ForSkip("PLAN 89 malformed archive gate requires the private ACV fixture.");
        Assert.Equal(ExpectedToolSha256, await HashAsync(sourceTool));

        var root = Path.Combine(Path.GetTempPath(), $"Audition PLAN89 malformed {Guid.NewGuid():N}");
        var working = Path.Combine(root, "Working");
        var extracted = Path.Combine(root, "Extracted");
        var buildOutput = Path.Combine(root, "BuildOutput");
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(extracted);
        Directory.CreateDirectory(buildOutput);
        var tool = Path.Combine(working, "acv.exe");
        var archive = Path.Combine(working, "corrupt.ab");
        File.Copy(sourceTool, tool);
        await File.WriteAllBytesAsync(archive, "not-an-acv-archive"u8.ToArray());
        var archiveHash = await HashAsync(archive);

        try
        {
            var workspace = new TestWorkspace(root, working, extracted, buildOutput);
            var pathSecurity = new PathSecurity();
            using var keydat = new KeydatService(pathSecurity);
            var runner = new AcvTool5Runner(pathSecurity,
                new ArchiveToolIntegrityPolicy(pathSecurity, TrustedArchiveToolManifest.Production), keydat);

            var result = await runner.RunAsync(new(
                AcvTool5Operation.Extract,
                workspace,
                tool,
                "corrupt.ab",
                "corrupt",
                GameRegionProfile.AuditionVietnam,
                TimeSpan.FromSeconds(30),
                MaximumDiagnosticCharacters: 64 * 1024));

            Assert.False(result.Succeeded);
            Assert.NotEqual(AcvTool5RunnerState.Completed, result.State);
            Assert.InRange(result.StandardOutput.Length, 0, 64 * 1024);
            Assert.InRange(result.StandardError.Length, 0, 64 * 1024);
            Assert.Equal(archiveHash, await HashAsync(archive));
            Assert.Empty(Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(repositoryRoot, "corrupt.keydat")));
            Assert.False(Directory.Exists(Path.Combine(repositoryRoot, "corrupt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Assert.Equal(ExpectedToolSha256, await HashAsync(sourceTool));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class TestWorkspace(
        string root,
        string working,
        string extracted,
        string buildOutput) : ISecureWorkspace
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SecureWorkspacePaths Paths { get; } = new(root, working, extracted, buildOutput);
        public string ResolveRelativePath(string relativePath) => Path.Combine(working, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
