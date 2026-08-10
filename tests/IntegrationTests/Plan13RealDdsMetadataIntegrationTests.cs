using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Dds;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;
using AuditionModStudio.Projects;
using IntegrationTests.Fixtures;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace IntegrationTests;

[Collection(RealAcvTool5Collection.Name)]
public sealed class Plan13RealDdsMetadataIntegrationTests(ITestOutputHelper output)
{
    private const string ExpectedArchiveSha256 =
        "3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081";

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    [Trait("Fixture", "RequiresPrivateFixture")]
    public async Task Every_dds_from_disposable_real_extract_is_parsed_without_modification()
    {
        var pathSecurity = new PathSecurity();
        var locator = new RepositoryFixtureLocator(pathSecurity);
        var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
        SkipWhenUnavailable(locator, repositoryRoot);
        var archivePath = locator.ResolveSourcePath(repositoryRoot, RealSampleFixtureCatalog.Archive015);
        var pristineHashBefore = await ComputeHashAsync(archivePath);
        Assert.Equal(ExpectedArchiveSha256, pristineHashBefore);

        var testRoot = Path.Combine(Path.GetTempPath(), $"Sàn DDS Metadata {Guid.NewGuid():N}");
        var appPaths = new AppPaths(testRoot);
        var manifest = TrustedArchiveToolManifest.Production;
        var integrityPolicy = new ArchiveToolIntegrityPolicy(pathSecurity, manifest);
        using var provisioning = new ArchiveToolProvisioningService(pathSecurity, manifest, integrityPolicy);
        using var keydat = new KeydatService(pathSecurity);
        var runner = new AcvTool5Runner(pathSecurity, integrityPolicy, keydat);
        var engine = new AcvTool5ArchiveEngine(
            provisioning,
            keydat,
            runner,
            new GameRegionProfileCatalog(),
            new AcvTool5ArchiveEngineOptions(
                repositoryRoot,
                RealSampleFixtureCatalog.AcvTool.RepositoryRelativePath,
                MaximumDiagnosticCharacters: 5_242_880));
        var archiveService = new AuditionArchiveService([engine], pathSecurity);
        await using var secureWorkspaces = new SecureWorkspaceService(appPaths, pathSecurity);
        var projectService = new ProjectArchiveWorkspaceService(
            appPaths,
            pathSecurity,
            secureWorkspaces,
            new ProjectArchiveWorkspaceManifestStore(pathSecurity));
        var template = new AuditionArchiveTemplate(
            "archive-015",
            "015.ab",
            "015.ab",
            ArchiveEngineType.AcvTool5,
            GameRegionProfile.AuditionVietnam.RegionId,
            "015",
            "real-fixture-v1",
            ExpectedArchiveSha256);
        var created = await projectService.CreateAsync(new(
            "Kiểm thử DDS metadata thật",
            template,
            new PristineArchiveSource(repositoryRoot)));
        Assert.True(created.Succeeded, created.DiagnosticCode);
        await using var workspace = Assert.IsAssignableFrom<IProjectArchiveWorkspace>(created.Workspace);

        var extract = await archiveService.ExtractAsync(new(
            template,
            workspace.ArchiveWorkspace,
            new PristineArchiveSource(repositoryRoot),
            TimeSpan.FromMinutes(3)));
        Assert.True(extract.Command.Succeeded, string.Join(" | ", extract.Command.Diagnostics));

        var extractedRoot = Path.Combine(
            workspace.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
            template.ExpectedExtractFolderName);
        var ddsPaths = Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".dds", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(52, ddsPaths.Length);

        var hashesBefore = await HashAllAsync(ddsPaths);
        var reader = new DdsMetadataReader();
        var results = new List<DdsMetadataReadResult>(ddsPaths.Length);
        foreach (var path in ddsPaths)
        {
            results.Add(await reader.ReadAsync(path));
        }

        var hashesAfter = await HashAllAsync(ddsPaths);
        Assert.Equal(hashesBefore, hashesAfter);
        Assert.All(results, result => Assert.True(result.IsSuccess, result.ErrorCode));
        var metadata = results.Select(result => result.Metadata!).ToArray();

        WriteGroups("format", metadata.GroupBy(item => item.Format.ToString()));
        WriteGroups("dimension", metadata.GroupBy(item => $"{item.Width}x{item.Height}"));
        WriteGroups("mips", metadata.GroupBy(item => item.EffectiveMipLevelCount.ToString()));
        WriteGroups("header", metadata.GroupBy(item => item.HeaderType.ToString()));
        output.WriteLine(
            "DDS summary: total={0}; success={1}; failed={2}; alphaCapable={3}; unknown={4}",
            results.Count,
            results.Count(item => item.IsSuccess),
            results.Count(item => !item.IsSuccess),
            metadata.Count(item => item.SupportsAlpha),
            metadata.Count(item => item.FormatSupport != DdsFormatSupport.Known));

        Assert.Contains(metadata, item => item.Width == 6000 && item.Height == 1801);
        Assert.Equal(pristineHashBefore, await ComputeHashAsync(archivePath));
    }

    private void WriteGroups(string label, IEnumerable<IGrouping<string, DdsMetadata>> groups) =>
        output.WriteLine(
            "DDS {0}: {1}",
            label,
            string.Join(", ", groups.OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}={group.Count()}")));

    private static void SkipWhenUnavailable(RepositoryFixtureLocator locator, string repositoryRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("PLAN 13 real DDS gate requires Windows.");
        }

        var missing = new[] { RealSampleFixtureCatalog.Archive015, RealSampleFixtureCatalog.AcvTool }
            .Where(item => !File.Exists(locator.ResolveSourcePath(repositoryRoot, item)))
            .Select(item => item.FileName)
            .ToArray();
        if (missing.Length > 0)
        {
            throw SkipException.ForSkip($"PLAN 13 private fixtures unavailable: {string.Join(", ", missing)}.");
        }
    }

    private static async Task<Dictionary<string, string>> HashAllAsync(IEnumerable<string> paths)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            hashes.Add(path, await ComputeHashAsync(path));
        }

        return hashes;
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
