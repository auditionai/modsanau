using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Dds;
using AuditionModStudio.Imaging;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;
using AuditionModStudio.Mods;
using AuditionModStudio.Projects;
using IntegrationTests.Fixtures;
using Xunit.Sdk;

namespace IntegrationTests;

[Collection(RealAcvTool5Collection.Name)]
public sealed class Plan29SmartModScanIntegrationTests
{
    private const string ExpectedArchiveSha256 =
        "3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081";

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    [Trait("Fixture", "RequiresPrivateFixture")]
    public async Task Real_archive_without_manifest_returns_grouped_metadata_enriched_raw_textures_read_only()
    {
        var pathSecurity = new PathSecurity();
        var locator = new RepositoryFixtureLocator(pathSecurity);
        var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
        SkipWhenUnavailable(locator, repositoryRoot);
        var archivePath = locator.ResolveSourcePath(repositoryRoot, RealSampleFixtureCatalog.Archive015);
        var archiveHashBefore = await HashAsync(archivePath);
        Assert.Equal(ExpectedArchiveSha256, archiveHashBefore);

        var testRoot = Path.Combine(Path.GetTempPath(), $"Sàn Smart Scan {Guid.NewGuid():N}");
        var appPaths = new AppPaths(testRoot);
        var toolManifest = TrustedArchiveToolManifest.Production;
        var integrity = new ArchiveToolIntegrityPolicy(pathSecurity, toolManifest);
        using var provisioning = new ArchiveToolProvisioningService(pathSecurity, toolManifest, integrity);
        using var keydat = new KeydatService(pathSecurity);
        var runner = new AcvTool5Runner(pathSecurity, integrity, keydat);
        var regions = new GameRegionProfileCatalog();
        var engine = new AcvTool5ArchiveEngine(
            provisioning,
            keydat,
            runner,
            regions,
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
            "archive-015", "015.ab", "015.ab", ArchiveEngineType.AcvTool5,
            GameRegionProfile.AuditionVietnam.RegionId, "015", "real-fixture-v1", ExpectedArchiveSha256,
            "audition-vn-fixture");
        var created = await projectService.CreateAsync(new(
            "Smart scan real fixture", template, new PristineArchiveSource(repositoryRoot)));
        Assert.True(created.Succeeded, created.DiagnosticCode);
        await using var workspace = Assert.IsAssignableFrom<IProjectArchiveWorkspace>(created.Workspace);

        var extract = await archiveService.ExtractAsync(new(
            template, workspace.ArchiveWorkspace, new PristineArchiveSource(repositoryRoot), TimeSpan.FromMinutes(3)));
        Assert.True(extract.Command.Succeeded, string.Join(" | ", extract.Command.Diagnostics));
        var extractedRoot = Path.Combine(
            workspace.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
            template.ExpectedExtractFolderName);
        var hashesBefore = await HashTreeAsync(extractedRoot);

        var games = GameCatalog.CreateBuiltIn();
        var mods = ModCatalog.Create([CreateMod(template)], games, regions).Catalog!;
        var manifests = TextureManifestCatalog.Create([], mods).Catalog!;
        var thumbnailCache = new ThumbnailCache(
            appPaths,
            pathSecurity,
            new SyntheticPreviewService(),
            new SyntheticMemoryImageImportService(),
            new ImageResizeService(ImageImportResourcePolicy.Default),
            ThumbnailCacheOptions.Default);
        var service = new SmartModScanService(
            games,
            mods,
            manifests,
            new ArchiveAssetScanner(pathSecurity),
            new DdsMetadataReader(),
            thumbnailCache,
            pathSecurity);

        var result = await service.ScanAsync(new(new("audition"), new("fixture_mod"), workspace, 64));

        Assert.True(result.Succeeded, $"{result.DiagnosticCode}: {result.FailedRelativePath}");
        Assert.Equal(101, result.ObservedCatalog!.TotalFileCount);
        Assert.Equal(52, result.TextureCount);
        Assert.Equal(46, result.ObservedCatalog.Count(ArchiveAssetKind.Png));
        Assert.Equal(3, result.ObservedCatalog.Count(ArchiveAssetKind.Rgm));
        Assert.NotEmpty(result.Groups);
        Assert.All(result.Groups.SelectMany(group => group.Textures), texture =>
        {
            Assert.True(texture.UnknownSemantics);
            Assert.True(texture.CanBeLabeled);
            Assert.Null(texture.ManifestResolution.Slot);
            Assert.Equal(texture.Asset.FileName, texture.ManifestResolution.DisplayName);
            Assert.Equal(texture.Asset.RelativePath.Replace('\\', '/'), texture.ManifestResolution.RelativePath.Value);
            Assert.NotEqual(DdsFormat.Unknown, texture.Metadata.Format);
            Assert.InRange(Math.Max(texture.Thumbnail.Width, texture.Thumbnail.Height), 1, 64);
        });
        Assert.Empty(result.MissingManifestSlots);
        Assert.Equal(hashesBefore, await HashTreeAsync(extractedRoot));
        Assert.Equal(archiveHashBefore, await HashAsync(archivePath));
        Assert.DoesNotContain(
            result.Groups.SelectMany(group => group.Textures),
            texture => texture.ManifestResolution.Slot?.Id.Value == "tn_coby_logo");
    }

    private static ModDefinition CreateMod(AuditionArchiveTemplate template) => new(
        new("fixture_mod"), new("audition"), "Fixture mod", new("interface"),
        new("covers/fixture.png"), "Synthetic real integration definition.", template,
        ModKeydatStrategy.ReuseOrGenerate, new("Data/015.ab"), "Fixture compatibility.");

    private static void SkipWhenUnavailable(RepositoryFixtureLocator locator, string repositoryRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("PLAN 29 real Smart Scan gate requires Windows.");
        }

        var missing = new[] { RealSampleFixtureCatalog.Archive015, RealSampleFixtureCatalog.AcvTool }
            .Where(item => !File.Exists(locator.ResolveSourcePath(repositoryRoot, item)))
            .Select(item => item.FileName)
            .ToArray();
        if (missing.Length > 0)
        {
            throw SkipException.ForSkip($"PLAN 29 private fixtures unavailable: {string.Join(", ", missing)}.");
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task<Dictionary<string, string>> HashTreeAsync(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            result.Add(Path.GetRelativePath(root, path), await HashAsync(path));
        }

        return result;
    }

    private sealed class SyntheticPreviewService : IDdsPreviewService
    {
        public Task<DdsPreviewResult> CreateAsync(DdsPreviewRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(DdsPreviewResult.Success(SyntheticMetadata, new(128, 64, new byte[] { 1 })));
    }

    private sealed class SyntheticMemoryImageImportService : IImageImportService
    {
        public Task<ImageImportResult> ImportAsync(ImageImportRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ImageImportResult> ImportMemoryAsync(ImageImportMemoryRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ImageImportResult.Success(new InternalImage(
                128, 64, 512, Enumerable.Repeat((byte)255, 128 * 64 * 4).ToArray(),
                new(ImageSourceFormat.Png, 128, 64, ImageSourceOrientation.Normal, true, false))));
    }

    private static DdsMetadata SyntheticMetadata { get; } = new(
        128, 64, null, 1, 1, DdsFormat.BC3, DdsFormatSupport.Known, "DXT5", null,
        DdsHeaderType.Legacy, true, true, DdsAlphaMode.Interpolated, DdsColorSpace.Unknown,
        DdsResourceDimension.Texture2D, false, 1, 128, 128);
}
