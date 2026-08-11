namespace IntegrationTests;

public sealed class Plan45ImageEditorContractTests
{
    [Fact]
    public void Editor_view_declares_exact_target_interaction_modes_and_preview_only_boundary()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Editor", "ImageEditorPage.xaml");
        var text = File.ReadAllText(path);

        Assert.Contains("Interactive target frame", text, StringComparison.Ordinal);
        Assert.Contains("Exact DDS target dimensions", text, StringComparison.Ordinal);
        Assert.Contains("Canvas zoom", text, StringComparison.Ordinal);
        Assert.Contains("Update crop frame", text, StringComparison.Ordinal);
        Assert.Contains("Resize mode", text, StringComparison.Ordinal);
        Assert.Contains("Drag to pan", text, StringComparison.Ordinal);
        Assert.Contains("Pan canvas left", text, StringComparison.Ordinal);
        Assert.Contains("Apply selected texture", text, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"1000\"", text, StringComparison.Ordinal);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}", text);

        var shellPath = Path.Combine(root, "src", "AuditionModStudio.App", "MainPage.xaml");
        Assert.Contains("ImageEditorContent", File.ReadAllText(shellPath), StringComparison.Ordinal);
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
