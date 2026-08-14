namespace AuditionModStudio.Core.Paths;

/// <summary>
/// Provides the centralized locations used for normal application data.
/// </summary>
public interface IAppPaths
{
    string RootDirectory { get; }

    string LogsDirectory { get; }

    string CacheDirectory { get; }

    string ProjectsDirectory { get; }

    string TempDirectory { get; }

    string SettingsDirectory { get; }

    string DownloadsDirectory { get; }

    string SecureTemplateCacheDirectory { get; }

    string BackupsDirectory { get; }

    string WorkspacesDirectory { get; }

    IReadOnlyCollection<string> ManagedDirectories { get; }

    void EnsureDirectoriesExist();
}
