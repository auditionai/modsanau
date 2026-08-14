namespace AuditionModStudio.Core.Exports;

public enum ArchiveExportOverwritePolicy
{
    RejectExisting,
    ReplaceExisting
}

public enum ArchiveExportDestinationFailureReason
{
    None,
    InvalidRequest,
    InvalidDirectoryPath,
    DirectoryNotFound,
    DirectoryAccessDenied,
    DirectoryUnavailable,
    ReparsePointNotAllowed,
    InvalidFileName,
    InvalidArchiveExtension,
    DestinationCollision
}

public readonly record struct ArchiveExportFileContract
{
    private ArchiveExportFileContract(string extension)
    {
        Extension = extension;
    }

    public string Extension { get; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Extension);

    public static bool TryCreate(string? trustedArchiveFileName, out ArchiveExportFileContract contract)
    {
        contract = default;
        if (string.IsNullOrWhiteSpace(trustedArchiveFileName)
            || !string.Equals(Path.GetFileName(trustedArchiveFileName), trustedArchiveFileName,
                StringComparison.Ordinal)
            || trustedArchiveFileName.EndsWith(' ')
            || trustedArchiveFileName.EndsWith('.')
            || trustedArchiveFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var deviceName = trustedArchiveFileName.Split('.', 2)[0];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (deviceName.Length == 4
                && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && deviceName[3] is >= '1' and <= '9'))
        {
            return false;
        }

        var extension = Path.GetExtension(trustedArchiveFileName);
        if (extension.Length < 2 || extension.Any(char.IsControl))
        {
            return false;
        }

        contract = new ArchiveExportFileContract(extension);
        return true;
    }
}

public sealed record ArchiveExportDestinationRequest(
    string OutputDirectory,
    string OutputFileName,
    ArchiveExportFileContract FileContract,
    ArchiveExportOverwritePolicy OverwritePolicy);

public sealed record ArchiveExportDestination(
    string CanonicalDirectory,
    string FileName,
    string FullPath,
    ArchiveExportFileContract FileContract,
    ArchiveExportOverwritePolicy OverwritePolicy,
    bool ExistingFile);

public sealed record ArchiveExportDestinationResult(
    bool Succeeded,
    ArchiveExportDestinationFailureReason FailureReason,
    string DiagnosticCode,
    ArchiveExportDestination? Destination)
{
    public static ArchiveExportDestinationResult Success(ArchiveExportDestination destination) =>
        new(true, ArchiveExportDestinationFailureReason.None,
            "ARCHIVE_EXPORT_DESTINATION_VALID", destination);

    public static ArchiveExportDestinationResult Failure(
        ArchiveExportDestinationFailureReason reason,
        string diagnosticCode) =>
        new(false, reason, diagnosticCode, null);
}

public interface IArchiveExportDestinationValidator
{
    ArchiveExportDestinationResult Validate(ArchiveExportDestinationRequest request);
}

public enum ArchiveExportPhase
{
    Preparing,
    VerifyingSource,
    Copying,
    VerifyingCandidate,
    Promoting,
    Completed,
    Failed,
    Cancelled
}

public enum ArchiveExportFailureReason
{
    None,
    InvalidRequest,
    BuildArtifactUnavailable,
    BuildArtifactInvalid,
    DestinationInvalid,
    CopyFailed,
    VerificationFailed,
    PromotionFailed,
    RollbackFailed,
    Cancelled
}

public sealed record ArchiveExportProgress(ArchiveExportPhase Phase, string DiagnosticCode);

public sealed record ArchiveExportRequest(
    Projects.AuditionProject Project,
    Projects.IProjectArchiveWorkspace Workspace,
    string OutputDirectory,
    string OutputFileName,
    ArchiveExportOverwritePolicy OverwritePolicy);

public sealed record ArchiveExportResult(
    bool Succeeded,
    bool Cancelled,
    ArchiveExportPhase FinalPhase,
    ArchiveExportFailureReason FailureReason,
    string DiagnosticCode,
    ArchiveExportDestination? Destination,
    long Size,
    Archives.Sha256Digest? Sha256)
{
    public static ArchiveExportResult Success(
        ArchiveExportDestination destination,
        long size,
        Archives.Sha256Digest sha256) =>
        new(true, false, ArchiveExportPhase.Completed, ArchiveExportFailureReason.None,
            "ARCHIVE_EXPORT_COMPLETED", destination, size, sha256);

    public static ArchiveExportResult Failure(ArchiveExportFailureReason reason, string code) =>
        new(false, false, ArchiveExportPhase.Failed, reason, code, null, 0, null);

    public static ArchiveExportResult CancelledResult() =>
        new(false, true, ArchiveExportPhase.Cancelled, ArchiveExportFailureReason.Cancelled,
            "ARCHIVE_EXPORT_CANCELLED", null, 0, null);
}

public interface IArchiveExportService
{
    Task<ArchiveExportResult> ExportAsync(
        ArchiveExportRequest request,
        IProgress<ArchiveExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
