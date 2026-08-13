using System.Security.Cryptography;
using AuditionModStudio.Dds;
using Xunit.Sdk;

namespace IntegrationTests;

public sealed class Plan97ArchiveFilePipelineIntegrationTests
{
    private const string ArchiveHash = "3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081";
    private const string KeydatHash = "78F2910819D155CFBF0E366A80FEA4F40126BEA8C201E25EBDB0E0F956E2297F";
    private const string ToolHash = "6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3";
    private const string Target = "texture/hud/pointer.dds";
    private const string OriginalTargetHash =
        "854189B91972C17C483C2434FC077B1F94AE73DDA947FF72173C362A9D2512EA";

    [Fact]
    [Trait("Category", "Integration")]
    public void Exact_expected_change_is_accepted_and_all_non_targets_are_counted()
    {
        var baseline = Inventory((Target, "AA"), ("texture/hud/other.dds", "BB"), ("data/config.bin", "CC"));
        var reextracted = Inventory((Target, "DD"), ("texture/hud/other.dds", "BB"), ("data/config.bin", "CC"));

        var result = ArchiveFilePipelineEvidence.Verify(
            baseline,
            reextracted,
            Inventory((Target, "DD")));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(2, result.NonTargetIdenticalCount);
        Assert.Equal([Target], result.ActualChangedPaths);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Unexpected_non_target_mutation_is_rejected()
    {
        var baseline = Inventory((Target, "AA"), ("texture/hud/other.dds", "BB"));
        var reextracted = Inventory((Target, "DD"), ("texture/hud/other.dds", "EE"));

        var result = ArchiveFilePipelineEvidence.Verify(
            baseline,
            reextracted,
            Inventory((Target, "DD")));

        Assert.False(result.Succeeded);
        Assert.Equal("ARCHIVE_PIPELINE_EVIDENCE_CHANGED_SET_MISMATCH", result.DiagnosticCode);
        Assert.Equal(["texture/hud/other.dds", Target], result.ActualChangedPaths);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Missing_expected_target_change_is_rejected()
    {
        var baseline = Inventory((Target, "AA"), ("data/config.bin", "BB"));

        var result = ArchiveFilePipelineEvidence.Verify(
            baseline,
            Inventory((Target, "AA"), ("data/config.bin", "BB")),
            Inventory((Target, "DD")));

        Assert.False(result.Succeeded);
        Assert.Equal("ARCHIVE_PIPELINE_EVIDENCE_CHANGED_SET_MISMATCH", result.DiagnosticCode);
        Assert.Empty(result.ActualChangedPaths);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Changed_target_with_unexpected_content_hash_is_rejected()
    {
        var baseline = Inventory((Target, "AA"), ("data/config.bin", "BB"));

        var result = ArchiveFilePipelineEvidence.Verify(
            baseline,
            Inventory((Target, "CC"), ("data/config.bin", "BB")),
            Inventory((Target, "DD")));

        Assert.False(result.Succeeded);
        Assert.Equal("ARCHIVE_PIPELINE_EVIDENCE_TARGET_HASH_MISMATCH", result.DiagnosticCode);
        Assert.Equal([Target], result.ActualChangedPaths);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Added_or_missing_archive_entry_is_rejected_before_hash_evidence()
    {
        var baseline = Inventory((Target, "AA"), ("data/required.bin", "BB"));
        var reextracted = Inventory((Target, "DD"), ("data/unexpected.bin", "CC"));

        var result = ArchiveFilePipelineEvidence.Verify(
            baseline,
            reextracted,
            Inventory((Target, "DD")));

        Assert.False(result.Succeeded);
        Assert.Equal("ARCHIVE_PIPELINE_EVIDENCE_INVENTORY_MISMATCH", result.DiagnosticCode);
        Assert.Equal(["data/required.bin"], result.MissingPaths);
        Assert.Equal(["data/unexpected.bin"], result.AddedPaths);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Traversal_or_duplicate_normalized_identity_is_rejected()
    {
        var traversal = ArchiveFilePipelineEvidence.Verify(
            Inventory(("../escape.dds", "AA")),
            Inventory(("../escape.dds", "BB")),
            Inventory(("../escape.dds", "BB")));
        var duplicateBaseline = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["texture/hud/pointer.dds"] = "AA",
            ["texture\\hud\\POINTER.dds"] = "AA",
        };
        var duplicate = ArchiveFilePipelineEvidence.Verify(
            duplicateBaseline,
            Inventory((Target, "BB")),
            Inventory((Target, "BB")));

        Assert.Equal("ARCHIVE_PIPELINE_EVIDENCE_PATH_INVALID", traversal.DiagnosticCode);
        Assert.Equal("ARCHIVE_PIPELINE_EVIDENCE_PATH_INVALID", duplicate.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Fixture", "RequiresPrivateFixture")]
    public async Task Corrupted_copy_of_real_dds_is_rejected_and_pristine_fixture_remains_unchanged()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pristine = Path.Combine(repositoryRoot, "015", Target.Replace('/', Path.DirectorySeparatorChar));
        var archive = Path.Combine(repositoryRoot, "015.ab");
        var keydat = Path.Combine(repositoryRoot, "015.keydat");
        var tool = Path.Combine(repositoryRoot, "acv.exe");
        if (new[] { pristine, archive, keydat, tool }.Any(path => !File.Exists(path)))
        {
            throw SkipException.ForSkip("PLAN 97 real fixture set is unavailable.");
        }

        Assert.Equal(ArchiveHash, await HashAsync(archive));
        Assert.Equal(KeydatHash, await HashAsync(keydat));
        Assert.Equal(ToolHash, await HashAsync(tool));
        Assert.Equal(OriginalTargetHash, await HashAsync(pristine));
        var root = Path.Combine(Path.GetTempPath(), "Audition PLAN 97 lỗi DDS có dấu", Guid.NewGuid().ToString("N"));
        var corrupted = Path.Combine(root, "Bản sao", "pointer.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupted)!);
        File.Copy(pristine, corrupted);

        try
        {
            await using (var stream = new FileStream(corrupted, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync("FAIL"u8.ToArray());
                await stream.FlushAsync();
            }

            var result = await new DdsMetadataReader().ReadAsync(corrupted);
            Assert.False(result.IsSuccess);
            Assert.Equal(ArchiveHash, await HashAsync(archive));
            Assert.Equal(KeydatHash, await HashAsync(keydat));
            Assert.Equal(ToolHash, await HashAsync(tool));
            Assert.Equal(OriginalTargetHash, await HashAsync(pristine));
            Assert.NotEqual(OriginalTargetHash, await HashAsync(corrupted));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Dictionary<string, string> Inventory(params (string Path, string Hash)[] entries) =>
        entries.ToDictionary(item => item.Path, item => item.Hash, StringComparer.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException();
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
}
