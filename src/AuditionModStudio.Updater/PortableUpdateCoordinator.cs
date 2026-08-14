using System.Collections.Immutable;
using System.Diagnostics;

namespace AuditionModStudio.Updater;

public enum PortableUpdateAvailability
{
    Unavailable,
    Current,
    Available,
    Failed
}

public sealed record PortableUpdateCheckResult(
    PortableUpdateAvailability Availability,
    string DiagnosticCode,
    Version CurrentVersion,
    PortableUpdateManifest? Manifest,
    DateTimeOffset AttemptedAt);

public interface IPortableUpdateCoordinator
{
    bool IsAvailable { get; }
    DateTimeOffset? LastCheckAt { get; }
    Task<PortableUpdateCheckResult> CheckAsync(Version currentVersion, bool manual,
        CancellationToken cancellationToken = default);
    Task<PortableUpdateStageResult> DownloadAsync(PortableUpdateManifest manifest,
        IProgress<PortableUpdateStageProgress>? progress = null,
        CancellationToken cancellationToken = default);
    bool TryLaunchUpdater(PortableStagedUpdate update, int parentProcessId, string installDirectory,
        out string diagnosticCode);
}

public interface IPortableUpdaterProcessLauncher
{
    bool TryStart(ProcessStartInfo startInfo);
}

public sealed class UnavailablePortableUpdateCoordinator : IPortableUpdateCoordinator
{
    public bool IsAvailable => false;
    public DateTimeOffset? LastCheckAt => null;

    public Task<PortableUpdateCheckResult> CheckAsync(Version currentVersion, bool manual,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PortableUpdateCheckResult(
            cancellationToken.IsCancellationRequested ? PortableUpdateAvailability.Failed : PortableUpdateAvailability.Unavailable,
            cancellationToken.IsCancellationRequested ? "PORTABLE_UPDATE_CANCELLED" : "PORTABLE_UPDATE_FEED_NOT_CONFIGURED",
            currentVersion,
            null,
            DateTimeOffset.UtcNow));

    public Task<PortableUpdateStageResult> DownloadAsync(PortableUpdateManifest manifest,
        IProgress<PortableUpdateStageProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(PortableUpdateStageResult.Failure("PORTABLE_UPDATE_FEED_NOT_CONFIGURED"));

    public bool TryLaunchUpdater(PortableStagedUpdate update, int parentProcessId, string installDirectory,
        out string diagnosticCode)
    {
        diagnosticCode = "PORTABLE_UPDATE_FEED_NOT_CONFIGURED";
        return false;
    }
}
