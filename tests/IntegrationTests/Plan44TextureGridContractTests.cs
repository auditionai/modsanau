namespace IntegrationTests;

public sealed class Plan44TextureGridContractTests
{
    [Fact]
    public void Texture_grid_declares_cards_filters_and_lazy_container_loading()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Workspace", "ProjectWorkspacePage.xaml");
        var text = File.ReadAllText(path);

        Assert.Contains("Texture Grid", text, StringComparison.Ordinal);
        Assert.Contains("ThumbnailImage", text, StringComparison.Ordinal);
        Assert.Contains("DisplayName", text, StringComparison.Ordinal);
        Assert.Contains("FileName", text, StringComparison.Ordinal);
        Assert.Contains("TargetSize", text, StringComparison.Ordinal);
        Assert.Contains("Texture status filter", text, StringComparison.Ordinal);
        Assert.Contains("Texture category filter", text, StringComparison.Ordinal);
        Assert.Contains("Texture size filter", text, StringComparison.Ordinal);
        Assert.Contains("Texture alpha filter", text, StringComparison.Ordinal);
        Assert.Contains("ContainerContentChanging=\"OnTextureContainerContentChanging\"", text, StringComparison.Ordinal);
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
