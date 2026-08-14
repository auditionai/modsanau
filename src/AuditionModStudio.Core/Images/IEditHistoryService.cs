namespace AuditionModStudio.Core.Images;

public interface IEditHistoryService
{
    EditHistoryCreateResult CreateSession(
        ImageEditorState initialState,
        EditHistoryOptions? options = null);
}

public interface IEditHistorySession
{
    EditHistoryState State { get; }

    EditHistoryResult Push(ImageEditorState state, EditOperationDescriptor operation);

    EditHistoryResult Undo();

    EditHistoryResult Redo();

    EditHistoryResult BeginTransaction(EditOperationDescriptor operation);

    EditHistoryResult UpdateTransaction(ImageEditorState state);

    EditHistoryResult CommitTransaction();

    EditHistoryResult CancelTransaction();

    EditHistoryResult MarkSavedCheckpoint();

    EditHistoryResult ClearHistory();
}
