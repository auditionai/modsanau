namespace AuditionModStudio.Core.Settings;

/// <summary>
/// Contains non-secret local configuration only. Credentials, tokens, API keys,
/// encryption keys, and signing keys must use the dedicated secure-storage boundary.
/// </summary>
public sealed record ApplicationSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public GameSettings Game { get; init; } = new();

    public ToolingSettings Tooling { get; init; } = new();

    public ProjectSettings Project { get; init; } = new();

    public AppearanceSettings Appearance { get; init; } = new();

    public BackendSettings Backend { get; init; } = new();
}

public sealed record GameSettings
{
    public string? InstallationDirectory { get; init; }
}

public sealed record ToolingSettings
{
    public string? AcvExecutablePath { get; init; }
}

public sealed record ProjectSettings
{
    public string? DefaultProjectDirectory { get; init; }

    public bool AutoBackupEnabled { get; init; } = true;

    public DefaultInstallBehavior DefaultInstallBehavior { get; init; } =
        DefaultInstallBehavior.Prompt;
}

public sealed record AppearanceSettings
{
    public ApplicationLanguage Language { get; init; } = ApplicationLanguage.Vietnamese;

    public ApplicationTheme Theme { get; init; } = ApplicationTheme.System;

    public int ThumbnailSize { get; init; } = 256;
}

public sealed record BackendSettings
{
    public string? ApiBaseUrl { get; init; }
}
