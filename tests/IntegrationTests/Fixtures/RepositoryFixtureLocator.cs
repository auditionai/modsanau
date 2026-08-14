using AuditionModStudio.Core.Paths;

namespace IntegrationTests.Fixtures;

internal sealed class RepositoryFixtureLocator
{
    private readonly IPathSecurity _pathSecurity;

    public RepositoryFixtureLocator(IPathSecurity pathSecurity)
    {
        _pathSecurity = pathSecurity ?? throw new ArgumentNullException(nameof(pathSecurity));
    }

    public string FindRepositoryRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AuditionModStudio.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("The AuditionModStudio repository root was not found.");
    }

    public string ResolveSourcePath(
        string repositoryRoot,
        RealSampleFixtureRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var sourcePath = _pathSecurity.ResolvePathWithinRoot(
            repositoryRoot,
            registration.RepositoryRelativePath);
        _pathSecurity.EnsureNoReparsePoints(repositoryRoot, sourcePath);
        return sourcePath;
    }
}
