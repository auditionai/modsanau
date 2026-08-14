using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuditionModStudio.Core.Settings;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationTests;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task Missing_file_creates_and_returns_valid_defaults()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            using var service = CreateService(paths);

            var settings = await service.LoadAsync();

            Assert.Equal(ApplicationSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Null(settings.Game.InstallationDirectory);
            Assert.Null(settings.Tooling.AcvExecutablePath);
            Assert.Equal(paths.ProjectsDirectory, settings.Project.DefaultProjectDirectory);
            Assert.True(new SettingsValidator().Validate(settings).IsValid);
            Assert.True(File.Exists(PrimaryPath(paths)));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Save_then_load_preserves_strongly_typed_unicode_data()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            using var service = CreateService(paths);
            var expected = CreateConfiguredSettings(testRoot, 384, ApplicationTheme.Dark);

            await service.SaveAsync(expected);
            var actual = await service.LoadAsync();

            Assert.Equal(expected, actual);
            var json = await File.ReadAllTextAsync(PrimaryPath(paths), Encoding.UTF8);
            Assert.Contains("Dự án thử nghiệm", json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Writer_outputs_readable_utf8_without_bom()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            using var service = CreateService(paths);

            await service.SaveAsync(
                CreateConfiguredSettings(testRoot, 384, ApplicationTheme.Dark));
            var bytes = await File.ReadAllBytesAsync(PrimaryPath(paths));
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var json = utf8.GetString(bytes);

            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            Assert.Contains("Dự án thử nghiệm", json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(bytes);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Loader_accepts_valid_utf8_with_bom()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            var expected = CreateConfiguredSettings(testRoot, 448, ApplicationTheme.Light);
            using (var writerService = CreateService(paths))
            {
                await writerService.SaveAsync(expected);
            }

            var jsonWithoutBom = await File.ReadAllBytesAsync(PrimaryPath(paths));
            var preamble = Encoding.UTF8.GetPreamble();
            var jsonWithBom = new byte[preamble.Length + jsonWithoutBom.Length];
            preamble.CopyTo(jsonWithBom, 0);
            jsonWithoutBom.CopyTo(jsonWithBom, preamble.Length);
            await File.WriteAllBytesAsync(PrimaryPath(paths), jsonWithBom);

            using var loaderService = CreateService(paths);
            var actual = await loaderService.LoadAsync();

            Assert.True(jsonWithBom.AsSpan().StartsWith(preamble));
            Assert.Equal(expected, actual);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Loader_rejects_truly_corrupt_json_and_preserves_evidence()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            Directory.CreateDirectory(paths.SettingsDirectory);
            const string corruptJson = "{ this is not valid JSON";
            await File.WriteAllTextAsync(PrimaryPath(paths), corruptJson, Encoding.UTF8);
            using var service = CreateService(paths);

            var loaded = await service.LoadAsync();

            Assert.Equal(ApplicationSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(256, loaded.Appearance.ThumbnailSize);
            Assert.Equal(corruptJson, await File.ReadAllTextAsync(PrimaryPath(paths), Encoding.UTF8));
            Assert.False(File.Exists(BackupPath(paths)));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Atomic_save_leaves_no_temporary_file()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            using var service = CreateService(paths);

            await service.SaveAsync(CreateConfiguredSettings(testRoot, 256, ApplicationTheme.System));

            Assert.Empty(Directory.EnumerateFiles(paths.SettingsDirectory, "*.tmp"));
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(PrimaryPath(paths)));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Failed_save_preserves_previous_primary()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            using (var workingService = CreateService(paths))
            {
                await workingService.SaveAsync(
                    CreateConfiguredSettings(testRoot, 256, ApplicationTheme.Light));
            }

            var original = await File.ReadAllBytesAsync(PrimaryPath(paths));
            using var failingService = CreateService(paths, new ThrowingAtomicSettingsWriter());

            await Assert.ThrowsAsync<IOException>(
                () => failingService.SaveAsync(
                    CreateConfiguredSettings(testRoot, 512, ApplicationTheme.Dark)));

            Assert.Equal(original, await File.ReadAllBytesAsync(PrimaryPath(paths)));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Save_rotates_one_previous_primary_to_backup()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            using var service = CreateService(paths);
            var previous = CreateConfiguredSettings(testRoot, 256, ApplicationTheme.Light);
            var current = CreateConfiguredSettings(testRoot, 512, ApplicationTheme.Dark);

            await service.SaveAsync(previous);
            await service.SaveAsync(current);

            Assert.True(File.Exists(BackupPath(paths)));
            using var backup = JsonDocument.Parse(await File.ReadAllBytesAsync(BackupPath(paths)));
            Assert.Equal(
                256,
                backup.RootElement.GetProperty("appearance").GetProperty("thumbnailSize").GetInt32());
            Assert.Equal(2, Directory.EnumerateFiles(paths.SettingsDirectory, "settings.json*").Count());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Corrupt_primary_recovers_valid_backup_and_preserves_evidence()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            var recoverable = CreateConfiguredSettings(testRoot, 320, ApplicationTheme.Light);
            using (var service = CreateService(paths))
            {
                await service.SaveAsync(recoverable);
                await service.SaveAsync(CreateConfiguredSettings(testRoot, 640, ApplicationTheme.Dark));
            }

            await File.WriteAllTextAsync(PrimaryPath(paths), "{ truncated", Encoding.UTF8);
            using var recoveryService = CreateService(paths);

            var recovered = await recoveryService.LoadAsync();

            Assert.Equal(recoverable, recovered);
            Assert.True(File.Exists(Path.Combine(
                paths.SettingsDirectory,
                SettingsService.CorruptEvidenceFileName)));
            using var primary = JsonDocument.Parse(await File.ReadAllBytesAsync(PrimaryPath(paths)));
            Assert.Equal(320, primary.RootElement
                .GetProperty("appearance")
                .GetProperty("thumbnailSize")
                .GetInt32());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Corrupt_primary_and_backup_use_defaults_without_destroying_evidence()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            Directory.CreateDirectory(paths.SettingsDirectory);
            const string corruptPrimary = "{ invalid primary";
            const string corruptBackup = "{ invalid backup";
            await File.WriteAllTextAsync(PrimaryPath(paths), corruptPrimary, Encoding.UTF8);
            await File.WriteAllTextAsync(BackupPath(paths), corruptBackup, Encoding.UTF8);
            using var service = CreateService(paths);

            var settings = await service.LoadAsync();

            Assert.Equal(ApplicationSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(corruptPrimary, await File.ReadAllTextAsync(PrimaryPath(paths), Encoding.UTF8));
            Assert.Equal(corruptBackup, await File.ReadAllTextAsync(BackupPath(paths), Encoding.UTF8));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Unsupported_newer_schema_is_not_loaded_or_overwritten()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            Directory.CreateDirectory(paths.SettingsDirectory);
            const string newerSettings = """
                { "schemaVersion": 99, "appearance": { "thumbnailSize": 999 } }
                """;
            await File.WriteAllTextAsync(PrimaryPath(paths), newerSettings, Encoding.UTF8);
            using var service = CreateService(paths);

            var loaded = await service.LoadAsync();

            Assert.Equal(256, loaded.Appearance.ThumbnailSize);
            Assert.Equal(newerSettings, await File.ReadAllTextAsync(PrimaryPath(paths), Encoding.UTF8));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Registered_older_schema_migration_is_applied_and_persisted()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            Directory.CreateDirectory(paths.SettingsDirectory);
            const string legacyJson = """
                { "schemaVersion": 0, "legacyTheme": "dark" }
                """;
            await File.WriteAllTextAsync(PrimaryPath(paths), legacyJson, Encoding.UTF8);
            var logger = new RecordingSettingsLogger();
            using var service = CreateService(
                paths,
                migrations: [new SchemaZeroToOneMigration()],
                logger: logger);

            var loaded = await service.LoadAsync();

            Assert.Empty(logger.Exceptions);
            Assert.Equal(ApplicationSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(ApplicationTheme.Dark, loaded.Appearance.Theme);
            using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(PrimaryPath(paths)));
            Assert.Equal(1, persisted.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(File.Exists(BackupPath(paths)));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Unknown_property_cannot_override_secure_workspace_root()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            Directory.CreateDirectory(paths.SettingsDirectory);
            var maliciousJson = $$"""
                {
                  "schemaVersion": 1,
                  "tempDirectory": "{{JsonEncodedText.Encode(testRoot)}}"
                }
                """;
            await File.WriteAllTextAsync(PrimaryPath(paths), maliciousJson, Encoding.UTF8);
            using var service = CreateService(paths);

            var loaded = await service.LoadAsync();

            Assert.Equal(paths.ProjectsDirectory, loaded.Project.DefaultProjectDirectory);
            Assert.Equal(
                Path.Combine(paths.TempDirectory, "Workspaces"),
                paths.WorkspacesDirectory);
            Assert.Equal(maliciousJson, await File.ReadAllTextAsync(PrimaryPath(paths), Encoding.UTF8));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Concurrent_saves_are_serialized_and_final_json_is_valid()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            using var service = CreateService(paths);
            var candidates = Enumerable.Range(0, 24)
                .Select(index => CreateConfiguredSettings(
                    testRoot,
                    128 + index,
                    index % 2 == 0 ? ApplicationTheme.Light : ApplicationTheme.Dark))
                .ToArray();

            await Task.WhenAll(candidates.Select(settings => service.SaveAsync(settings)));
            var loaded = await service.LoadAsync();

            Assert.Contains(loaded, candidates);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(PrimaryPath(paths)));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Pre_cancelled_operations_do_not_create_settings_file()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(testRoot);
            using var service = CreateService(paths);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.LoadAsync(cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.SaveAsync(new ApplicationSettings(), cancellation.Token));

            Assert.False(File.Exists(PrimaryPath(paths)));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Settings_file_is_confined_to_managed_settings_directory()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var paths = new AppPaths(Path.Combine(testRoot, "Local App Data"));
            using var service = CreateService(paths);

            await service.LoadAsync();

            Assert.Equal(
                Path.Combine(paths.SettingsDirectory, SettingsService.PrimaryFileName),
                PrimaryPath(paths));
            Assert.False(Path.GetFullPath(PrimaryPath(paths)).StartsWith(
                Path.GetFullPath(AppContext.BaseDirectory),
                StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public void Settings_model_exposes_no_secret_or_credential_properties()
    {
        var forbiddenTerms = new[]
        {
            "password", "token", "secret", "apikey", "encryptionkey", "signingkey", "credential",
        };
        var propertyNames = typeof(ApplicationSettings).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(ApplicationSettings).Namespace)
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant())
            .ToArray();

        Assert.DoesNotContain(
            propertyNames,
            propertyName => forbiddenTerms.Any(propertyName.Contains));
    }

    private static SettingsService CreateService(
        AppPaths paths,
        IAtomicSettingsWriter? writer = null,
        IEnumerable<ISettingsSchemaMigration>? migrations = null,
        ILogger<SettingsService>? logger = null)
    {
        var pathSecurity = new PathSecurity();
        return new SettingsService(
            paths,
            pathSecurity,
            new SettingsValidator(),
            writer ?? new AtomicSettingsWriter(paths, pathSecurity),
            migrations ?? Array.Empty<ISettingsSchemaMigration>(),
            logger ?? NullLogger<SettingsService>.Instance);
    }

    private static ApplicationSettings CreateConfiguredSettings(
        string testRoot,
        int thumbnailSize,
        ApplicationTheme theme)
    {
        return new ApplicationSettings
        {
            Game = new GameSettings
            {
                InstallationDirectory = Path.Combine(testRoot, "Audition Việt Nam"),
            },
            Tooling = new ToolingSettings
            {
                AcvExecutablePath = Path.Combine(testRoot, "Công cụ", "acv.exe"),
            },
            Project = new ProjectSettings
            {
                DefaultProjectDirectory = Path.Combine(testRoot, "Dự án thử nghiệm"),
                AutoBackupEnabled = true,
                DefaultInstallBehavior = DefaultInstallBehavior.CreateBackupAndInstall,
            },
            Appearance = new AppearanceSettings
            {
                Language = ApplicationLanguage.Vietnamese,
                Theme = theme,
                ThumbnailSize = thumbnailSize,
            },
            Backend = new BackendSettings
            {
                ApiBaseUrl = "https://api.example.test/",
            },
        };
    }

    private static string PrimaryPath(AppPaths paths)
    {
        return Path.Combine(paths.SettingsDirectory, SettingsService.PrimaryFileName);
    }

    private static string BackupPath(AppPaths paths)
    {
        return Path.Combine(paths.SettingsDirectory, SettingsService.BackupFileName);
    }

    private static string CreateTestRoot()
    {
        return Path.Combine(Path.GetTempPath(), "AuditionModStudio.Tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string testRoot)
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private sealed class ThrowingAtomicSettingsWriter : IAtomicSettingsWriter
    {
        public Task WriteAsync(
            string destinationFileName,
            string? backupFileName,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            throw new IOException("Simulated atomic settings write failure.");
        }
    }

    private sealed class SchemaZeroToOneMigration : ISettingsSchemaMigration
    {
        public int SourceVersion => 0;

        public int TargetVersion => 1;

        public JsonObject Migrate(JsonObject source)
        {
            var migrated = (JsonObject)source.DeepClone();
            migrated.Remove("legacyTheme");
            migrated["schemaVersion"] = TargetVersion;
            migrated["appearance"] = new JsonObject
            {
                ["language"] = "vietnamese",
                ["theme"] = "dark",
                ["thumbnailSize"] = 256,
            };
            return migrated;
        }
    }

    private sealed class RecordingSettingsLogger : ILogger<SettingsService>
    {
        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
    }
}
