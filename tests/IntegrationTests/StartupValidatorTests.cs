using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationTests;

public sealed class StartupValidatorTests
{
    [Fact]
    public async Task Validation_initializes_directories_outside_installation_directory()
    {
        var testRoot = CreateTestRoot();
        var installationDirectory = Path.Combine(testRoot, "InstalledApplication");
        var localApplicationData = Path.Combine(testRoot, "LocalApplicationData");

        try
        {
            Directory.CreateDirectory(installationDirectory);
            var paths = new AppPaths(localApplicationData);
            var pathSecurity = new PathSecurity();
            var validator = new StartupValidator(
                paths,
                pathSecurity,
                NullLogger<StartupValidator>.Instance,
                installationDirectory);

            await validator.ValidateAsync(CancellationToken.None);

            Assert.All(paths.ManagedDirectories, directory => Assert.True(Directory.Exists(directory)));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Validation_rejects_application_data_inside_installation_directory()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            var pathSecurity = new PathSecurity();
            var validator = new StartupValidator(
                paths,
                pathSecurity,
                NullLogger<StartupValidator>.Instance,
                testRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => validator.ValidateAsync(CancellationToken.None));

            Assert.Contains("installation directory", exception.Message, StringComparison.OrdinalIgnoreCase);
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
