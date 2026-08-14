using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Dds;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace IntegrationTests;

[Collection(RealAcvTool5Collection.Name)]
public sealed class Plan50RealReplacePackGateTests(ITestOutputHelper output)
{
    private const string ArchiveHash = "3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081";
    private const string KeydatHash = "78F2910819D155CFBF0E366A80FEA4F40126BEA8C201E25EBDB0E0F956E2297F";
    private const string ToolHash = "6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3";
    private const string TargetRelativePath = "texture/hud/pointer.dds";

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    [Trait("Fixture", "RequiresPrivateFixture")]
    public async Task Real_replace_one_safe_dds_pack_and_reextract_preserves_all_other_assets()
    {
        var prerequisites = RequirePrerequisites();
        var originalArchive = Path.Combine(prerequisites.RepositoryRoot, "015.ab");
        var originalKeydat = Path.Combine(prerequisites.RepositoryRoot, "015.keydat");
        var originalTool = Path.Combine(prerequisites.RepositoryRoot, "acv.exe");
        var originalExtracted = Path.Combine(prerequisites.RepositoryRoot, "015");
        var originalTarget = Path.Combine(originalExtracted,
            TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(ArchiveHash, await HashAsync(originalArchive));
        Assert.Equal(KeydatHash, await HashAsync(originalKeydat));
        Assert.Equal(ToolHash, await HashAsync(originalTool));
        var originalTargetHash = await HashAsync(originalTarget);
        var originalCatalog = await HashTreeAsync(originalExtracted);

        var testRoot = Path.Combine(Path.GetTempPath(), "Audition PLAN 50 Gate C", Guid.NewGuid().ToString("N"));
        var pathSecurity = new PathSecurity();
        var appPaths = new AppPaths(testRoot);
        await using var secureWorkspaceService = new SecureWorkspaceService(appPaths, pathSecurity);
        using var provisioning = new ArchiveToolProvisioningService(
            pathSecurity,
            TrustedArchiveToolManifest.Production,
            new ArchiveToolIntegrityPolicy(pathSecurity, TrustedArchiveToolManifest.Production));
        var integrity = new ArchiveToolIntegrityPolicy(pathSecurity, TrustedArchiveToolManifest.Production);
        var keydat = new KeydatService(pathSecurity);
        var runner = new AcvTool5Runner(pathSecurity, integrity, keydat);
        var engine = new AcvTool5ArchiveEngine(
            provisioning,
            keydat,
            runner,
            new GameRegionProfileCatalog(),
            new(prerequisites.RepositoryRoot, "acv.exe", 5_242_880));
        var archiveService = new AuditionArchiveService([engine], pathSecurity);
        var template = new AuditionArchiveTemplate(
            "archive-015", "015.ab", "015.ab", ArchiveEngineType.AcvTool5,
            "audition_vn", "015", "plan50", ArchiveHash, "audition-vn-gate-c");

        try
        {
            await using var buildWorkspace = await secureWorkspaceService.CreateAsync();
            await CopyFileAsync(originalArchive,
                Path.Combine(buildWorkspace.Paths.WorkingDirectory, "015.ab"));
            await CopyFileAsync(originalKeydat,
                Path.Combine(buildWorkspace.Paths.WorkingDirectory, "015.keydat"));
            await CopyTreeAsync(originalExtracted,
                Path.Combine(buildWorkspace.Paths.ExtractedDirectory, "015"));

            var metadataReader = new DdsMetadataReader();
            var targetPath = buildWorkspace.ResolveRelativePath(
                $"Extracted/015/{TargetRelativePath}".Replace('/', Path.DirectorySeparatorChar));
            var targetMetadata = await metadataReader.ReadAsync(targetPath);
            Assert.True(targetMetadata.IsSuccess, targetMetadata.ErrorCode);
            Assert.Equal(164, targetMetadata.Metadata!.Width);
            Assert.Equal(128, targetMetadata.Metadata.Height);
            Assert.Equal(DdsFormat.BC3, targetMetadata.Metadata.Format);
            Assert.Equal(1u, targetMetadata.Metadata.EffectiveMipLevelCount);

            var harness = new DirectXTexEvaluationHarness(
                pathSecurity, metadataReader, DirectXTexEvaluationToolCatalog.May2026X64);
            var encoder = new DdsEncoder(
                metadataReader,
                harness,
                pathSecurity,
                new(prerequisites.TexconvPath, TimeSpan.FromMinutes(2), DdsPreviewResourcePolicy.Default));
            var validator = new DdsValidationService(metadataReader, pathSecurity);
            var matcher = new DdsMatchOriginalService(metadataReader, encoder, validator);
            var replacement = await matcher.MatchAsync(new(
                buildWorkspace,
                $"Extracted/015/{TargetRelativePath}",
                CreateGatePattern(164, 128),
                "BuildOutput/Plan50/pointer-replacement.dds"));
            Assert.True(replacement.Succeeded, replacement.DiagnosticCode);
            Assert.True(replacement.MatchReport!.OverallMatch);
            var validation = await validator.ValidateAsync(new(
                buildWorkspace,
                $"Extracted/015/{TargetRelativePath}",
                replacement.OutputRelativePath!));
            Assert.True(validation.Succeeded, validation.DiagnosticCode);

            var replacementPath = buildWorkspace.ResolveRelativePath(replacement.OutputRelativePath!);
            var backupPath = buildWorkspace.ResolveRelativePath("BuildOutput/Plan50/pointer-original.backup");
            await CopyFileAsync(targetPath, backupPath);
            File.Move(replacementPath, targetPath, overwrite: true);
            File.Delete(backupPath);
            var replacementHash = await HashAsync(targetPath);
            Assert.NotEqual(originalTargetHash, replacementHash);

            var archiveWorkspace = ArchiveWorkspace.Create(buildWorkspace, template);
            var progress = new ProgressCollector();
            var pack = await archiveService.PackAsync(
                new(template, archiveWorkspace, TimeSpan.FromMinutes(3)), progress);
            Assert.True(pack.Command.Succeeded, FormatFailure(pack.Command));
            Assert.True(pack.Command.ProcessedItemCount > 0);
            Assert.Contains(progress.Items, item => item.State == ArchiveOperationState.Packing);

            var packedPath = Path.Combine(buildWorkspace.Paths.WorkingDirectory, "015.ab");
            var packedHash = await HashAsync(packedPath);
            Assert.NotEqual(ArchiveHash, packedHash);
            Directory.CreateDirectory(prerequisites.OutputDirectory);
            var manualArtifact = Path.Combine(prerequisites.OutputDirectory, "015-plan50-pointer.ab");
            await CopyReplacingAsync(packedPath, manualArtifact);
            Assert.Equal(packedHash, await HashAsync(manualArtifact));

            var repackedSource = Path.Combine(testRoot, "RepackedSource");
            Directory.CreateDirectory(repackedSource);
            await CopyFileAsync(packedPath, Path.Combine(repackedSource, "015.ab"));
            var repackedTemplate = template with { };
            repackedTemplate = new(
                repackedTemplate.ArchiveId,
                repackedTemplate.FileName,
                repackedTemplate.SourceRelativePath,
                repackedTemplate.EngineType,
                repackedTemplate.RegionProfileId,
                repackedTemplate.ExpectedExtractFolderName,
                repackedTemplate.TemplateVersion?.Value,
                packedHash,
                repackedTemplate.CompatibleGameBuild?.Value);
            await using var verifyWorkspace = await secureWorkspaceService.CreateAsync();
            var extract = await archiveService.ExtractAsync(new(
                repackedTemplate,
                ArchiveWorkspace.Create(verifyWorkspace, repackedTemplate),
                new(repackedSource),
                TimeSpan.FromMinutes(3)));
            Assert.True(extract.Command.Succeeded, FormatFailure(extract.Command));

            var verifiedRoot = Path.Combine(verifyWorkspace.Paths.ExtractedDirectory, "015");
            var verifiedCatalog = await HashTreeAsync(verifiedRoot);
            Assert.Equal(originalCatalog.Keys.Order(), verifiedCatalog.Keys.Order());
            foreach (var item in originalCatalog)
            {
                if (item.Key.Equals(TargetRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal(replacementHash, verifiedCatalog[item.Key]);
                }
                else
                {
                    Assert.Equal(item.Value, verifiedCatalog[item.Key]);
                }
            }

            Assert.Equal(ArchiveHash, await HashAsync(originalArchive));
            Assert.Equal(KeydatHash, await HashAsync(originalKeydat));
            Assert.Equal(ToolHash, await HashAsync(originalTool));
            Assert.Equal(originalTargetHash, await HashAsync(originalTarget));
            output.WriteLine(
                "PLAN 50 TECHNICAL: target={0}; original={1}; replacement={2}; packed={3}; files={4}; artifact={5}",
                TargetRelativePath,
                originalTargetHash,
                replacementHash,
                packedHash,
                verifiedCatalog.Count,
                manualArtifact);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static GatePrerequisites RequirePrerequisites()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("PLAN 50 Gate C requires Windows.");
        }

        var repositoryRoot = FindRepositoryRoot();
        var texconv = Environment.GetEnvironmentVariable("AUDITION_DIRECTXTEX_TEXCONV_PATH");
        var outputDirectory = Environment.GetEnvironmentVariable("AUDITION_PLAN50_OUTPUT_DIRECTORY");
        var required = new[]
        {
            Path.Combine(repositoryRoot, "015.ab"),
            Path.Combine(repositoryRoot, "015.keydat"),
            Path.Combine(repositoryRoot, "acv.exe"),
            Path.Combine(repositoryRoot, "015"),
            Path.Combine(repositoryRoot, "015", TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            texconv ?? string.Empty,
        };
        if (required.Any(path => string.IsNullOrWhiteSpace(path)
                                 || (!File.Exists(path) && !Directory.Exists(path))))
        {
            throw SkipException.ForSkip("PLAN 50 private fixtures or approved texconv are unavailable.");
        }
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
        {
            throw SkipException.ForSkip("PLAN 50 output directory was not explicitly configured.");
        }

        return new(repositoryRoot, texconv!, Path.GetFullPath(outputDirectory));
    }

    private static DdsRgbaImage CreateGatePattern(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = checked((y * width + x) * 4);
                var magenta = ((x / 16) + (y / 16)) % 2 == 0;
                pixels[offset] = magenta ? (byte)255 : (byte)0;
                pixels[offset + 1] = magenta ? (byte)0 : (byte)255;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }
        return DdsRgbaImage.Create(width, height, checked(width * 4), pixels);
    }

    private static async Task<Dictionary<string, string>> HashTreeAsync(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            result.Add(relative, await HashAsync(path));
        }
        return result;
    }

    private static async Task CopyTreeAsync(string sourceRoot, string destinationRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            await CopyFileAsync(file, Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file)));
        }
    }

    private static async Task CopyFileAsync(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var outputStream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(outputStream);
        await outputStream.FlushAsync();
        outputStream.Flush(flushToDisk: true);
    }

    private static async Task CopyReplacingAsync(string source, string destination)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await CopyFileAsync(source, temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string FormatFailure(ArchiveCommandResult result) =>
        $"state={result.FinalState}; reason={result.FailureReason}; diagnostics={string.Join(',', result.Diagnostics.Select(item => item.Code))}";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class ProgressCollector : IProgress<ArchiveProgress>
    {
        public List<ArchiveProgress> Items { get; } = [];
        public void Report(ArchiveProgress value) => Items.Add(value);
    }

    private sealed record GatePrerequisites(
        string RepositoryRoot,
        string TexconvPath,
        string OutputDirectory);
}
