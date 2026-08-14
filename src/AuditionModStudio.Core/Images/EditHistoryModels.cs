using System.Collections.Immutable;

namespace AuditionModStudio.Core.Images;

public enum EditOperationKind
{
    Import,
    Transform,
    Crop,
    Resize,
    Adjustment,
    Alpha
}

public sealed record EditOperationDescriptor(EditOperationKind Kind);

public sealed record ImageEditorState(
    InternalImage Image,
    InteractiveImageTransformState Transform,
    ImageAdjustmentSettings Adjustments);

public sealed record EditHistoryOptions(
    int MaximumEntries,
    long MaximumEstimatedBytes)
{
    public const int DefaultMaximumEntries = 100;
    public const long DefaultMaximumEstimatedBytes = 256L * 1024 * 1024;
    public const int MaximumAllowedEntries = 10_000;
    public const long MaximumAllowedEstimatedBytes = 4L * 1024 * 1024 * 1024;

    public static EditHistoryOptions Default { get; } = new(
        DefaultMaximumEntries,
        DefaultMaximumEstimatedBytes);
}

public sealed record EditHistoryEntry(
    long FromRevision,
    long ToRevision,
    EditOperationDescriptor Operation,
    ImageEditorState Before,
    ImageEditorState After);

public sealed record EditHistoryState(
    ImageEditorState Current,
    long CurrentRevision,
    long SavedRevision,
    bool IsDirty,
    bool CanUndo,
    bool CanRedo,
    bool HasActiveTransaction,
    EditOperationDescriptor? ActiveTransaction,
    ImmutableArray<EditHistoryEntry> UndoEntries,
    ImmutableArray<EditHistoryEntry> RedoEntries,
    long EstimatedMemoryBytes);

public enum EditHistoryFailureReason
{
    None,
    InvalidInitialState,
    InvalidOptions,
    InvalidState,
    InvalidOperation,
    NothingToUndo,
    NothingToRedo,
    TransactionAlreadyActive,
    NoActiveTransaction,
    OperationNotAllowedDuringTransaction,
    HistoryCapacityExceeded,
    RevisionOverflow
}

public sealed record EditHistoryCreateResult(
    bool Succeeded,
    EditHistoryFailureReason FailureReason,
    string? DiagnosticCode,
    IEditHistorySession? Session)
{
    public static EditHistoryCreateResult Success(IEditHistorySession session) =>
        new(true, EditHistoryFailureReason.None, null, session);

    public static EditHistoryCreateResult Failure(
        EditHistoryFailureReason reason,
        string diagnosticCode) =>
        new(false, reason, diagnosticCode, null);
}

public sealed record EditHistoryResult(
    bool Succeeded,
    EditHistoryFailureReason FailureReason,
    string? DiagnosticCode,
    EditHistoryState State,
    EditHistoryEntry? Entry)
{
    public static EditHistoryResult Success(
        EditHistoryState state,
        EditHistoryEntry? entry = null) =>
        new(true, EditHistoryFailureReason.None, null, state, entry);

    public static EditHistoryResult Failure(
        EditHistoryFailureReason reason,
        string diagnosticCode,
        EditHistoryState state) =>
        new(false, reason, diagnosticCode, state, null);
}
