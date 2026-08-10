using System.Text.Json;
using System.Text.Json.Nodes;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AuditionModStudio.Infrastructure.Settings;

public sealed class SettingsService : ISettingsService, IDisposable
{
    public const string PrimaryFileName = "settings.json";
    public const string BackupFileName = "settings.json.bak";
    public const string CorruptEvidenceFileName = "settings.json.corrupt";

    private readonly IAppPaths _appPaths;
    private readonly IAtomicSettingsWriter _atomicWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<SettingsService> _logger;
    private readonly IReadOnlyList<ISettingsSchemaMigration> _migrations;
    private readonly IPathSecurity _pathSecurity;
    private readonly ISettingsValidator _validator;
    private int _disposeState;

    public SettingsService(
        IAppPaths appPaths,
        IPathSecurity pathSecurity,
        ISettingsValidator validator,
        IAtomicSettingsWriter atomicWriter,
        IEnumerable<ISettingsSchemaMigration> migrations,
        ILogger<SettingsService> logger)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _pathSecurity = pathSecurity ?? throw new ArgumentNullException(nameof(pathSecurity));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _atomicWriter = atomicWriter ?? throw new ArgumentNullException(nameof(atomicWriter));
        ArgumentNullException.ThrowIfNull(migrations);
        _migrations = migrations.ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureSettingsDirectory();
            var primaryPath = ResolveFile(PrimaryFileName);
            var backupPath = ResolveFile(BackupFileName);
            var primaryExists = File.Exists(primaryPath);

            if (primaryExists)
            {
                var primary = await TryLoadFileAsync(
                    primaryPath,
                    "primary",
                    cancellationToken).ConfigureAwait(false);
                if (primary is not null)
                {
                    if (primary.WasMigrated)
                    {
                        await SaveCoreAsync(
                            primary.Settings,
                            BackupFileName,
                            cancellationToken).ConfigureAwait(false);
                    }

                    _logger.LogInformation(
                        "Settings loaded with schema version {SchemaVersion} from {SettingsSource}",
                        primary.Settings.SchemaVersion,
                        "primary");
                    return primary.Settings;
                }
            }
            else
            {
                _logger.LogInformation("Settings primary file is missing");
            }

            if (File.Exists(backupPath))
            {
                var backup = await TryLoadFileAsync(
                    backupPath,
                    "backup",
                    cancellationToken).ConfigureAwait(false);
                if (backup is not null)
                {
                    if (primaryExists)
                    {
                        PreserveCorruptPrimary(primaryPath);
                    }

                    await SaveCoreAsync(
                        backup.Settings,
                        backupFileName: null,
                        cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning(
                        "Settings recovered from backup with schema version {SchemaVersion}",
                        backup.Settings.SchemaVersion);
                    return backup.Settings;
                }
            }

            var defaults = CreateDefaults();
            var defaultValidation = _validator.Validate(defaults);
            if (!defaultValidation.IsValid)
            {
                throw new SettingsValidationException(defaultValidation);
            }

            if (!primaryExists)
            {
                await SaveCoreAsync(
                    defaults,
                    backupFileName: null,
                    cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning(
                "Default settings are being used with schema version {SchemaVersion}",
                defaults.SchemaVersion);
            return defaults;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureSettingsDirectory();
            await SaveCoreAsync(settings, BackupFileName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _gate.Dispose();
        }
    }

    private ApplicationSettings CreateDefaults()
    {
        return new ApplicationSettings
        {
            Project = new ProjectSettings
            {
                DefaultProjectDirectory = _appPaths.ProjectsDirectory,
            },
        };
    }

    private async Task SaveCoreAsync(
        ApplicationSettings settings,
        string? backupFileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = _validator.Validate(settings);
        LogValidationIssues(validation);
        if (!validation.IsValid)
        {
            throw new SettingsValidationException(validation);
        }

        var content = SettingsJson.Serialize(settings);
        _ = DeserializeAndValidate(content, out _);

        try
        {
            await _atomicWriter.WriteAsync(
                PrimaryFileName,
                backupFileName,
                content,
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Settings save completed with schema version {SchemaVersion}",
                settings.SchemaVersion);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Settings save failed for schema version {SchemaVersion}",
                settings.SchemaVersion);
            throw;
        }
    }

    private async Task<SettingsLoadCandidate?> TryLoadFileAsync(
        string path,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            _pathSecurity.EnsureNoReparsePoints(_appPaths.SettingsDirectory, path);
            var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var settings = DeserializeAndValidate(content, out var wasMigrated);
            return new SettingsLoadCandidate(settings, wasMigrated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
                                          or NotSupportedException
                                          or UnsupportedSettingsSchemaException
                                          or SettingsValidationException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Settings load rejected from {SettingsSource}",
                source);
            return null;
        }
    }

