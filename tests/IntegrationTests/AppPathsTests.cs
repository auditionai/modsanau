using AuditionModStudio.Infrastructure.Paths;

namespace IntegrationTests;

public sealed class AppPathsTests
{
    [Fact]
    public void Default_paths_are_rooted_in_local_application_data()
    {
        var paths = new AppPaths();
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.ApplicationDirectoryName);

        Assert.Equal(Path.GetFullPath(expectedRoot), paths.RootDirectory);
        Assert.DoesNotContain(AppContext.BaseDirectory, paths.RootDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ensure_directories_exist_creates_the_complete_baseline()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            paths.EnsureDirectoriesExist();

            Assert.Equal(10, paths.ManagedDirectories.Count);
            Assert.All(paths.ManagedDirectories, directory => Assert.True(Directory.Exists(directory)));
            Assert.EndsWith(Path.Combine("AuditionModStudio", "Logs"), paths.LogsDirectory);
            Assert.EndsWith(Path.Combine("AuditionModStudio", "Cache"), paths.CacheDirectory);
            Assert.EndsWith(Path.Combine("AuditionModStudio", "Projects"), paths.ProjectsDirectory);
            Assert.EndsWith(Path.Combine("AuditionModStudio", "Temp"), paths.TempDirectory);
            Assert.EndsWith(Path.Combine("AuditionModStudio", "Settings"), paths.SettingsDirectory);
            Assert.EndsWith(Path.Combine("AuditionModStudio", "Downloads"), paths.DownloadsDirectory);
            Assert.EndsWith(
                Path.Combine("AuditionModStudio", "SecureTemplateCache"),
                paths.SecureTemplateCacheDirectory);
            Assert.EndsWith(Path.Combine("AuditionModStudio", "Backups"), paths.BackupsDirectory);
            Assert.EndsWith(
                Path.Combine("AuditionModStudio", "Temp", "Workspaces"),
                paths.WorkspacesDirectory);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static string CreateTestRoot()
    {
        return Path.Combine(Path.GetTempPath(), "AuditionModStudio.Tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string testRoot)
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
