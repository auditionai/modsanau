using System.Collections.Immutable;
using AuditionModStudio.App.AiStudio;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;

namespace IntegrationTests;

public sealed class AiStudioViewModelTests
{
    [Fact]
    public async Task Activation_presents_server_quote_and_history_authority()
    {
        var studio = new StubStudioService
        {
            Quote = new(true, "AI_PRICE_QUOTED", new(7, "v2")),
            HistoryResult = new(true, "AI_JOB_LISTED",
            [
                new(Guid.NewGuid(), AiStudioOperation.Generate, AiStudioJobState.Completed,
                    7, 7, DateTimeOffset.UnixEpoch, false),
            ]),
        };
        var viewModel = CreateViewModel(studioService: studio);

        await viewModel.ActivateAsync();

        Assert.Contains("7", viewModel.QuoteText, StringComparison.Ordinal);
        Assert.Contains("v2", viewModel.QuoteText, StringComparison.Ordinal);
        Assert.Single(viewModel.History);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public void Bounded_prompt_and_reference_requirements_control_submission()
    {
        var selection = new StubSelection(null, null);
        var viewModel = CreateViewModel(selection: selection);
        viewModel.Prompt = new string('a', AiPrompt.MaximumLength + 1);

        Assert.False(viewModel.CanSubmit);
        Assert.NotEmpty(viewModel.PromptValidationMessage);

        viewModel.Prompt = "edit the texture";
        viewModel.SelectedOperation = AiStudioOptions.Operations.Single(option =>
            option.Operation == AiStudioOperation.Edit);

        Assert.False(viewModel.CanSubmit);
        Assert.Contains("Select", viewModel.ReferenceMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_generation_only_sets_internal_image_preview()
    {
        var image = Image(2, 2);
        var ai = new RecordingAiService(AiImageResult.Success(image));
        var viewModel = CreateViewModel(ai);
        viewModel.Prompt = "neon pointer";
        viewModel.NegativePrompt = "blur";
        viewModel.SelectedModel = AiStudioOptions.Models[1];
        viewModel.SelectedQuality = AiStudioOptions.Qualities[1];

        await viewModel.SubmitAsync();

        Assert.Same(image, viewModel.PreviewImage);
        Assert.True(viewModel.HasPreview);
        Assert.Equal("balanced", ai.GenerateRequest!.Preferences!.Model);
        Assert.Equal("high", ai.GenerateRequest.Preferences.Quality);
        Assert.Equal("blur", ai.GenerateRequest.Preferences.NegativePrompt!.Value.Value);
        Assert.Contains("project has not been changed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Offline_services_have_explicit_recoverable_states()
    {
        var viewModel = CreateViewModel(
            new RecordingAiService(AiImageResult.Failure(
                AiServiceFailureReason.Unavailable, "AI_TRUSTED_BACKEND_UNAVAILABLE")),
            new StubStudioService());
        viewModel.Prompt = "test";

        await viewModel.ActivateAsync();
        await viewModel.SubmitAsync();

        Assert.Contains("offline", viewModel.QuoteText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offline", viewModel.HistoryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offline", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(viewModel.PreviewImage);
    }

    [Fact]
    public async Task Reference_operation_loads_selected_image_without_mutating_selection()
    {
        var reference = Image(2, 2);
        var selection = new StubSelection(Texture(), reference);
        var ai = new RecordingAiService(AiImageResult.Success(reference));
        var viewModel = CreateViewModel(ai, selection: selection);
        viewModel.SelectedOperation = AiStudioOptions.Operations.Single(option =>
            option.Operation == AiStudioOperation.Edit);
        viewModel.Prompt = "recolor";

        await viewModel.SubmitAsync();

        Assert.Equal(1, selection.LoadCount);
        Assert.Same(reference, ai.EditRequest!.Image);
        Assert.Same(reference, viewModel.PreviewImage);
    }

    [Fact]
    public async Task Current_AI_task_cancellation_propagates_and_keeps_preview_unchanged()
    {
        var manager = new CancellableTaskManager();
        var viewModel = new AiStudioViewModel(
            new CancellableAiService(),
            new StubStudioService(),
            manager,
            new StubSelection(null, null))
        {
            Prompt = "cancel me",
        };

        var submit = viewModel.SubmitAsync();
        await manager.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.CancelCurrent());
        await submit;

        Assert.Null(viewModel.PreviewImage);
        Assert.Contains("cancelled", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
    }

    private static AiStudioViewModel CreateViewModel(
        IAiService? aiService = null,
        IAiStudioService? studioService = null,
        StubSelection? selection = null)
    {
        var manager = new ImmediateTaskManager();
        return new(aiService ?? new RecordingAiService(AiImageResult.Failure(
                AiServiceFailureReason.Unavailable, "AI_OFFLINE")),
            studioService ?? new StubStudioService(), manager,
            selection ?? new StubSelection(null, null));
    }

    private static InternalImage Image(int width, int height) => new(
        width, height, width * 4,
        Enumerable.Repeat((byte)255, width * height * 4).ToArray(),
        new ImageSourceMetadata(ImageSourceFormat.Png, width, height,
            ImageSourceOrientation.Normal, true, false));

    private static WorkspaceTextureItem Texture() => new(
        "texture/hud/pointer.dds", "texture/hud", "pointer.dds", "Pointer", 2, 2, "2 × 2", "BC3",
        "hud", true, TextureState.Original, "Valid", true, true, "crop");

    private sealed class StubSelection(WorkspaceTextureItem? selected, InternalImage? image)
        : IWorkspaceTextureSelection
    {
        public WorkspaceTextureItem? SelectedTexture { get; } = selected;
        public int LoadCount { get; private set; }
        public Task<InternalImage?> LoadSelectedImageAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(image);
        }
        public bool CancelSelectedImageLoading() => false;
    }

    private sealed class StubStudioService : IAiStudioService
    {
        public AiStudioQuoteResult Quote { get; init; } = new(false, "AI_STUDIO_OFFLINE", null);
        public AiStudioHistoryResult HistoryResult { get; init; } = new(false, "AI_STUDIO_OFFLINE", []);
        public Task<AiStudioQuoteResult> GetQuoteAsync(
            AiStudioOperation operation, CancellationToken cancellationToken = default) => Task.FromResult(Quote);
        public Task<AiStudioHistoryResult> GetHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(HistoryResult);
        public Task<AiStudioHistoryResult> CancelJobAsync(
            Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(HistoryResult);
    }

    private sealed class RecordingAiService(AiImageResult result) : IAiService
    {
        public AiGenerateRequest? GenerateRequest { get; private set; }
        public AiEditRequest? EditRequest { get; private set; }
        public Task<AiImageResult> GenerateAsync(AiGenerateRequest request,
            IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        { GenerateRequest = request; return Task.FromResult(result); }
        public Task<AiImageResult> EditAsync(AiEditRequest request,
            IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        { EditRequest = request; return Task.FromResult(result); }
        public Task<AiImageResult> InpaintAsync(AiInpaintRequest request,
            IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(result);
        public Task<AiImageResult> OutpaintAsync(AiOutpaintRequest request,
            IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(result);
        public Task<AiImageResult> RemoveObjectAsync(AiRemoveObjectRequest request,
            IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(result);
        public Task<AiImageResult> ReplaceObjectAsync(AiReplaceObjectRequest request,
            IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(result);
        public Task<AiImageResult> UpscaleAsync(AiUpscaleRequest request,
            IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class CancellableAiService : IAiService
    {
        public async Task<AiImageResult> GenerateAsync(AiGenerateRequest request,
            IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return AiImageResult.CancelledResult(); }
        public Task<AiImageResult> EditAsync(AiEditRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiImageResult> InpaintAsync(AiInpaintRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiImageResult> OutpaintAsync(AiOutpaintRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiImageResult> RemoveObjectAsync(AiRemoveObjectRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiImageResult> ReplaceObjectAsync(AiReplaceObjectRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiImageResult> UpscaleAsync(AiUpscaleRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ImmediateTaskManager : IBackgroundTaskManager
    {
        private BackgroundTaskSnapshot? _snapshot;
        public event EventHandler<BackgroundTaskNotification>? Notification { add { } remove { } }
        public BackgroundTaskKind EnqueuedKind { get; private set; }
        public async ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(
            BackgroundTaskRequest request, CancellationToken cancellationToken = default)
        {
            EnqueuedKind = request.Kind;
            var id = new BackgroundTaskId(Guid.NewGuid());
            var result = await request.Operation(new Progress<BackgroundTaskProgress>(), cancellationToken);
            _snapshot = new(id, request.Kind, result.Succeeded ? BackgroundTaskState.Succeeded : BackgroundTaskState.Failed,
                null, result.DiagnosticCode, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return BackgroundTaskEnqueueResult.Success(id);
        }
        public bool TryCancel(BackgroundTaskId taskId) => false;
        public bool TryGetSnapshot(BackgroundTaskId taskId, out BackgroundTaskSnapshot snapshot)
        { snapshot = _snapshot!; return _snapshot?.TaskId == taskId; }
        public ImmutableArray<BackgroundTaskSnapshot> GetSnapshots() => _snapshot is null ? [] : [_snapshot];
        public Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(
            BackgroundTaskId taskId, CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);
    }

    private sealed class CancellableTaskManager : IBackgroundTaskManager
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly BackgroundTaskId _id = new(Guid.NewGuid());
        private Task<BackgroundTaskExecutionResult>? _execution;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<BackgroundTaskNotification>? Notification { add { } remove { } }
        public ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(
            BackgroundTaskRequest request, CancellationToken cancellationToken = default)
        {
            _execution = Task.Run(async () =>
            {
                Started.TrySetResult();
                return await request.Operation(new Progress<BackgroundTaskProgress>(), _cancellation.Token);
            }, CancellationToken.None);
            return ValueTask.FromResult(BackgroundTaskEnqueueResult.Success(_id));
        }
        public bool TryCancel(BackgroundTaskId taskId)
        { if (taskId != _id) return false; _cancellation.Cancel(); return true; }
        public bool TryGetSnapshot(BackgroundTaskId taskId, out BackgroundTaskSnapshot snapshot)
        { snapshot = null!; return false; }
        public ImmutableArray<BackgroundTaskSnapshot> GetSnapshots() => [];
        public async Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(
            BackgroundTaskId taskId, CancellationToken cancellationToken = default)
        {
            try { await _execution!.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            return new(_id, BackgroundTaskKind.Ai, BackgroundTaskState.Cancelled, null,
                "AI_CANCELLED", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
    }
}
