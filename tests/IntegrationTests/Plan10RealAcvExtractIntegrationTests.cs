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
public sealed class Plan10RealAcvExtractIntegrationTests(ITestOutputHelper output)
{
    private const string ExpectedArchiveSha256 =
        "3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081";
    private const string ExpectedSampleKeydatSha256 =
        "78F2910819D155CFBF0E366A80FEA4F40126BEA8C201E25EBDB0E0F956E2297F";
    private const string ExpectedToolSha256 =
        "6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3";
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    [Trait("Fixture", "RequiresPrivateFixture")]
    public async Task Real_extract_without_then_with_keydat_passes_gate_a_in_unicode_workspace()
    {
        SkipWhenEnvironmentCannotRunPrivateFixture();

        var pathSecurity = new PathSecurity();
        var locator = new RepositoryFixtureLocator(pathSecurity);
        var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
        var archiveSourcePath = locator.ResolveSourcePath(repositoryRoot, RealSampleFixtureCatalog.Archive015);
        var toolSourcePath = locator.ResolveSourcePath(repositoryRoot, RealSampleFixtureCatalog.AcvTool);
        var sampleKeydatPath = Path.Combine(repositoryRoot, "015.keydat");
        await AssertPristineFixturesAsync(archiveSourcePath, toolSourcePath, sampleKeydatPath);

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"Sàn Audition Test {Guid.NewGuid():N}");
        var appPaths = new AppPaths(testRoot);
        var manifest = TrustedArchiveToolManifest.Production;
        var integrityPolicy = new ArchiveToolIntegrityPolicy(pathSecurity, manifest);
        using var provisioningService = new ArchiveToolProvisioningService(
            pathSecurity,
            manifest,
            integrityPolicy);
        using var keydatService = new KeydatService(pathSecurity);
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
        await using var secureWorkspaceService = new SecureWorkspaceService(appPaths, pathSecurity);
        var projectWorkspaceService = new ProjectArchiveWorkspaceService(
            appPaths,
            pathSecurity,
            secureWorkspaceService,
            new ProjectArchiveWorkspaceManifestStore(pathSecurity));
        var archiveTemplate = new AuditionArchiveTemplate(
            "archive-015",
            "015.ab",
            "015.ab",
            ArchiveEngineType.AcvTool5,
            GameRegionProfile.AuditionVietnam.RegionId,
            "015",
            templateVersion: "real-fixture-v1",
            sha256: ExpectedArchiveSha256);

