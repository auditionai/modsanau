namespace AuditionModStudio.Core.Diagnostics;

public sealed record DiagnosticExportRequest(
    string OutputDirectory,
    string OutputFileName,
    bool IncludeUserContent = false)
{
    public override string ToString() => "DiagnosticExportRequest { [REDACTED] }";
}

public enum DiagnosticExportPhase
{
    Validating,
    CollectingLogs,
    CreatingBundle,
    Promoting,
    Completed,
    Failed,
    Cancelled,
}

public enum DiagnosticExportFailureReason
{
    None,
    InvalidRequest,
    UserContentNotAllowed,
    DestinationInvalid,
    UnsafeLogSource,
    LogTooLarge,
    ExportFailed,
    Cancelled,
}

public sealed record DiagnosticExportProgress(
    DiagnosticExportPhase Phase,
    int Percent,
    string DiagnosticCode);

public sealed record DiagnosticExportResult(
    bool Succeeded,
    bool Cancelled,
    DiagnosticExportFailureReason FailureReason,
    string DiagnosticCode,
    string? OutputPath,
    int ExportedLogCount,
    long BundleSize)
{
    public static DiagnosticExportResult Success(string outputPath, int logCount, long size) =>
        new(true, false, DiagnosticExportFailureReason.None, "DIAGNOSTIC_EXPORT_COMPLETED",
            outputPath, logCount, size);

    public static DiagnosticExportResult Failure(DiagnosticExportFailureReason reason, string code) =>
        new(false, false, reason, code, null, 0, 0);

    public static DiagnosticExportResult CancelledResult() =>
        new(false, true, DiagnosticExportFailureReason.Cancelled, "DIAGNOSTIC_EXPORT_CANCELLED",
            null, 0, 0);

    public override string ToString() =>
        $"DiagnosticExportResult {{ Succeeded = {Succeeded}, Cancelled = {Cancelled}, " +
        $"FailureReason = {FailureReason}, DiagnosticCode = {DiagnosticCode}, " +
        $"ExportedLogCount = {ExportedLogCount}, BundleSize = {BundleSize}, OutputPath = [REDACTED] }}";
}

public interface IDiagnosticExportService
{
    Task<DiagnosticExportResult> ExportAsync(
        DiagnosticExportRequest request,
        IProgress<DiagnosticExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
