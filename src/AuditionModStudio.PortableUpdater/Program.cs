using System.Diagnostics;
using System.Reflection;
using AuditionModStudio.Updater;

namespace AuditionModStudio.PortableUpdater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!TryParse(args, out var request)) return 2;
            var stagingRoot = Path.GetFullPath(request!.StagingDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var runningRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!runningRoot.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase)) return 3;

            using var keyStream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("TrustedUpdatePublicKey.pem");
            if (keyStream is null) return 4;
            using var reader = new StreamReader(keyStream);
            var publicKey = await reader.ReadToEndAsync().ConfigureAwait(false);
            using var verifier = new EcdsaUpdateManifestVerifier(publicKey);
            var engine = new PortableUpdateInstallEngine(verifier);
            var result = await engine.InstallAsync(request, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            await WriteStatusAsync(stagingRoot, result.DiagnosticCode).ConfigureAwait(false);
            if (!result.Succeeded || result.RestartExecutablePath is null) return result.RolledBack ? 11 : 10;

            var startInfo = new ProcessStartInfo
            {
                FileName = result.RestartExecutablePath,
                WorkingDirectory = request.InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            SanitizeEnvironment(startInfo.Environment);
            using var process = Process.Start(startInfo);
            if (process is null) return 12;
            await WriteStatusAsync(stagingRoot, "PORTABLE_UPDATE_RESTARTED").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException)
        {
            return 20;
        }
    }

    private static bool TryParse(string[] args, out PortableInstallRequest? request)
    {
        request = null;
        if (args.Length != 10) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (args[index] is not ("--parent-pid" or "--staging-dir" or "--install-dir"
                or "--expected-version" or "--restart-exe") || !values.TryAdd(args[index], args[index + 1]))
                return false;
        }
        if (!int.TryParse(values["--parent-pid"], out var parentProcessId) || parentProcessId <= 0
            || !Version.TryParse(values["--expected-version"], out var version)
            || version.Build < 0
            || values["--restart-exe"] != PortableUpdateProduct.PrimaryExecutable
            || !Path.IsPathFullyQualified(values["--staging-dir"])
            || !Path.IsPathFullyQualified(values["--install-dir"])) return false;
        request = new(parentProcessId, Path.GetFullPath(values["--install-dir"]),
            Path.GetFullPath(values["--staging-dir"]), version, values["--restart-exe"]);
        return true;
    }

    private static async Task WriteStatusAsync(string stagingRoot, string code)
    {
        var statusPath = Path.Combine(stagingRoot, "install-status.txt");
        await File.WriteAllTextAsync(statusPath, code).ConfigureAwait(false);
    }

    private static void SanitizeEnvironment(IDictionary<string, string?> environment)
    {
        var allowed = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "USERPROFILE" };
        var values = allowed.Select(name => (name, Environment.GetEnvironmentVariable(name)))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Item2)).ToArray();
        environment.Clear();
        foreach (var (name, value) in values) environment[name] = value;
    }
}
