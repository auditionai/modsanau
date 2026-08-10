using AuditionModStudio.Core.Paths;

namespace AuditionModStudio.Infrastructure.Paths;

/// <summary>
/// Resolves application data below the current user's LocalApplicationData folder.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string ApplicationDirectoryName = "AuditionModStudio";

    public AppPaths()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    public AppPaths(string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);

        var localRoot = Path.GetFullPath(localApplicationDataDirectory);
        RootDirectory = Path.Combine(localRoot, ApplicationDirectoryName);
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        CacheDirectory = Path.Combine(RootDirectory, "Cache");
        ProjectsDirectory = Path.Combine(RootDirectory, "Projects");
        TempDirectory = Path.Combine(RootDirectory, "Temp");
        SettingsDirectory = Path.Combine(RootDirectory, "Settings");
        DownloadsDirectory = Path.Combine(RootDirectory, "Downloads");
        SecureTemplateCacheDirectory = Path.Combine(RootDirectory, "SecureTemplateCache");
        BackupsDirectory = Path.Combine(RootDirectory, "Backups");
        WorkspacesDirectory = Path.Combine(TempDirectory, "Workspaces");

        ManagedDirectories = Array.AsReadOnly(
        [
            RootDirectory,
            LogsDirectory,
            CacheDirectory,
            ProjectsDirectory,
            TempDirectory,
            SettingsDirectory,
            DownloadsDirectory,
            SecureTemplateCacheDirectory,
            BackupsDirectory,
            WorkspacesDirectory,
        ]);
    }

    public string RootDirectory { get; }

    public string LogsDirectory { get; }

    public string CacheDirectory { get; }

    public string ProjectsDirectory { get; }

    public string TempDirectory { get; }

    public string SettingsDirectory { get; }

    public string DownloadsDirectory { get; }

    public string SecureTemplateCacheDirectory { get; }

    public string BackupsDirectory { get; }

    public string WorkspacesDirectory { get; }

    public IReadOnlyCollection<string> ManagedDirectories { get; }

    public void EnsureDirectoriesExist()
    {
        foreach (var directory in ManagedDirectories)
        {
            Directory.CreateDirectory(directory);
        }
    }
}
