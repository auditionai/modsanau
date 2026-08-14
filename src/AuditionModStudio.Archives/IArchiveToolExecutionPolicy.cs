namespace AuditionModStudio.Archives;

/// <summary>
/// Authorizes the exact executable immediately before launch.
/// </summary>
public interface IArchiveToolExecutionPolicy
{
    Task EnsureApprovedAsync(
        string toolId,
        string executablePath,
        string trustedRootDirectory,
        CancellationToken cancellationToken);
}
