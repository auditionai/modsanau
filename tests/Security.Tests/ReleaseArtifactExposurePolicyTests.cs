using System.Diagnostics;

namespace Security.Tests;

public sealed class ReleaseArtifactExposurePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"release-exposure-{Guid.NewGuid():N}");

    [Fact]
    public async Task Preparation_removes_symbols_and_clean_public_layout_passes()
    {
        CreateLayout();
        await File.WriteAllTextAsync(Path.Combine(_root, "AuditionModStudio.App.pdb"), "source path");
        await File.WriteAllTextAsync(Path.Combine(_root, "libSkiaSharp.pdb"), "symbols");

        var result = await RunAsync("-Prepare");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"PolicyStatus\":\"PASS\"", result.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.pdb", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("source.cs", "source")]
    [InlineData("view.xaml", "source")]
    [InlineData("signing.pfx", "private_key_material")]
    [InlineData("acv.exe", "proprietary_fixture")]
    [InlineData("texconv.exe", "proprietary_fixture")]
    [InlineData("working.acv", "proprietary_fixture")]
    [InlineData("session.log", "runtime_user_data")]
    [InlineData("AuditionModStudio.obfuscation-map.xml", "private_mapping")]
    public async Task Public_layout_rejects_sensitive_development_or_proprietary_artifacts(
        string fileName, string category)
    {
        CreateLayout();
        await File.WriteAllTextAsync(Path.Combine(_root, fileName), "not public");

        var result = await RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(category, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Portable_layout_accepts_exact_product_entry_point()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "AuditionAI.ModStudio.exe"), "release");
        await File.WriteAllTextAsync(Path.Combine(_root, "AuditionModStudio.App.dll"), "managed");

        var result = await RunAsync("-EntryPoint AuditionAI.ModStudio.exe");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"PolicyStatus\":\"PASS\"", result.Output, StringComparison.Ordinal);
    }

    private void CreateLayout()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "AuditionModStudio.App.exe"), "release");
        File.WriteAllText(Path.Combine(_root, "AuditionModStudio.App.dll"), "managed");
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string arguments = "")
    {
        var script = Path.Combine(FindRepositoryRoot(), "scripts", "Protect-ReleaseArtifactExposure.ps1");
        var command = $"& '{script.Replace("'", "''", StringComparison.Ordinal)}' " +
            $"-ArtifactRoot '{_root.Replace("'", "''", StringComparison.Ordinal)}' {arguments}";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command },
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
