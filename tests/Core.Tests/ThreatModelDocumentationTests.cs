namespace Core.Tests;

public sealed class ThreatModelDocumentationTests
{
    [Fact]
    public void Plan67_threat_model_contains_required_boundaries_and_traceability()
    {
        var root = FindRepositoryRoot();
        var text = File.ReadAllText(Path.Combine(root, "docs", "THREAT_MODEL.md"));
        string[] required =
        [
            "## Assets", "## Attackers", "## Trust boundaries", "## Data flows",
            "## Ranked abuse cases", "## Owner / mitigation / test mapping", "## Residual risks",
            "AI provider secret", "Supabase privileged keys", "Credit balances/transactions",
            "Payment state", "Premium template catalog", "Raw pristine archive templates",
            "Mod manifests/prompt presets", "App binary/IP", "Update channel", "User tokens/session material",
            "decompile", "patch local license", "copy raw premium", "inspect temp", "dump process memory",
            "replace `acv.exe`", "DLL hijack", "MITM", "fake payment", "Concurrent/replayed spend",
            "Tamper update", "Path traversal", "game process", "anti-cheat", "mod installation",
        ];

        foreach (var value in required)
            Assert.Contains(value, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Implemented", text, StringComparison.Ordinal);
        Assert.Contains("Planned", text, StringComparison.Ordinal);
        Assert.Contains("Operational", text, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Audition_AI_Mod_Studio_MASTER_ROADMAP_V3.md")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
