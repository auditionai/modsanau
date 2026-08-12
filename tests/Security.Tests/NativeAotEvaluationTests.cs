using System.Diagnostics;

namespace Security.Tests;

public sealed class NativeAotEvaluationTests
{
    [Fact]
    public async Task Debug_probe_is_rejected()
    {
        var result = await RunAsync("-Configuration Debug");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("NATIVE_AOT_RELEASE_ONLY", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policy_reports_evaluated_without_adopting_project_property()
    {
        var result = await RunAsync("-Configuration Release");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("EVALUATED_NOT_ADOPTED", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"PublishAotAdopted\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("false", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adr_records_real_analyzer_failure_and_required_compatibility_matrix()
    {
        var repository = FindRepositoryRoot();
        var adr = File.ReadAllText(Path.Combine(repository, "docs", "ADR", "0004-native-aot-evaluation.md"));
        var project = File.ReadAllText(Path.Combine(repository, "src", "AuditionModStudio.App", "AuditionModStudio.App.csproj"));

        Assert.Contains("EVALUATED / NOT ADOPTED / PRODUCTION NOT VERIFIED", adr, StringComparison.Ordinal);
        Assert.Contains("IL2026", adr, StringComparison.Ordinal);
        Assert.Contains("IL3050", adr, StringComparison.Ordinal);
        Assert.Contains("WinUI 3/XAML", adr, StringComparison.Ordinal);
        Assert.Contains("DirectXTex", adr, StringComparison.Ordinal);
        Assert.Contains("Supabase/backend client", adr, StringComparison.Ordinal);
        Assert.Contains("Plugin", adr, StringComparison.Ordinal);
        Assert.DoesNotContain("<PublishAot>true</PublishAot>", project, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string arguments)
    {
        var script = Path.Combine(FindRepositoryRoot(), "scripts", "Invoke-NativeAotEvaluation.ps1");
        var command = $"& '{script.Replace("'", "''", StringComparison.Ordinal)}' {arguments}";
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
}
