using System.Diagnostics;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Imaging;
using Xunit.Abstractions;

namespace Imaging.Tests;

public sealed class EditHistoryServiceTests
{
    private readonly EditHistoryService _service = new();
    private readonly ITestOutputHelper _output;

    public EditHistoryServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Initial_state_cannot_undo_or_redo_and_is_clean()
    {
        var session = CreateSession(CreateState());

        Assert.False(session.State.CanUndo);
        Assert.False(session.State.CanRedo);
        Assert.False(session.State.IsDirty);
        Assert.Equal(0, session.State.CurrentRevision);
        Assert.Equal(0, session.State.SavedRevision);
    }

    [Fact]
    public void Push_creates_one_immutable_entry_and_advances_revision()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        var adjusted = WithBrightness(initial, 0.2);

        var result = session.Push(adjusted, Operation(EditOperationKind.Adjustment));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.State.CurrentRevision);
        Assert.Same(adjusted, result.State.Current);
        var entry = Assert.Single(result.State.UndoEntries);
        Assert.Equal((0, 1), (entry.FromRevision, entry.ToRevision));
        Assert.Same(initial, entry.Before);
        Assert.Same(adjusted, entry.After);
    }

    [Fact]
    public void Undo_and_redo_restore_exact_states_without_inverse_math()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        var adjusted = WithBrightness(initial, 0.2);
        session.Push(adjusted, Operation(EditOperationKind.Adjustment));

        var undo = session.Undo();
        var redo = session.Redo();

        Assert.Same(initial, undo.State.Current);
        Assert.Equal(0, undo.State.CurrentRevision);
        Assert.Same(adjusted, redo.State.Current);
        Assert.Equal(1, redo.State.CurrentRevision);
    }

    [Fact]
    public void Multiple_undo_and_redo_follow_linear_order()
    {
        var initial = CreateState();
        var a = WithBrightness(initial, 0.1);
        var b = WithBrightness(initial, 0.2);
        var session = CreateSession(initial);
        session.Push(a, Operation(EditOperationKind.Adjustment));
        session.Push(b, Operation(EditOperationKind.Adjustment));

        Assert.Same(a, session.Undo().State.Current);
        Assert.Same(initial, session.Undo().State.Current);
        Assert.Same(a, session.Redo().State.Current);
        Assert.Same(b, session.Redo().State.Current);
    }

    [Fact]
    public void Undo_then_new_edit_discards_old_redo_branch()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        session.Push(WithBrightness(initial, 0.1), Operation(EditOperationKind.Adjustment));
        session.Push(WithBrightness(initial, 0.2), Operation(EditOperationKind.Adjustment));
        session.Undo();

        var branch = session.Push(WithBrightness(initial, 0.3), Operation(EditOperationKind.Adjustment));

        Assert.True(branch.Succeeded);
        Assert.False(branch.State.CanRedo);
        Assert.Empty(branch.State.RedoEntries);
        Assert.Equal(EditHistoryFailureReason.NothingToRedo, session.Redo().FailureReason);
    }

    [Fact]
    public void New_branch_uses_new_revision_instead_of_reusing_discarded_revision()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        session.Push(WithBrightness(initial, 0.1), Operation(EditOperationKind.Adjustment));
        session.Push(WithBrightness(initial, 0.2), Operation(EditOperationKind.Adjustment));
        session.Undo();

        var branch = session.Push(WithBrightness(initial, 0.3), Operation(EditOperationKind.Adjustment));

        Assert.Equal(3, branch.State.CurrentRevision);
        Assert.Equal(3, branch.Entry!.ToRevision);
    }

    [Fact]
    public void Nothing_to_undo_or_redo_returns_structured_failure_without_state_change()
    {
        var session = CreateSession(CreateState());

        var undo = session.Undo();
        var redo = session.Redo();

        Assert.Equal(EditHistoryFailureReason.NothingToUndo, undo.FailureReason);
        Assert.Equal("EDIT_HISTORY_NOTHING_TO_UNDO", undo.DiagnosticCode);
        Assert.Equal(EditHistoryFailureReason.NothingToRedo, redo.FailureReason);
        Assert.Equal(0, redo.State.CurrentRevision);
    }

    [Fact]
    public void Saved_checkpoint_tracks_clean_state_across_undo_and_redo()
    {
        var initial = CreateState();
        var a = WithBrightness(initial, 0.1);
        var b = WithBrightness(initial, 0.2);
        var session = CreateSession(initial);
        session.Push(a, Operation(EditOperationKind.Adjustment));

        var saved = session.MarkSavedCheckpoint();
        session.Push(b, Operation(EditOperationKind.Adjustment));
        var undo = session.Undo();
        var redo = session.Redo();

        Assert.False(saved.State.IsDirty);
        Assert.Equal(1, saved.State.SavedRevision);
        Assert.False(undo.State.IsDirty);
        Assert.True(redo.State.IsDirty);
    }

    [Fact]
    public void Lightweight_transform_snapshot_reuses_image_storage()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        var transformed = initial with
        {
            Transform = initial.Transform with { Zoom = 2, Pan = new(10, 20) }
        };

        var result = session.Push(transformed, Operation(EditOperationKind.Transform));

        Assert.True(result.Succeeded);
        Assert.Same(initial.Image, result.State.Current.Image);
        Assert.Equal(ImageBytes(initial.Image) + 512, result.State.EstimatedMemoryBytes);
    }

    [Fact]
    public void Adjustment_snapshot_reuses_image_storage_instead_of_rendered_pixels()
    {
        var initial = CreateState();
        var session = CreateSession(initial);

        for (var index = 1; index <= 20; index++)
        {
            var result = session.Push(
                WithBrightness(initial, index / 100.0),
                Operation(EditOperationKind.Adjustment));
            Assert.True(result.Succeeded);
        }

        Assert.Equal(20, session.State.UndoEntries.Length);
        Assert.Equal(ImageBytes(initial.Image) + (20 * 512), session.State.EstimatedMemoryBytes);
    }

    [Fact]
    public void Destructive_resize_history_restores_exact_previous_image_reference()
    {
        var initial = CreateState(width: 2, height: 2);
        var resizedImage = CreateImage(3, 1);
        var resized = CreateState(resizedImage);
        var session = CreateSession(initial);

        session.Push(resized, Operation(EditOperationKind.Resize));
        var undo = session.Undo();
        var redo = session.Redo();

        Assert.Same(initial.Image, undo.State.Current.Image);
        Assert.Same(resizedImage, redo.State.Current.Image);
        Assert.Equal((2, 2), (undo.State.Current.Image.Width, undo.State.Current.Image.Height));
        Assert.Equal((3, 1), (redo.State.Current.Image.Width, redo.State.Current.Image.Height));
    }

    [Fact]
    public void Maximum_entry_count_evicts_oldest_undo_entry_and_retains_current()
    {
        var initial = CreateState();
        var session = CreateSession(initial, new(2, 1024 * 1024));
        for (var index = 1; index <= 3; index++)
        {
            session.Push(WithBrightness(initial, index / 10.0), Operation(EditOperationKind.Adjustment));
        }

        Assert.Equal(2, session.State.UndoEntries.Length);
        Assert.Equal(3, session.State.CurrentRevision);
        Assert.Equal(1, session.State.UndoEntries[0].FromRevision);
        Assert.True(session.Undo().Succeeded);
        Assert.True(session.Undo().Succeeded);
        Assert.Equal(EditHistoryFailureReason.NothingToUndo, session.Undo().FailureReason);
    }

    [Fact]
    public void Memory_budget_evicts_oldest_destructive_snapshot()
    {
        var initial = CreateState();
        var options = new EditHistoryOptions(10, 520);
        var session = CreateSession(initial, options);
        var second = CreateState(CreateImage(1, 1, 10));
        var third = CreateState(CreateImage(1, 1, 20));

        Assert.True(session.Push(second, Operation(EditOperationKind.Resize)).Succeeded);
        Assert.True(session.Push(third, Operation(EditOperationKind.Resize)).Succeeded);

        Assert.Single(session.State.UndoEntries);
        Assert.Same(third.Image, session.State.Current.Image);
        Assert.InRange(session.State.EstimatedMemoryBytes, 0, options.MaximumEstimatedBytes);
    }

    [Fact]
    public void Entry_larger_than_total_budget_is_rejected_atomically()
    {
        var initial = CreateState();
        var session = CreateSession(initial, new(10, 519));
        var before = session.State;

        var result = session.Push(
            CreateState(CreateImage(1, 1, 10)),
            Operation(EditOperationKind.Resize));

        Assert.Equal(EditHistoryFailureReason.HistoryCapacityExceeded, result.FailureReason);
        Assert.Same(before.Current, result.State.Current);
        Assert.Equal(before.CurrentRevision, result.State.CurrentRevision);
        Assert.Empty(result.State.UndoEntries);
    }

    [Fact]
    public void Clear_history_retains_current_revision_and_dirty_state()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        var edited = WithBrightness(initial, 0.1);
        session.Push(edited, Operation(EditOperationKind.Adjustment));

        var result = session.ClearHistory();

        Assert.Same(edited, result.State.Current);
        Assert.Equal(1, result.State.CurrentRevision);
        Assert.True(result.State.IsDirty);
        Assert.False(result.State.CanUndo);
        Assert.False(result.State.CanRedo);
    }

    [Fact]
    public void Transaction_updates_current_preview_and_commit_creates_one_entry()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        Assert.True(session.BeginTransaction(Operation(EditOperationKind.Adjustment)).Succeeded);

        for (var index = 1; index <= 80; index++)
        {
            var update = session.UpdateTransaction(WithBrightness(initial, index / 100.0));
            Assert.True(update.Succeeded);
            Assert.True(update.State.HasActiveTransaction);
        }

        var commit = session.CommitTransaction();

        Assert.True(commit.Succeeded);
        Assert.False(commit.State.HasActiveTransaction);
        Assert.Single(commit.State.UndoEntries);
        Assert.Equal(0.8, commit.State.Current.Adjustments.Brightness);
    }

    [Fact]
    public void Transform_drag_transaction_coalesces_updates_into_one_entry()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        session.BeginTransaction(Operation(EditOperationKind.Transform));
        for (var index = 1; index <= 100; index++)
        {
            session.UpdateTransaction(initial with
            {
                Transform = initial.Transform with { Pan = new(index, index * 2) }
            });
        }

        var result = session.CommitTransaction();

        Assert.Single(result.State.UndoEntries);
        Assert.Equal(new ViewportVector(100, 200), result.State.Current.Transform.Pan);
    }

    [Fact]
    public void Cancel_transaction_restores_exact_pre_transaction_state()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        session.BeginTransaction(Operation(EditOperationKind.Adjustment));
        session.UpdateTransaction(WithBrightness(initial, 0.5));

        var result = session.CancelTransaction();

        Assert.Same(initial, result.State.Current);
        Assert.False(result.State.HasActiveTransaction);
        Assert.Empty(result.State.UndoEntries);
        Assert.False(result.State.IsDirty);
    }

    [Fact]
    public void Nested_transaction_is_rejected()
    {
        var session = CreateSession(CreateState());
        session.BeginTransaction(Operation(EditOperationKind.Transform));

        var result = session.BeginTransaction(Operation(EditOperationKind.Adjustment));

        Assert.Equal(EditHistoryFailureReason.TransactionAlreadyActive, result.FailureReason);
    }

    [Theory]
    [InlineData("undo")]
    [InlineData("redo")]
    [InlineData("push")]
    [InlineData("save")]
    [InlineData("clear")]
    public void Conflicting_mutation_is_rejected_during_transaction(string operation)
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        session.BeginTransaction(Operation(EditOperationKind.Adjustment));

        var result = operation switch
        {
            "undo" => session.Undo(),
            "redo" => session.Redo(),
            "push" => session.Push(WithBrightness(initial, 0.2), Operation(EditOperationKind.Adjustment)),
            "save" => session.MarkSavedCheckpoint(),
            "clear" => session.ClearHistory(),
            _ => throw new UnreachableException()
        };

        Assert.Equal(EditHistoryFailureReason.OperationNotAllowedDuringTransaction, result.FailureReason);
        Assert.True(result.State.HasActiveTransaction);
    }

    [Fact]
    public void Transaction_without_changes_does_not_create_entry()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        session.BeginTransaction(Operation(EditOperationKind.Adjustment));

        var result = session.CommitTransaction();

        Assert.Equal(EditHistoryFailureReason.InvalidState, result.FailureReason);
        Assert.True(result.State.HasActiveTransaction);
        Assert.Empty(result.State.UndoEntries);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("commit")]
    [InlineData("cancel")]
    public void Transaction_operation_without_begin_is_structured_failure(string operation)
    {
        var initial = CreateState();
        var session = CreateSession(initial);

        var result = operation switch
        {
            "update" => session.UpdateTransaction(WithBrightness(initial, 0.2)),
            "commit" => session.CommitTransaction(),
            "cancel" => session.CancelTransaction(),
            _ => throw new UnreachableException()
        };

        Assert.Equal(EditHistoryFailureReason.NoActiveTransaction, result.FailureReason);
        Assert.Equal(0, result.State.CurrentRevision);
    }

    [Fact]
    public void Transaction_preview_is_dirty_without_advancing_committed_revision()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        session.BeginTransaction(Operation(EditOperationKind.Adjustment));

        var preview = session.UpdateTransaction(WithBrightness(initial, 0.2));

        Assert.True(preview.State.IsDirty);
        Assert.Equal(0, preview.State.CurrentRevision);
        Assert.Empty(preview.State.UndoEntries);
    }

    [Fact]
    public void Push_of_equivalent_state_is_rejected_without_revision_advance()
    {
        var initial = CreateState();
        var session = CreateSession(initial);

        var result = session.Push(initial with { }, Operation(EditOperationKind.Transform));

        Assert.Equal(EditHistoryFailureReason.InvalidState, result.FailureReason);
        Assert.Equal("EDIT_HISTORY_NO_STATE_CHANGE", result.DiagnosticCode);
        Assert.Equal(0, result.State.CurrentRevision);
    }

    [Fact]
    public void Failed_transaction_commit_remains_active_and_can_be_cancelled()
    {
        var initial = CreateState();
        var session = CreateSession(initial, new(10, ImageBytes(initial.Image) + 511));
        session.BeginTransaction(Operation(EditOperationKind.Adjustment));
        session.UpdateTransaction(WithBrightness(initial, 0.2));

        var failed = session.CommitTransaction();
        var cancelled = session.CancelTransaction();

        Assert.Equal(EditHistoryFailureReason.HistoryCapacityExceeded, failed.FailureReason);
        Assert.True(failed.State.HasActiveTransaction);
        Assert.Same(initial, cancelled.State.Current);
    }

    [Fact]
    public void Invalid_state_and_operation_do_not_advance_history()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        var mismatched = initial with { Transform = initial.Transform with { ImageWidth = 2 } };

        var invalidState = session.Push(mismatched, Operation(EditOperationKind.Transform));
        var invalidOperation = session.Push(
            WithBrightness(initial, 0.2),
            new((EditOperationKind)999));

        Assert.Equal(EditHistoryFailureReason.InvalidState, invalidState.FailureReason);
        Assert.Equal(EditHistoryFailureReason.InvalidOperation, invalidOperation.FailureReason);
        Assert.Equal(0, session.State.CurrentRevision);
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(10, 0)]
    [InlineData(-1, 1024)]
    [InlineData(10, -1)]
    [InlineData(10001, 1024)]
    [InlineData(10, 4294967297)]
    public void Invalid_options_are_structured_failure(int entries, long bytes)
    {
        var result = _service.CreateSession(CreateState(), new(entries, bytes));

        Assert.Equal(EditHistoryFailureReason.InvalidOptions, result.FailureReason);
        Assert.Null(result.Session);
    }

    [Fact]
    public void Initial_image_larger_than_budget_is_rejected()
    {
        var result = _service.CreateSession(CreateState(), new(10, 3));

        Assert.Equal(EditHistoryFailureReason.HistoryCapacityExceeded, result.FailureReason);
    }

    [Fact]
    public void External_stack_views_are_immutable_snapshots()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        session.Push(WithBrightness(initial, 0.1), Operation(EditOperationKind.Adjustment));
        var captured = session.State.UndoEntries;
        session.Push(WithBrightness(initial, 0.2), Operation(EditOperationKind.Adjustment));

        Assert.Single(captured);
        Assert.Equal(2, session.State.UndoEntries.Length);
    }

    [Fact]
    public async Task Concurrent_mutations_are_serialized_without_stack_corruption()
    {
        var initial = CreateState();
        var session = CreateSession(initial);
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(1, 20).Select(index => Task.Run(() =>
        {
            gate.Wait();
            return session.Push(
                WithBrightness(initial, index / 100.0),
                Operation(EditOperationKind.Adjustment));
        })).ToArray();
        gate.Set();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(20, session.State.UndoEntries.Length);
        Assert.Equal(20, session.State.CurrentRevision);
        Assert.Equal(20, session.State.UndoEntries.Select(entry => entry.ToRevision).Distinct().Count());
    }

    [Theory]
    [InlineData(6000, 1801)]
    [InlineData(4000, 4000)]
    public void Large_image_adjustment_history_does_not_multiply_pixel_storage(int width, int height)
    {
        var initial = CreateState(width, height);
        var session = CreateSession(initial);
        var stopwatch = Stopwatch.StartNew();
        for (var index = 1; index <= 20; index++)
        {
            Assert.True(session.Push(
                WithBrightness(initial, index / 100.0),
                Operation(EditOperationKind.Adjustment)).Succeeded);
        }

        stopwatch.Stop();
        _output.WriteLine($"{width}x{height}, 20 pushes: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        var onePixelBuffer = ImageBytes(initial.Image);
        Assert.Equal(onePixelBuffer + (20 * 512), session.State.EstimatedMemoryBytes);
        Assert.True(session.State.EstimatedMemoryBytes < onePixelBuffer * 2);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Push_undo_redo_lightweight_state_has_representative_timing()
    {
        var initial = CreateState();
        var session = CreateSession(initial, new(2000, 1024 * 1024));
        var stopwatch = Stopwatch.StartNew();
        for (var index = 1; index <= 1000; index++)
        {
            session.Push(
                WithBrightness(initial, index / 1000.0),
                Operation(EditOperationKind.Adjustment));
        }

        for (var index = 0; index < 1000; index++)
        {
            session.Undo();
        }

        for (var index = 0; index < 1000; index++)
        {
            session.Redo();
        }

        stopwatch.Stop();
        _output.WriteLine($"1000 push + undo + redo: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    private IEditHistorySession CreateSession(
        ImageEditorState initial,
        EditHistoryOptions? options = null)
    {
        var result = _service.CreateSession(initial, options);
        Assert.True(result.Succeeded);
        return Assert.IsAssignableFrom<IEditHistorySession>(result.Session);
    }

    private static EditOperationDescriptor Operation(EditOperationKind kind) => new(kind);

    private static ImageEditorState WithBrightness(ImageEditorState state, double brightness) =>
        state with { Adjustments = state.Adjustments with { Brightness = brightness } };

    private static ImageEditorState CreateState(int width = 1, int height = 1) =>
        CreateState(CreateImage(width, height));

    private static ImageEditorState CreateState(InternalImage image) =>
        new(
            image,
            new InteractiveImageTransformState(
                image.Width,
                image.Height,
                new(0, 0, 1, 1),
                1,
                new(0, 0),
                ImageScale.Identity,
                ImageQuarterTurn.None,
                false,
                false,
                new(0, 0),
                ImageTransformConstraints.Default),
            new());

    private static InternalImage CreateImage(int width, int height, byte value = 0)
    {
        var pixels = new byte[checked(width * height * 4)];
        Array.Fill(pixels, value);
        return new InternalImage(
            width,
            height,
            checked(width * 4),
            pixels,
            new ImageSourceMetadata(
                ImageSourceFormat.Png,
                width,
                height,
                ImageSourceOrientation.Normal,
                true,
                false));
    }

    private static long ImageBytes(InternalImage image) => (long)image.Stride * image.Height;
}
