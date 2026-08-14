using System.Collections.Immutable;
using AuditionModStudio.App.AiStudio;
using AuditionModStudio.App.Shell;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;
using AuditionModStudio.Imaging;

namespace IntegrationTests;

public sealed class AiMaskEditorViewModelTests
{
    [Fact]
    public async Task Initialize_uses_selected_AI_preview_and_exact_source_dimensions()
    {
        var preview = Image(100, 50);
        var (studio, mask) = Create(preview);
        studio.Prompt = "preview";
        await studio.SubmitAsync();

        var initialized = mask.InitializeFromPreview();

        Assert.True(initialized);
        Assert.Equal(100, mask.Mask!.Width);
        Assert.Equal(50, mask.Mask.Height);
        Assert.Same(preview, mask.SourceImage);
    }

    [Fact]
    public async Task Viewport_projection_remains_source_aligned_under_zoom_and_pan()
    {
        var (studio, mask) = Create(Image(100, 50));
        studio.Prompt = "preview";
        await studio.SubmitAsync();
        mask.InitializeFromPreview();

        var centered = mask.ViewportToSource(100, 100, 200, 200);
        mask.Zoom = 2;
        mask.PanBy(20, 10);
        var transformed = mask.ViewportToSource(100, 100, 200, 200);

        Assert.Equal(50, centered!.Value.X, 6);
        Assert.Equal(25, centered.Value.Y, 6);
        Assert.Equal(45, transformed!.Value.X, 6);
        Assert.Equal(22.5, transformed.Value.Y, 6);
    }

    [Fact]
    public async Task Stroke_undo_redo_are_non_destructive_and_history_bounded()
    {
        var (studio, mask) = Create(Image(8, 8));
        studio.Prompt = "preview";
        await studio.SubmitAsync();
        mask.InitializeFromPreview();
        var initial = mask.Mask!;

        Assert.True(mask.ApplySourceStroke([new(4, 4)]));
        var painted = mask.Mask!;
        Assert.True(mask.CanUndo);
        Assert.NotEqual(initial.Opacity, painted.Opacity);
        Assert.True(mask.Undo());
        Assert.True(initial.Opacity.SequenceEqual(mask.Mask!.Opacity));
        Assert.True(mask.Redo());
        Assert.True(painted.Opacity.SequenceEqual(mask.Mask!.Opacity));
        Assert.Contains("MiB", mask.MemoryStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_preview_fails_without_creating_mask()
    {
        var (_, mask) = Create(null);

        Assert.False(mask.InitializeFromPreview());
        Assert.Null(mask.Mask);
        Assert.Contains("Chưa có bản xem trước AI", mask.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_is_explicit_and_delegates_atomic_workspace_store_only_when_project_exists()
    {
        var assetStore = new StubAssetStore();
        var (studio, mask) = Create(Image(4, 4), assetStore, new StubProjectSession(new StubWorkspace()));
        studio.Prompt = "preview";
        await studio.SubmitAsync();
        mask.InitializeFromPreview();

        await mask.SaveAsync();

        Assert.Equal(1, assetStore.SaveCount);
        Assert.Contains("an toàn", mask.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static (AiStudioViewModel Studio, AiMaskEditorViewModel Mask) Create(
        InternalImage? preview,
        IAiMaskAssetStore? assetStore = null,
        IApplicationProjectSession? session = null)
    {
        var manager = new ImmediateTaskManager();
        var studio = new AiStudioViewModel(new StubAiService(preview), new StubStudioService(), manager,
            new StubSelection());
        var mask = new AiMaskEditorViewModel(studio, new AiMaskEditingService(), new EditHistoryService(),
            assetStore ?? new StubAssetStore(), session ?? new StubProjectSession(null));
        return (studio, mask);
    }

    private static InternalImage Image(int width, int height) => new(
        width, height, width * 4, Enumerable.Repeat((byte)255, width * height * 4).ToArray(),
        new(ImageSourceFormat.Png, width, height, ImageSourceOrientation.Normal, true, false));

    private sealed class StubAiService(InternalImage? image) : IAiService
    {
        private AiImageResult Result => image is null
            ? AiImageResult.Failure(AiServiceFailureReason.Unavailable, "AI_OFFLINE")
            : AiImageResult.Success(image);
        public Task<AiImageResult> GenerateAsync(AiGenerateRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);
        public Task<AiImageResult> EditAsync(AiEditRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);
        public Task<AiImageResult> InpaintAsync(AiInpaintRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);
        public Task<AiImageResult> OutpaintAsync(AiOutpaintRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);
        public Task<AiImageResult> RemoveObjectAsync(AiRemoveObjectRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);
        public Task<AiImageResult> ReplaceObjectAsync(AiReplaceObjectRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);
        public Task<AiImageResult> UpscaleAsync(AiUpscaleRequest request, IProgress<AiOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class StubStudioService : IAiStudioService
    {
        public Task<AiStudioQuoteResult> GetQuoteAsync(AiStudioOperation operation,
            CancellationToken cancellationToken = default) => Task.FromResult(new AiStudioQuoteResult(false, "OFFLINE", null));
        public Task<AiStudioHistoryResult> GetHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiStudioHistoryResult(false, "OFFLINE", []));
        public Task<AiStudioHistoryResult> CancelJobAsync(Guid jobId,
            CancellationToken cancellationToken = default) => GetHistoryAsync(cancellationToken);
    }

    private sealed class StubSelection : IWorkspaceTextureSelection
    {
        public WorkspaceTextureItem? SelectedTexture => null;
        public Task<InternalImage?> LoadSelectedImageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<InternalImage?>(null);
        public bool CancelSelectedImageLoading() => false;
    }

    private sealed class StubAssetStore : IAiMaskAssetStore
    {
        public int SaveCount { get; private set; }
        public Task<AiMaskAssetResult> SaveAsync(IProjectArchiveWorkspace workspace, AiMask mask,
            CancellationToken cancellationToken = default)
        { SaveCount++; return Task.FromResult(new AiMaskAssetResult(true, "AI_MASK_SAVED", "ai/masks/current.amsmask")); }
    }

    private sealed class StubProjectSession(IProjectArchiveWorkspace? workspace) : IApplicationProjectSession
    {
        public AuditionProject? Project => null;
        public IProjectArchiveWorkspace? Workspace { get; } = workspace;
        public ValueTask ActivateAsync(AuditionProject project, IProjectArchiveWorkspace value) => ValueTask.CompletedTask;
        public ValueTask<bool> TryUpdateProjectAsync(AuditionProject expectedProject,
            IProjectArchiveWorkspace expectedWorkspace, AuditionProject updatedProject) => ValueTask.FromResult(false);
    }

    private sealed class StubWorkspace : IProjectArchiveWorkspace
    {
        public ProjectArchiveWorkspaceDescriptor Descriptor => throw new NotSupportedException();
        public AuditionModStudio.Core.Archives.ArchiveWorkspace ArchiveWorkspace => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediateTaskManager : IBackgroundTaskManager
    {
        private BackgroundTaskSnapshot? _snapshot;
        public event EventHandler<BackgroundTaskNotification>? Notification { add { } remove { } }
        public async ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(BackgroundTaskRequest request,
            CancellationToken cancellationToken = default)
        {
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
        public Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(BackgroundTaskId taskId,
            CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);
    }
}
