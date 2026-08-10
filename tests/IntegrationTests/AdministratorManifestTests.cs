using System.Xml.Linq;

namespace IntegrationTests;

public sealed class AdministratorManifestTests
{
    [Fact]
    public void Application_manifest_requires_administrator_without_ui_access()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(
            repositoryRoot,
            "src",
            "AuditionModStudio.App",
            "app.manifest");
        var manifest = XDocument.Load(manifestPath);
        var requestedExecutionLevel = manifest
            .Descendants()
            .Single(element => element.Name.LocalName == "requestedExecutionLevel");

        Assert.Equal("requireAdministrator", requestedExecutionLevel.Attribute("level")?.Value);
        Assert.Equal("false", requestedExecutionLevel.Attribute("uiAccess")?.Value);
    }

    [Fact]
    public void App_project_embeds_the_administrator_manifest()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "AuditionModStudio.App",
            "AuditionModStudio.App.csproj");
        var project = XDocument.Load(projectPath);
        var applicationManifest = project
            .Descendants()
            .Single(element => element.Name.LocalName == "ApplicationManifest");

        Assert.Equal("app.manifest", applicationManifest.Value);
    }

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

        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
