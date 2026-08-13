using System.Collections.Immutable;

namespace AuditionModStudio.Updater;

public static class AppUpdateProduct
{
    public const string Identity = "AuditionAIModStudio";
}

public enum AppUpdateChannel
{
    Stable
}

public enum AppUpdatePhase
{
    ValidatingManifest,
    Downloading,
    VerifyingHash,
    VerifyingPackageIdentity,
    VerifyingAuthenticode,
    ReadyToInstall,
    Installing,
    Completed
}

public enum AppUpdateStatus
{
    Installed,
    NoUpdate,
    Deferred,
    Failed,
    Cancelled
}

public enum AppUpdateFailureReason
{
    None,
    NoUpdate,
    RolloutDeferred,
    ActivityDeferred,
    ConcurrentOperation,
    InvalidRequest,
    InvalidManifestEnvelope,
    InvalidManifestSignature,
    InvalidManifest,
    ProductRejected,
    ChannelRejected,
    DowngradeRejected,
    NetworkFailure,
    DownloadInvalid,
    HashMismatch,
    PackageIdentityRejected,
    AuthenticodeRejected,
    InstallUnavailable,
    InstallationFailed,
    UnexpectedFailure,
    Cancelled
}

public sealed record AppUpdateProgress(AppUpdatePhase Phase, long BytesProcessed, long? TotalBytes);

public sealed record AppUpdateManifest(
    string ProductId,
    AppUpdateChannel Channel,
    int RolloutBasisPoints,
    Version Version,
    string ArtifactFileName,
    Uri ArtifactUri,
    long ContentLength,
    string Sha256,
    string PublisherSubject,
    string PublisherThumbprint);

public sealed class SignedUpdateManifestEnvelope
{
    public SignedUpdateManifestEnvelope(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        Payload = payload.ToArray().ToImmutableArray();
        Signature = signature.ToArray().ToImmutableArray();
    }

    public ImmutableArray<byte> Payload { get; }
    public ImmutableArray<byte> Signature { get; }
}

public sealed record AppUpdateRequest(
    Version CurrentVersion,
    ReadOnlyMemory<byte> SignedManifestEnvelope,
    int RolloutBucket = 0);

public sealed record AppUpdatePolicy(
    string ProductId,
    AppUpdateChannel Channel,
    ImmutableHashSet<string> AllowedArtifactHosts,
    string ExpectedPackageName,
    string ExpectedApplicationId,
    string ExpectedExecutable,
    string ExpectedEntryPoint,
    string ExpectedArchitecture,
    string ExpectedPublisherSubject,
    string ExpectedPublisherThumbprint)
{
    public static AppUpdatePolicy CreateStable(
        IEnumerable<string> allowedArtifactHosts,
        string expectedPublisherSubject,
        string expectedPublisherThumbprint) =>
        new(
            AppUpdateProduct.Identity,
            AppUpdateChannel.Stable,
            allowedArtifactHosts.Select(static host => host.Trim().ToLowerInvariant())
                .ToImmutableHashSet(StringComparer.Ordinal),
            AppUpdateProduct.Identity,
            "App",
            "AuditionModStudio.App.exe",
            "Windows.FullTrustApplication",
            "x64",
            expectedPublisherSubject,
            expectedPublisherThumbprint.ToUpperInvariant());

    public bool IsValid =>
        ProductId == AppUpdateProduct.Identity
        && Channel == AppUpdateChannel.Stable
        && AllowedArtifactHosts is { Count: > 0 and <= 8 }
        && AllowedArtifactHosts.All(static host => UpdateUriPolicy.IsSafeHost(host))
        && ExpectedPackageName == AppUpdateProduct.Identity
        && ExpectedApplicationId == "App"
        && ExpectedExecutable == "AuditionModStudio.App.exe"
        && ExpectedEntryPoint == "Windows.FullTrustApplication"
        && string.Equals(ExpectedArchitecture, "x64", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(ExpectedPublisherSubject)
        && ExpectedPublisherSubject.Length <= 512
        && UpdateManifestCodec.IsValidThumbprint(ExpectedPublisherThumbprint);
}

public sealed record VerifiedUpdateCandidate(
    AppUpdateManifest Manifest,
    string StagedArtifactPath,
    string VerifiedSha256);

public sealed record AppUpdateResult(
    bool Succeeded,
    AppUpdateStatus Status,
    AppUpdateFailureReason FailureReason,
    string DiagnosticCode,
    Version? InstalledVersion,
    bool RestartRequired)
{
    public static AppUpdateResult Success(Version version) =>
        new(true, AppUpdateStatus.Installed, AppUpdateFailureReason.None,
            "APP_UPDATE_INSTALLED_RESTART_REQUIRED", version, true);

    public static AppUpdateResult NoUpdate() =>
        new(false, AppUpdateStatus.NoUpdate, AppUpdateFailureReason.NoUpdate,
            "APP_UPDATE_CURRENT", null, false);

    public static AppUpdateResult Deferred(AppUpdateFailureReason reason, string code) =>
        new(false, AppUpdateStatus.Deferred, reason, code, null, false);

    public static AppUpdateResult Failure(AppUpdateFailureReason reason, string code) =>
        new(false, reason == AppUpdateFailureReason.Cancelled ? AppUpdateStatus.Cancelled : AppUpdateStatus.Failed,
            reason, code, null, false);
}

public sealed record AuthenticodeVerificationResult(bool Succeeded, string DiagnosticCode)
{
    public static AuthenticodeVerificationResult Success() => new(true, "AUTHENTICODE_VALID");
    public static AuthenticodeVerificationResult Failure(string code) => new(false, code);
}

public sealed record PackageIdentityVerificationResult(bool Succeeded, string DiagnosticCode)
{
    public static PackageIdentityVerificationResult Success() => new(true, "MSIX_IDENTITY_VALID");
    public static PackageIdentityVerificationResult Failure(string code) => new(false, code);
}

public interface IAppUpdateManifestVerifier
{
    bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature);
}

public interface IAuthenticodeUpdateVerifier
{
    Task<AuthenticodeVerificationResult> VerifyAsync(
        string artifactPath,
        string expectedPublisherSubject,
        string expectedPublisherThumbprint,
        CancellationToken cancellationToken = default);
}

public interface IAppUpdatePackageVerifier
{
    Task<PackageIdentityVerificationResult> VerifyAsync(
        string artifactPath,
        AppUpdateManifest manifest,
        AppUpdatePolicy policy,
        CancellationToken cancellationToken = default);
}

public interface IAppUpdateActivityGuard
{
    bool MustDeferInstallation();
}

public interface IVerifiedAppUpdateInstaller
{
    Task<bool> InstallAsync(
        VerifiedUpdateCandidate candidate,
        CancellationToken cancellationToken = default);
}

public interface IAppUpdateService
{
    Task<AppUpdateResult> VerifyAndInstallAsync(
        AppUpdateRequest request,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
