using System.Collections.Immutable;
using AuditionModStudio.Updater;

namespace AuditionModStudio.App.Settings;

internal sealed class Plan102UiEvidenceUpdateCoordinator : IPortableUpdateCoordinator
{
    private int _downloadAttempt;

    public bool IsAvailable => true;
    public DateTimeOffset? LastCheckAt { get; private set; }

    public async Task<PortableUpdateCheckResult> CheckAsync(
        Version currentVersion,
        bool manual,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(250, cancellationToken);
        LastCheckAt = DateTimeOffset.UtcNow;
        if (File.Exists(Path.Combine(Path.GetTempPath(), "plan102-update-success-evidence.flag")))
            return new(PortableUpdateAvailability.Current, "PORTABLE_UPDATE_JUST_INSTALLED", currentVersion,
                null, LastCheckAt.Value);
        return new(PortableUpdateAvailability.Available, "PORTABLE_UPDATE_AVAILABLE", currentVersion,
            Manifest(), LastCheckAt.Value);
    }

    public async Task<PortableUpdateStageResult> DownloadAsync(
        PortableUpdateManifest manifest,
        IProgress<PortableUpdateStageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var attempt = Interlocked.Increment(ref _downloadAttempt);
        for (var step = 1; step <= 8; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(PortableUpdateStage.Downloading, step * 12, 100));
            await Task.Delay(250, cancellationToken);
        }
        progress?.Report(new(PortableUpdateStage.Verifying, 100, 100));
        await Task.Delay(500, cancellationToken);
        if (attempt == 1) return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_HASH_MISMATCH");
        var root = Path.Combine(Path.GetTempPath(), "plan-102-ui-evidence");
        return PortableUpdateStageResult.Success(new(manifest, root,
            Path.Combine(root, "manifest.signed.json"), Path.Combine(root, "package.zip"),
            Path.Combine(root, "extracted"), new string('A', 64)));
    }

    public bool TryLaunchUpdater(PortableStagedUpdate update, int parentProcessId, string installDirectory,
        out string diagnosticCode)
    {
        diagnosticCode = "PORTABLE_UPDATE_UI_EVIDENCE_NO_INSTALL";
        return false;
    }

    private static PortableUpdateManifest Manifest()
    {
        var inventory = ImmutableArray.Create(
            new PortablePackageFile(PortableUpdateProduct.PrimaryExecutable, 1, new string('A', 64)),
            new PortablePackageFile(PortableUpdateProduct.UpdaterExecutable, 1, new string('B', 64)));
        return new(PortableUpdateProduct.Identity, AppUpdateChannel.Stable, new Version(1, 0, 1),
            new Version(1, 0, 0), DateTimeOffset.UtcNow, PortableUpdatePolicy.Optional,
            new(new("https://release.example.invalid/AuditionAI-Mod-Studio-1.0.1-win-x64.zip"),
                "AuditionAI-Mod-Studio-1.0.1-win-x64.zip", 100, new string('C', 64),
                PortableUpdateProduct.Identity, PortableUpdateProduct.Architecture,
                PortableUpdateProduct.Distribution, inventory, []),
            ["Sửa lỗi Build", "Cải thiện hiệu năng", "Sửa lỗi giao diện"]);
    }
}
