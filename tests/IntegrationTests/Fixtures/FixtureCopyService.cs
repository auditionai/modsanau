using System.Security.Cryptography;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Workspaces;

namespace IntegrationTests.Fixtures;

internal sealed class FixtureCopyService
{
    private readonly RepositoryFixtureLocator _locator;
    private readonly IPathSecurity _pathSecurity;

    public FixtureCopyService(
        IPathSecurity pathSecurity,
        RepositoryFixtureLocator locator)
    {
        _pathSecurity = pathSecurity ?? throw new ArgumentNullException(nameof(pathSecurity));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public async Task<FixtureCopyResult> CopyToWorkspaceAsync(
        RealSampleFixtureRegistration registration,
        string repositoryRoot,
        ISecureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();

        var sourcePath = _locator.ResolveSourcePath(repositoryRoot, registration);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Registered fixture is unavailable: {registration.Id}",
                sourcePath);
        }

        var destinationPath = _pathSecurity.ResolvePathWithinRoot(
            workspace.Paths.WorkingDirectory,
            registration.FileName);
        _pathSecurity.EnsureNoReparsePoints(
            workspace.Paths.WorkingDirectory,
            destinationPath);

        string sourceHash;
        await using (var source = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            sourceHash = Convert.ToHexString(
                await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
            source.Position = 0;

            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }

        string copyHash;
        await using (var copy = new FileStream(
                         destinationPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            copyHash = Convert.ToHexString(
                await SHA256.HashDataAsync(copy, cancellationToken).ConfigureAwait(false));
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(sourceHash),
                Convert.FromHexString(copyHash)))
        {
            File.Delete(destinationPath);
            throw new IOException("The fixture working copy failed integrity validation.");
        }

        return new FixtureCopyResult(
            registration,
            sourcePath,
            destinationPath,
            sourceHash);
    }
}
