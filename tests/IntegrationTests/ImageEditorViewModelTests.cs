using AuditionModStudio.App.Editor;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Imaging;

namespace IntegrationTests;

public sealed class ImageEditorViewModelTests
{
    [Fact]
    public async Task Missing_selection_is_an_explicit_empty_state()
    {
        var selection = new TestSelection(null, null);
        var viewModel = CreateViewModel(selection);

        await viewModel.ActivateAsync();

        Assert.False(viewModel.HasImage);
        Assert.Contains("Select a project texture", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, selection.LoadCount);
    }

    [Fact]
    public async Task Supported_modes_create_typed_requests_with_exact_target_dimensions()
    {
        var image = Image(378, 126);
        var selection = new TestSelection(TextureItem(378, 126), image);
        var viewModel = CreateViewModel(selection);
        await viewModel.ActivateAsync();

        Assert.True(viewModel.HasImage);
        Assert.Equal("378 × 126 px", viewModel.TargetDimensions);
        Assert.Equal(
            [
                ImageResizeMode.ManualCrop,
                ImageResizeMode.Fit,
                ImageResizeMode.Fill,
                ImageResizeMode.Stretch,
                ImageResizeMode.CanvasResize,
                ImageResizeMode.TransparentPadding
            ],
            viewModel.Modes.Select(option => option.Mode));

        foreach (var mode in viewModel.Modes)
        {
            viewModel.SelectedMode = mode;
            var request = Assert.IsType<ImageResizeRequest>(viewModel.CreateResizeRequest());
            Assert.Equal(378, request.TargetWidth);
            Assert.Equal(126, request.TargetHeight);
            Assert.Equal(mode.Mode, request.Options.Mode);
        }
    }

