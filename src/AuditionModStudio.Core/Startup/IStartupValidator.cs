namespace AuditionModStudio.Core.Startup;

/// <summary>
/// Validates the minimum environment required before the application UI starts.
/// </summary>
public interface IStartupValidator
{
    Task ValidateAsync(CancellationToken cancellationToken);
}
