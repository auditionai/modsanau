using AuditionModStudio.Core.Images;

namespace AuditionModStudio.Imaging;

public sealed class EditHistoryService : IEditHistoryService
{
    internal const long EstimatedEntryOverheadBytes = 512;

    public EditHistoryCreateResult CreateSession(
        ImageEditorState initialState,
        EditHistoryOptions? options = null)
    {
        if (!IsValidState(initialState))
        {
            return EditHistoryCreateResult.Failure(
                EditHistoryFailureReason.InvalidInitialState,
                "EDIT_HISTORY_INVALID_INITIAL_STATE");
        }

        options ??= EditHistoryOptions.Default;
        if (options.MaximumEntries <= 0
            || options.MaximumEntries > EditHistoryOptions.MaximumAllowedEntries
            || options.MaximumEstimatedBytes <= 0
            || options.MaximumEstimatedBytes > EditHistoryOptions.MaximumAllowedEstimatedBytes)
        {
            return EditHistoryCreateResult.Failure(
                EditHistoryFailureReason.InvalidOptions,
                "EDIT_HISTORY_INVALID_OPTIONS");
        }

        var initialBytes = EstimateImageBytes(initialState.Image);
        if (initialBytes > options.MaximumEstimatedBytes)
        {
            return EditHistoryCreateResult.Failure(
                EditHistoryFailureReason.HistoryCapacityExceeded,
                "EDIT_HISTORY_INITIAL_STATE_EXCEEDS_CAPACITY");
        }

        return EditHistoryCreateResult.Success(new EditHistorySession(initialState, options));
    }

    internal static bool IsValidState(ImageEditorState? state) =>
        state?.Image is not null
        && state.Transform is not null
        && state.Adjustments is not null
        && state.Transform.ImageWidth == state.Image.Width
        && state.Transform.ImageHeight == state.Image.Height;

    internal static long EstimateImageBytes(InternalImage image) =>
        checked((long)image.Stride * image.Height);

    private sealed class EditHistorySession : IEditHistorySession
    {
        private readonly object _sync = new();
        private readonly EditHistoryOptions _options;
        private readonly List<EditHistoryEntry> _undo = [];
        private readonly List<EditHistoryEntry> _redo = [];
        private ImageEditorState _current;
        private long _currentRevision;
        private long _nextRevision = 1;
        private long _savedRevision;
        private EditTransaction? _transaction;

        public EditHistorySession(ImageEditorState initialState, EditHistoryOptions options)
        {
            _current = initialState;
            _options = options;
        }

        public EditHistoryState State
        {
            get
            {
                lock (_sync)
                {
                    return CreateState();
                }
            }
        }

        public EditHistoryResult Push(ImageEditorState state, EditOperationDescriptor operation)
        {
            lock (_sync)
            {
                if (_transaction is not null)
                {
                    return Failure(
                        EditHistoryFailureReason.OperationNotAllowedDuringTransaction,
                        "EDIT_HISTORY_PUSH_DURING_TRANSACTION");
                }

                return CommitState(state, operation);
            }
        }

        public EditHistoryResult Undo()
        {
            lock (_sync)
            {
                if (_transaction is not null)
                {
                    return Failure(
                        EditHistoryFailureReason.OperationNotAllowedDuringTransaction,
                        "EDIT_HISTORY_UNDO_DURING_TRANSACTION");
                }

                if (_undo.Count == 0)
                {
                    return Failure(EditHistoryFailureReason.NothingToUndo, "EDIT_HISTORY_NOTHING_TO_UNDO");
                }

                var entry = _undo[^1];
                _undo.RemoveAt(_undo.Count - 1);
                _redo.Add(entry);
                _current = entry.Before;
                _currentRevision = entry.FromRevision;
                return EditHistoryResult.Success(CreateState(), entry);
            }
        }

