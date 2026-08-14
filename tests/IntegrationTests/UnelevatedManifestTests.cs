using System.Xml.Linq;

namespace IntegrationTests;

public sealed class UnelevatedManifestTests
{
    [Fact]
    public void Application_manifest_runs_as_invoker_without_ui_access()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "src", "AuditionModStudio.App", "app.manifest");
        var manifest = XDocument.Load(manifestPath);
        var requestedExecutionLevel = manifest.Descendants()
            .Single(element => element.Name.LocalName == "requestedExecutionLevel");

        Assert.Equal("asInvoker", requestedExecutionLevel.Attribute("level")?.Value);
        Assert.Equal("false", requestedExecutionLevel.Attribute("uiAccess")?.Value);
        Assert.DoesNotContain("requireAdministrator", File.ReadAllText(manifestPath), StringComparison.Ordinal);
    }

    [Fact]
    public void App_project_embeds_the_unelevated_manifest_without_self_elevation_contract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", "AuditionModStudio.App",
            "AuditionModStudio.App.csproj");
        var project = XDocument.Load(projectPath);
        var applicationManifest = project.Descendants()
            .Single(element => element.Name.LocalName == "ApplicationManifest");

        Assert.Equal("app.manifest", applicationManifest.Value);
        var runtimeSource = string.Join('\n', Directory.EnumerateFiles(
            Path.Combine(repositoryRoot, "src", "AuditionModStudio.App"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        Assert.DoesNotContain("runas", runtimeSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Verb =", runtimeSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