        string? firstWorkspaceRoot = null;
        IProjectArchiveWorkspace? firstWorkspace = null;
        try
        {
            firstWorkspace = await CreateProjectWorkspaceAsync(
                projectWorkspaceService,
                archiveTemplate,
                repositoryRoot,
                "Kiểm thử extract thật lần đầu");
            firstWorkspaceRoot = firstWorkspace.ArchiveWorkspace.SecureWorkspace.Paths.RootDirectory;
            Assert.Contains("Sàn Audition Test ", firstWorkspaceRoot, StringComparison.Ordinal);

            var firstKeydatBefore = keydatService.Describe(
                firstWorkspace.ArchiveWorkspace.SecureWorkspace,
                archiveTemplate.FileName);
            Assert.Equal(KeydatStatus.Missing, firstKeydatBefore.Status);
            Assert.False(File.Exists(firstKeydatBefore.KeydatPath));

            var firstExtract = await archiveService.ExtractAsync(new(
                archiveTemplate,
                firstWorkspace.ArchiveWorkspace,
                new PristineArchiveSource(repositoryRoot),
                ExtractTimeout));
            var firstRun = observedRunner.Results.Single();
            WriteRunDiagnostics("first-run", firstRun);

            Assert.True(firstExtract.Command.Succeeded, FormatCommandFailure(firstExtract.Command));
            Assert.True(firstRun.Succeeded, FormatRunFailure(firstRun));
            Assert.Equal(AcvTool5RunnerState.Completed, firstRun.State);
            Assert.Equal(0, firstRun.ExitCode);
            Assert.True(firstRun.CountrySelectionSent);
            Assert.Single(
                firstRun.Progress,
                item => item.State == AcvTool5RunnerState.WaitingForCountrySelection);
            Assert.Equal(KeydatStatus.Missing, firstRun.KeydatStatusBefore);
            Assert.Equal(KeydatStatus.PresentUnverified, firstRun.KeydatStatusAfter);
            Assert.Contains("SUPPORT COUNTRY LIST", firstRun.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("AuditionVN", firstRun.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Select:", firstRun.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("writing :", firstRun.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFFFD', firstRun.StandardOutput);
            Assert.Contains(firstRun.Progress, item =>
                item.Operation == AcvTool5Operation.Extract
                && item.CurrentItemPath is not null);

            var generatedKeydat = keydatService.Describe(
                firstWorkspace.ArchiveWorkspace.SecureWorkspace,
                archiveTemplate.FileName);
            Assert.Equal(KeydatStatus.PresentUnverified, generatedKeydat.Status);
            Assert.True(generatedKeydat.Length > 0);
            var generatedKeydatSha256 = await ComputeSha256Async(generatedKeydat.KeydatPath);
            output.WriteLine(
                "Generated keydat: file={0}; bytes={1}; sha256={2}; status={3}; matches sample={4}",
                Path.GetFileName(generatedKeydat.KeydatPath),
                generatedKeydat.Length,
                generatedKeydatSha256,
                generatedKeydat.Status,
                string.Equals(
                    generatedKeydatSha256,
                    ExpectedSampleKeydatSha256,
                    StringComparison.OrdinalIgnoreCase));

            var firstExtractDirectory = Path.Combine(
                firstWorkspace.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
                archiveTemplate.ExpectedExtractFolderName);
            var firstInventory = BuildInventory(firstExtractDirectory);
            WriteInventory("first-run", firstInventory);
            Assert.True(Directory.Exists(firstExtractDirectory));
            Assert.True(firstInventory.FileCount > 0);
            var scanResult = await new ArchiveAssetScanner(pathSecurity).ScanAsync(firstWorkspace);
            Assert.True(scanResult.IsSuccess, scanResult.ErrorCode);
            var assetCatalog = Assert.IsType<ArchiveAssetCatalog>(scanResult.Catalog);
            Assert.Equal(firstInventory.FileCount, assetCatalog.TotalFileCount);
            Assert.Equal(firstInventory.TotalBytes, assetCatalog.TotalByteSize);
            Assert.All(assetCatalog.Assets, asset => Assert.False(Path.IsPathFullyQualified(asset.RelativePath)));
            output.WriteLine(
                "PLAN 11 inventory: files={0}, directories={1}, dds={2}, png={3}, slk={4}, rgm={5}, other={6}, bytes={7}",
                assetCatalog.TotalFileCount,
                assetCatalog.DirectoryCount,
                assetCatalog.Count(ArchiveAssetKind.Dds),
                assetCatalog.Count(ArchiveAssetKind.Png),
                assetCatalog.Count(ArchiveAssetKind.Slk),
                assetCatalog.Count(ArchiveAssetKind.Rgm),
                assetCatalog.Count(ArchiveAssetKind.Other),
                assetCatalog.TotalByteSize);
            var workingArchivePath = Path.Combine(
                firstWorkspace.ArchiveWorkspace.SecureWorkspace.Paths.WorkingDirectory,
                archiveTemplate.FileName);
            Assert.True(File.Exists(workingArchivePath));
            Assert.Equal(ExpectedArchiveSha256, await ComputeSha256Async(workingArchivePath));

            var secondExtract = await archiveService.ExtractAsync(new(
                archiveTemplate,
                firstWorkspace.ArchiveWorkspace,
                new PristineArchiveSource(repositoryRoot),
                ExtractTimeout));
            var secondRun = observedRunner.Results.ElementAt(1);
            WriteRunDiagnostics("existing-keydat", secondRun);

            Assert.True(secondExtract.Command.Succeeded, FormatCommandFailure(secondExtract.Command));
            Assert.True(secondRun.Succeeded, FormatRunFailure(secondRun));
            Assert.Equal(AcvTool5RunnerState.Completed, secondRun.State);
            Assert.Equal(0, secondRun.ExitCode);
            Assert.True(secondRun.CountrySelectionSent);
            Assert.Single(
                secondRun.Progress,
                item => item.State == AcvTool5RunnerState.WaitingForCountrySelection);
            Assert.Equal(KeydatStatus.PresentUnverified, secondRun.KeydatStatusBefore);
            Assert.Equal(KeydatStatus.PresentUnverified, secondRun.KeydatStatusAfter);
            Assert.Contains("SUPPORT COUNTRY LIST", secondRun.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Select:", secondRun.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("writing :", secondRun.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFFFD', secondRun.StandardOutput);
            Assert.Equal(ExpectedArchiveSha256, await ComputeSha256Async(workingArchivePath));

            var secondExtractDirectory = Path.Combine(
                firstWorkspace.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
                archiveTemplate.ExpectedExtractFolderName);
            var secondInventory = BuildInventory(secondExtractDirectory);
            WriteInventory("existing-keydat", secondInventory);
            Assert.True(secondInventory.FileCount > 0);
            Assert.Equal(2, observedRunner.Results.Count);
        }
        finally
        {
            if (firstWorkspace is not null)
            {
                await firstWorkspace.DisposeAsync();
            }

            if (firstWorkspaceRoot is not null)
            {
                Assert.False(Directory.Exists(firstWorkspaceRoot));
            }
        }

        await AssertPristineFixturesAsync(archiveSourcePath, toolSourcePath, sampleKeydatPath);
    }

    private static void SkipWhenEnvironmentCannotRunPrivateFixture()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("PLAN 10 requires Windows to execute ACV Tool 5.");
        }

        var pathSecurity = new PathSecurity();
        var locator = new RepositoryFixtureLocator(pathSecurity);
        var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
        var required = new[]
        {
            RealSampleFixtureCatalog.Archive015,
            RealSampleFixtureCatalog.AcvTool,
        };
        var missing = required
            .Where(item => !File.Exists(locator.ResolveSourcePath(repositoryRoot, item)))
            .Select(item => item.FileName)
            .ToArray();
        if (missing.Length > 0)
        {
            throw SkipException.ForSkip(
                $"PLAN 10 requires private fixtures that are unavailable: {string.Join(", ", missing)}.");
        }
    }

