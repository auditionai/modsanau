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
        var viewModel = new ImageEditorViewModel(selection, new ImageTransformService());

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
        var viewModel = new ImageEditorViewModel(selection, new ImageTransformService());
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
        var viewModel = new ImageEditorViewModel(selection, new ImageTransformService());
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
        var viewModel = new ImageEditorViewModel(selection, new ImageTransformService());

        Assert.True(viewModel.CancelLoading());
        Assert.Equal(1, selection.CancelCount);
    }

    [Fact]
    public async Task Unload_releases_full_image_preview_state()
    {
        var selection = new TestSelection(TextureItem(16, 8), Image(16, 8));
        var viewModel = new ImageEditorViewModel(selection, new ImageTransformService());
        await viewModel.ActivateAsync();

        viewModel.Unload();

        Assert.False(viewModel.HasImage);
        Assert.Null(viewModel.SourceImage);
    }

    [Fact]
    public async Task Completed_load_is_not_published_after_editor_was_unloaded()
    {
        var selection = new DeferredSelection(TextureItem(16, 8));
        var viewModel = new ImageEditorViewModel(selection, new ImageTransformService());
        var activation = viewModel.ActivateAsync();

        viewModel.Unload();
        selection.Complete(Image(16, 8));
        await activation;

        Assert.False(viewModel.HasImage);
        Assert.Null(viewModel.SourceImage);
    }

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

    private static InternalImage Image(int width, int height) => new(
        width,
        height,
        checked(width * 4),
        Enumerable.Repeat((byte)255, checked(width * height * 4)).ToArray(),
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
}
