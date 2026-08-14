namespace IntegrationTests;

public sealed class VietnameseUiLocalizationTests
{
    [Fact]
    public void Production_ui_uses_vi_vn_resources_and_has_no_rejected_english_copy()
    {
        var root = FindRepositoryRoot();
        var appRoot = Path.Combine(root, "src", "AuditionModStudio.App");
        var project = File.ReadAllText(Path.Combine(appRoot, "AuditionModStudio.App.csproj"));
        var resources = File.ReadAllText(Path.Combine(appRoot, "Strings", "vi-VN", "Resources.resw"));
        var terminology = File.ReadAllText(Path.Combine(root, "docs", "UI_VIETNAMESE_TERMINOLOGY.md"));

        Assert.Contains("<DefaultLanguage>vi-VN</DefaultLanguage>", project, StringComparison.Ordinal);
        Assert.Contains("<NeutralLanguage>vi-VN</NeutralLanguage>", project, StringComparison.Ordinal);
        Assert.Contains("HomeHeading.Text", resources, StringComparison.Ordinal);
        Assert.Contains("ALLOWED_BRAND", terminology, StringComparison.Ordinal);
        Assert.Contains("NEEDS_TRANSLATION", terminology, StringComparison.Ordinal);

        var visibleSources = Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Concat(
            [
                Path.Combine(appRoot, "Shell", "AppShellViewModel.cs"),
                Path.Combine(appRoot, "Home", "HomeViewModel.cs"),
                Path.Combine(appRoot, "Workspace", "ProjectWorkspaceViewModel.cs"),
                Path.Combine(appRoot, "Workspace", "BuildExportViewModel.cs"),
                Path.Combine(appRoot, "Editor", "ImageEditorViewModel.cs"),
                Path.Combine(appRoot, "Editor", "ImageEditorPresentationModels.cs"),
                Path.Combine(appRoot, "AiStudio", "AiStudioViewModel.cs"),
                Path.Combine(appRoot, "AiStudio", "AiMaskEditorViewModel.cs"),
                Path.Combine(appRoot, "AiStudio", "AiStudioPresentationModels.cs"),
                Path.Combine(appRoot, "Account", "AccountViewModel.cs"),
                Path.Combine(appRoot, "Settings", "SettingsPage.xaml.cs")
            ]);
        var text = string.Join('\n', visibleSources.Select(File.ReadAllText));

        foreach (var rejected in new[]
        {
            "Choose game", "Choose Mod Type", "Create project", "Recent projects",
            "Project Workspace", "Texture Grid", "No active project", "No texture selected",
            "Image Editor", "Before / After", "Side-by-side", "Apply texture",
            "Create with AI", "Advanced settings", "Job history", "Your creative account",
            "Account services are unavailable", "Application theme", "Build &amp; Export",
            "Export complete", "Texture status", "Price unavailable", "Cancel loading"
        })
        {
            Assert.DoesNotContain(rejected, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