    private static async Task<IProjectArchiveWorkspace> CreateProjectWorkspaceAsync(
        ProjectArchiveWorkspaceService service,
        AuditionArchiveTemplate archiveTemplate,
        string repositoryRoot,
        string displayName)
    {
        var result = await service.CreateAsync(new(
            displayName,
            archiveTemplate,
            new PristineArchiveSource(repositoryRoot)));
        Assert.True(result.Succeeded, result.DiagnosticCode);
        return Assert.IsAssignableFrom<IProjectArchiveWorkspace>(result.Workspace);
    }

    private static async Task AssertPristineFixturesAsync(
        string archivePath,
        string toolPath,
        string sampleKeydatPath)
    {
        Assert.Equal(ExpectedArchiveSha256, await ComputeSha256Async(archivePath));
        Assert.Equal(ExpectedToolSha256, await ComputeSha256Async(toolPath));
        if (File.Exists(sampleKeydatPath))
        {
            Assert.Equal(ExpectedSampleKeydatSha256, await ComputeSha256Async(sampleKeydatPath));
        }
    }

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

    private static ExtractInventory BuildInventory(string extractedRoot)
    {
        Assert.True(Directory.Exists(extractedRoot));
        var files = Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToArray();
        var extensionCounts = files
            .GroupBy(
                file => string.IsNullOrEmpty(file.Extension) ? "<none>" : file.Extension.ToLowerInvariant(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return new(
            files.Length,
            Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.AllDirectories).Count(),
            extensionCounts.GetValueOrDefault(".dds"),
            extensionCounts.GetValueOrDefault(".png"),
            files.Sum(file => file.Length),
            extensionCounts);
    }

    private void WriteRunDiagnostics(string operation, AcvTool5RunResult result)
    {
        output.WriteLine(
            "ACV {0}: state={1}; exit={2}; selectionSent={3}; items={4}; stdoutChars={5}; stdoutTruncated={6}; stderrChars={7}; stderrTruncated={8}; replacementChars={9}; promptTermination={10}",
            operation,
            result.State,
            result.ExitCode,
            result.CountrySelectionSent,
            result.Progress.Max(item => item.ProcessedItemCount),
            result.StandardOutput.Length,
            result.StandardOutputTruncated,
            result.StandardError.Length,
            result.StandardErrorTruncated,
            result.StandardOutput.Count(character => character == '\uFFFD'),
            DescribePromptTermination(result.StandardOutput));
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            output.WriteLine("ACV {0} bounded stderr: {1}", operation, result.StandardError);
        }
    }

    private void WriteInventory(string operation, ExtractInventory inventory)
    {
        var otherExtensions = inventory.ExtensionCounts
            .Where(item => item.Key is not ".dds" and not ".png")
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={item.Value}");
        output.WriteLine(
            "Inventory {0}: files={1}; directories={2}; dds={3}; png={4}; other=[{5}]; totalBytes={6}",
            operation,
            inventory.FileCount,
            inventory.DirectoryCount,
            inventory.DdsCount,
            inventory.PngCount,
            string.Join(", ", otherExtensions),
            inventory.TotalBytes);
    }

    private static string DescribePromptTermination(string standardOutput)
    {
        var promptIndex = standardOutput.IndexOf("Select:", StringComparison.Ordinal);
        if (promptIndex < 0)
        {
            return "not-observed";
        }

        var nextIndex = promptIndex + "Select:".Length;
        if (nextIndex >= standardOutput.Length)
        {
            return "no-trailing-character-observed";
        }

        return standardOutput[nextIndex] switch
        {
            '\r' => "carriage-return",
            '\n' => "line-feed",
            _ => $"non-newline-U+{(int)standardOutput[nextIndex]:X4}",
        };
    }

    private static string FormatCommandFailure(ArchiveCommandResult result) =>
        $"state={result.FinalState}; reason={result.FailureReason}; diagnostics={string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}"))}";

    private static string FormatRunFailure(AcvTool5RunResult result) =>
        $"state={result.State}; exit={result.ExitCode}; diagnostics={string.Join(" | ", result.Diagnostics)}; stdout={result.StandardOutput}; stderr={result.StandardError}";

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

    private sealed record ExtractInventory(
        int FileCount,
        int DirectoryCount,
        int DdsCount,
        int PngCount,
        long TotalBytes,
        IReadOnlyDictionary<string, int> ExtensionCounts);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealAcvTool5Collection
{
    public const string Name = "Real ACV Tool 5 integration";
}
