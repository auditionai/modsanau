namespace IntegrationTests;

public sealed class Plan43WorkspaceContractTests
{
    [Fact]
    public void Workspace_view_declares_three_panes_and_complete_status_contract()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "AuditionModStudio.App", "Workspace", "ProjectWorkspacePage.xaml");
        var text = File.ReadAllText(path);

        Assert.Contains("Thư mục", text, StringComparison.Ordinal);
        Assert.Contains("Xem trước / Chỉnh sửa", text, StringComparison.Ordinal);
        Assert.Contains("Thông tin Texture", text, StringComparison.Ordinal);
        Assert.Contains("Kích thước đích", text, StringComparison.Ordinal);
        Assert.Contains("Định dạng", text, StringComparison.Ordinal);
        Assert.Contains("Trạng thái", text, StringComparison.Ordinal);
        Assert.Contains("Kiểm tra", text, StringComparison.Ordinal);
        Assert.Contains("Tìm kiếm Texture", text, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"1100\"", text, StringComparison.Ordinal);
        Assert.Contains("AmsCardStyle", text, StringComparison.Ordinal);
        Assert.Contains("AmsAccentCardStyle", text, StringComparison.Ordinal);
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
