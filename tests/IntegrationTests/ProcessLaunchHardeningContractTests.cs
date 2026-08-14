using System.Diagnostics;
using AuditionModStudio.Infrastructure.Processes;

namespace IntegrationTests;

public sealed class ProcessLaunchHardeningContractTests
{
    [Fact]
    public void Child_environment_does_not_forward_sensitive_or_user_search_variables()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"launch-policy-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(root, "trusted.exe"),
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--typed-option");
            startInfo.Environment["AUDITION_PROVIDER_API_KEY"] = "must-not-forward";
            startInfo.Environment["DATABASE_CONNECTION_STRING"] = "must-not-forward";
            startInfo.Environment["PATH"] = Path.Combine(root, "malicious-path-first");

            WindowsProcessLaunchHardening.HardenChildStartInfo(startInfo, root);

            Assert.True(Path.IsPathFullyQualified(startInfo.FileName));
            Assert.False(startInfo.UseShellExecute);
            Assert.Empty(startInfo.Arguments);
            Assert.Equal(["--typed-option"], startInfo.ArgumentList);
            Assert.False(startInfo.Environment.ContainsKey("AUDITION_PROVIDER_API_KEY"));
            Assert.False(startInfo.Environment.ContainsKey("DATABASE_CONNECTION_STRING"));
            Assert.DoesNotContain(root, startInfo.Environment["PATH"] ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public void Shell_executable_is_rejected_even_with_absolute_path(string shellName)
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(root, shellName),
            WorkingDirectory = root,
            UseShellExecute = false
        };

        Assert.Throws<ArgumentException>(() =>
            WindowsProcessLaunchHardening.HardenChildStartInfo(startInfo, root));
    }

    [Fact]
    public void App_applies_system32_and_application_dll_policy_after_winui_composition()
    {
        var root = FindRepositoryRoot();
        var policy = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.Infrastructure", "Processes",
            "WindowsProcessLaunchHardening.cs"));
        var app = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.App", "App.xaml.cs"));

        Assert.Contains("LoadLibrarySearchApplicationDirectory | LoadLibrarySearchSystem32", policy,
            StringComparison.Ordinal);
        Assert.Contains("SetDllDirectory(string.Empty)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadLibrarySearchUser", policy, StringComparison.Ordinal);
        Assert.True(app.IndexOf("ApplyProcessDllPolicy", StringComparison.Ordinal)
            > app.IndexOf("GetRequiredService<MainWindow>", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
