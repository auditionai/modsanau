using System.Text.Json;
using System.Xml.Linq;

namespace Security.Tests;

public sealed class ReleasePenetrationReviewTests
{
    [Fact]
    public void Plan90_review_covers_exact_attack_paths_and_records_fixed_findings_and_release_blocker()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "release-security-review.json")));
        var review = document.RootElement;

        Assert.Equal(1, review.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(90, review.GetProperty("plan").GetInt32());
        Assert.Equal("internal-manual", review.GetProperty("reviewType").GetString());
        Assert.False(review.GetProperty("independentCommissionedReview").GetBoolean());
        var paths = review.GetProperty("attackPaths").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(1, 8).Select(value => $"RPR-{value:D2}"),
            paths.Select(path => path.GetProperty("id").GetString()));
        Assert.All(paths, path => Assert.False(string.IsNullOrWhiteSpace(
            path.GetProperty("status").GetString())));

        var findings = review.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Equal("fixed", findings.Single(item =>
            item.GetProperty("id").GetString() == "F-90-01").GetProperty("status").GetString());
        Assert.Equal("fixed", findings.Single(item =>
            item.GetProperty("id").GetString() == "F-90-02").GetProperty("status").GetString());
        Assert.Equal("release-blocker", findings.Single(item =>
            item.GetProperty("id").GetString() == "F-90-03").GetProperty("status").GetString());
    }

    [Fact]
    public void Runtime_is_unelevated_client_has_no_gateway_reference_and_release_policy_precedes_signing()
    {
        var root = FindRepositoryRoot();
        var manifest = XDocument.Load(Path.Combine(root, "src", "AuditionModStudio.App", "app.manifest"));
        var level = manifest.Descendants().Single(element =>
            element.Name.LocalName == "requestedExecutionLevel");
        Assert.Equal("asInvoker", level.Attribute("level")?.Value);
        Assert.Equal("false", level.Attribute("uiAccess")?.Value);

        var appProject = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.App",
            "AuditionModStudio.App.csproj"));
        Assert.DoesNotContain("AuditionModStudio.Gateway", appProject, StringComparison.Ordinal);

        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release-signing.yml"));
        var exposure = workflow.IndexOf("Protect-ReleaseArtifactExposure.ps1", StringComparison.Ordinal);
        var signing = workflow.IndexOf("Invoke-AppCodeSigning.ps1", StringComparison.Ordinal);
        Assert.True(exposure >= 0 && signing > exposure);
        Assert.Contains("DebugSymbols=false", workflow, StringComparison.Ordinal);
        Assert.Contains("Invoke-SecretScan.ps1", workflow, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "scripts", "Invoke-ReleaseSecurityReview.ps1")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