    [Fact]
    public async Task Zoom_pan_crop_and_reset_use_transform_service_state()
    {
        var selection = new TestSelection(TextureItem(400, 200), Image(400, 200));
        var viewModel = CreateViewModel(selection);
        await viewModel.ActivateAsync();

        Assert.True(viewModel.SetZoom(2));
        Assert.True(viewModel.PanBy(25, -10));
        Assert.True(viewModel.SetCropPercent(25, 25, 50, 50));
        var cropRequest = Assert.IsType<ImageResizeRequest>(viewModel.CreateResizeRequest());
        Assert.Equal(new ImageCropRectangle(100, 50, 200, 100), cropRequest.Options.CropRectangle);
        Assert.NotNull(viewModel.GetProjection(800, 400));

        Assert.True(viewModel.ResetTransform());
        Assert.Equal(1, viewModel.Zoom);
        Assert.Contains("400 × 200", viewModel.CropSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_is_delegated_to_background_selection_source()
    {
        var selection = new TestSelection(TextureItem(1, 1), Image(1, 1)) { CancelResult = true };
        var viewModel = CreateViewModel(selection);

        Assert.True(viewModel.CancelLoading());
        Assert.Equal(1, selection.CancelCount);
    }

    [Fact]
    public async Task Unload_releases_full_image_preview_state()
    {
        var selection = new TestSelection(TextureItem(16, 8), Image(16, 8));
        var viewModel = CreateViewModel(selection);
        await viewModel.ActivateAsync();

        viewModel.Unload();

        Assert.False(viewModel.HasImage);
        Assert.Null(viewModel.SourceImage);
    }

    [Fact]
    public async Task Completed_load_is_not_published_after_editor_was_unloaded()
    {
        var selection = new DeferredSelection(TextureItem(16, 8));
        var viewModel = CreateViewModel(selection);
        var activation = viewModel.ActivateAsync();

        viewModel.Unload();
        selection.Complete(Image(16, 8));
        await activation;

        Assert.False(viewModel.HasImage);
        Assert.Null(viewModel.SourceImage);
    }

    [Fact]
    public async Task Editor_load_baseline_is_reused_and_live_after_does_not_mutate_it()
    {
        var baseline = Image(4, 2, 17);
        var selection = new TestSelection(TextureItem(2, 2), baseline);
        var viewModel = CreateViewModel(selection);

        await viewModel.ActivateAsync();

        Assert.Same(baseline, viewModel.BeforeImage);
        Assert.Same(baseline, viewModel.SourceImage);
        Assert.NotNull(viewModel.AfterImage);
        Assert.NotSame(baseline, viewModel.AfterImage);
        Assert.All(baseline.Pixels, pixel => Assert.Equal(17, pixel));
        Assert.Equal(1, selection.LoadCount);
    }

    [Fact]
    public async Task Compare_modes_divider_toggle_checkerboard_and_camera_are_presentation_only()
    {
        var resize = new RecordingResizeService();
        var selection = new TestSelection(TextureItem(4, 2), Image(4, 2));
        var viewModel = CreateViewModel(selection, resize);
        await viewModel.ActivateAsync();
        var generationCount = resize.CallCount;

        foreach (var mode in viewModel.CompareModes)
        {
            viewModel.SelectedCompareMode = mode;
            Assert.Equal(mode, viewModel.SelectedCompareMode);
        }

        Assert.True(viewModel.SetCompareDivider(0));
        Assert.Equal(0, viewModel.CompareDivider);
        Assert.True(viewModel.SetCompareDivider(0.5));
        Assert.Equal(0.5, viewModel.CompareDivider);
        Assert.True(viewModel.SetCompareDivider(1));
        Assert.Equal(1, viewModel.CompareDivider);
        Assert.False(viewModel.SetCompareDivider(double.NaN));
        Assert.Equal(1, viewModel.CompareDivider);
        Assert.True(viewModel.SetCompareDivider(2));
        Assert.Equal(1, viewModel.CompareDivider);

        Assert.True(viewModel.SetCompareToggleState(ImageCompareToggleState.Before));
        Assert.True(viewModel.SetCompareToggleState(ImageCompareToggleState.After));
        viewModel.SetCheckerboard(false);
        Assert.False(viewModel.ShowCheckerboard);
        Assert.True(viewModel.SetCompareZoom(2));
        Assert.True(viewModel.PanCompareBy(12, -7));
        Assert.Equal(2, viewModel.CompareZoom);
        Assert.Equal(new ViewportVector(12, -7), viewModel.ComparePan);
        Assert.NotNull(viewModel.GetCompareProjection(viewModel.BeforeImage!, 800, 400));
        Assert.NotNull(viewModel.GetCompareProjection(viewModel.AfterImage!, 800, 400));
        Assert.Equal(generationCount, resize.CallCount);
        Assert.Equal(1, selection.LoadCount);
    }

    [Fact]
    public async Task Edit_changes_refresh_after_without_replacing_immutable_before()
    {
        var baseline = Image(8, 4, 29);
        var resize = new RecordingResizeService();
        var viewModel = CreateViewModel(new TestSelection(TextureItem(4, 2), baseline), resize);
        await viewModel.ActivateAsync();
        var initialAfter = viewModel.AfterImage;

        Assert.True(viewModel.SetCropPercent(25, 0, 50, 100));
        await viewModel.WaitForAfterPreviewAsync();

        Assert.Same(baseline, viewModel.BeforeImage);
        Assert.NotSame(initialAfter, viewModel.AfterImage);
        Assert.Equal(2, resize.CallCount);
    }

    [Fact]
    public async Task Stale_after_preview_cannot_publish_over_latest_edit()
    {
        var resize = new DeferredResizeService();
        var viewModel = CreateViewModel(
            new TestSelection(TextureItem(4, 2), Image(8, 4)),
            resize);
        await viewModel.ActivateAsync();

        viewModel.SelectedMode = viewModel.Modes.Single(option => option.Mode == ImageResizeMode.Fit);
        await resize.WaitUntilRequestedAsync(ImageResizeMode.Fit);
        viewModel.SelectedMode = viewModel.Modes.Single(option => option.Mode == ImageResizeMode.Fill);
        await resize.WaitUntilRequestedAsync(ImageResizeMode.Fill);

        var latest = Image(4, 2, 71);
        resize.Complete(ImageResizeMode.Fill, latest);
        await viewModel.WaitForAfterPreviewAsync();
        resize.Complete(ImageResizeMode.Fit, Image(4, 2, 39));
        await resize.WaitForAllCompletionsAsync();

        Assert.Same(latest, viewModel.AfterImage);
    }

    [Fact]
    public async Task Unload_releases_before_and_after_compare_resources()
    {
        var viewModel = CreateViewModel(
            new TestSelection(TextureItem(4, 2), Image(4, 2)));
        await viewModel.ActivateAsync();

        viewModel.Unload();

        Assert.Null(viewModel.BeforeImage);
        Assert.Null(viewModel.AfterImage);
        Assert.False(viewModel.HasImage);
    }

    private static ImageEditorViewModel CreateViewModel(
        IWorkspaceTextureSelection selection,
        IImageResizeService? resizeService = null) => new(
        selection,
        new ImageTransformService(),
        resizeService ?? new ImageResizeService(ImageImportResourcePolicy.Default));

    private static WorkspaceTextureItem TextureItem(int width, int height) => new(
        "interface/texture.dds",
        "interface",
        "texture.dds",
        "Texture",
        width,
        height,
        $"{width} × {height}",
        "BC3",
        "interface",
        true,
        TextureState.Original,
        "Valid",
        true,
        true,
        "crop");

    private static InternalImage Image(int width, int height, byte value = 255) => new(
        width,
        height,
        checked(width * 4),
        Enumerable.Repeat(value, checked(width * height * 4)).ToArray(),
        new ImageSourceMetadata(
            ImageSourceFormat.Png,
            width,
            height,
            ImageSourceOrientation.Normal,
            true,
            false));

    private sealed class TestSelection(
        WorkspaceTextureItem? selectedTexture,
        InternalImage? image) : IWorkspaceTextureSelection
    {
        public WorkspaceTextureItem? SelectedTexture { get; } = selectedTexture;
        public int LoadCount { get; private set; }
        public int CancelCount { get; private set; }
        public bool CancelResult { get; init; }

        public Task<InternalImage?> LoadSelectedImageAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(image);
        }

        public bool CancelSelectedImageLoading()
        {
            CancelCount++;
            return CancelResult;
        }
    }

    private sealed class DeferredSelection(WorkspaceTextureItem selectedTexture) : IWorkspaceTextureSelection
    {
        private readonly TaskCompletionSource<InternalImage?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkspaceTextureItem? SelectedTexture { get; } = selectedTexture;

        public Task<InternalImage?> LoadSelectedImageAsync(CancellationToken cancellationToken = default) =>
            _completion.Task;

        public bool CancelSelectedImageLoading() => true;

        public void Complete(InternalImage image) => _completion.TrySetResult(image);
    }

    private sealed class RecordingResizeService : IImageResizeService
    {
        private readonly ImageResizeService _inner = new(ImageImportResourcePolicy.Default);

        public int CallCount { get; private set; }

        public Task<ImageResizeResult> ResizeAsync(
            ImageResizeRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _inner.ResizeAsync(request, cancellationToken);
        }
    }

    private sealed class DeferredResizeService : IImageResizeService
    {
        private readonly Dictionary<ImageResizeMode, TaskCompletionSource<ImageResizeResult>> _requests = [];
        private readonly Dictionary<ImageResizeMode, TaskCompletionSource> _started = [];
        private readonly List<Task> _completions = [];

        public Task<ImageResizeResult> ResizeAsync(
            ImageResizeRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Options.Mode == ImageResizeMode.ManualCrop)
            {
                return Task.FromResult(ImageResizeResult.Success(Image(request.TargetWidth, request.TargetHeight, 11)));
            }

            lock (_requests)
            {
                var completion = new TaskCompletionSource<ImageResizeResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _requests[request.Options.Mode] = completion;
                _completions.Add(completion.Task);
                if (!_started.TryGetValue(request.Options.Mode, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _started[request.Options.Mode] = started;
                }

                started.TrySetResult();
                return completion.Task;
            }
        }

        public Task WaitUntilRequestedAsync(ImageResizeMode mode)
        {
            lock (_requests)
            {
                if (_requests.ContainsKey(mode))
                {
                    return Task.CompletedTask;
                }

                if (!_started.TryGetValue(mode, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _started[mode] = started;
                }

                return started.Task;
            }
        }

        public void Complete(ImageResizeMode mode, InternalImage image)
        {
            lock (_requests)
            {
                _requests[mode].TrySetResult(ImageResizeResult.Success(image));
            }
        }

        public Task WaitForAllCompletionsAsync()
        {
            lock (_requests)
            {
                return Task.WhenAll(_completions);
            }
        }
    }
}
