using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Dds;
using AuditionModStudio.Imaging;
using AuditionModStudio.Infrastructure.Exports;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;
using AuditionModStudio.Mods;
using AuditionModStudio.Projects;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace IntegrationTests;

[Collection(RealAcvTool5Collection.Name)]
public sealed class Plan55FileOnlyProductionGateTests(ITestOutputHelper output)
{
    private const string ArchiveHash = "3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081";
    private const string KeydatHash = "78F2910819D155CFBF0E366A80FEA4F40126BEA8C201E25EBDB0E0F956E2297F";
    private const string ToolHash = "6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3";
    private const string TargetRelativePath = "texture/hud/pointer.dds";
    private const string OriginalTargetHash = "854189B91972C17C483C2434FC077B1F94AE73DDA947FF72173C362A9D2512EA";

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    [Trait("Fixture", "RequiresPrivateFixture")]
    public async Task File_only_pipeline_creates_applies_builds_exports_and_reextracts_standalone_archive()
    {
        var prerequisites = RequirePrerequisites();
        var originalArchive = Path.Combine(prerequisites.RepositoryRoot, "015.ab");
        var originalKeydat = Path.Combine(prerequisites.RepositoryRoot, "015.keydat");
        var originalTool = Path.Combine(prerequisites.RepositoryRoot, "acv.exe");
        var originalExtracted = Path.Combine(prerequisites.RepositoryRoot, "015");
        var originalTarget = Path.Combine(
            originalExtracted,
            TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(ArchiveHash, await HashAsync(originalArchive));
        Assert.Equal(KeydatHash, await HashAsync(originalKeydat));
        Assert.Equal(ToolHash, await HashAsync(originalTool));
        Assert.Equal(OriginalTargetHash, await HashAsync(originalTarget));
        var originalInventory = await HashTreeAsync(originalExtracted);
        Assert.Equal(320, originalInventory.Count);

        var testRoot = Path.Combine(Path.GetTempPath(), "Audition PLAN 55 File Gate", Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(testRoot);
        var pathSecurity = new PathSecurity();
        appPaths.EnsureDirectoriesExist();
        await using var secureWorkspaceService = new SecureWorkspaceService(appPaths, pathSecurity);
        using var provisioning = new ArchiveToolProvisioningService(
            pathSecurity,
            TrustedArchiveToolManifest.Production,
            new ArchiveToolIntegrityPolicy(pathSecurity, TrustedArchiveToolManifest.Production));
        var toolIntegrityPolicy = new ArchiveToolIntegrityPolicy(
            pathSecurity,
            TrustedArchiveToolManifest.Production);
        var keydatService = new KeydatService(pathSecurity);
        var runner = new AcvTool5Runner(pathSecurity, toolIntegrityPolicy, keydatService);
        var regionProfiles = new GameRegionProfileCatalog();
        var engine = new AcvTool5ArchiveEngine(
            provisioning,
            keydatService,
            runner,
            regionProfiles,
            new(prerequisites.RepositoryRoot, "acv.exe", 5_242_880));
        var archiveService = new AuditionArchiveService([engine], pathSecurity);

        var seedTemplate = new AuditionArchiveTemplate(
            "archive-015-plan55-seed",
            "015.ab",
            "015.ab",
            ArchiveEngineType.AcvTool5,
            "audition_vn",
            "015",
            "plan55-seed",
            ArchiveHash,
            "audition-vn-file-gate");
        var preparedTemplateRoot = Path.Combine(testRoot, "PreparedTemplate");
        Directory.CreateDirectory(preparedTemplateRoot);
        string preparedTemplateHash;
        await using (var preparationWorkspace = await secureWorkspaceService.CreateAsync())
        {
            await CopyFileAsync(
                originalArchive,
                Path.Combine(preparationWorkspace.Paths.WorkingDirectory, "015.ab"));
            await CopyFileAsync(
                originalKeydat,
                Path.Combine(preparationWorkspace.Paths.WorkingDirectory, "015.keydat"));
            await CopyTreeAsync(
                originalExtracted,
                Path.Combine(preparationWorkspace.Paths.ExtractedDirectory, "015"));
            var prepared = await archiveService.PackAsync(new(
                seedTemplate,
                ArchiveWorkspace.Create(preparationWorkspace, seedTemplate),
                TimeSpan.FromMinutes(3)));
            Assert.True(prepared.Command.Succeeded, FormatFailure(prepared.Command));
            var preparedArchive = Path.Combine(preparationWorkspace.Paths.WorkingDirectory, "015.ab");
            preparedTemplateHash = await HashAsync(preparedArchive);
            await CopyFileAsync(preparedArchive, Path.Combine(preparedTemplateRoot, "015.ab"));
            await CopyFileAsync(
                Path.Combine(preparationWorkspace.Paths.WorkingDirectory, "015.keydat"),
                Path.Combine(preparedTemplateRoot, "015.keydat"));
        }

        var gameId = new GameId("audition");
        var modId = new ModId("plan55_pointer");
        var template = new AuditionArchiveTemplate(
            "archive-015-plan55",
            "015.ab",
            "015.ab",
            ArchiveEngineType.AcvTool5,
            "audition_vn",
            "015",
            "plan55",
            preparedTemplateHash,
            "audition-vn-file-gate");
        var gameCatalog = GameCatalog.CreateBuiltIn();
        var mod = new ModDefinition(
            modId,
            gameId,
            "PLAN 55 Pointer",
            new("hud"),
            new("assets/pointer.png"),
            "File-only production gate fixture.",
            template,
            ModKeydatStrategy.ReuseOrGenerate,
            new("archives/015.ab"),
            "Fixture-gated production archive pipeline.");
        var modCatalogResult = ModCatalog.Create([mod], gameCatalog, regionProfiles);
        Assert.True(modCatalogResult.Succeeded);
        var modCatalog = modCatalogResult.Catalog!;
        var slot = new TextureSlot(
            new("pointer"),
            new(TargetRelativePath),
            "HUD Pointer",
            new("hud"),
            "Pointer texture used by the file-only gate.",
            ["pointer", "hud"],
            true,
            true,
            new("stretch"));
        var manifestResult = TextureManifest.Create(gameId, modId, [slot]);
        Assert.True(manifestResult.Succeeded);
        var manifestCatalogResult = TextureManifestCatalog.Create([manifestResult.Manifest], modCatalog);
        Assert.True(manifestCatalogResult.Succeeded);

        var metadataReader = new DdsMetadataReader();
        var assetScanner = new ArchiveAssetScanner(pathSecurity);
        var smartScan = new SmartModScanService(
            gameCatalog,
            modCatalog,
            manifestCatalogResult.Catalog!,
            assetScanner,
            metadataReader,
            pathSecurity);
        var metadataCache = new ProjectMetadataCache(appPaths, pathSecurity);
        var projectStore = new AuditionProjectStore(appPaths, pathSecurity);
        var workspaceService = new ProjectArchiveWorkspaceService(
            appPaths,
            pathSecurity,
            secureWorkspaceService,
            new ProjectArchiveWorkspaceManifestStore(pathSecurity));
        var creationService = new ProjectCreationService(
            gameCatalog,
            modCatalog,
            regionProfiles,
            new GateEntitlementService(),
            new GateTemplateAcquisitionService(preparedTemplateRoot),
            workspaceService,
            archiveService,
            smartScan,
            metadataCache,
            projectStore);

        try
        {
            var creationProgress = new List<ProjectCreationPhase>();
            var created = await creationService.CreateAsync(
                new(gameId, modId, "PLAN 55 File-Only Gate"),
                new CallbackProgress<ProjectCreationProgress>(item => creationProgress.Add(item.Phase)));
            Assert.True(created.Succeeded, created.DiagnosticCode);
            Assert.NotNull(created.Project);
            Assert.NotNull(created.Workspace);
            Assert.Contains(ProjectCreationPhase.ExtractingArchive, creationProgress);
            Assert.Contains(ProjectCreationPhase.ScanningTextures, creationProgress);
            Assert.Contains(ProjectCreationPhase.Completed, creationProgress);

            await using var workspace = created.Workspace!;
            var project = created.Project!;
            var scan = await smartScan.ScanAsync(new(gameId, modId, workspace));
            Assert.True(scan.Succeeded, scan.DiagnosticCode);
            var selected = Assert.Single(
                scan.Groups.SelectMany(group => group.Textures),
                texture => string.Equals(
                    texture.Asset.RelativePath.Replace('\\', '/'),
                    TargetRelativePath,
                    StringComparison.OrdinalIgnoreCase));
            Assert.False(selected.ManifestResolution.UsedFallback);
            Assert.Equal(164, selected.Metadata.Width);
            Assert.Equal(128, selected.Metadata.Height);
            Assert.Equal(DdsFormat.BC3, selected.Metadata.Format);
            Assert.Equal(1u, selected.Metadata.EffectiveMipLevelCount);

            var evaluationHarness = new DirectXTexEvaluationHarness(
                pathSecurity,
                metadataReader,
                DirectXTexEvaluationToolCatalog.May2026X64);
            var encoder = new DdsEncoder(
                metadataReader,
                evaluationHarness,
                pathSecurity,
                new(
                    prerequisites.TexconvPath,
                    TimeSpan.FromMinutes(2),
                    DdsPreviewResourcePolicy.Default));
            var ddsValidation = new DdsValidationService(metadataReader, pathSecurity);
            var matchOriginal = new DdsMatchOriginalService(metadataReader, encoder, ddsValidation);
            var resizeService = new ImageResizeService(ImageImportResourcePolicy.Default);
            var previewService = new DdsPreviewService(
                metadataReader,
                evaluationHarness,
                pathSecurity,
                new(
                    prerequisites.TexconvPath,
                    TimeSpan.FromMinutes(2),
                    DdsPreviewResourcePolicy.Default));
            var thumbnailCache = new ThumbnailCache(
                appPaths,
                pathSecurity,
                previewService,
                new ImageImportService(ImageImportResourcePolicy.Default),
                resizeService,
                ThumbnailCacheOptions.Default);
            var applyService = new TextureApplyService(
                metadataReader,
                resizeService,
                matchOriginal,
                ddsValidation,
                new TextureStateMachine(),
                thumbnailCache,
                projectStore);
            var applyProgress = new List<TextureApplyPhase>();
            var pattern = CreateGatePattern(164, 128);
            var applied = await applyService.ApplyAsync(
                new(
                    project,
                    workspace,
                    new(TargetRelativePath),
                    new(pattern, 164, 128, new(ImageResizeMode.Stretch))),
                new CallbackProgress<TextureApplyProgress>(item => applyProgress.Add(item.Phase)));
            Assert.True(applied.Succeeded, applied.DiagnosticCode);
            Assert.Equal(TextureState.Modified, applied.State);
            Assert.Single(applied.Project!.EditedTextures);
            Assert.Contains(TextureApplyPhase.Replacing, applyProgress);
            Assert.Contains(TextureApplyPhase.SavingProject, applyProgress);
            project = applied.Project;

            var workingTarget = workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(Path.Combine(
                "Extracted",
                workspace.ArchiveWorkspace.ExtractDirectoryRelativePath,
                TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var replacementHash = await HashAsync(workingTarget);
            Assert.NotEqual(OriginalTargetHash, replacementHash);
            var replacementMetadata = await metadataReader.ReadAsync(workingTarget);
            Assert.True(replacementMetadata.IsSuccess, replacementMetadata.ErrorCode);
            Assert.Equal(164, replacementMetadata.Metadata!.Width);
            Assert.Equal(128, replacementMetadata.Metadata.Height);
            Assert.Equal(DdsFormat.BC3, replacementMetadata.Metadata.Format);
            Assert.Equal(1u, replacementMetadata.Metadata.EffectiveMipLevelCount);

            var projectToolValidator = new ProjectArchiveToolIntegrityValidator(
                toolIntegrityPolicy,
                new(prerequisites.RepositoryRoot, "acv.exe"));
            var projectValidator = new ProjectValidator(
                metadataCache,
                assetScanner,
                metadataReader,
                projectToolValidator);
            var validation = await projectValidator.ValidateAsync(new(project, workspace));
            Assert.True(validation.CanBuild, string.Join(',', validation.Issues.Select(item => item.DiagnosticCode)));

            using var buildService = new ProjectBuildService(
                projectStore,
                projectValidator,
                secureWorkspaceService,
                archiveService,
                pathSecurity,
                ProjectBuildOptions.Default);
            var buildProgress = new List<ProjectBuildPhase>();
            var build = await buildService.BuildAsync(
                new(project, workspace),
                new CallbackProgress<ProjectBuildProgress>(item => buildProgress.Add(item.Phase)));
            Assert.True(build.Succeeded, build.DiagnosticCode);
            Assert.NotNull(build.Project);
            Assert.True(build.OutputSha256?.IsValid);
            Assert.Contains(ProjectBuildPhase.Packing, buildProgress);
            Assert.Contains(ProjectBuildPhase.Verifying, buildProgress);
            project = build.Project!;

            Directory.CreateDirectory(prerequisites.OutputDirectory);
            var artifactName = "015-plan55-pointer.ab";
            using var exportService = new ArchiveExportService(
                new ArchiveExportDestinationValidator(
                    pathSecurity,
                    new SystemExportDestinationFileSystem()),
                pathSecurity,
                new SystemArchiveExportFileOperations());
            var exported = await exportService.ExportAsync(new(
                project,
                workspace,
                prerequisites.OutputDirectory,
                artifactName,
                ArchiveExportOverwritePolicy.ReplaceExisting));
            Assert.True(exported.Succeeded, exported.DiagnosticCode);
            Assert.True(exported.Size > 0);
            Assert.True(exported.Sha256?.IsValid);
            var artifactPath = Path.Combine(prerequisites.OutputDirectory, artifactName);
            Assert.True(File.Exists(artifactPath));
            Assert.Equal(exported.Size, new FileInfo(artifactPath).Length);
            Assert.Equal(exported.Sha256!.Value.Value, await HashAsync(artifactPath));

            var verifySourceRoot = Path.Combine(testRoot, "VerifySource");
            Directory.CreateDirectory(verifySourceRoot);
            var stagedVerifyArchive = Path.Combine(verifySourceRoot, "015.ab");
            await CopyFileAsync(artifactPath, stagedVerifyArchive);
            Assert.Equal(exported.Sha256.Value.Value, await HashAsync(stagedVerifyArchive));
            var verifyTemplate = new AuditionArchiveTemplate(
                "archive-015-plan55-verify",
                "015.ab",
                "015.ab",
                ArchiveEngineType.AcvTool5,
                "audition_vn",
                "015",
                "plan55-verify",
                exported.Sha256.Value.Value,
                "audition-vn-file-gate");
            await using var verifyWorkspace = await secureWorkspaceService.CreateAsync();
            var extracted = await archiveService.ExtractAsync(new(
                verifyTemplate,
                ArchiveWorkspace.Create(verifyWorkspace, verifyTemplate),
                new(verifySourceRoot),
                TimeSpan.FromMinutes(3)));
            Assert.True(extracted.Command.Succeeded, FormatFailure(extracted.Command));
            var verifiedRoot = Path.Combine(verifyWorkspace.Paths.ExtractedDirectory, "015");
            var verifiedInventory = await HashTreeAsync(verifiedRoot);
            Assert.Equal(originalInventory.Keys.Order(), verifiedInventory.Keys.Order());
            Assert.Equal(320, verifiedInventory.Count);
            foreach (var item in originalInventory)
            {
                Assert.Equal(
                    item.Key.Equals(TargetRelativePath, StringComparison.OrdinalIgnoreCase)
                        ? replacementHash
                        : item.Value,
                    verifiedInventory[item.Key]);
            }

            var workingArchive = workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
                workspace.Descriptor.WorkingArchiveRelativePath);
            Assert.Equal(preparedTemplateHash, await HashAsync(workingArchive));
            Assert.Equal(ArchiveHash, await HashAsync(originalArchive));
            Assert.Equal(KeydatHash, await HashAsync(originalKeydat));
            Assert.Equal(ToolHash, await HashAsync(originalTool));
            Assert.Equal(OriginalTargetHash, await HashAsync(originalTarget));
            output.WriteLine(
                "PLAN 55 FILE GATE: artifact={0}; size={1}; sha256={2}; files={3}; target={4}; replacement={5}; nonTarget={6}; template={7}",
                artifactPath,
                exported.Size,
                exported.Sha256.Value.Value,
                verifiedInventory.Count,
                TargetRelativePath,
                replacementHash,
                verifiedInventory.Count - 1,
                preparedTemplateHash);
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
            throw SkipException.ForSkip("PLAN 55 production gate requires Windows.");
        }

        var repositoryRoot = FindRepositoryRoot();
        var texconv = Environment.GetEnvironmentVariable("AUDITION_DIRECTXTEX_TEXCONV_PATH");
        var outputDirectory = Environment.GetEnvironmentVariable("AUDITION_PLAN55_OUTPUT_DIRECTORY");
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
            throw SkipException.ForSkip("PLAN 55 private fixtures or approved texconv are unavailable.");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
        {
            throw SkipException.ForSkip("PLAN 55 output directory was not explicitly configured.");
        }

        return new(repositoryRoot, texconv!, Path.GetFullPath(outputDirectory));
    }

    private static InternalImage CreateGatePattern(int width, int height)
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

        return new(
            width,
            height,
            checked(width * 4),
            pixels,
            new(ImageSourceFormat.Png, width, height, ImageSourceOrientation.Normal, true, false));
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

        Directory.CreateDirectory(destinationRoot);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, sourcePath));
            await CopyFileAsync(sourcePath, destinationPath);
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination);
        await destination.FlushAsync();
    }

    private static async Task<string> HashAsync(string path)
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

    private static string FormatFailure(ArchiveCommandResult result) =>
        $"state={result.FinalState}; reason={result.FailureReason}; diagnostics={string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}"))}";

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

    private sealed class GateEntitlementService : ITemplateEntitlementService
    {
        public Task<TemplateEntitlementResult> CheckAsync(
            TemplateIdentity identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TemplateEntitlementResult(TemplateEntitlementStatus.Granted, null));
    }

    private sealed class GateTemplateAcquisitionService(string trustedRoot)
        : IProjectTemplateAcquisitionService
    {
        public Task<ProjectTemplateAcquisitionResult> AcquireAsync(
            AuditionArchiveTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectTemplateAcquisitionResult.Success(new(trustedRoot)));
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record GatePrerequisites(
        string RepositoryRoot,
        string TexconvPath,
        string OutputDirectory);
}
