using System.Xml.Linq;

namespace IntegrationTests;

public sealed class Plan42HomeContractTests
{
    [Fact]
    public void Home_view_presents_game_mod_project_order_without_filesystem_entry_point()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Home", "HomePage.xaml");
        var text = File.ReadAllText(path);
        var document = XDocument.Load(path);

        Assert.NotNull(document.Root);
        Assert.Contains("Choose game", text, StringComparison.Ordinal);
        Assert.Contains("Choose Mod Type", text, StringComparison.Ordinal);
        Assert.Contains("Create project", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Choose game", StringComparison.Ordinal)
                    < text.IndexOf("Choose Mod Type", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Choose Mod Type", StringComparison.Ordinal)
                    < text.IndexOf("Create project", StringComparison.Ordinal));
        Assert.Contains("AmsCardStyle", text, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"860\"", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FilePicker", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acv", text, StringComparison.OrdinalIgnoreCase);
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
