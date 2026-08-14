namespace AuditionModStudio.Core.Archives;

public sealed record ArchiveCommandResult(
    ArchiveOperation Operation,
    ArchiveOperationState FinalState,
    bool Succeeded,
    int ProcessedItemCount,
    ArchiveFailureReason FailureReason,
    string? OutputRelativePath,
    IReadOnlyList<ArchiveDiagnostic> Diagnostics)
{
    public static ArchiveCommandResult Failure(
        ArchiveOperation operation,
        ArchiveOperationState state,
        ArchiveFailureReason reason,
        string code,
        string message) => new(
            operation,
            state,
            false,
            0,
            reason,
            null,
            [new(code, message)]);
}
