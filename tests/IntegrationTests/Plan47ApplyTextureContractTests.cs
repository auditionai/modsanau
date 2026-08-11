namespace IntegrationTests;

public sealed class Plan47ApplyTextureContractTests
{
    [Fact]
    public void Editor_declares_accessible_apply_progress_and_cancellation_controls()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Editor", "ImageEditorPage.xaml");
        var text = File.ReadAllText(path);

        Assert.Contains("Apply selected texture", text, StringComparison.Ordinal);
        Assert.Contains("Apply texture", text, StringComparison.Ordinal);
        Assert.Contains("Texture Apply progress", text, StringComparison.Ordinal);
        Assert.Contains("Cancel texture Apply", text, StringComparison.Ordinal);
        Assert.Contains("atomically replaces only the selected texture", text, StringComparison.Ordinal);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}", text);
    }

    [Fact]
    public void Apply_is_registered_behind_project_service_and_page_has_no_dds_or_file_calls()
    {
        var root = FindRepositoryRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "src",
            "AuditionModStudio.App",
            "Bootstrap",
            "ApplicationBootstrapper.cs"));
        var page = File.ReadAllText(Path.Combine(
            root,
            "src",
            "AuditionModStudio.App",
            "Editor",
            "ImageEditorPage.xaml.cs"));

        Assert.Contains("ITextureApplyService, TextureApplyService", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("IDds", page, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("acv", page, StringComparison.OrdinalIgnoreCase);
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
