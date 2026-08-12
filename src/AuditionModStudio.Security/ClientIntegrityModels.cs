namespace AuditionModStudio.Security;

public enum ClientIntegrityFailureReason
{
    None,
    InvalidPolicy,
    ExecutableSignatureRejected,
    ManifestMissing,
    ManifestHashMismatch,
    ManifestInvalid,
    ArtifactMissing,
    ArtifactPathRejected,
    ArtifactLengthMismatch,
    ArtifactHashMismatch,
    UnexpectedFailure
}

public sealed record ClientIntegrityCapabilities(
    bool ArchiveBuildExportEnabled,
    bool UpdateInstallEnabled,
    bool ResourceDependentMutationEnabled)
{
    public static ClientIntegrityCapabilities Full { get; } = new(true, true, true);
    public static ClientIntegrityCapabilities DiagnosticsOnly { get; } = new(false, false, false);
}

public sealed record ClientIntegrityResult(
    bool Succeeded,
    bool ProductionVerified,
    ClientIntegrityFailureReason FailureReason,
    string DiagnosticCode,
    ClientIntegrityCapabilities Capabilities)
{
    public static ClientIntegrityResult Success(bool productionVerified) => new(
        true,
        productionVerified,
        ClientIntegrityFailureReason.None,
        productionVerified ? "CLIENT_INTEGRITY_VALID" : "CLIENT_INTEGRITY_DEVELOPMENT_VALID",
        ClientIntegrityCapabilities.Full);

    public static ClientIntegrityResult Failure(ClientIntegrityFailureReason reason, string diagnosticCode) => new(
        false,
        false,
        reason,
        diagnosticCode,
        ClientIntegrityCapabilities.DiagnosticsOnly);
}

public sealed record ClientExecutableTrustResult(bool Succeeded, string DiagnosticCode)
{
    public static ClientExecutableTrustResult Success() => new(true, "CLIENT_EXECUTABLE_SIGNATURE_VALID");
    public static ClientExecutableTrustResult Failure(string diagnosticCode) => new(false, diagnosticCode);
}

public interface IClientExecutableTrustVerifier
{
    Task<ClientExecutableTrustResult> VerifyAsync(
        string executablePath,
        string expectedPublisherSubject,
        string expectedPublisherThumbprint,
        CancellationToken cancellationToken = default);
}

public sealed record ClientIntegrityOptions(
    string ApplicationRoot,
    string ExecutableRelativePath,
    string ManifestRelativePath,
    string ExpectedManifestSha256,
    bool RequireExecutableSignature,
    string ExpectedPublisherSubject,
    string ExpectedPublisherThumbprint);

public interface IClientIntegrityService
{
    Task<ClientIntegrityResult> VerifyAsync(CancellationToken cancellationToken = default);
}
