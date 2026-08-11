using System.Xml.Linq;

namespace IntegrationTests;

public sealed class Plan41AppShellContractTests
{
    [Fact]
    public void App_shell_declares_exact_routes_and_top_bar_contract()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.App", "Shell", "AppShellViewModel.cs"));

        var labels = new[]
        {
            "Home", "Projects", "AI Studio", "Image Editor",
            "Mod Library", "Batch", "Cloud", "Settings"
        };

        foreach (var label in labels)
        {
            Assert.Contains($"\"{label}\"", source, StringComparison.Ordinal);
        }

        Assert.Contains("AccountStatus", source, StringComparison.Ordinal);
        Assert.Contains("CreditsStatus", source, StringComparison.Ordinal);
        Assert.Contains("NotificationStatus", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStatus", source, StringComparison.Ordinal);
        Assert.DoesNotContain("acv.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.IO", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_xaml_reuses_design_system_and_adaptive_native_navigation()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "MainPage.xaml");
        var document = XDocument.Load(path);
        var text = File.ReadAllText(path);

        Assert.Equal("NavigationView", document.Root!.Elements().Single().Name.LocalName);
        Assert.Contains("PaneDisplayMode=\"Auto\"", text, StringComparison.Ordinal);
        Assert.Contains("CompactModeThresholdWidth=\"640\"", text, StringComparison.Ordinal);
        Assert.Contains("ExpandedModeThresholdWidth=\"1000\"", text, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"900\"", text, StringComparison.Ordinal);
        Assert.Contains("AmsShellHeaderStyle", text, StringComparison.Ordinal);
        Assert.Contains("AmsGamingPanelStyle", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.HeadingLevel=\"Level1\"", text, StringComparison.Ordinal);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}", text);
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

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
