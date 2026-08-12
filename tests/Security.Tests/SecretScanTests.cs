using System.Diagnostics;

namespace Security.Tests;

public sealed class SecretScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"secret-scan-{Guid.NewGuid():N}");

    [Fact]
    public async Task Scanner_accepts_clean_source_build_log_and_crash_artifacts()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "source.cs"), "const string value = \"[REDACTED]\";");
        await File.WriteAllTextAsync(Path.Combine(_root, "app.log"), "Request failed with AUTH_UNAVAILABLE");
        await File.WriteAllBytesAsync(Path.Combine(_root, "app.dll"), "safe-build-artifact"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(_root, "app.dmp"), "safe-crash-artifact"u8.ToArray());

        var result = await RunScannerAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PASS", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scanner_rejects_secret_in_every_required_artifact_class()
    {
        var jwtSegment = string.Concat("eyJ", new string('a', 24));
        (string FileName, string Content)[] fixtures =
        [
            ("source.cs", string.Concat("providerApiKey = \"", "live-value-must-rotate-", new string('1', 6), "\"")),
            ("release.dll", string.Concat("sk-", new string('a', 32))),
            ("session.log", string.Join('.', jwtSegment, new string('b', 24), new string('c', 24))),
            ("failure.dmp", string.Concat("-----BEGIN ", "PRIVATE KEY-----")),
        ];

        foreach (var (fileName, content) in fixtures)
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(Path.Combine(_root, fileName), content);
            var result = await RunScannerAsync();

            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain(content, result.Output, StringComparison.Ordinal);
            File.Delete(Path.Combine(_root, fileName));
        }
    }

    private async Task<(int ExitCode, string Output)> RunScannerAsync()
    {
        var script = Path.Combine(FindRepositoryRoot(), "scripts", "Invoke-SecretScan.ps1");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-File", script, "-Paths", _root },
        })!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return (process.ExitCode, await outputTask + await errorTask);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
