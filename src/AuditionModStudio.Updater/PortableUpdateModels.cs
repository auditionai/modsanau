using System.Collections.Immutable;

namespace AuditionModStudio.Updater;

public static class PortableUpdateProduct
{
    public const string Identity = "AuditionAI.ModStudio";
    public const string Architecture = "win-x64";
    public const string Distribution = "portable";
    public const string PrimaryExecutable = "AuditionModStudio.App.exe";
    public const string UpdaterExecutable = "AuditionAI.Updater.exe";
}

public enum PortableUpdatePolicy
{
    Optional,
    Required
}

public sealed record PortablePackageFile(string Path, long Length, string Sha256);

public sealed record PortableUpdatePackage(
    Uri Uri,
    string FileName,
    long Length,
    string Sha256,
    string Product,
    string Architecture,
    string Distribution,
    ImmutableArray<PortablePackageFile> Inventory,
    ImmutableArray<string> RemoveOwnedFiles);

public sealed record PortableUpdateManifest(
    string Product,
    AppUpdateChannel Channel,
    Version Version,
    Version MinimumSupportedVersion,
    DateTimeOffset PublishedAt,
    PortableUpdatePolicy UpdatePolicy,
    PortableUpdatePackage Package,
    ImmutableArray<string> ReleaseNotes);

public sealed record PortableStagedUpdate(
    PortableUpdateManifest Manifest,
    string OperationRoot,
    string SignedEnvelopePath,
    string PackagePath,
    string ExtractedRoot,
    string VerifiedPackageSha256);

public enum PortableUpdateStage
{
    Downloading,
    Verifying,
    Extracting,
    ValidatingInventory,
    Ready
}

public sealed record PortableUpdateStageProgress(
    PortableUpdateStage Stage,
    long ProcessedBytes,
    long? TotalBytes);

public sealed record PortableUpdateStageResult(
    bool Succeeded,
    string DiagnosticCode,
    PortableStagedUpdate? Update)
{
    public static PortableUpdateStageResult Success(PortableStagedUpdate update) =>
        new(true, "PORTABLE_UPDATE_STAGED", update);

    public static PortableUpdateStageResult Failure(string code) => new(false, code, null);
}

public sealed record PortableInstallRequest(
    int ParentProcessId,
    string InstallDirectory,
    string StagingDirectory,
    Version ExpectedVersion,
    string RestartExecutableName);

public sealed record PortableInstallResult(
    bool Succeeded,
    bool RolledBack,
    string DiagnosticCode,
    string? RestartExecutablePath = null);

public interface IPortableInstallFailureInjector
{
    void OnCheckpoint(string checkpoint, string relativePath);
}

public sealed class NoPortableInstallFailureInjector : IPortableInstallFailureInjector
{
    public static NoPortableInstallFailureInjector Instance { get; } = new();
    private NoPortableInstallFailureInjector() { }
    public void OnCheckpoint(string checkpoint, string relativePath) { }
}
