using System.Security.Cryptography;

namespace IntegrationTests;

public sealed class Plan99CrashRecoveryMatrixTests : IDisposable
{
    private const string MatrixStart = "<!-- MATRIX:START -->";
    private const string MatrixEnd = "<!-- MATRIX:END -->";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Audition PLAN 99 crash recovery", Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Coverage", "Plan99")]
    public void Matrix_is_typed_complete_deterministic_and_documentation_cannot_drift()
    {
        var cases = CrashRecoveryEvidence.Cases;

        Assert.True(cases.Count >= 10);
        Assert.Equal(cases.Count, cases.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(cases, item => item.CaseId == "archive-extract-staging");
        Assert.Contains(cases, item => item.CaseId == "archive-export-after-durable-copy");
        Assert.Contains(cases, item => item.CaseId == "cache-encrypted-mid-chunk");
        Assert.Contains(cases, item => item.CaseId == "template-before-immutable-commit");
        Assert.Contains(cases, item => item.CaseId == "updater-before-installer-handoff");
        Assert.Contains(cases, item => item.CaseId == "payment-duplicate-delivery");
        Assert.All(cases, item =>
        {
            Assert.Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$", item.CaseId);
            Assert.DoesNotContain('|', item.RecoveryRule);
            Assert.Matches("^[A-Za-z0-9_.]+$", item.EvidenceTest);
        });

        var document = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "CRASH_RECOVERY_EVIDENCE.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = document.IndexOf(MatrixStart, StringComparison.Ordinal);
        var end = document.IndexOf(MatrixEnd, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var actual = document[(start + MatrixStart.Length)..end].Trim('\n') + "\n";
        Assert.Equal(CrashRecoveryEvidence.RenderMarkdownTable(), actual);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Coverage", "Plan99")]
    public void Deterministic_checkpoint_interrupts_once_without_sleep_and_clean_retry_promotes()
    {
        Directory.CreateDirectory(_root);
        var committed = Path.Combine(_root, "committed.bin");
        var staging = Path.Combine(_root, "candidate.tmp");
        File.WriteAllBytes(committed, "old"u8.ToArray());
        File.WriteAllBytes(staging, "new"u8.ToArray());
        var controller = new DeterministicCrashController("archive.promote.before-commit");

        Assert.Throws<SimulatedCrashException>(() => controller.Reach("archive.promote.before-commit"));
        Assert.Equal("old"u8.ToArray(), File.ReadAllBytes(committed));
        Assert.Equal("new"u8.ToArray(), File.ReadAllBytes(staging));

        controller.Reach("archive.promote.before-commit");
        File.Move(staging, committed, overwrite: true);

        Assert.Equal("new"u8.ToArray(), File.ReadAllBytes(committed));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Coverage", "Plan99")]
    public void Residue_inventory_is_deterministic_and_unknown_state_never_becomes_trusted()
    {
        Directory.CreateDirectory(_root);
        var committedBytes = "committed"u8.ToArray();
        Create("01-committed", ("commit.json", "{}"u8.ToArray()), ("payload.bin", committedBytes));
        Create("02-recoverable", (".workspace.json", "{}"u8.ToArray()), (".retained", []));
        Create("03-incomplete", ("candidate.partial", "partial"u8.ToArray()));
        Create("04-stale", (".stale", []));
        Create("05-corrupt", (".corrupt", []), ("payload.bin", "bad"u8.ToArray()));
        Create("06-unknown", ("mystery.bin", "unknown"u8.ToArray()));
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["01-committed"] = Convert.ToHexString(SHA256.HashData(committedBytes)),
        };

        var inventory = CrashResidueInventory.Inspect(_root, expected);

        Assert.Equal(
        [
            new CrashResidueEntry("01-committed", CrashRecoveryClassification.Committed),
            new CrashResidueEntry("02-recoverable", CrashRecoveryClassification.RecoverableWorkspace),
            new CrashResidueEntry("03-incomplete", CrashRecoveryClassification.IncompleteTransaction),
            new CrashResidueEntry("04-stale", CrashRecoveryClassification.StaleResidue),
            new CrashResidueEntry("05-corrupt", CrashRecoveryClassification.CorruptArtifact),
            new CrashResidueEntry("06-unknown", CrashRecoveryClassification.UnknownUntrusted),
        ], inventory);
        Assert.DoesNotContain(inventory, item => item.Name == "06-unknown"
                                                && item.Classification == CrashRecoveryClassification.Committed);
    }

    private void Create(string name, params (string Name, byte[] Bytes)[] files)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        foreach (var file in files)
        {
            File.WriteAllBytes(Path.Combine(directory, file.Name), file.Bytes);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AuditionModStudio.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
