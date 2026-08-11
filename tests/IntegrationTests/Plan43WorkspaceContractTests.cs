namespace IntegrationTests;

public sealed class Plan43WorkspaceContractTests
{
    [Fact]
    public void Workspace_view_declares_three_panes_and_complete_status_contract()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Workspace", "ProjectWorkspacePage.xaml");
        var text = File.ReadAllText(path);

        Assert.Contains("Folders", text, StringComparison.Ordinal);
        Assert.Contains("Preview / Editor", text, StringComparison.Ordinal);
        Assert.Contains("Metadata / Edit options", text, StringComparison.Ordinal);
        Assert.Contains("Target size", text, StringComparison.Ordinal);
        Assert.Contains("Format", text, StringComparison.Ordinal);
        Assert.Contains("State", text, StringComparison.Ordinal);
        Assert.Contains("Validation", text, StringComparison.Ordinal);
        Assert.Contains("Search textures", text, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"1100\"", text, StringComparison.Ordinal);
        Assert.Contains("AmsGamingPanelStyle", text, StringComparison.Ordinal);
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
