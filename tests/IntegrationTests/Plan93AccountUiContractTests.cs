using System.Xml.Linq;

namespace IntegrationTests;

public sealed class Plan93AccountUiContractTests
{
    [Fact]
    public void Account_page_is_read_only_accessible_adaptive_and_uses_design_system()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Account", "AccountPage.xaml");
        var text = File.ReadAllText(path);
        var document = XDocument.Load(path);

        Assert.Equal("Page", document.Root!.Name.LocalName);
        Assert.Contains("AutomationProperties.HeadingLevel=\"Level1\"", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", text, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"840\"", text, StringComparison.Ordinal);
        Assert.Contains("AmsGamingPanelStyle", text, StringComparison.Ordinal);
        Assert.Contains("AmsAccentCardStyle", text, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"44\"", text, StringComparison.Ordinal);
        Assert.Contains("Chưa có giao dịch Credits", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TextBox", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordBox", text, StringComparison.Ordinal);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}", text);
    }

    [Fact]
    public void Account_code_behind_only_routes_ui_events_to_view_model()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.App", "Account", "AccountPage.xaml.cs"));

        Assert.Contains("ViewModel.ActivateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
