namespace AuditionModStudio.Archives;

/// <summary>
/// Minimal PLAN 06 allowlist. This is path authorization, not an integrity guarantee.
/// </summary>
public sealed class ExactPathArchiveToolExecutionPolicy : IArchiveToolExecutionPolicy
{
    private readonly string _approvedExecutablePath;

    public ExactPathArchiveToolExecutionPolicy(string approvedExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedExecutablePath);
        if (!Path.IsPathFullyQualified(approvedExecutablePath))
        {
            throw new ArgumentException("The approved executable path must be absolute.", nameof(approvedExecutablePath));
        }

        _approvedExecutablePath = Path.GetFullPath(approvedExecutablePath);
    }

    public ValueTask EnsureApprovedAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = Path.GetFullPath(executablePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.Equals(_approvedExecutablePath, comparison))
        {
            throw new UnauthorizedAccessException("The archive tool executable is not approved for this operation.");
        }

        return ValueTask.CompletedTask;
    }
}
