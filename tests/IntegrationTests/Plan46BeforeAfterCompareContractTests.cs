namespace IntegrationTests;

public sealed class Plan46BeforeAfterCompareContractTests
{
    [Fact]
    public void Compare_view_declares_all_modes_alpha_keyboard_and_read_only_semantics()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Editor", "ImageEditorPage.xaml");
        var text = File.ReadAllText(path);

        Assert.Contains("Before / After Compare", text, StringComparison.Ordinal);
        Assert.Contains("SideBySideCompare", text, StringComparison.Ordinal);
        Assert.Contains("SliderCompare", text, StringComparison.Ordinal);
        Assert.Contains("ToggleCompare", text, StringComparison.Ordinal);
        Assert.Contains("Alpha checkerboard", text, StringComparison.Ordinal);
        Assert.Contains("Before session baseline", text, StringComparison.Ordinal);
        Assert.Contains("After live preview", text, StringComparison.Ordinal);
        Assert.Contains("Before and After comparison divider", text, StringComparison.Ordinal);
        Assert.Contains("Use Left and Right Arrow keys", text, StringComparison.Ordinal);
        Assert.Contains("Shared Before and After compare zoom", text, StringComparison.Ordinal);
        Assert.Contains("Read-only session baseline", text, StringComparison.Ordinal);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}", text);
    }

    [Fact]
    public void Compare_implementation_reuses_bitmap_adapter_and_slider_uses_clipping()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Editor", "ImageEditorPage.xaml.cs");
        var text = File.ReadAllText(path);

        Assert.Contains("InternalImageBitmapAdapter.CreateBitmap", text, StringComparison.Ordinal);
        Assert.Contains("AfterSliderLayer.Clip", text, StringComparison.Ordinal);
        Assert.Contains("ReleaseCompareBitmaps", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Encode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Save", text, StringComparison.Ordinal);
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
