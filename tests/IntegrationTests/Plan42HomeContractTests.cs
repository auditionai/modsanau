namespace IntegrationTests;

public sealed class Plan42HomeContractTests
{
    [Fact]
    public void Home_view_presents_game_mod_project_order_without_filesystem_entry_point()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Home", "HomePage.xaml");
        var text = File.ReadAllText(path);
        Assert.Contains("Chọn game", text, StringComparison.Ordinal);
        Assert.Contains("Chọn loại Mod", text, StringComparison.Ordinal);
        Assert.Contains("Tạo dự án", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("01  ·  Chọn game", StringComparison.Ordinal)
                    < text.IndexOf("02  ·  Chọn loại Mod", StringComparison.Ordinal));
        Assert.True(text.IndexOf("02  ·  Chọn loại Mod", StringComparison.Ordinal)
                    < text.IndexOf("03  ·  Thông tin dự án", StringComparison.Ordinal));
        Assert.Contains("AmsCardStyle", text, StringComparison.Ordinal);
        Assert.Contains("AmsAccentCardStyle", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HomeWorkspace\"", text, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"470\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxWidth=\"960\"", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FilePicker", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acv.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.IO", text, StringComparison.Ordinal);
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
