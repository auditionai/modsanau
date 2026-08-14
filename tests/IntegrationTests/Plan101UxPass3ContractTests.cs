namespace IntegrationTests;

public sealed class Plan101UxPass3ContractTests
{
    [Fact]
    public void Shell_exposes_context_commands_bounded_activity_and_notifications()
    {
        var shell = Read("src", "AuditionModStudio.App", "MainPage.xaml");
        var code = Read("src", "AuditionModStudio.App", "MainPage.xaml.cs");

        Assert.Contains("Modifiers=\"Control\"", shell, StringComparison.Ordinal);
        Assert.Contains("Nhật ký tác vụ", shell, StringComparison.Ordinal);
        Assert.Contains("Tối đa 100 sự kiện", shell, StringComparison.Ordinal);
        Assert.Contains("InfoBar", shell, StringComparison.Ordinal);
        Assert.Contains("MaximumActivityEntries = 100", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FinalOutputPath", code, StringComparison.Ordinal);
        Assert.DoesNotContain("stdout", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack trace", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Home_uses_three_keyboard_reachable_real_content_slides_without_continuous_animation()
    {
        var home = Read("src", "AuditionModStudio.App", "Home", "HomePage.xaml");

        Assert.Contains("x:Name=\"HeroSlider\"", home, StringComparison.Ordinal);
        Assert.Equal(3, Count(home, "<FlipViewItem>"));
        Assert.Contains("Tạo dự án → Chỉnh sửa → Build → Xuất tệp độc lập.", home, StringComparison.Ordinal);
        Assert.Contains("working copy", home, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".ab/.acv", home, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard RepeatBehavior=\"Forever\"", home, StringComparison.Ordinal);
    }

    [Fact]
    public void Studio_night_is_a_complete_semantic_theme_and_pages_remain_adaptive()
    {
        var semantic = Read("src", "AuditionModStudio.App", "DesignSystem", "SemanticTokens.xaml");
        var settings = Read("src", "AuditionModStudio.App", "Settings", "SettingsPage.xaml");
        var workspace = Read("src", "AuditionModStudio.App", "Workspace", "ProjectWorkspacePage.xaml");
        var editor = Read("src", "AuditionModStudio.App", "Editor", "ImageEditorPage.xaml");

        Assert.Contains("x:Key=\"Dark\"", semantic, StringComparison.Ordinal);
        Assert.Contains("#FF080D18", semantic, StringComparison.Ordinal);
        Assert.Contains("Studio Night", settings, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger", workspace, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger", editor, StringComparison.Ordinal);
        Assert.Contains("TextureCountDisplay", workspace, StringComparison.Ordinal);
        Assert.Contains("ZoomBadgeText", editor, StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static string Read(params string[] segments) => File.ReadAllText(PathOf(segments));

    private static string PathOf(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return Path.Combine([directory?.FullName ?? throw new DirectoryNotFoundException(), .. segments]);
    }
}