    private ApplicationSettings DeserializeAndValidate(
        ReadOnlyMemory<byte> content,
        out bool wasMigrated)
    {
        var json = content.Span;
        if (json.Length >= 3
            && json[0] == 0xEF
            && json[1] == 0xBB
            && json[2] == 0xBF)
        {
            json = json[3..];
        }

        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("The settings JSON root must be an object.");
        var schemaVersion = ReadSchemaVersion(root);
        wasMigrated = false;

        if (schemaVersion > ApplicationSettings.CurrentSchemaVersion)
        {
            _logger.LogWarning(
                "A newer settings schema version {SchemaVersion} was rejected",
                schemaVersion);
            throw new UnsupportedSettingsSchemaException(schemaVersion);
        }

        while (schemaVersion < ApplicationSettings.CurrentSchemaVersion)
        {
            var migration = _migrations.SingleOrDefault(item => item.SourceVersion == schemaVersion)
                ?? throw new UnsupportedSettingsSchemaException(schemaVersion);
            if (migration.TargetVersion <= schemaVersion
                || migration.TargetVersion > ApplicationSettings.CurrentSchemaVersion)
            {
                throw new InvalidOperationException("The settings migration chain is invalid.");
            }

            root = migration.Migrate(root)
                ?? throw new InvalidOperationException("A settings migration returned no document.");
            var migratedVersion = ReadSchemaVersion(root);
            if (migratedVersion != migration.TargetVersion)
            {
                throw new InvalidOperationException("A settings migration produced an unexpected version.");
            }

            _logger.LogInformation(
                "Settings schema migrated from {SourceVersion} to {TargetVersion}",
                schemaVersion,
                migratedVersion);
            schemaVersion = migratedVersion;
            wasMigrated = true;
        }

        var settings = root.Deserialize<ApplicationSettings>(SettingsJson.Options)
            ?? throw new JsonException("The settings document could not be deserialized.");
        var validation = _validator.Validate(settings);
        LogValidationIssues(validation);
        if (!validation.IsValid)
        {
            throw new SettingsValidationException(validation);
        }

        return settings;
    }

    private static int ReadSchemaVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue("schemaVersion", out var versionNode)
            || versionNode is not JsonValue versionValue
            || !versionValue.TryGetValue<int>(out var schemaVersion))
        {
            throw new UnsupportedSettingsSchemaException(schemaVersion: null);
        }

        return schemaVersion;
    }

    private void LogValidationIssues(SettingsValidationResult validation)
    {
        foreach (var issue in validation.Issues)
        {
            if (issue.Severity == SettingsValidationSeverity.Error)
            {
                _logger.LogError(
                    "Settings validation error {ValidationCode} at {SettingsPath}",
                    issue.Code,
                    issue.Path);
            }
            else
            {
                _logger.LogWarning(
                    "Settings validation warning {ValidationCode} at {SettingsPath}",
                    issue.Code,
                    issue.Path);
            }
        }
    }

    private void EnsureSettingsDirectory()
    {
        _pathSecurity.EnsureNoReparsePoints(
            _appPaths.RootDirectory,
            _appPaths.SettingsDirectory);
        Directory.CreateDirectory(_appPaths.SettingsDirectory);
        _pathSecurity.EnsureNoReparsePoints(
            _appPaths.RootDirectory,
            _appPaths.SettingsDirectory);
    }

    private string ResolveFile(string fileName)
    {
        return _pathSecurity.ResolvePathWithinRoot(_appPaths.SettingsDirectory, fileName);
    }

    private void PreserveCorruptPrimary(string primaryPath)
    {
        try
        {
            var evidencePath = ResolveFile(CorruptEvidenceFileName);
            _pathSecurity.EnsureNoReparsePoints(_appPaths.SettingsDirectory, evidencePath);
            File.Copy(primaryPath, evidencePath, overwrite: true);
            _logger.LogWarning("Corrupt primary settings evidence was preserved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Corrupt primary settings evidence could not be preserved");
        }
    }

    private sealed record SettingsLoadCandidate(
        ApplicationSettings Settings,
        bool WasMigrated);
}
