using System.Buffers.Binary;
using System.IO.Compression;
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
public sealed class Plan14DirectXTexEvaluationIntegrationTests(ITestOutputHelper output)
{
    private const string ExpectedArchiveSha256 =
        "3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081";

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "WindowsOnly")]
    [Trait("Fixture", "RequiresPrivateFixtureAndDirectXTex")]
    public async Task Official_texconv_evaluates_real_decode_and_bc3_legacy_encode()
    {
        var pathSecurity = new PathSecurity();
        var locator = new RepositoryFixtureLocator(pathSecurity);
        var repositoryRoot = locator.FindRepositoryRoot(AppContext.BaseDirectory);
        var texconvPath = GetApprovedTexconvPath();
        SkipWhenUnavailable(locator, repositoryRoot, texconvPath);
        var archivePath = locator.ResolveSourcePath(repositoryRoot, RealSampleFixtureCatalog.Archive015);
        var archiveHashBefore = await ComputeHashAsync(archivePath);
        Assert.Equal(ExpectedArchiveSha256, archiveHashBefore);

        var testRoot = Path.Combine(Path.GetTempPath(), $"Sàn DirectXTex Evaluation {Guid.NewGuid():N}");
        var appPaths = new AppPaths(testRoot);
        var archiveManifest = TrustedArchiveToolManifest.Production;
        var archiveIntegrity = new ArchiveToolIntegrityPolicy(pathSecurity, archiveManifest);
        using var provisioning = new ArchiveToolProvisioningService(
            pathSecurity,
            archiveManifest,
            archiveIntegrity);
        using var keydat = new KeydatService(pathSecurity);
        var runner = new AcvTool5Runner(pathSecurity, archiveIntegrity, keydat);
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
            "DirectXTex evaluation",
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
        var ddsPaths = Directory.EnumerateFiles(extractedRoot, "*.dds", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(52, ddsPaths.Length);
        var sourceHashesBefore = await HashAllAsync(ddsPaths);
        var metadataReader = new DdsMetadataReader();
        var realMetadata = new List<(string Path, DdsMetadata Metadata)>();
        foreach (var path in ddsPaths)
        {
            var result = await metadataReader.ReadAsync(path);
            Assert.True(result.IsSuccess, result.ErrorCode);
            realMetadata.Add((path, result.Metadata!));
        }

        var representatives = realMetadata
            .GroupBy(item => item.Metadata.Format)
            .Select(group => group.OrderByDescending(item => (long)item.Metadata.Width * item.Metadata.Height).First())
            .ToList();
        var largest = realMetadata.MaxBy(item => (long)item.Metadata.Width * item.Metadata.Height);
        if (representatives.All(item => !string.Equals(item.Path, largest.Path, StringComparison.OrdinalIgnoreCase)))
        {
            representatives.Add(largest);
        }

        var harness = new DirectXTexEvaluationHarness(
            pathSecurity,
            metadataReader,
            DirectXTexEvaluationToolCatalog.May2026X64);
        var decoded = new List<DirectXTexEvaluationResult>();
        for (var index = 0; index < representatives.Count; index++)
        {
            var representative = representatives[index];
            var relativeInput = Path.GetRelativePath(
                workspace.ArchiveWorkspace.SecureWorkspace.Paths.RootDirectory,
                representative.Path);
            var result = await harness.RunAsync(new(
                DirectXTexEvaluationOperation.DecodeToPng,
                workspace.ArchiveWorkspace.SecureWorkspace,
                texconvPath!,
                relativeInput,
                $"real-preview-{index}",
                null,
                null,
                1,
                false,
                TimeSpan.FromMinutes(2)));
            Assert.True(result.Succeeded, FormatFailure(result));
            Assert.Equal(representative.Metadata.Width, result.OutputWidth);
            Assert.Equal(representative.Metadata.Height, result.OutputHeight);
            decoded.Add(result);
        }

        var unusualPngRelative = @"Working\evaluation-input-137x512.png";
        var unusualPngPath = workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(unusualPngRelative);
        await WriteSolidRgbaPngAsync(unusualPngPath, 137, 512);
        var progressStates = new List<DirectXTexEvaluationState>();
        var unusualEncode = await harness.RunAsync(new(
            DirectXTexEvaluationOperation.EncodeBc3,
            workspace.ArchiveWorkspace.SecureWorkspace,
            texconvPath!,
            unusualPngRelative,
            "encode-137x512",
            137,
            512,
            4,
            true,
            TimeSpan.FromMinutes(2)),
            new InlineProgress<DirectXTexEvaluationProgress>(item => progressStates.Add(item.State)));
        Assert.True(unusualEncode.Succeeded, FormatFailure(unusualEncode));
        AssertBc3Legacy(unusualEncode, 137, 512, 4);
        Assert.Equal(
            [
                DirectXTexEvaluationState.Starting,
                DirectXTexEvaluationState.ValidatingInput,
                DirectXTexEvaluationState.ProvisioningTool,
                DirectXTexEvaluationState.Running,
                DirectXTexEvaluationState.VerifyingOutput,
                DirectXTexEvaluationState.Completed,
            ],
            progressStates);

        var largePngRelative = @"Working\evaluation-input-6000x1801.png";
        var largePngPath = workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(largePngRelative);
        await WriteSolidRgbaPngAsync(largePngPath, 6000, 1801);
        var largeEncode = await harness.RunAsync(new(
            DirectXTexEvaluationOperation.EncodeBc3,
            workspace.ArchiveWorkspace.SecureWorkspace,
            texconvPath!,
            largePngRelative,
            "encode-6000x1801",
            6000,
            1801,
            1,
            true,
            TimeSpan.FromMinutes(3)));
        Assert.True(largeEncode.Succeeded, FormatFailure(largeEncode));
        AssertBc3Legacy(largeEncode, 6000, 1801, 1);

        var encodedRelative = largeEncode.OutputRelativePath!;
        var encodedPreview = await harness.RunAsync(new(
            DirectXTexEvaluationOperation.DecodeToPng,
            workspace.ArchiveWorkspace.SecureWorkspace,
            texconvPath!,
            encodedRelative,
            "encoded-preview-6000x1801",
            null,
            null,
            1,
            false,
            TimeSpan.FromMinutes(2)));
        Assert.True(encodedPreview.Succeeded, FormatFailure(encodedPreview));
        Assert.Equal(6000, encodedPreview.OutputWidth);
        Assert.Equal(1801, encodedPreview.OutputHeight);

        Assert.Equal(sourceHashesBefore, await HashAllAsync(ddsPaths));
        Assert.Equal(archiveHashBefore, await ComputeHashAsync(archivePath));
        output.WriteLine(
            "DirectXTex {0}: real DDS metadata={1}; representative decodes={2}; formats=[{3}]",
            DirectXTexEvaluationToolCatalog.May2026X64.Version,
            realMetadata.Count,
            decoded.Count,
            string.Join(", ", representatives.Select(item => item.Metadata.Format).Distinct().Order()));
        output.WriteLine("Encode BC3 legacy: 137x512 mips=4; 6000x1801 mips=1; decode-back=6000x1801");
    }

    private static string? GetApprovedTexconvPath() =>
        Environment.GetEnvironmentVariable("AUDITION_DIRECTXTEX_TEXCONV_PATH");

    private static void SkipWhenUnavailable(
        RepositoryFixtureLocator locator,
        string repositoryRoot,
        string? texconvPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("PLAN 14 DirectXTex evaluation requires Windows.");
        }

        var missing = new[] { RealSampleFixtureCatalog.Archive015, RealSampleFixtureCatalog.AcvTool }
            .Where(item => !File.Exists(locator.ResolveSourcePath(repositoryRoot, item)))
            .Select(item => item.FileName)
            .ToList();
        if (string.IsNullOrWhiteSpace(texconvPath) || !File.Exists(texconvPath))
        {
            missing.Add("approved texconv.exe via AUDITION_DIRECTXTEX_TEXCONV_PATH");
        }

        if (missing.Count > 0)
        {
            throw SkipException.ForSkip($"PLAN 14 fixtures unavailable: {string.Join(", ", missing)}.");
        }
    }

    private static void AssertBc3Legacy(
        DirectXTexEvaluationResult result,
        int width,
        int height,
        uint mipLevels)
    {
        var metadata = Assert.IsType<DdsMetadata>(result.OutputDdsMetadata);
        Assert.Equal(width, metadata.Width);
        Assert.Equal(height, metadata.Height);
        Assert.Equal(DdsFormat.BC3, metadata.Format);
        Assert.Equal("DXT5", metadata.FourCC);
        Assert.Equal(DdsHeaderType.Legacy, metadata.HeaderType);
        Assert.Equal(mipLevels, metadata.EffectiveMipLevelCount);
    }

    private static async Task WriteSolidRgbaPngAsync(string path, int width, int height)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), checked((uint)height));
        ihdr[8] = 8;
        ihdr[9] = 6;
        await WriteChunkAsync(stream, "IHDR"u8.ToArray(), ihdr);

        await using var compressed = new MemoryStream();
        await using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[checked(1 + width * 4)];
            for (var x = 0; x < width; x++)
            {
                var offset = 1 + x * 4;
                row[offset] = 0x24;
                row[offset + 1] = 0x78;
                row[offset + 2] = 0xc8;
                row[offset + 3] = (byte)(x % 251);
            }

            for (var y = 0; y < height; y++)
            {
                await zlib.WriteAsync(row);
            }
        }

        await WriteChunkAsync(stream, "IDAT"u8.ToArray(), compressed.ToArray());
        await WriteChunkAsync(stream, "IEND"u8.ToArray(), []);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteChunkAsync(Stream stream, byte[] type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        await stream.WriteAsync(length);
        await stream.WriteAsync(type);
        await stream.WriteAsync(data);
        var crcInput = new byte[checked(type.Length + data.Length)];
        type.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, type.Length);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(crcInput));
        await stream.WriteAsync(crc);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
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

    private static string FormatFailure(DirectXTexEvaluationResult result) =>
        $"state={result.FinalState}; reason={result.FailureReason}; code={result.ErrorCode}; exit={result.ExitCode}; stdout={result.StandardOutput}; stderr={result.StandardError}";

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
