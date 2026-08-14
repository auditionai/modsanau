namespace IntegrationTests;

public sealed class Plan44TextureGridContractTests
{
    [Fact]
    public void Texture_grid_declares_cards_filters_and_lazy_container_loading()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Workspace", "ProjectWorkspacePage.xaml");
        var text = File.ReadAllText(path);

        Assert.Contains("Thư viện Texture", text, StringComparison.Ordinal);
        Assert.Contains("ThumbnailImage", text, StringComparison.Ordinal);
        Assert.Contains("DisplayName", text, StringComparison.Ordinal);
        Assert.Contains("FileName", text, StringComparison.Ordinal);
        Assert.Contains("TargetSize", text, StringComparison.Ordinal);
        Assert.Contains("Lọc theo trạng thái Texture", text, StringComparison.Ordinal);
        Assert.Contains("Lọc theo danh mục Texture", text, StringComparison.Ordinal);
        Assert.Contains("Lọc theo kích thước Texture", text, StringComparison.Ordinal);
        Assert.Contains("Lọc theo Alpha của Texture", text, StringComparison.Ordinal);
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
