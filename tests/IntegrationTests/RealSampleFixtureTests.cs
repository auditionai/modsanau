using System.Security.Cryptography;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;
using IntegrationTests.Fixtures;

namespace IntegrationTests;

public sealed class RealSampleFixtureTests
{
    [Fact]
    public void Catalog_registers_exact_real_sample_set()
    {
        Assert.Equal(3, RealSampleFixtureCatalog.All.Count);
        Assert.Equal(
            new[] { "015.ab", "acv.exe", "tn_coby_logo.dds" },
            RealSampleFixtureCatalog.All.Select(item => item.FileName).Order().ToArray());
        Assert.Equal(
            RealSampleFixtureCatalog.All.Count,
            RealSampleFixtureCatalog.All.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void Coby_logo_registration_records_verified_expected_metadata()
    {
        var registration = RealSampleFixtureCatalog.CobyLogoTexture;

        Assert.Equal(RealSampleFixtureKind.Texture, registration.Kind);
        Assert.NotNull(registration.DdsMetadata);
        Assert.Equal(6000, registration.DdsMetadata.Width);
        Assert.Equal(1801, registration.DdsMetadata.Height);
        Assert.Equal("DXT5/BC3", registration.DdsMetadata.Format);
        Assert.Equal(1, registration.DdsMetadata.MipLevels);
    }

    [Fact]
    public void Registered_paths_are_canonical_and_availability_is_explicit()
    {
        var pathSecurity = new PathSecurity();
        var locator = new RepositoryFixtureLocator(pathSecurity);
        var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);

        var availability = RealSampleFixtureCatalog.All.ToDictionary(
            registration => registration.Id,
            registration => File.Exists(locator.ResolveSourcePath(repositoryRoot, registration)));

        Assert.True(availability[RealSampleFixtureCatalog.AcvTool.Id]);
        Assert.True(availability[RealSampleFixtureCatalog.Archive015.Id]);
        Assert.Equal(
            File.Exists(Path.Combine(repositoryRoot, "samples", "private", "tn_coby_logo.dds")),
            availability[RealSampleFixtureCatalog.CobyLogoTexture.Id]);
    }

    [Fact]
    public async Task Real_available_fixtures_are_copied_before_mutation_and_sources_remain_pristine()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var pathSecurity = new PathSecurity();
            var locator = new RepositoryFixtureLocator(pathSecurity);
            var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
            var appPaths = new AppPaths(testRoot);
            await using var workspaceService = new SecureWorkspaceService(appPaths, pathSecurity);
            await using var workspace = await workspaceService.CreateAsync();
            var copyService = new FixtureCopyService(pathSecurity, locator);

            foreach (var registration in new[]
                     {
                         RealSampleFixtureCatalog.AcvTool,
                         RealSampleFixtureCatalog.Archive015,
                     })
            {
                var sourcePath = locator.ResolveSourcePath(repositoryRoot, registration);
                var sourceHashBefore = await ComputeSha256Async(sourcePath);
                var copy = await copyService.CopyToWorkspaceAsync(
                    registration,
                    repositoryRoot,
                    workspace);

                Assert.Equal(sourceHashBefore, copy.Sha256);
                Assert.StartsWith(
                    workspace.Paths.WorkingDirectory,
                    copy.WorkingCopyPath,
                    StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(sourcePath, copy.WorkingCopyPath);

                await using (var mutation = new FileStream(
                                 copy.WorkingCopyPath,
                                 FileMode.Append,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 1,
                                 FileOptions.Asynchronous))
                {
                    await mutation.WriteAsync(new byte[] { 0x00 });
                }

                Assert.NotEqual(sourceHashBefore, await ComputeSha256Async(copy.WorkingCopyPath));
                Assert.Equal(sourceHashBefore, await ComputeSha256Async(sourcePath));
            }
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Missing_registration_fails_without_creating_a_working_copy()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var pathSecurity = new PathSecurity();
            var locator = new RepositoryFixtureLocator(pathSecurity);
            var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
            var appPaths = new AppPaths(testRoot);
            await using var workspaceService = new SecureWorkspaceService(appPaths, pathSecurity);
            await using var workspace = await workspaceService.CreateAsync();
            var copyService = new FixtureCopyService(pathSecurity, locator);
            var missing = new RealSampleFixtureRegistration(
                "missing-test-fixture",
                "missing.bin",
                Path.Combine("samples", "private", "missing.bin"),
                RealSampleFixtureKind.Texture);

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => copyService.CopyToWorkspaceAsync(missing, repositoryRoot, workspace));

            Assert.False(File.Exists(Path.Combine(workspace.Paths.WorkingDirectory, "missing.bin")));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public void Gitignore_keeps_real_fixtures_outside_source_control()
    {
        var locator = new RepositoryFixtureLocator(new PathSecurity());
        var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
        var ignoreRules = File.ReadAllLines(Path.Combine(repositoryRoot, ".gitignore"));

        Assert.Contains("/015.ab", ignoreRules);
        Assert.Contains("/015.keydat", ignoreRules);
        Assert.Contains("/acv.exe", ignoreRules);
        Assert.Contains("/samples/private/", ignoreRules);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string CreateTestRoot()
    {
        return Path.Combine(Path.GetTempPath(), "AuditionModStudio.Tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string testRoot)
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
