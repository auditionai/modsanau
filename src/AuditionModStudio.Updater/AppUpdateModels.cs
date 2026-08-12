using System.Collections.Immutable;

namespace AuditionModStudio.Updater;

public enum AppUpdatePhase
{
    ValidatingManifest,
    Downloading,
    VerifyingHash,
    VerifyingAuthenticode,
    Installing,
    Completed
}

public enum AppUpdateFailureReason
{
    None,
    InvalidRequest,
    InvalidManifestEnvelope,
    InvalidManifestSignature,
    InvalidManifest,
    DowngradeRejected,
    NetworkFailure,
    DownloadInvalid,
    HashMismatch,
    AuthenticodeRejected,
    InstallationFailed,
    UnexpectedFailure,
    Cancelled
}

public sealed record AppUpdateProgress(AppUpdatePhase Phase, long BytesProcessed, long? TotalBytes);

public sealed record AppUpdateManifest(
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
    ReadOnlyMemory<byte> SignedManifestEnvelope);

public sealed record VerifiedUpdateCandidate(
    AppUpdateManifest Manifest,
    string StagedArtifactPath,
    string VerifiedSha256);

public sealed record AppUpdateResult(
    bool Succeeded,
    AppUpdateFailureReason FailureReason,
    string DiagnosticCode,
    Version? InstalledVersion)
{
    public static AppUpdateResult Success(Version version) =>
        new(true, AppUpdateFailureReason.None, "APP_UPDATE_INSTALLED", version);

    public static AppUpdateResult Failure(AppUpdateFailureReason reason, string code) =>
        new(false, reason, code, null);
}

public sealed record AuthenticodeVerificationResult(bool Succeeded, string DiagnosticCode)
{
    public static AuthenticodeVerificationResult Success() => new(true, "AUTHENTICODE_VALID");
    public static AuthenticodeVerificationResult Failure(string code) => new(false, code);
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
