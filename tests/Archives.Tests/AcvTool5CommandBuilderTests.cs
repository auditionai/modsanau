using AuditionModStudio.Archives;

namespace Archives.Tests;

public sealed class AcvTool5CommandBuilderTests
{
    [Fact]
    public void Extract_uses_direct_process_and_structured_arguments()
    {
        var startInfo = AcvTool5CommandBuilder.Create(
            @"C:\trusted tool\acv.exe",
            @"C:\workspace",
            AcvTool5Operation.Extract,
            "015 sample.ab",
            "Sàn Aubiz 015");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(@"C:\trusted tool\acv.exe", startInfo.FileName);
        Assert.Equal(new[] { "-da", "015 sample.ab", "Sàn Aubiz 015" }, startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
        Assert.DoesNotContain(startInfo.Environment.Keys,
            key => key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || key.Contains("CONNECTION", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\workspace", startInfo.Environment["TEMP"]);
        Assert.DoesNotContain(@"C:\workspace", startInfo.Environment["PATH"] ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pack_uses_expected_argument_switch()
    {
        var startInfo = AcvTool5CommandBuilder.Create(
            @"C:\tool\acv.exe",
            @"C:\workspace",
            AcvTool5Operation.Pack,
            "archive.custom",
            "folder");

        Assert.Equal(new[] { "-ca", "archive.custom", "folder" }, startInfo.ArgumentList);
    }

    [Fact]
    public void Bare_executable_name_is_rejected_before_process_start()
    {
        Assert.Throws<ArgumentException>(() => AcvTool5CommandBuilder.Create(
            "acv.exe", @"C:\workspace", AcvTool5Operation.Extract, "015.ab", "015"));
    }
}
