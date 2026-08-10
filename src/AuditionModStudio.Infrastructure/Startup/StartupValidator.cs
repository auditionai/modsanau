using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Startup;
using Microsoft.Extensions.Logging;

namespace AuditionModStudio.Infrastructure.Startup;

public sealed class StartupValidator : IStartupValidator
{
    private readonly string _installationDirectory;
    private readonly IPathSecurity _pathSecurity;
    private readonly IAppPaths _paths;
    private readonly ILogger<StartupValidator> _logger;

    public StartupValidator(
        IAppPaths paths,
        IPathSecurity pathSecurity,
        ILogger<StartupValidator> logger)
        : this(paths, pathSecurity, logger, AppContext.BaseDirectory)
    {
    }

    public StartupValidator(
        IAppPaths paths,
        IPathSecurity pathSecurity,
        ILogger<StartupValidator> logger,
        string installationDirectory)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _pathSecurity = pathSecurity ?? throw new ArgumentNullException(nameof(pathSecurity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        _installationDirectory = Path.GetFullPath(installationDirectory);
    }

    public Task ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _paths.EnsureDirectoriesExist();

        foreach (var directory in _paths.ManagedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException(
                    $"Required application directory was not created: {directory}");
            }

            _pathSecurity.EnsureNoReparsePoints(_paths.RootDirectory, directory);

            if (IsSameAsOrBelow(directory, _installationDirectory))
            {
                throw new InvalidOperationException(
                    "Application data directories must not be located in the installation directory.");
            }
        }

        _logger.LogInformation(
            "Startup path validation completed for {ManagedDirectoryCount} directories",
            _paths.ManagedDirectories.Count);

        return Task.CompletedTask;
    }

    private static bool IsSameAsOrBelow(string candidatePath, string parentPath)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        var relative = Path.GetRelativePath(parent, candidate);
        return relative.Equals(".", StringComparison.Ordinal)
            || (!Path.IsPathRooted(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
