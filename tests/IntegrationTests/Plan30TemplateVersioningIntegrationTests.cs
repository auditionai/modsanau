using System.Security.Cryptography;
using System.Text.Json;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Workspaces;
using AuditionModStudio.Projects;

namespace IntegrationTests;

public sealed class Plan30TemplateVersioningIntegrationTests
{
    [Fact]
    public async Task Existing_project_keeps_exact_template_snapshot_when_catalog_current_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "AuditionModStudio-Plan30", Guid.NewGuid().ToString("N"));
        try
        {
            var appPaths = new AppPaths(root);
            appPaths.EnsureDirectoriesExist();
            var sourceRoot = Path.Combine(root, "Pristine");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "templates"));
            var sourcePath = Path.Combine(sourceRoot, "templates", "015.ab");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4, 5]);
            var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));
            var v1 = Template("1", sourceHash);
            var v2 = Template("2", new string('B', 64));
            var catalogResult = TemplateVersionCatalog.Create([new(v1, false), new(v2, true)]);
            Assert.True(catalogResult.Succeeded);

            var pathSecurity = new PathSecurity();
            await using var workspaces = new SecureWorkspaceService(appPaths, pathSecurity);
            var service = new ProjectArchiveWorkspaceService(
                appPaths, pathSecurity, workspaces, new ProjectArchiveWorkspaceManifestStore(pathSecurity));
            var created = await service.CreateAsync(new(
                "PLAN 30 exact snapshot", v1, new PristineArchiveSource(sourceRoot)));
            Assert.True(created.Succeeded, created.DiagnosticCode);
            await using var workspace = Assert.IsAssignableFrom<IProjectArchiveWorkspace>(created.Workspace);

            var resolution = catalogResult.Catalog!.Resolve(workspace.Descriptor.ArchiveTemplate);
            Assert.Equal(TemplateResolutionStatus.CurrentVersionDiffers, resolution.Status);
            Assert.Equal("1", workspace.Descriptor.ArchiveTemplate.TemplateVersion?.Value);
            Assert.Equal(sourceHash, workspace.Descriptor.ArchiveTemplate.SourceSha256.Value);
            Assert.Equal("audition-vn-2026", workspace.Descriptor.ArchiveTemplate.CompatibleGameBuild?.Value);

            var manifestPath = Path.Combine(
                workspace.ArchiveWorkspace.SecureWorkspace.Paths.RootDirectory,
                workspace.Descriptor.ManifestRelativePath);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.Equal("1", manifest.RootElement.GetProperty("archiveTemplateVersion").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static AuditionArchiveTemplate Template(string version, string hash) => new(
        "archive-015", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
        "audition_vn", "015", version, hash, "audition-vn-2026");
}
