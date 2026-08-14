namespace IntegrationTests;

public sealed class Plan101UxRedesignContractTests
{
    [Fact]
    public void Creative_tech_studio_pairs_daylight_and_night_with_bounded_hero_depth()
    {
        var root = FindRepositoryRoot();
        var appRoot = Path.Combine(root, "src", "AuditionModStudio.App");
        var semantic = File.ReadAllText(Path.Combine(appRoot, "DesignSystem", "SemanticTokens.xaml"));
        var componentTokens = File.ReadAllText(Path.Combine(appRoot, "DesignSystem", "ComponentTokens.xaml"));
        var components = File.ReadAllText(Path.Combine(appRoot, "DesignSystem", "Components.xaml"));
        var shell = File.ReadAllText(Path.Combine(appRoot, "MainPage.xaml"));
        var window = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml"));
        var settings = File.ReadAllText(Path.Combine(appRoot, "Settings", "SettingsPage.xaml"));

        Assert.Contains("#FFF4F6FB", semantic, StringComparison.Ordinal);
        Assert.Contains("#FF315EFB", semantic, StringComparison.Ordinal);
        Assert.Contains("#FFFF6B5E", semantic, StringComparison.Ordinal);
        Assert.Contains("LinearGradientBrush", componentTokens, StringComparison.Ordinal);
        Assert.Contains("AmsHeroGradientBrush", componentTokens, StringComparison.Ordinal);
        Assert.Contains("AmsFlatButtonTemplate", components, StringComparison.Ordinal);
        Assert.DoesNotContain("AmsDepthButtonTemplate", components, StringComparison.Ordinal);
        Assert.Contains("PaneDisplayMode=\"Top\"", shell, StringComparison.Ordinal);
        Assert.Contains("RequestedTheme=\"Light\"", window, StringComparison.Ordinal);
        Assert.Contains("Creative Daylight", settings, StringComparison.Ordinal);
        Assert.Contains("Studio Night", settings, StringComparison.Ordinal);
        Assert.Contains("Theo Windows", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Design_system_exposes_premium_editor_hierarchy_and_accessible_interaction_contracts()
    {
        var root = FindRepositoryRoot();
        var designRoot = Path.Combine(root, "src", "AuditionModStudio.App", "DesignSystem");
        var primitive = File.ReadAllText(Path.Combine(designRoot, "PrimitiveTokens.xaml"));
        var semantic = File.ReadAllText(Path.Combine(designRoot, "SemanticTokens.xaml"));
        var components = File.ReadAllText(Path.Combine(designRoot, "Components.xaml"));

        Assert.Contains("x:Key=\"AmsSpace4\"", primitive, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AmsSpace64\"", primitive, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AmsTypeDisplay\"", primitive, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AmsCanvasColor\"", semantic, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AmsTextMutedColor\"", semantic, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AmsAccentSubtleColor\"", semantic, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AmsEditorSurfaceStyle\"", components, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AmsEmptyStateStyle\"", components, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AmsNavigationItemStyle\"", components, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AmsPrimaryButtonStyle\"", components, StringComparison.Ordinal);
        Assert.Contains("MinHeight\" Value=\"48\"", components, StringComparison.Ordinal);
        Assert.Contains("MinHeight\" Value=\"44\"", components, StringComparison.Ordinal);
        Assert.DoesNotContain("ThemeShadow", components, StringComparison.Ordinal);
    }

    [Fact]
    public void Primary_surfaces_use_full_width_editor_layouts_without_legacy_page_caps()
    {
        var root = FindRepositoryRoot();
        var appRoot = Path.Combine(root, "src", "AuditionModStudio.App");
        var pages = new[]
        {
            Path.Combine(appRoot, "Home", "HomePage.xaml"),
            Path.Combine(appRoot, "Workspace", "ProjectWorkspacePage.xaml"),
            Path.Combine(appRoot, "Editor", "ImageEditorPage.xaml"),
            Path.Combine(appRoot, "AiStudio", "AiStudioPage.xaml"),
            Path.Combine(appRoot, "Account", "AccountPage.xaml"),
            Path.Combine(appRoot, "Settings", "SettingsPage.xaml"),
        };

        foreach (var page in pages)
        {
            var text = File.ReadAllText(page);
            Assert.DoesNotContain("MaxWidth=\"960\"", text, StringComparison.Ordinal);
            Assert.Contains("AmsShellContentPadding", text, StringComparison.Ordinal);
        }

        var workspace = File.ReadAllText(pages[1]);
        var editor = File.ReadAllText(pages[2]);
        var aiStudio = File.ReadAllText(pages[3]);
        Assert.Contains("2.7*", workspace, StringComparison.Ordinal);
        Assert.Contains("3*", editor, StringComparison.Ordinal);
        Assert.Contains("2.6*", aiStudio, StringComparison.Ordinal);
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
