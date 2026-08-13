using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Security.Tests;

public sealed partial class SupplyChainPolicyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Nuget_versions_sources_audit_and_lock_generation_are_pinned()
    {
        var build = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        Assert.Equal("true", Property(build, "RestorePackagesWithLockFile"));
        Assert.Equal("true", Property(build, "NuGetAudit"));
        Assert.Equal("all", Property(build, "NuGetAuditMode"));

        var packages = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Packages.props"));
        var versions = packages.Descendants("PackageVersion").ToArray();
        Assert.NotEmpty(versions);
        Assert.All(versions, item =>
        {
            var version = Assert.IsType<XAttribute>(item.Attribute("Version")).Value;
            Assert.Matches(ExactVersionPattern(), version);
        });

        var nuget = XDocument.Load(Path.Combine(RepositoryRoot, "NuGet.Config"));
        var sources = nuget.Descendants("packageSources").Elements("add").ToArray();
        var source = Assert.Single(sources);
        Assert.Equal("nuget.org", source.Attribute("key")?.Value);
        Assert.Equal("https://api.nuget.org/v3/index.json", source.Attribute("value")?.Value);
        Assert.Equal("require", nuget.Descendants("config").Elements("add")
            .Single(item => item.Attribute("key")?.Value == "signatureValidationMode")
            .Attribute("value")?.Value);
    }

    [Fact]
    public void Every_project_has_a_nonempty_lock_file()
    {
        var projects = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj",
                SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "tests"), "*.csproj",
                SearchOption.AllDirectories))
            .ToArray();

        Assert.Equal(22, projects.Length);
        Assert.All(projects, project =>
        {
            var lockPath = Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json");
            Assert.True(File.Exists(lockPath), $"Missing lock file for {Path.GetFileName(project)}.");
            using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
            Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
            Assert.True(document.RootElement.GetProperty("dependencies").EnumerateObject().Any());
        });
    }

    [Fact]
    public void Spdx_sbom_covers_exact_resolved_packages_without_local_paths()
    {
        var path = Path.Combine(RepositoryRoot, "docs", "supply-chain", "AuditionModStudio.spdx.json");
        var text = File.ReadAllText(path);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        Assert.Equal("SPDX-2.3", root.GetProperty("spdxVersion").GetString());
        var packages = root.GetProperty("packages").EnumerateArray().ToArray();
        Assert.True(packages.Length >= 60);
        Assert.Contains(packages, package => package.GetProperty("name").GetString() == "SkiaSharp"
            && package.GetProperty("versionInfo").GetString() == "4.150.1");
        Assert.All(packages, package => Assert.StartsWith("pkg:nuget/",
            package.GetProperty("externalRefs")[0].GetProperty("referenceLocator").GetString(),
            StringComparison.Ordinal));
        Assert.DoesNotContain(RepositoryRoot, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users\\", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Proprietary_redistribution_is_fail_closed_and_directxtex_has_exact_evidence()
    {
        var path = Path.Combine(RepositoryRoot, "docs", "supply-chain", "THIRD_PARTY_PROVENANCE.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var artifacts = document.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();

        foreach (var id in new[] { "acv_tool_5", "audition_template_015", "audition_game_assets" })
        {
            var artifact = Assert.Single(artifacts, item => item.GetProperty("id").GetString() == id);
            Assert.False(artifact.GetProperty("commercialDistributionAllowed").GetBoolean());
            Assert.StartsWith("BLOCKED_", artifact.GetProperty("distributionCondition").GetString(),
                StringComparison.Ordinal);
        }

        var directXTex = Assert.Single(artifacts,
            item => item.GetProperty("id").GetString() == "microsoft_directxtex_texconv_may2026_x64");
        Assert.Equal("2026.5.8.1", directXTex.GetProperty("version").GetString());
        Assert.Equal("DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06",
            directXTex.GetProperty("sha256").GetString());
        Assert.Equal("MIT", directXTex.GetProperty("license").GetString());
    }

    [Fact]
    public void Github_action_dependencies_use_full_commit_sha()
    {
        var workflows = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, ".github", "workflows"), "*.yml");
        foreach (var workflow in workflows)
        {
            foreach (var line in File.ReadLines(workflow).Where(line => line.Contains("uses:", StringComparison.Ordinal)))
                Assert.Matches(PinnedActionPattern(), line);
        }
    }

    private static string? Property(XDocument document, string name) =>
        document.Descendants(name).SingleOrDefault()?.Value;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    [GeneratedRegex(@"^\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersionPattern();

    [GeneratedRegex(@"^\s*-?\s*uses:\s+[^\s]+@[0-9a-f]{40}(?:\s+#.*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PinnedActionPattern();
}
