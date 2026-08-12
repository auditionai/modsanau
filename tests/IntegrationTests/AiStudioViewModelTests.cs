using System.Collections.Immutable;
using AuditionModStudio.App.AiStudio;
using AuditionModStudio.App.Shell;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
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

    [Theory]
    [InlineData(AiStudioOperation.Inpaint, true, true)]
    [InlineData(AiStudioOperation.Outpaint, false, true)]
    [InlineData(AiStudioOperation.RemoveObject, true, false)]
    [InlineData(AiStudioOperation.ReplaceObject, true, true)]
    [InlineData(AiStudioOperation.Upscale, false, false)]
    public async Task Plan65_operations_use_durable_studio_execution_and_only_publish_preview(
        AiStudioOperation operation, bool needsMask, bool needsPrompt)
    {
        var source = Image(2, 2);
        var output = Image(4, 4);
        var studio = new StubStudioService { ExecutionResult = new(true, false, "AI_OPERATION_COMPLETED", output) };
        var viewModel = CreateViewModel(studioService: studio,
            selection: new StubSelection(Texture(), source));
        viewModel.SelectedOperation = AiStudioOptions.Operations.Single(option => option.Operation == operation);
        viewModel.Prompt = needsPrompt ? "repair" : string.Empty;
        var mask = needsMask ? new AiMask(2, 2, new byte[] { 0, 255, 255, 0 }) : null;

        await viewModel.SubmitAsync(mask);

        Assert.Equal(operation, studio.ExecutionRequest!.Operation);
        Assert.Equal(needsMask, studio.ExecutionRequest.Mask is not null);
        Assert.Equal(needsPrompt, studio.ExecutionRequest.Prompt is not null);
        Assert.Same(output, viewModel.PreviewImage);
        Assert.Contains("project has not been changed", viewModel.StatusMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mask_dimension_mismatch_stops_before_server_execution()
    {
        var studio = new StubStudioService();
        var viewModel = CreateViewModel(studioService: studio,
            selection: new StubSelection(Texture(), Image(2, 2)));
        viewModel.SelectedOperation = AiStudioOptions.Operations.Single(option =>
            option.Operation == AiStudioOperation.Inpaint);
        viewModel.Prompt = "repair";

        await viewModel.SubmitAsync(new AiMask(1, 1, new byte[] { 255 }));

        Assert.Null(studio.ExecutionRequest);
        Assert.Null(viewModel.PreviewImage);
        Assert.Contains("source-aligned mask", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Outpaint_requires_explicit_bounded_expansion_geometry()
    {
        var studio = new StubStudioService
        {
            ExecutionResult = new(true, false, "AI_OPERATION_COMPLETED", Image(3000, 2500)),
        };
        var texture = Texture() with { Width = 2000, Height = 2000 };
        var viewModel = CreateViewModel(studioService: studio,
            selection: new StubSelection(texture, Image(2000, 2000)));
        viewModel.SelectedOperation = AiStudioOptions.Operations.Single(option =>
            option.Operation == AiStudioOperation.Outpaint);
        viewModel.Prompt = "expand";

        Assert.False(viewModel.CanSubmit);
        viewModel.OutputWidth = "3000";
        viewModel.OutputHeight = "2500";
        Assert.True(viewModel.CanSubmit);
        await viewModel.SubmitAsync();

        Assert.Equal(new AiTargetSize(3000, 2500), studio.ExecutionRequest!.TargetSize);
    }

    [Fact]
    public async Task Explicit_approval_reuses_texture_apply_and_updates_session_only_after_success()
    {
        var preview = Image(4, 4);
        var selection = new StubSelection(Texture(), Image(2, 2));
        var project = CreateProject();
        var workspace = new TestWorkspace();
        var session = new TestSession(project, workspace);
        var apply = new RecordingApplyService(project, preview);
        var viewModel = CreateViewModel(new RecordingAiService(AiImageResult.Success(preview)),
            selection: selection, applyService: apply, session: session);
        viewModel.Prompt = "candidate";
        await viewModel.SubmitAsync();

        Assert.Equal(0, apply.CallCount);
        Assert.True(viewModel.CanApprovePreview);
        Assert.True(await viewModel.ApprovePreviewAsync());

        Assert.Equal(1, apply.CallCount);
        Assert.Same(preview, apply.Request!.ResizeRequest.Source);
        Assert.Equal(ImageResizeMode.Stretch, apply.Request.ResizeRequest.Options.Mode);
        Assert.Equal(2, apply.Request.ResizeRequest.TargetWidth);
        Assert.Equal(2, apply.Request.ResizeRequest.TargetHeight);
        Assert.Equal(1, session.ActivateCount);
        Assert.Equal(1, selection.RefreshCount);
    }

    private static AiStudioViewModel CreateViewModel(
        IAiService? aiService = null,
        IAiStudioService? studioService = null,
        StubSelection? selection = null,
        ITextureApplyService? applyService = null,
        IApplicationProjectSession? session = null)
    {
        var manager = new ImmediateTaskManager();
        return new(aiService ?? new RecordingAiService(AiImageResult.Failure(
                AiServiceFailureReason.Unavailable, "AI_OFFLINE")),
            studioService ?? new StubStudioService(), manager,
            selection ?? new StubSelection(null, null), applyService, session);
    }

    private static InternalImage Image(int width, int height) => new(
        width, height, width * 4,
        Enumerable.Repeat((byte)255, width * height * 4).ToArray(),
        new ImageSourceMetadata(ImageSourceFormat.Png, width, height,
            ImageSourceOrientation.Normal, true, false));

    private static WorkspaceTextureItem Texture() => new(
        "texture/hud/pointer.dds", "texture/hud", "pointer.dds", "Pointer", 2, 2, "2 × 2", "BC3",
        "hud", true, TextureState.Original, "Valid", true, true, "crop");

    private static AuditionProject CreateProject()
    {
        var now = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);
        return AuditionProject.Create(AuditionProject.CurrentSchemaVersion, Guid.NewGuid(), "AI Project",
            new GameId("audition"), new ModId("pointer_mod"), new TemplateIdentity(
                new("archive-015"), new("1"), new(new string('A', 64)), new("audition-vn-2026")),
            new("0123456789abcdef0123456789abcdef", new("Working/015.ab"), new("Extracted/015")),
            [], [], [], new(0, 0, null, []), new(ProjectBuildStatus.NotBuilt, null, null, null),
            now, now).Project!;
    }

    private sealed class StubSelection(WorkspaceTextureItem? selected, InternalImage? image)
        : IWorkspaceTextureSelection
    {
        public WorkspaceTextureItem? SelectedTexture { get; } = selected;
        public int LoadCount { get; private set; }
        public int RefreshCount { get; private set; }
        public Task<InternalImage?> LoadSelectedImageAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(image);
        }
        public bool CancelSelectedImageLoading() => false;
        public Task RefreshAfterApplyAsync(ModRelativePath textureRelativePath,
            CancellationToken cancellationToken = default)
        { RefreshCount++; return Task.CompletedTask; }
    }

    private sealed class RecordingApplyService(AuditionProject project, InternalImage thumbnail)
        : ITextureApplyService
    {
        public int CallCount { get; private set; }
        public TextureApplyRequest? Request { get; private set; }
        public Task<TextureApplyResult> ApplyAsync(TextureApplyRequest request,
            IProgress<TextureApplyProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            return Task.FromResult(TextureApplyResult.Success(project, thumbnail, TextureState.Modified, false));
        }
    }

    private sealed class TestSession(AuditionProject project, IProjectArchiveWorkspace workspace)
        : IApplicationProjectSession
    {
        public AuditionProject? Project { get; private set; } = project;
        public IProjectArchiveWorkspace? Workspace { get; private set; } = workspace;
        public int ActivateCount { get; private set; }
        public ValueTask ActivateAsync(AuditionProject nextProject, IProjectArchiveWorkspace nextWorkspace)
        { Project = nextProject; Workspace = nextWorkspace; ActivateCount++; return ValueTask.CompletedTask; }
        public ValueTask<bool> TryUpdateProjectAsync(AuditionProject expectedProject,
            IProjectArchiveWorkspace expectedWorkspace, AuditionProject updatedProject) => ValueTask.FromResult(false);
    }

    private sealed class TestWorkspace : IProjectArchiveWorkspace
    {
        public ProjectArchiveWorkspaceDescriptor Descriptor => throw new NotSupportedException();
        public ArchiveWorkspace ArchiveWorkspace { get; } = new(null!, "015.ab", "015");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubStudioService : IAiStudioService
    {
        public AiStudioQuoteResult Quote { get; init; } = new(false, "AI_STUDIO_OFFLINE", null);
        public AiStudioHistoryResult HistoryResult { get; init; } = new(false, "AI_STUDIO_OFFLINE", []);
        public AiStudioExecutionResult ExecutionResult { get; init; } =
            new(false, false, "AI_STUDIO_OFFLINE", null);
        public AiStudioExecutionRequest? ExecutionRequest { get; private set; }
        public Task<AiStudioQuoteResult> GetQuoteAsync(
            AiStudioOperation operation, CancellationToken cancellationToken = default) => Task.FromResult(Quote);
        public Task<AiStudioHistoryResult> GetHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(HistoryResult);
        public Task<AiStudioHistoryResult> CancelJobAsync(
            Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(HistoryResult);
        public Task<AiStudioExecutionResult> ExecuteAsync(AiStudioExecutionRequest request,
            IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            ExecutionRequest = request;
            return Task.FromResult(ExecutionResult);
        }
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