        public EditHistoryResult Redo()
        {
            lock (_sync)
            {
                if (_transaction is not null)
                {
                    return Failure(
                        EditHistoryFailureReason.OperationNotAllowedDuringTransaction,
                        "EDIT_HISTORY_REDO_DURING_TRANSACTION");
                }

                if (_redo.Count == 0)
                {
                    return Failure(EditHistoryFailureReason.NothingToRedo, "EDIT_HISTORY_NOTHING_TO_REDO");
                }

                var entry = _redo[^1];
                _redo.RemoveAt(_redo.Count - 1);
                _undo.Add(entry);
                _current = entry.After;
                _currentRevision = entry.ToRevision;
                return EditHistoryResult.Success(CreateState(), entry);
            }
        }

        public EditHistoryResult BeginTransaction(EditOperationDescriptor operation)
        {
            lock (_sync)
            {
                if (!IsValidOperation(operation))
                {
                    return Failure(EditHistoryFailureReason.InvalidOperation, "EDIT_HISTORY_INVALID_OPERATION");
                }

                if (_transaction is not null)
                {
                    return Failure(
                        EditHistoryFailureReason.TransactionAlreadyActive,
                        "EDIT_HISTORY_TRANSACTION_ALREADY_ACTIVE");
                }

                _transaction = new(_current, _current, operation);
                return EditHistoryResult.Success(CreateState());
            }
        }

        public EditHistoryResult UpdateTransaction(ImageEditorState state)
        {
            lock (_sync)
            {
                if (_transaction is null)
                {
                    return Failure(
                        EditHistoryFailureReason.NoActiveTransaction,
                        "EDIT_HISTORY_NO_ACTIVE_TRANSACTION");
                }

                if (!IsValidState(state))
                {
                    return Failure(EditHistoryFailureReason.InvalidState, "EDIT_HISTORY_INVALID_STATE");
                }

                _transaction = _transaction with { Current = state };
                _current = state;
                return EditHistoryResult.Success(CreateState());
            }
        }

        public EditHistoryResult CommitTransaction()
        {
            lock (_sync)
            {
                if (_transaction is null)
                {
                    return Failure(
                        EditHistoryFailureReason.NoActiveTransaction,
                        "EDIT_HISTORY_NO_ACTIVE_TRANSACTION");
                }

                var transaction = _transaction;
                _current = transaction.Before;
                var result = CommitState(transaction.Current, transaction.Operation);
                if (result.Succeeded)
                {
                    _transaction = null;
                }
                else
                {
                    _current = transaction.Current;
                }

                return result.Succeeded ? result with { State = CreateState() } : Failure(
                    result.FailureReason,
                    result.DiagnosticCode ?? "EDIT_HISTORY_TRANSACTION_COMMIT_FAILED");
            }
        }

        public EditHistoryResult CancelTransaction()
        {
            lock (_sync)
            {
                if (_transaction is null)
                {
                    return Failure(
                        EditHistoryFailureReason.NoActiveTransaction,
                        "EDIT_HISTORY_NO_ACTIVE_TRANSACTION");
                }

                _current = _transaction.Before;
                _transaction = null;
                return EditHistoryResult.Success(CreateState());
            }
        }

        public EditHistoryResult MarkSavedCheckpoint()
        {
            lock (_sync)
            {
                if (_transaction is not null)
                {
                    return Failure(
                        EditHistoryFailureReason.OperationNotAllowedDuringTransaction,
                        "EDIT_HISTORY_SAVE_DURING_TRANSACTION");
                }

                _savedRevision = _currentRevision;
                return EditHistoryResult.Success(CreateState());
            }
        }

        public EditHistoryResult ClearHistory()
        {
            lock (_sync)
            {
                if (_transaction is not null)
                {
                    return Failure(
                        EditHistoryFailureReason.OperationNotAllowedDuringTransaction,
                        "EDIT_HISTORY_CLEAR_DURING_TRANSACTION");
                }

                _undo.Clear();
                _redo.Clear();
                return EditHistoryResult.Success(CreateState());
            }
        }

