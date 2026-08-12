using System.Diagnostics;

namespace AuditionModStudio.Security.Tests;

public sealed class AppCodeSigningPolicyTests
{
    [Fact]
    public async Task Unsigned_artifact_is_rejected_by_release_verifier()
    {
        var repository = FindRepositoryRoot();
        var root = Directory.CreateTempSubdirectory("audition-signing-");
        try
        {
            var artifact = Path.Combine(root.FullName, "AuditionModStudio.App.exe");
            await File.WriteAllBytesAsync(artifact, [0x4d, 0x5a, 0, 0]);
            var result = await RunPowerShellAsync(repository,
                $"& './scripts/Invoke-AppCodeSigning.ps1' -Mode Verify -ArtifactRoot '{Escape(root.FullName)}' " +
                $"-Artifacts '{Escape(artifact)}' -CertificateThumbprint '{new string('A', 40)}' " +
                "-ExpectedPublisherSubject 'CN=Test Only'");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("AUTHENTICODE_INVALID", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task Archive_output_is_never_an_allowed_code_signing_artifact()
    {
        var repository = FindRepositoryRoot();
        var root = Directory.CreateTempSubdirectory("audition-signing-");
        try
        {
            var artifact = Path.Combine(root.FullName, "015.ab");
            await File.WriteAllBytesAsync(artifact, [1, 2, 3]);
            var result = await RunPowerShellAsync(repository,
                $"& './scripts/Invoke-AppCodeSigning.ps1' -Mode Verify -ArtifactRoot '{Escape(root.FullName)}' " +
                $"-Artifacts '{Escape(artifact)}' -CertificateThumbprint '{new string('A', 40)}' " +
                "-ExpectedPublisherSubject 'CN=Test Only'");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("SIGNING_ARTIFACT_TYPE_NOT_ALLOWED", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Protected_workflow_is_manual_master_only_and_does_not_import_private_key_files()
    {
        var repository = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repository, ".github", "workflows", "release-signing.yml"));

        Assert.Contains("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.Contains("github.ref == 'refs/heads/master'", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: production-signing", workflow, StringComparison.Ordinal);
        Assert.Contains("AUDITION_SIGNING_CERTIFICATE_SHA1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain(".pfx", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".p12", workflow, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AuditionModStudio.sln")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new DirectoryNotFoundException("Không tìm thấy repository root.");
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(string repository, string command)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = repository,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(command);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}
