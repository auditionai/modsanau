using AuditionModStudio.Core.Paths;

namespace AuditionModStudio.Infrastructure.Settings;

public sealed class AtomicSettingsWriter : IAtomicSettingsWriter
{
    private readonly IAppPaths _appPaths;
    private readonly IPathSecurity _pathSecurity;

    public AtomicSettingsWriter(IAppPaths appPaths, IPathSecurity pathSecurity)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _pathSecurity = pathSecurity ?? throw new ArgumentNullException(nameof(pathSecurity));
    }

    public async Task WriteAsync(
        string destinationFileName,
        string? backupFileName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ValidateFileName(destinationFileName, nameof(destinationFileName));
        if (backupFileName is not null)
        {
            ValidateFileName(backupFileName, nameof(backupFileName));
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureSettingsDirectory();

        var destinationPath = ResolveFile(destinationFileName);
        var backupPath = backupFileName is null ? null : ResolveFile(backupFileName);
        var temporaryFileName = $".{destinationFileName}.{Guid.NewGuid():N}.tmp";
        var temporaryPath = ResolveFile(temporaryFileName);

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _pathSecurity.EnsureNoReparsePoints(_appPaths.SettingsDirectory, temporaryPath);
            _pathSecurity.EnsureNoReparsePoints(_appPaths.SettingsDirectory, destinationPath);
            if (backupPath is not null)
            {
                _pathSecurity.EnsureNoReparsePoints(_appPaths.SettingsDirectory, backupPath);
            }

            if (!File.Exists(destinationPath))
            {
                File.Move(temporaryPath, destinationPath);
            }
            else if (backupPath is not null)
            {
                File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                _pathSecurity.EnsureNoReparsePoints(_appPaths.SettingsDirectory, temporaryPath);
                File.Delete(temporaryPath);
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

    private static void ValidateFileName(string fileName, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName, parameterName);
        if (!fileName.Equals(Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new ArgumentException("A direct settings filename was required.", parameterName);
        }
    }
}
