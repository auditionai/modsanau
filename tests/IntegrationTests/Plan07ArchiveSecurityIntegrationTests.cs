using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;
using IntegrationTests.Fixtures;

namespace IntegrationTests;

public sealed class Plan07ArchiveSecurityIntegrationTests
{
    [Fact]
    public async Task Approved_real_acv_fixture_is_verified_copied_and_verified_again_without_execution()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var pathSecurity = new PathSecurity();
            var locator = new RepositoryFixtureLocator(pathSecurity);
            var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
            var sourcePath = locator.ResolveSourcePath(repositoryRoot, RealSampleFixtureCatalog.AcvTool);
            var sourceHashBefore = await ComputeSha256Async(sourcePath);
            var appPaths = new AppPaths(testRoot);
            await using var workspaceService = new SecureWorkspaceService(appPaths, pathSecurity);
            await using var workspace = await workspaceService.CreateAsync();
            var manifest = TrustedArchiveToolManifest.Production;
            var policy = new ArchiveToolIntegrityPolicy(pathSecurity, manifest);
            using var provisioning = new ArchiveToolProvisioningService(pathSecurity, manifest, policy);

            var result = await provisioning.ProvisionAsync(
                ArchiveToolIds.AcvTool5,
                repositoryRoot,
                RealSampleFixtureCatalog.AcvTool.RepositoryRelativePath,
                workspace);

            Assert.False(result.ReusedExistingCopy);
            Assert.Equal(sourceHashBefore, result.Sha256);
            Assert.Equal(
                "6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3",
                result.Sha256);
            Assert.StartsWith(workspace.Paths.WorkingDirectory, result.WorkingCopyPath, PathComparison);
            Assert.False(File.Exists(Path.Combine(workspace.Paths.WorkingDirectory, ".fake-launched")));

            await File.AppendAllTextAsync(result.WorkingCopyPath, "modified working copy");
            var modified = await policy.VerifyAsync(
                ArchiveToolIds.AcvTool5,
                result.WorkingCopyPath,
                workspace.Paths.WorkingDirectory);

            Assert.Equal(ArchiveToolIntegrityFailureReason.HashMismatch, modified.FailureReason);
            Assert.Equal(sourceHashBefore, await ComputeSha256Async(sourcePath));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Keydat_copies_are_workspace_scoped_and_cleanup_of_one_does_not_touch_another_or_source()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var pathSecurity = new PathSecurity();
            var locator = new RepositoryFixtureLocator(pathSecurity);
            var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
            var sourceKeydatPath = Path.Combine(repositoryRoot, "015.keydat");
            var sourceHashBefore = await ComputeSha256Async(sourceKeydatPath);
            var appPaths = new AppPaths(testRoot);
            await using var workspaceService = new SecureWorkspaceService(appPaths, pathSecurity);
            var workspaceA = await workspaceService.CreateAsync();
            var workspaceB = await workspaceService.CreateAsync();
            var copyService = new FixtureCopyService(pathSecurity, locator);
            using var keydatService = new KeydatService(pathSecurity);

            try
            {
                await copyService.CopyToWorkspaceAsync(
                    RealSampleFixtureCatalog.Archive015,
                    repositoryRoot,
                    workspaceA);
                await copyService.CopyToWorkspaceAsync(
                    RealSampleFixtureCatalog.Archive015,
                    repositoryRoot,
                    workspaceB);

                var copies = await Task.WhenAll(
                    keydatService.CopyToWorkspaceAsync(
                        workspaceA,
                        "015.ab",
                        repositoryRoot,
                        "015.keydat",
                        sourceHashBefore),
                    keydatService.CopyToWorkspaceAsync(
                        workspaceB,
                        "015.ab",
                        repositoryRoot,
                        "015.keydat",
                        sourceHashBefore));

                Assert.NotEqual(copies[0].KeydatPath, copies[1].KeydatPath);
                Assert.Equal(KeydatStatus.PresentUnverified, copies[0].Status);
                Assert.Equal(KeydatStatus.PresentUnverified, copies[1].Status);

                var workspaceARoot = workspaceA.Paths.RootDirectory;
                await workspaceA.DisposeAsync();

                Assert.False(Directory.Exists(workspaceARoot));
                Assert.True(File.Exists(copies[1].KeydatPath));
                Assert.Equal(sourceHashBefore, await ComputeSha256Async(sourceKeydatPath));
            }
            finally
            {
                await workspaceA.DisposeAsync();
                await workspaceB.DisposeAsync();
            }
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string CreateTestRoot() => Path.Combine(
        Path.GetTempPath(),
        "AuditionModStudio.Tests",
        Guid.NewGuid().ToString("N"));

    private static void DeleteTestRoot(string testRoot)
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
