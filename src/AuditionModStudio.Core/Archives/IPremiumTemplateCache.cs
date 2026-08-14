namespace AuditionModStudio.Core.Archives;

public enum PremiumTemplateCacheStatus
{
    Succeeded,
    Missing,
    InvalidRequest,
    Corrupt,
    KeyUnavailable,
    IoFailure,
    Cancelled,
}

public sealed record PremiumTemplateCacheResult(
    PremiumTemplateCacheStatus Status,
    string DiagnosticCode,
    string? MaterializedPath = null)
{
    public bool Succeeded => Status == PremiumTemplateCacheStatus.Succeeded;
}

public interface IPremiumTemplateCache
{
    Task<PremiumTemplateCacheResult> StoreAsync(PremiumTemplatePackageManifest manifest, Stream package,
        CancellationToken cancellationToken = default);

    Task<PremiumTemplateCacheResult> MaterializeAsync(PremiumTemplatePackageManifest manifest,
        string controlledWorkspaceDirectory, CancellationToken cancellationToken = default);

    Task<PremiumTemplateCacheResult> DeleteAsync(PremiumTemplatePackageManifest manifest,
        CancellationToken cancellationToken = default);
}
