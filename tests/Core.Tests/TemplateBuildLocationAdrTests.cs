namespace Core.Tests;

public sealed class TemplateBuildLocationAdrTests
{
    [Fact]
    public void Plan73_adr_records_options_decision_constraints_and_residual_risk()
    {
        var root = FindRepositoryRoot();
        var text = File.ReadAllText(Path.Combine(root, "docs", "ADR",
            "0001-template-exposure-and-build-location.md"));
        string[] required =
        [
            "Trạng thái: Accepted", "Client-side build", "Server-side build worker", "Hybrid",
            "Legal/licensing", "Latency/cost", "Offline", "Exposure", "ACV Tool 5",
            "server-controlled acquisition", "client-side local build", "encrypted local cache",
            "standalone .ab/.acv", "authorized user", "residual risk", "không thể extract",
            "game path", "install output", "END", "PLAN 74", "PLAN 75",
        ];
        foreach (var value in required) Assert.Contains(value, text, StringComparison.OrdinalIgnoreCase);
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
