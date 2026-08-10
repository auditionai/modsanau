namespace AuditionModStudio.Archives;

/// <summary>
/// Authorizes the exact executable before launch. PLAN 07 can replace this boundary
/// with hash/version integrity verification without changing the runner.
/// </summary>
public interface IArchiveToolExecutionPolicy
{
    ValueTask EnsureApprovedAsync(string executablePath, CancellationToken cancellationToken);
}
