using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;
using AuditionModStudio.Projects;
using IntegrationTests.Fixtures;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace IntegrationTests;

[Collection(RealAcvTool5Collection.Name)]
public sealed class Plan12RealArchiveRepackIntegrationTests(ITestOutputHelper output)
{
    private const string ExpectedArchiveSha256 =
        "3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081";
    private const string ExpectedSampleKeydatSha256 =
        "78F2910819D155CFBF0E366A80FEA4F40126BEA8C201E25EBDB0E0F956E2297F";
    private const string ExpectedToolSha256 =
        "6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    [Trait("Fixture", "RequiresPrivateFixture")]
    public async Task Real_no_edit_pack_with_existing_keydat_preserves_all_logical_assets()
    {
        var context = await RealRepackContext.CreateAsync(output);
        await using (context)
        {
            var workspace = await context.CreateWorkspaceAsync(
                context.RepositoryRoot,
                context.PristineTemplate,
                "Round-trip với keydat có sẵn");
            await using (workspace)
            {
                var extract = await context.ArchiveService.ExtractAsync(new(
                    context.PristineTemplate,
                    workspace.ArchiveWorkspace,
                    new PristineArchiveSource(context.RepositoryRoot),
                    OperationTimeout));
                Assert.True(extract.Command.Succeeded, FormatFailure(extract.Command));

                var catalogBefore = await ScanAsync(context.Scanner, workspace);
                Assert.True(catalogBefore.TotalFileCount > 0);
                Assert.True(catalogBefore.Count(ArchiveAssetKind.Dds) > 0);
                var keydatBefore = context.KeydatService.Describe(
                    workspace.ArchiveWorkspace.SecureWorkspace,
                    context.PristineTemplate.FileName);
                Assert.Equal(KeydatStatus.PresentUnverified, keydatBefore.Status);

                var workingArchive = GetWorkingArchivePath(workspace, context.PristineTemplate.FileName);
                var prePackSize = new FileInfo(workingArchive).Length;
                var prePackHash = await ComputeSha256Async(workingArchive);
                var packProgress = new SynchronousProgress<ArchiveProgress>();
                var pack = await context.ArchiveService.PackAsync(new(
                    context.PristineTemplate,
                    workspace.ArchiveWorkspace,
                    OperationTimeout), packProgress);
                var packRun = context.ObservedRunner.Results.Last();
                WriteRawDiagnostics("existing-keydat", packRun);

                Assert.True(pack.Command.Succeeded, FormatFailure(pack.Command));
                Assert.True(packRun.Succeeded, FormatRunFailure(packRun));
                Assert.Equal(AcvTool5RunnerState.Completed, packRun.State);
                Assert.Equal(1, packRun.ExitCode);
                Assert.Contains("Packing:", packRun.StandardOutput, StringComparison.Ordinal);
                Assert.Contains(packRun.Progress, item =>
                    item.Operation == AcvTool5Operation.Pack && item.CurrentItemPath is not null);
                Assert.Contains(packProgress.Values, item =>
                    item.Operation == ArchiveOperation.Pack && item.State == ArchiveOperationState.Packing);
                Assert.True(File.Exists(workingArchive));
                Assert.True(new FileInfo(workingArchive).Length > 0);
                await using (var readable = File.OpenRead(workingArchive))
                {
                    Assert.True(readable.CanRead);
                }

                Assert.True(Directory.Exists(GetExtractedPath(workspace, context.PristineTemplate)));
                var postPackSize = new FileInfo(workingArchive).Length;
                var postPackHash = await ComputeSha256Async(workingArchive);
                WritePackDiagnostics("existing-keydat", packRun, prePackSize, postPackSize, prePackHash, postPackHash);

                Assert.True(packRun.CountrySelectionSent);
                Assert.Contains("SUPPORT COUNTRY LIST", packRun.StandardOutput, StringComparison.Ordinal);
                Assert.Contains("Select:", packRun.StandardOutput, StringComparison.Ordinal);
                Assert.Equal(KeydatStatus.PresentUnverified, packRun.KeydatStatusBefore);
                Assert.Equal(KeydatStatus.PresentUnverified, packRun.KeydatStatusAfter);
                Assert.True(string.IsNullOrEmpty(packRun.StandardError), packRun.StandardError);

                var repackedSourceRoot = Path.Combine(context.TestRoot, "Repacked Source");
                Directory.CreateDirectory(repackedSourceRoot);
                var repackedSource = Path.Combine(repackedSourceRoot, context.PristineTemplate.FileName);
                File.Copy(workingArchive, repackedSource);
                var repackedTemplate = new AuditionArchiveTemplate(
                    context.PristineTemplate.ArchiveId,
                    context.PristineTemplate.FileName,
                    context.PristineTemplate.SourceRelativePath,
                    context.PristineTemplate.EngineType,
                    context.PristineTemplate.RegionProfileId,
                    context.PristineTemplate.ExpectedExtractFolderName,
                    context.PristineTemplate.TemplateVersion,
                    postPackHash);
                var roundTripWorkspace = await context.CreateWorkspaceAsync(
                    repackedSourceRoot,
                    repackedTemplate,
                    "Extract lại archive đã repack");
                await using (roundTripWorkspace)
                {
                    var reExtract = await context.ArchiveService.ExtractAsync(new(
                        repackedTemplate,
                        roundTripWorkspace.ArchiveWorkspace,
                        new PristineArchiveSource(repackedSourceRoot),
                        OperationTimeout));
                    Assert.True(reExtract.Command.Succeeded, FormatFailure(reExtract.Command));
                    var catalogAfter = await ScanAsync(context.Scanner, roundTripWorkspace);
                    AssertCatalogsEqual(catalogBefore, catalogAfter);
                    WriteCatalog("before-pack", catalogBefore);
                    WriteCatalog("after-reextract", catalogAfter);
                }
            }

            await context.AssertPristineFixturesAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    [Trait("Fixture", "RequiresPrivateFixture")]
    public async Task Real_pack_with_missing_keydat_regenerates_it_using_trusted_region_selection()
    {
        var context = await RealRepackContext.CreateAsync(output);
        await using (context)
        {
            var workspace = await context.CreateWorkspaceAsync(
                context.RepositoryRoot,
                context.PristineTemplate,
                "Pack thiếu keydat");
            await using (workspace)
            {
                var extract = await context.ArchiveService.ExtractAsync(new(
                    context.PristineTemplate,
                    workspace.ArchiveWorkspace,
                    new PristineArchiveSource(context.RepositoryRoot),
                    OperationTimeout));
                Assert.True(extract.Command.Succeeded, FormatFailure(extract.Command));

                var keydat = context.KeydatService.Describe(
                    workspace.ArchiveWorkspace.SecureWorkspace,
                    context.PristineTemplate.FileName);
                Assert.Equal(KeydatStatus.PresentUnverified, keydat.Status);
                File.Delete(keydat.KeydatPath);
                Assert.Equal(
                    KeydatStatus.Missing,
                    context.KeydatService.Describe(
                        workspace.ArchiveWorkspace.SecureWorkspace,
                        context.PristineTemplate.FileName).Status);

                var workingArchive = GetWorkingArchivePath(workspace, context.PristineTemplate.FileName);
                var prePackSize = new FileInfo(workingArchive).Length;
                var prePackHash = await ComputeSha256Async(workingArchive);

                var pack = await context.ArchiveService.PackAsync(new(
                    context.PristineTemplate,
                    workspace.ArchiveWorkspace,
                    OperationTimeout));
                var packRun = context.ObservedRunner.Results.Last();
                WriteRawDiagnostics("missing-keydat", packRun);

                Assert.True(pack.Command.Succeeded, FormatFailure(pack.Command));
                Assert.True(packRun.Succeeded, FormatRunFailure(packRun));
                Assert.Equal(1, packRun.ExitCode);
                Assert.True(packRun.CountrySelectionSent);
                Assert.Equal(KeydatStatus.Missing, packRun.KeydatStatusBefore);
                Assert.Equal(KeydatStatus.PresentUnverified, packRun.KeydatStatusAfter);
                Assert.Contains("SUPPORT COUNTRY LIST", packRun.StandardOutput, StringComparison.Ordinal);
                Assert.Contains("Select:", packRun.StandardOutput, StringComparison.Ordinal);
                Assert.Contains("Packing:", packRun.StandardOutput, StringComparison.Ordinal);
                Assert.True(File.Exists(keydat.KeydatPath));
                Assert.True(new FileInfo(keydat.KeydatPath).Length > 0);
                Assert.True(string.IsNullOrEmpty(packRun.StandardError), packRun.StandardError);
                WritePackDiagnostics(
                    "missing-keydat",
                    packRun,
                    prePackSize,
                    new FileInfo(workingArchive).Length,
                    prePackHash,
                    await ComputeSha256Async(workingArchive));
            }

            await context.AssertPristineFixturesAsync();
        }
    }

    private static async Task<ArchiveAssetCatalog> ScanAsync(
        ArchiveAssetScanner scanner,
        IProjectArchiveWorkspace workspace)
    {
        var result = await scanner.ScanAsync(workspace);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return Assert.IsType<ArchiveAssetCatalog>(result.Catalog);
    }

    private static void AssertCatalogsEqual(ArchiveAssetCatalog expected, ArchiveAssetCatalog actual)
    {
        Assert.Equal(expected.TotalFileCount, actual.TotalFileCount);
        var expectedByIdentity = expected.Assets.ToDictionary(asset => asset.Identity, StringComparer.Ordinal);
        var actualByIdentity = actual.Assets.ToDictionary(asset => asset.Identity, StringComparer.Ordinal);
        Assert.Equal(expectedByIdentity.Keys.Order(StringComparer.Ordinal), actualByIdentity.Keys.Order(StringComparer.Ordinal));
        foreach (var (identity, expectedAsset) in expectedByIdentity)
        {
            var actualAsset = actualByIdentity[identity];
            Assert.Equal(expectedAsset.FileSize, actualAsset.FileSize);
            Assert.Equal(expectedAsset.AssetKind, actualAsset.AssetKind);
            Assert.Equal(expectedAsset.Sha256, actualAsset.Sha256);
        }
    }

    private static string GetWorkingArchivePath(IProjectArchiveWorkspace workspace, string fileName) =>
        Path.Combine(workspace.ArchiveWorkspace.SecureWorkspace.Paths.WorkingDirectory, fileName);

    private static string GetExtractedPath(
        IProjectArchiveWorkspace workspace,
        AuditionArchiveTemplate template) =>
        Path.Combine(
            workspace.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
            template.ExpectedExtractFolderName);

    private static async Task<string> ComputeSha256Async(string path)
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

    private void WritePackDiagnostics(
        string scenario,
        AcvTool5RunResult run,
        long prePackSize,
        long postPackSize,
        string prePackHash,
        string postPackHash) => output.WriteLine(
        "PLAN 12 {0}: state={1}; exit={2}; selectionSent={3}; items={4}; keydatBefore={5}; keydatAfter={6}; stdoutChars={7}; stderrChars={8}; preSize={9}; postSize={10}; preSha256={11}; postSha256={12}",
        scenario,
        run.State,
        run.ExitCode,
        run.CountrySelectionSent,
        run.Progress.Max(item => item.ProcessedItemCount),
        run.KeydatStatusBefore,
        run.KeydatStatusAfter,
        run.StandardOutput.Length,
        run.StandardError.Length,
        prePackSize,
        postPackSize,
        prePackHash,
        postPackHash);

    private void WriteRawDiagnostics(string scenario, AcvTool5RunResult run)
    {
        output.WriteLine(
            "PLAN 12 raw {0}: state={1}; exit={2}; selectionSent={3}; keydatBefore={4}; keydatAfter={5}",
            scenario,
            run.State,
            run.ExitCode,
            run.CountrySelectionSent,
            run.KeydatStatusBefore,
            run.KeydatStatusAfter);
        if (!run.Succeeded)
        {
            output.WriteLine("PLAN 12 bounded stdout {0}: {1}", scenario, run.StandardOutput);
            output.WriteLine("PLAN 12 bounded stderr {0}: {1}", scenario, run.StandardError);
        }
    }

    private void WriteCatalog(string label, ArchiveAssetCatalog catalog) => output.WriteLine(
        "PLAN 12 catalog {0}: total={1}; directories={2}; dds={3}; png={4}; slk={5}; rgm={6}; other={7}; bytes={8}",
        label,
        catalog.TotalFileCount,
        catalog.DirectoryCount,
        catalog.Count(ArchiveAssetKind.Dds),
        catalog.Count(ArchiveAssetKind.Png),
        catalog.Count(ArchiveAssetKind.Slk),
        catalog.Count(ArchiveAssetKind.Rgm),
        catalog.Count(ArchiveAssetKind.Other),
        catalog.TotalByteSize);

    private static string FormatFailure(ArchiveCommandResult result) =>
        $"state={result.FinalState}; reason={result.FailureReason}; diagnostics={string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}"))}";

    private static string FormatRunFailure(AcvTool5RunResult result) =>
        $"state={result.State}; exit={result.ExitCode}; diagnostics={string.Join(" | ", result.Diagnostics)}; stdout={result.StandardOutput}; stderr={result.StandardError}";

    private sealed class RealRepackContext : IAsyncDisposable
    {
        private readonly string _archiveSourcePath;
        private readonly string _toolSourcePath;
        private readonly string _sampleKeydatPath;
        private readonly ArchiveToolProvisioningService _provisioningService;
        private readonly SecureWorkspaceService _secureWorkspaceService;

        private RealRepackContext(
            string testRoot,
            string repositoryRoot,
            string archiveSourcePath,
            string toolSourcePath,
            string sampleKeydatPath,
            KeydatService keydatService,
            ArchiveToolProvisioningService provisioningService,
            SecureWorkspaceService secureWorkspaceService,
            ProjectArchiveWorkspaceService projectWorkspaceService,
            AuditionArchiveService archiveService,
            ObservingArchiveToolRunner observedRunner,
            ArchiveAssetScanner scanner)
        {
            TestRoot = testRoot;
            RepositoryRoot = repositoryRoot;
            _archiveSourcePath = archiveSourcePath;
            _toolSourcePath = toolSourcePath;
            _sampleKeydatPath = sampleKeydatPath;
            KeydatService = keydatService;
            _provisioningService = provisioningService;
            _secureWorkspaceService = secureWorkspaceService;
            ProjectWorkspaceService = projectWorkspaceService;
            ArchiveService = archiveService;
            ObservedRunner = observedRunner;
            Scanner = scanner;
            PristineTemplate = new(
                "archive-015",
                "015.ab",
                "015.ab",
                ArchiveEngineType.AcvTool5,
                GameRegionProfile.AuditionVietnam.RegionId,
                "015",
                templateVersion: "real-fixture-v1",
                sha256: ExpectedArchiveSha256);
        }

        public string TestRoot { get; }

        public string RepositoryRoot { get; }

        public KeydatService KeydatService { get; }

        public ProjectArchiveWorkspaceService ProjectWorkspaceService { get; }

        public AuditionArchiveService ArchiveService { get; }

        public ObservingArchiveToolRunner ObservedRunner { get; }

        public ArchiveAssetScanner Scanner { get; }

        public AuditionArchiveTemplate PristineTemplate { get; }

        public static async Task<RealRepackContext> CreateAsync(ITestOutputHelper output)
        {
            SkipWhenUnavailable();
            var pathSecurity = new PathSecurity();
            var locator = new RepositoryFixtureLocator(pathSecurity);
            var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
            var archiveSourcePath = locator.ResolveSourcePath(repositoryRoot, RealSampleFixtureCatalog.Archive015);
            var toolSourcePath = locator.ResolveSourcePath(repositoryRoot, RealSampleFixtureCatalog.AcvTool);
            var sampleKeydatPath = Path.Combine(repositoryRoot, "015.keydat");
            var testRoot = Path.Combine(Path.GetTempPath(), $"Sàn Audition Repack Test {Guid.NewGuid():N}");
            var appPaths = new AppPaths(testRoot);
            var manifest = TrustedArchiveToolManifest.Production;
            var integrityPolicy = new ArchiveToolIntegrityPolicy(pathSecurity, manifest);
            var provisioningService = new ArchiveToolProvisioningService(pathSecurity, manifest, integrityPolicy);
            var keydatService = new KeydatService(pathSecurity);
            var productionRunner = new AcvTool5Runner(pathSecurity, integrityPolicy, keydatService);
            var observedRunner = new ObservingArchiveToolRunner(productionRunner);
            var engine = new AcvTool5ArchiveEngine(
                provisioningService,
                keydatService,
                observedRunner,
                new GameRegionProfileCatalog(),
                new AcvTool5ArchiveEngineOptions(
                    repositoryRoot,
                    RealSampleFixtureCatalog.AcvTool.RepositoryRelativePath,
                    MaximumDiagnosticCharacters: 5_242_880));
            var archiveService = new AuditionArchiveService([engine], pathSecurity);
            var secureWorkspaceService = new SecureWorkspaceService(appPaths, pathSecurity);
            var projectWorkspaceService = new ProjectArchiveWorkspaceService(
                appPaths,
                pathSecurity,
                secureWorkspaceService,
                new ProjectArchiveWorkspaceManifestStore(pathSecurity));
            var context = new RealRepackContext(
                testRoot,
                repositoryRoot,
                archiveSourcePath,
                toolSourcePath,
                sampleKeydatPath,
                keydatService,
                provisioningService,
                secureWorkspaceService,
                projectWorkspaceService,
                archiveService,
                observedRunner,
                new ArchiveAssetScanner(pathSecurity));
            try
            {
                await context.AssertPristineFixturesAsync();
                return context;
            }
            catch
            {
                await context.DisposeAsync();
                throw;
            }
        }

        public async Task<IProjectArchiveWorkspace> CreateWorkspaceAsync(
            string sourceRoot,
            AuditionArchiveTemplate template,
            string displayName)
        {
            var result = await ProjectWorkspaceService.CreateAsync(new(
                displayName,
                template,
                new PristineArchiveSource(sourceRoot)));
            Assert.True(result.Succeeded, result.DiagnosticCode);
            return Assert.IsAssignableFrom<IProjectArchiveWorkspace>(result.Workspace);
        }

        public async Task AssertPristineFixturesAsync()
        {
            Assert.Equal(ExpectedArchiveSha256, await ComputeSha256Async(_archiveSourcePath));
            Assert.Equal(ExpectedToolSha256, await ComputeSha256Async(_toolSourcePath));
            Assert.Equal(ExpectedSampleKeydatSha256, await ComputeSha256Async(_sampleKeydatPath));
        }

        public async ValueTask DisposeAsync()
        {
            KeydatService.Dispose();
            _provisioningService.Dispose();
            await _secureWorkspaceService.DisposeAsync();
            if (Directory.Exists(TestRoot))
            {
                Directory.Delete(TestRoot, true);
            }
        }

        private static void SkipWhenUnavailable()
        {
            if (!OperatingSystem.IsWindows())
            {
                throw SkipException.ForSkip("PLAN 12 requires Windows to execute ACV Tool 5.");
            }

            var pathSecurity = new PathSecurity();
            var locator = new RepositoryFixtureLocator(pathSecurity);
            var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
            var missing = new[]
            {
                RealSampleFixtureCatalog.Archive015.RepositoryRelativePath,
                RealSampleFixtureCatalog.AcvTool.RepositoryRelativePath,
                "015.keydat",
            }.Where(path => !File.Exists(Path.Combine(repositoryRoot, path))).ToArray();
            if (missing.Length > 0)
            {
                throw SkipException.ForSkip($"PLAN 12 private fixtures unavailable: {string.Join(", ", missing)}.");
            }
        }
    }

    private sealed class ObservingArchiveToolRunner(IArchiveToolRunner inner) : IArchiveToolRunner
    {
        private readonly List<AcvTool5RunResult> _results = [];

        public IReadOnlyList<AcvTool5RunResult> Results => _results;

        public async Task<AcvTool5RunResult> RunAsync(
            AcvTool5RunRequest request,
            IProgress<AcvTool5Progress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.RunAsync(request, progress, cancellationToken);
            _results.Add(result);
            return result;
        }
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
