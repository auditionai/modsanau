namespace IntegrationTests;

public sealed class Plan45ImageEditorContractTests
{
    [Fact]
    public void Editor_view_declares_exact_target_interaction_modes_and_preview_only_boundary()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Editor", "ImageEditorPage.xaml");
        var text = File.ReadAllText(path);

        Assert.Contains("Khung cắt và đổi kích thước tương tác", text, StringComparison.Ordinal);
        Assert.Contains("Kích thước đích DDS chính xác", text, StringComparison.Ordinal);
        Assert.Contains("Thu phóng khung chỉnh sửa", text, StringComparison.Ordinal);
        Assert.Contains("Cập nhật vùng cắt", text, StringComparison.Ordinal);
        Assert.Contains("Chế độ đổi kích thước", text, StringComparison.Ordinal);
        Assert.Contains("Kéo để di chuyển", text, StringComparison.Ordinal);
        Assert.Contains("Di chuyển khung sang trái", text, StringComparison.Ordinal);
        Assert.Contains("Áp dụng Texture đã chọn", text, StringComparison.Ordinal);
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
