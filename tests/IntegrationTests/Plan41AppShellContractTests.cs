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
            "Trang chủ", "Dự án", "AI Studio", "Trình chỉnh sửa ảnh", "Tài khoản", "Cài đặt"
        };

        foreach (var label in labels)
        {
            Assert.Contains($"\"{label}\"", source, StringComparison.Ordinal);
        }

        Assert.Contains("AccountStatus", source, StringComparison.Ordinal);
        Assert.Contains("CreditsStatus", source, StringComparison.Ordinal);
        Assert.Contains("NotificationStatus", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStatus", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Mod Library", source, StringComparison.Ordinal);
        Assert.DoesNotContain("will be available in a later plan", source, StringComparison.OrdinalIgnoreCase);
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

        Assert.Single(document.Root!.Elements(), element => element.Name.LocalName == "NavigationView");
        Assert.Contains("PaneDisplayMode=\"Top\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenPaneLength", text, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", text, StringComparison.Ordinal);
        Assert.Contains("VerticalContentAlignment=\"Stretch\"", text, StringComparison.Ordinal);
        Assert.Contains("NavigationView.PaneHeader", text, StringComparison.Ordinal);
        Assert.Contains("AmsGamingPanelStyle", text, StringComparison.Ordinal);
        Assert.Contains("Text=\"KHÔNG GIAN SÁNG TẠO\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestedTheme=\"Light\"", text, StringComparison.Ordinal);
        Assert.Contains("Studio Night", File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.App", "Settings", "SettingsPage.xaml")), StringComparison.Ordinal);
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
