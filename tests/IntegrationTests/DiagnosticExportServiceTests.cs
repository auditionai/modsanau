using System.IO.Compression;
using System.Text.Json;
using AuditionModStudio.Core.Diagnostics;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Infrastructure.Exports;
using AuditionModStudio.Infrastructure.Logging;
using AuditionModStudio.Infrastructure.Paths;

namespace IntegrationTests;

public sealed class DiagnosticExportServiceTests
{
    [Fact]
    public async Task Default_export_allowlists_redacted_logs_and_excludes_all_user_content_roots()
    {
        var root = TestRoot();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "local"));
            paths.EnsureDirectoriesExist();
            var secrets = new[]
            {
                "password: not-for-support", "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3OCJ9.c2lnbmF0dXJlMTIzNDU2",
                "https://storage.example.invalid/template.ab?sig=do-not-export", "provider_secret=provider-private",
                "payment_webhook_secret=payment-private",
            };
            await File.WriteAllTextAsync(Path.Combine(paths.LogsDirectory,
                "audition-mod-studio-20260813.log"), string.Join(Environment.NewLine, secrets));
            await SeedExcludedContentAsync(paths);
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            var service = CreateService(paths);

            var result = await service.ExportAsync(new(output, "diagnostics.zip"));

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.ExportedLogCount);
            Assert.True(result.BundleSize > 0);
            Assert.DoesNotContain(root, result.ToString(), StringComparison.OrdinalIgnoreCase);
            using var archive = ZipFile.OpenRead(result.OutputPath!);
            Assert.Equal(["diagnostic-manifest.json", "logs/log-01.log"],
                archive.Entries.Select(entry => entry.FullName).Order().ToArray());
            var manifest = await ReadEntryAsync(archive, "diagnostic-manifest.json");
            using var document = JsonDocument.Parse(manifest);
            Assert.False(document.RootElement.GetProperty("userContentIncluded").GetBoolean());
            var excluded = document.RootElement.GetProperty("excludedCategories")
                .EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("projects", excluded);
            Assert.Contains("images", excluded);
            Assert.Contains("templates", excluded);
            Assert.Contains("secure-template-cache", excluded);
            var log = await ReadEntryAsync(archive, "logs/log-01.log");
            Assert.Contains("[REDACTED]", log, StringComparison.Ordinal);
            Assert.Contains("[REDACTED_SIGNED_URL]", log, StringComparison.Ordinal);
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, log, StringComparison.Ordinal));
            Assert.DoesNotContain("user-image-bytes", log, StringComparison.Ordinal);
            Assert.DoesNotContain("template-bytes", log, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task User_content_opt_in_is_not_implemented_and_existing_bundle_is_never_overwritten()
    {
        var root = TestRoot();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "local"));
            paths.EnsureDirectoriesExist();
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            var destination = Path.Combine(output, "diagnostics.zip");
            await File.WriteAllTextAsync(destination, "existing-evidence");
            var service = CreateService(paths);

            var optedIn = await service.ExportAsync(new(output, "other.zip", IncludeUserContent: true));
            var collision = await service.ExportAsync(new(output, "diagnostics.zip"));

            Assert.Equal(DiagnosticExportFailureReason.UserContentNotAllowed, optedIn.FailureReason);
            Assert.Equal("DIAGNOSTIC_EXPORT_USER_CONTENT_NOT_ALLOWED", optedIn.DiagnosticCode);
            Assert.Equal(DiagnosticExportFailureReason.DestinationInvalid, collision.FailureReason);
            Assert.Equal("existing-evidence", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.GetFiles(output, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../diagnostics.zip")]
    [InlineData("diagnostics.txt")]
    [InlineData("CON.zip")]
    public async Task Unsafe_destination_names_are_rejected_without_artifact(string fileName)
    {
        var root = TestRoot();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "local"));
            paths.EnsureDirectoriesExist();
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);

            var result = await CreateService(paths).ExportAsync(new(output, fileName));

            Assert.False(result.Succeeded);
            Assert.Equal(DiagnosticExportFailureReason.DestinationInvalid, result.FailureReason);
            Assert.Empty(Directory.GetFiles(output));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Pre_cancelled_export_is_cancellable_and_leaves_no_partial_artifact()
    {
        var root = TestRoot();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "local"));
            paths.EnsureDirectoriesExist();
            await File.WriteAllTextAsync(Path.Combine(paths.LogsDirectory,
                "audition-mod-studio-20260813.log"), "safe log");
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await CreateService(paths).ExportAsync(
                new(output, "diagnostics.zip"), cancellationToken: cancellation.Token);

            Assert.True(result.Cancelled);
            Assert.Empty(Directory.GetFiles(output));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static DiagnosticExportService CreateService(AppPaths paths)
    {
        var pathSecurity = new PathSecurity();
        return new(paths, pathSecurity,
            new ArchiveExportDestinationValidator(pathSecurity, new SystemExportDestinationFileSystem()),
            new SensitiveDataRedactor());
    }

    private static async Task SeedExcludedContentAsync(AppPaths paths)
    {
        await File.WriteAllTextAsync(Path.Combine(paths.ProjectsDirectory, "private.audproj"), "user-project");
        await File.WriteAllTextAsync(Path.Combine(paths.CacheDirectory, "user-image.png"), "user-image-bytes");
        await File.WriteAllTextAsync(Path.Combine(paths.SecureTemplateCacheDirectory, "premium.ab"), "template-bytes");
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspacesDirectory, "working-image.png"), "user-image-bytes");
        await File.WriteAllTextAsync(Path.Combine(paths.SettingsDirectory, "settings.json"), "private-settings");
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Missing entry {name}.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static string TestRoot() => Path.Combine(Path.GetTempPath(),
        "AuditionModStudio.Tests", Guid.NewGuid().ToString("N"));
}