        private EditHistoryResult CommitState(ImageEditorState state, EditOperationDescriptor operation)
        {
            if (!IsValidState(state))
            {
                return Failure(EditHistoryFailureReason.InvalidState, "EDIT_HISTORY_INVALID_STATE");
            }

            if (!IsValidOperation(operation))
            {
                return Failure(EditHistoryFailureReason.InvalidOperation, "EDIT_HISTORY_INVALID_OPERATION");
            }

            if (Equals(_current, state))
            {
                return Failure(EditHistoryFailureReason.InvalidState, "EDIT_HISTORY_NO_STATE_CHANGE");
            }

            long revision;
            try
            {
                revision = checked(_nextRevision);
                _ = checked(revision + 1);
            }
            catch (OverflowException)
            {
                return Failure(EditHistoryFailureReason.RevisionOverflow, "EDIT_HISTORY_REVISION_OVERFLOW");
            }

            var entry = new EditHistoryEntry(
                _currentRevision,
                revision,
                operation,
                _current,
                state);
            var candidateUndo = new List<EditHistoryEntry>(_undo) { entry };
            var candidateRedo = new List<EditHistoryEntry>();
            if (!TryApplyCapacity(candidateUndo, candidateRedo, state))
            {
                return Failure(
                    EditHistoryFailureReason.HistoryCapacityExceeded,
                    "EDIT_HISTORY_CAPACITY_EXCEEDED");
            }

            _undo.Clear();
            _undo.AddRange(candidateUndo);
            _redo.Clear();
            _current = state;
            _currentRevision = revision;
            _nextRevision = revision + 1;
            return EditHistoryResult.Success(CreateState(), entry);
        }

        private bool TryApplyCapacity(
            List<EditHistoryEntry> undo,
            List<EditHistoryEntry> redo,
            ImageEditorState current)
        {
            while (undo.Count > 1
                && (undo.Count + redo.Count > _options.MaximumEntries
                    || EstimateMemoryBytes(undo, redo, current) > _options.MaximumEstimatedBytes))
            {
                undo.RemoveAt(0);
            }

            return undo.Count + redo.Count <= _options.MaximumEntries
                && EstimateMemoryBytes(undo, redo, current) <= _options.MaximumEstimatedBytes;
        }

        private EditHistoryState CreateState()
        {
            var current = _transaction?.Current ?? _current;
            var transactionChanged = _transaction is not null && !Equals(_transaction.Before, _transaction.Current);
            return new EditHistoryState(
                current,
                _currentRevision,
                _savedRevision,
                transactionChanged || _currentRevision != _savedRevision,
                _undo.Count > 0 && _transaction is null,
                _redo.Count > 0 && _transaction is null,
                _transaction is not null,
                _transaction?.Operation,
                [.. _undo],
                [.. _redo],
                EstimateMemoryBytes(_undo, _redo, current));
        }

        private static long EstimateMemoryBytes(
            IReadOnlyCollection<EditHistoryEntry> undo,
            IReadOnlyCollection<EditHistoryEntry> redo,
            ImageEditorState current)
        {
            var images = new HashSet<InternalImage>(ReferenceEqualityComparer.Instance)
            {
                current.Image
            };

            foreach (var entry in undo.Concat(redo))
            {
                images.Add(entry.Before.Image);
                images.Add(entry.After.Image);
            }

            var bytes = checked((undo.Count + redo.Count) * EstimatedEntryOverheadBytes);
            foreach (var image in images)
            {
                bytes = checked(bytes + EstimateImageBytes(image));
            }

            return bytes;
        }

        private EditHistoryResult Failure(EditHistoryFailureReason reason, string diagnosticCode) =>
            EditHistoryResult.Failure(reason, diagnosticCode, CreateState());

        private static bool IsValidOperation(EditOperationDescriptor? operation) =>
            operation is not null && Enum.IsDefined(operation.Kind);

        private sealed record EditTransaction(
            ImageEditorState Before,
            ImageEditorState Current,
            EditOperationDescriptor Operation);
    }
}
