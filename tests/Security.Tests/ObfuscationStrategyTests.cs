using System.Diagnostics;

namespace Security.Tests;

public sealed class ObfuscationStrategyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"obfuscation-policy-{Guid.NewGuid():N}");

    [Fact]
    public async Task Debug_obfuscation_is_rejected_and_normal_debug_is_explicitly_not_adopted()
    {
        Directory.CreateDirectory(_root);
        var rejected = await RunAsync("-Configuration Debug -ObfuscationEnabled");
        var allowed = await RunAsync("-Configuration Debug");

        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("OBFUSCATION_DEBUG_FORBIDDEN", rejected.Output, StringComparison.Ordinal);
        Assert.Equal(0, allowed.ExitCode);
        Assert.Contains("NOT_ADOPTED", allowed.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_release_rejects_mapping_or_dotfuscator_report()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "AuditionModStudio.obfuscation-map.xml"), "private symbols");

        var result = await RunAsync("-Configuration Release");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OBFUSCATION_MAPPING_PUBLIC_LEAK", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Adr_keeps_anti_tamper_disabled_and_maps_private_until_compatibility_gate()
    {
        var repository = FindRepositoryRoot();
        var adr = File.ReadAllText(Path.Combine(repository, "docs", "ADR", "0003-binary-obfuscation-strategy.md"));
        var packages = File.ReadAllText(Path.Combine(repository, "Directory.Packages.props"));
        var releaseWorkflow = File.ReadAllText(Path.Combine(repository, ".github", "workflows", "release-signing.yml"));

        Assert.Contains("EVALUATED / NOT ADOPTED", adr, StringComparison.Ordinal);
        Assert.Contains("Anti-tamper/checks:** disabled", adr, StringComparison.Ordinal);
        Assert.Contains("Debug", adr, StringComparison.Ordinal);
        Assert.Contains("private", adr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WinUI", adr, StringComparison.Ordinal);
        Assert.DoesNotContain("Dotfuscator", packages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Test-ObfuscationArtifactPolicy.ps1", releaseWorkflow, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string arguments)
    {
        var script = Path.Combine(FindRepositoryRoot(), "scripts", "Test-ObfuscationArtifactPolicy.ps1");
        var command = $"& '{script.Replace("'", "''", StringComparison.Ordinal)}' " +
            $"-PublicArtifactRoot '{_root.Replace("'", "''", StringComparison.Ordinal)}' {arguments}";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command }
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AuditionModStudio.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
