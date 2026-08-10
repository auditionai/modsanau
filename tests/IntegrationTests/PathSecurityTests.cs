using AuditionModStudio.Infrastructure.Paths;

namespace IntegrationTests;

public sealed class PathSecurityTests
{
    private readonly PathSecurity _pathSecurity = new();

    [Fact]
    public void Safe_texture_relative_path_is_accepted()
    {
        var root = CreateTestRoot();

        var result = _pathSecurity.ResolvePathWithinRoot(root, @"texture\gui\file.dds");

        Assert.Equal(Path.Combine(root, "texture", "gui", "file.dds"), result);
    }

    [Theory]
    [InlineData(@"..\..\Windows\file")]
    [InlineData("../../file")]
    [InlineData(@"texture/../file.dds")]
    [InlineData(@"texture\.\file.dds")]
    public void Relative_navigation_is_rejected(string relativePath)
    {
        var root = CreateTestRoot();

        Assert.Throws<ArgumentException>(
            () => _pathSecurity.ResolvePathWithinRoot(root, relativePath));
    }

    [Theory]
    [InlineData(@"C:\Windows\file")]
    [InlineData(@"D:file.dds")]
    [InlineData(@"\\server\share\file.dds")]
    [InlineData("/Windows/file")]
    public void Rooted_or_drive_path_injection_is_rejected(string path)
    {
        var root = CreateTestRoot();

        Assert.Throws<ArgumentException>(() => _pathSecurity.ResolvePathWithinRoot(root, path));
    }

    [Theory]
    [InlineData(@"Sàn Aubiz 015\texture file.dds")]
    [InlineData(@"Hiệu ứng\Áo dài.dds")]
    public void Spaces_and_unicode_are_supported(string relativePath)
    {
        var root = CreateTestRoot();

        var result = _pathSecurity.ResolvePathWithinRoot(root, relativePath);
        var relative = Path.GetRelativePath(root, result);

        Assert.False(Path.IsPathRooted(relative));
        Assert.DoesNotContain("..", relative.Split(Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.dds")]
    [InlineData("file. ")]
    [InlineData("stream:alternate")]
    public void Unsafe_windows_filename_segments_are_rejected(string relativePath)
    {
        var root = CreateTestRoot();

        Assert.Throws<ArgumentException>(
            () => _pathSecurity.ResolvePathWithinRoot(root, relativePath));
    }

    [Fact]
    public void Candidate_outside_root_is_rejected_by_reparse_validation()
    {
        var root = CreateTestRoot();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside");

        Assert.Throws<InvalidOperationException>(
            () => _pathSecurity.EnsureNoReparsePoints(root, outside));
    }

    [Fact(Skip = "Requires Windows symbolic-link creation privilege; run manually in an elevated test process.")]
    public void Existing_directory_symbolic_link_is_rejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = CreateTestRoot();
        var outside = testRoot + ".outside";
        var link = Path.Combine(testRoot, "linked");

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(outside);

            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                throw new InvalidOperationException(
                    "The elevated integration-test environment cannot create a symbolic link.",
                    exception);
            }

            var candidate = Path.Combine(link, "file.dds");
            Assert.Throws<InvalidOperationException>(
                () => _pathSecurity.EnsureNoReparsePoints(testRoot, candidate));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    private static string CreateTestRoot()
    {
        return Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "AuditionModStudio.Tests", Guid.NewGuid().ToString("N")));
    }
}
