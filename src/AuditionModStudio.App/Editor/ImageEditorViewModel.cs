using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.Images;

namespace AuditionModStudio.App.Editor;

public sealed class ImageEditorViewModel : INotifyPropertyChanged
{
    private readonly IWorkspaceTextureSelection _selection;
    private readonly IImageTransformService _transformService;
    private readonly IImageResizeService _resizeService;
    private InternalImage? _sourceImage;
    private InternalImage? _afterImage;
    private InteractiveImageTransformState? _transform;
    private EditorResizeModeOption _selectedMode = ImageEditorModes.Supported[0];
    private ImageCompareModeOption _selectedCompareMode = ImageCompareModes.Supported[0];
    private ImageCompareToggleState _compareToggleState = ImageCompareToggleState.After;
    private string _loadedRelativePath = string.Empty;
    private string _statusMessage = "Select a project texture before opening the Image Editor.";
    private string _afterPreviewStatus = "Live preview is unavailable until a texture is loaded.";
    private bool _isLoading;
    private bool _isAfterPreviewLoading;
    private bool _showCheckerboard = true;
    private double _compareDivider = 0.5;
    private double _compareZoom = 1;
    private ViewportVector _comparePan;
    private int _transformRevision;
    private int _compareRevision;
    private long _activationVersion;
    private long _afterPreviewVersion;
    private CancellationTokenSource? _afterPreviewCancellation;
    private Task _afterPreviewTask = Task.CompletedTask;

    public ImageEditorViewModel(
        IWorkspaceTextureSelection selection,
        IImageTransformService transformService,
        IImageResizeService resizeService)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _transformService = transformService ?? throw new ArgumentNullException(nameof(transformService));
        _resizeService = resizeService ?? throw new ArgumentNullException(nameof(resizeService));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<EditorResizeModeOption> Modes => ImageEditorModes.Supported;

    public EditorResizeModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is null || !ImageEditorModes.Supported.Contains(value) || value == _selectedMode)
            {
                return;
            }

            _selectedMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeDescription));
            AdvanceTransformRevision();
            StartAfterPreviewRefresh();
        }
    }

    public InternalImage? BeforeImage => _sourceImage;

    public InternalImage? SourceImage => _sourceImage;

    public InternalImage? AfterImage => _afterImage;

    public IReadOnlyList<ImageCompareModeOption> CompareModes => ImageCompareModes.Supported;

    public ImageCompareModeOption SelectedCompareMode
    {
        get => _selectedCompareMode;
        set
        {
            if (value is null || !ImageCompareModes.Supported.Contains(value) || value == _selectedCompareMode)
            {
                return;
            }

            _selectedCompareMode = value;
            OnPropertyChanged();
            AdvanceCompareRevision();
        }
    }

    public ImageCompareToggleState CompareToggleState => _compareToggleState;

    public bool ShowCheckerboard => _showCheckerboard;

    public double CompareDivider => _compareDivider;

    public double CompareZoom => _compareZoom;

    public ViewportVector ComparePan => _comparePan;

    public string ComparePanSummary => $"X {_comparePan.X:0.#}, Y {_comparePan.Y:0.#}";

    public string AfterPreviewStatus => _afterPreviewStatus;

    public bool IsAfterPreviewLoading => _isAfterPreviewLoading;

    public int CompareRevision => _compareRevision;

    public bool IsLoading => _isLoading;

    public bool CanCancel => _isLoading;

    public bool HasImage => _sourceImage is not null && _transform is not null;

    public string TextureName => _selection.SelectedTexture?.DisplayName ?? "No texture selected";

    public int TargetWidth => _selection.SelectedTexture?.Width ?? 0;

    public int TargetHeight => _selection.SelectedTexture?.Height ?? 0;

    public string TargetDimensions => HasImage ? $"{TargetWidth} × {TargetHeight} px" : "—";

    public string StatusMessage => _statusMessage;

    public double Zoom => _transform?.Zoom ?? 1;

    public string PanSummary => _transform is null
        ? "—"
        : $"X {_transform.Pan.X:0.#}, Y {_transform.Pan.Y:0.#}";

    public string CropSummary
    {
        get
        {
            if (_transform is null)
            {
                return "—";
            }

            var crop = _transformService.ToPixelCrop(_transform);
            return crop.Succeeded && crop.Value is { } value
                ? $"{value.X}, {value.Y} · {value.Width} × {value.Height} px"
                : "Crop unavailable";
        }
    }

    public string ModeDescription => _selectedMode.Mode switch
    {
        ImageResizeMode.ManualCrop => "Crop the selected area into the exact target frame.",
        ImageResizeMode.Fit => "Fit the whole image inside the target and preserve aspect ratio.",
        ImageResizeMode.Fill => "Fill the target while preserving aspect ratio; overflow is cropped.",
        ImageResizeMode.Stretch => "Stretch the image to the exact target dimensions.",
        ImageResizeMode.CanvasResize => "Keep image scale and resize the surrounding canvas.",
        ImageResizeMode.TransparentPadding => "Add transparent padding without scaling the image.",
        _ => "Unsupported preview mode."
    };

    public int TransformRevision => _transformRevision;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        var activationVersion = Interlocked.Increment(ref _activationVersion);
        var selected = _selection.SelectedTexture;
        if (selected is null)
        {
            ResetEditor("Select a project texture before opening the Image Editor.");
            return;
        }

        if (_sourceImage is not null
            && string.Equals(_loadedRelativePath, selected.RelativePath, StringComparison.Ordinal))
        {
            RaiseSelectionProperties();
            return;
        }

        PrepareForSelectionLoad();
        SetLoading(true, "Loading the selected texture through the verified preview pipeline.");
        var image = await _selection.LoadSelectedImageAsync(cancellationToken);
        if (activationVersion != Volatile.Read(ref _activationVersion))
        {
            return;
        }

        if (image is null || !ReferenceEquals(selected, _selection.SelectedTexture))
        {
            ResetEditor("The selected texture could not be loaded. Return to Projects and try again.");
            return;
        }

        var create = _transformService.Create(image);
        if (!create.Succeeded || create.Value is null)
        {
            ResetEditor("The editor transform could not be initialized for this texture.");
            return;
        }

        _sourceImage = image;
        _transform = create.Value;
        _compareZoom = 1;
        _comparePan = default;
        _loadedRelativePath = selected.RelativePath;
        SetLoading(false, "Texture ready. Changes remain a preview until a later Apply workflow.");
        RaiseSelectionProperties();
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(BeforeImage));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PanSummary));
        OnPropertyChanged(nameof(CropSummary));
        AdvanceTransformRevision();
        AdvanceCompareRevision();
        await RefreshAfterPreviewAsync();
    }

    public bool CancelLoading() => _selection.CancelSelectedImageLoading();

    public void Unload()
    {
        Interlocked.Increment(ref _activationVersion);
        CancelAfterPreview();
        if (_isLoading)
        {
            _selection.CancelSelectedImageLoading();
        }

        ResetEditor("Editor preview unloaded. Select a texture and reopen the Image Editor to continue.");
    }

    public bool SetZoom(double zoom)
    {
        if (_transform is null)
        {
            return false;
        }

        var result = _transformService.SetViewportTransform(_transform, zoom, _transform.Pan);
        return PublishTransform(result);
    }

    public bool PanBy(double horizontal, double vertical)
    {
        if (_transform is null || !double.IsFinite(horizontal) || !double.IsFinite(vertical))
        {
            return false;
        }

        var pan = new ViewportVector(_transform.Pan.X + horizontal, _transform.Pan.Y + vertical);
        return PublishTransform(_transformService.SetViewportTransform(_transform, _transform.Zoom, pan));
    }

    public bool SetCropPercent(double x, double y, double width, double height)
    {
        if (_transform is null)
        {
            return false;
        }

        var requested = new NormalizedImageRectangle(x / 100, y / 100, width / 100, height / 100);
        return PublishTransform(_transformService.SetCrop(_transform, requested));
    }

    public bool ResetTransform()
    {
        if (_transform is null)
        {
            return false;
        }

        return PublishTransform(_transformService.Reset(_transform));
    }

    public bool SetCompareDivider(double divider)
    {
        if (!double.IsFinite(divider))
        {
            return false;
        }

        var normalized = Math.Clamp(divider, 0, 1);
        if (_compareDivider == normalized)
        {
            return true;
        }

        _compareDivider = normalized;
        OnPropertyChanged(nameof(CompareDivider));
        AdvanceCompareRevision();
        return true;
    }

    public bool SetCompareToggleState(ImageCompareToggleState state)
    {
        if (!Enum.IsDefined(state))
        {
            return false;
        }

        if (_compareToggleState == state)
        {
            return true;
        }

        _compareToggleState = state;
        OnPropertyChanged(nameof(CompareToggleState));
        AdvanceCompareRevision();
        return true;
    }

    public void SetCheckerboard(bool visible)
    {
        if (_showCheckerboard == visible)
        {
            return;
        }

        _showCheckerboard = visible;
        OnPropertyChanged(nameof(ShowCheckerboard));
        AdvanceCompareRevision();
    }

    public bool SetCompareZoom(double zoom)
    {
        if (!double.IsFinite(zoom) || zoom < 0.1 || zoom > 8)
        {
            return false;
        }

        _compareZoom = zoom;
        OnPropertyChanged(nameof(CompareZoom));
        AdvanceCompareRevision();
        return true;
    }

    public bool PanCompareBy(double horizontal, double vertical)
    {
        if (!double.IsFinite(horizontal) || !double.IsFinite(vertical))
        {
            return false;
        }

        _comparePan = new ViewportVector(_comparePan.X + horizontal, _comparePan.Y + vertical);
        OnPropertyChanged(nameof(ComparePan));
        OnPropertyChanged(nameof(ComparePanSummary));
        AdvanceCompareRevision();
        return true;
    }

    public void ResetCompareCamera()
    {
        _compareZoom = 1;
        _comparePan = default;
        OnPropertyChanged(nameof(CompareZoom));
        OnPropertyChanged(nameof(ComparePan));
        OnPropertyChanged(nameof(ComparePanSummary));
        AdvanceCompareRevision();
    }

    public EditorCanvasRectangle? GetCompareProjection(
        InternalImage image,
        double viewportWidth,
        double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!double.IsFinite(viewportWidth)
            || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0
            || viewportHeight <= 0)
        {
            return null;
        }

        var created = _transformService.Create(image);
        if (!created.Succeeded || created.Value is null)
        {
            return null;
        }

        var camera = _transformService.SetViewportTransform(
            created.Value,
            _compareZoom,
            _comparePan);
        if (!camera.Succeeded || camera.Value is null)
        {
            return null;
        }

        var viewport = new ViewportSize(viewportWidth, viewportHeight);
        var topLeft = _transformService.MapImageToViewport(
            camera.Value,
            viewport,
            new ImagePixelPoint(0, 0));
        var bottomRight = _transformService.MapImageToViewport(
            camera.Value,
            viewport,
            new ImagePixelPoint(image.Width, image.Height));
        return topLeft.Succeeded && bottomRight.Succeeded
            ? ToRectangle(topLeft.Value!, bottomRight.Value!)
            : null;
    }

    public Task WaitForAfterPreviewAsync() => _afterPreviewTask;

    public ImageResizeRequest? CreateResizeRequest()
    {
        if (_sourceImage is null || _transform is null || TargetWidth <= 0 || TargetHeight <= 0)
        {
            return null;
        }

        if (_selectedMode.Mode == ImageResizeMode.ManualCrop)
        {
            var manual = _transformService.ToManualCropResizeRequest(
                _transform,
                _sourceImage,
                TargetWidth,
                TargetHeight);
            return manual.Succeeded ? manual.Value : null;
        }

        return new ImageResizeRequest(
            _sourceImage,
            TargetWidth,
            TargetHeight,
            new ImageResizeOptions(_selectedMode.Mode));
    }

    public EditorCanvasProjection? GetProjection(double viewportWidth, double viewportHeight)
    {
        if (_transform is null
            || !double.IsFinite(viewportWidth)
            || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0
            || viewportHeight <= 0)
        {
            return null;
        }

        if (_selectedMode.Mode == ImageResizeMode.Stretch)
        {
            var stretchCrop = _transform.Crop;
            return new EditorCanvasProjection(
                new EditorCanvasRectangle(0, 0, viewportWidth, viewportHeight),
                new EditorCanvasRectangle(
                    stretchCrop.X * viewportWidth,
                    stretchCrop.Y * viewportHeight,
                    stretchCrop.Width * viewportWidth,
                    stretchCrop.Height * viewportHeight));
        }

        if (_selectedMode.Mode is ImageResizeMode.CanvasResize or ImageResizeMode.TransparentPadding)
        {
            var targetScale = viewportWidth / TargetWidth;
            var imageWidth = _transform.ImageWidth * targetScale * _transform.Zoom;
            var imageHeight = _transform.ImageHeight * targetScale * _transform.Zoom;
            var canvasImageBounds = new EditorCanvasRectangle(
                (viewportWidth - imageWidth) / 2 + _transform.Pan.X,
                (viewportHeight - imageHeight) / 2 + _transform.Pan.Y,
                imageWidth,
                imageHeight);
            return new EditorCanvasProjection(
                canvasImageBounds,
                ProjectCrop(canvasImageBounds, _transform.Crop));
        }

        var viewport = new ViewportSize(viewportWidth, viewportHeight);
        var imageTopLeft = _transformService.MapImageToViewport(_transform, viewport, new ImagePixelPoint(0, 0));
        var imageBottomRight = _transformService.MapImageToViewport(
            _transform,
            viewport,
            new ImagePixelPoint(_transform.ImageWidth, _transform.ImageHeight));
        if (!imageTopLeft.Succeeded || !imageBottomRight.Succeeded)
        {
            return null;
        }

        var imageBounds = ToRectangle(imageTopLeft.Value!, imageBottomRight.Value!);
        if (_selectedMode.Mode == ImageResizeMode.Fill)
        {
            var fillScale = Math.Max(
                imageBounds.Width > 0 ? viewportWidth / imageBounds.Width : 1,
                imageBounds.Height > 0 ? viewportHeight / imageBounds.Height : 1);
            if (fillScale > 1)
            {
                var filledWidth = imageBounds.Width * fillScale;
                var filledHeight = imageBounds.Height * fillScale;
                imageBounds = new EditorCanvasRectangle(
                    imageBounds.X - (filledWidth - imageBounds.Width) / 2,
                    imageBounds.Y - (filledHeight - imageBounds.Height) / 2,
                    filledWidth,
                    filledHeight);
            }
        }

        return new EditorCanvasProjection(imageBounds, ProjectCrop(imageBounds, _transform.Crop));
    }

    private bool PublishTransform(ImageTransformResult<InteractiveImageTransformState> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            _statusMessage = "That transform is outside the supported image bounds.";
            OnPropertyChanged(nameof(StatusMessage));
            return false;
        }

        _transform = result.Value;
        _statusMessage = "Preview transform updated. No project files were changed.";
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PanSummary));
        OnPropertyChanged(nameof(CropSummary));
        AdvanceTransformRevision();
        StartAfterPreviewRefresh();
        return true;
    }

    private void ResetEditor(string message)
    {
        CancelAfterPreview();
        _sourceImage = null;
        _afterImage = null;
        _transform = null;
        _compareZoom = 1;
        _comparePan = default;
        _loadedRelativePath = string.Empty;
        _afterPreviewStatus = "Live preview is unavailable until a texture is loaded.";
        SetLoading(false, message);
        RaiseSelectionProperties();
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(BeforeImage));
        OnPropertyChanged(nameof(AfterImage));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PanSummary));
        OnPropertyChanged(nameof(CropSummary));
        OnPropertyChanged(nameof(CompareZoom));
        OnPropertyChanged(nameof(ComparePan));
        OnPropertyChanged(nameof(ComparePanSummary));
        OnPropertyChanged(nameof(AfterPreviewStatus));
        AdvanceCompareRevision();
        AdvanceTransformRevision();
    }

    private void PrepareForSelectionLoad()
    {
        CancelAfterPreview();
        _sourceImage = null;
        _afterImage = null;
        _transform = null;
        _loadedRelativePath = string.Empty;
        _afterPreviewStatus = "Waiting for the selected texture baseline.";
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(BeforeImage));
        OnPropertyChanged(nameof(AfterImage));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(AfterPreviewStatus));
        AdvanceCompareRevision();
        AdvanceTransformRevision();
    }

    private void StartAfterPreviewRefresh() => _afterPreviewTask = RefreshAfterPreviewAsync();

    private async Task RefreshAfterPreviewAsync()
    {
        var request = CreateResizeRequest();
        if (request is null)
        {
            return;
        }

        CancelAfterPreview();
        var version = Interlocked.Increment(ref _afterPreviewVersion);
        var cancellation = new CancellationTokenSource();
        _afterPreviewCancellation = cancellation;
        _isAfterPreviewLoading = true;
        _afterPreviewStatus = "Generating live After preview…";
        OnPropertyChanged(nameof(IsAfterPreviewLoading));
        OnPropertyChanged(nameof(AfterPreviewStatus));

        try
        {
            var result = await Task.Run(
                () => _resizeService.ResizeAsync(request, cancellation.Token),
                cancellation.Token);
            if (version != Volatile.Read(ref _afterPreviewVersion)
                || cancellation.IsCancellationRequested
                || !ReferenceEquals(request.Source, _sourceImage))
            {
                return;
            }

            if (!result.Succeeded || result.Image is null)
            {
                _afterImage = null;
                _afterPreviewStatus = result.Cancelled
                    ? "After preview generation was cancelled."
                    : $"After preview is unavailable ({result.DiagnosticCode ?? "unknown error"}).";
                OnPropertyChanged(nameof(AfterImage));
                OnPropertyChanged(nameof(AfterPreviewStatus));
                AdvanceCompareRevision();
                return;
            }

            _afterImage = result.Image;
            _afterPreviewStatus = "Live After preview ready. Compare remains read-only.";
            OnPropertyChanged(nameof(AfterImage));
            OnPropertyChanged(nameof(AfterPreviewStatus));
            AdvanceCompareRevision();
        }
        catch (OperationCanceledException)
        {
            if (version == Volatile.Read(ref _afterPreviewVersion))
            {
                _afterPreviewStatus = "After preview generation was cancelled.";
                OnPropertyChanged(nameof(AfterPreviewStatus));
            }
        }
        catch (Exception exception)
        {
            if (version == Volatile.Read(ref _afterPreviewVersion))
            {
                _afterImage = null;
                _afterPreviewStatus = $"After preview failed ({exception.GetType().Name}).";
                OnPropertyChanged(nameof(AfterImage));
                OnPropertyChanged(nameof(AfterPreviewStatus));
                AdvanceCompareRevision();
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _afterPreviewVersion))
            {
                _isAfterPreviewLoading = false;
                OnPropertyChanged(nameof(IsAfterPreviewLoading));
            }

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _afterPreviewCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void CancelAfterPreview()
    {
        Interlocked.Increment(ref _afterPreviewVersion);
        var cancellation = Interlocked.Exchange(ref _afterPreviewCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        if (_isAfterPreviewLoading)
        {
            _isAfterPreviewLoading = false;
            OnPropertyChanged(nameof(IsAfterPreviewLoading));
        }
    }

    private void SetLoading(bool loading, string message)
    {
        _isLoading = loading;
        _statusMessage = message;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void RaiseSelectionProperties()
    {
        OnPropertyChanged(nameof(TextureName));
        OnPropertyChanged(nameof(TargetWidth));
        OnPropertyChanged(nameof(TargetHeight));
        OnPropertyChanged(nameof(TargetDimensions));
    }

    private void AdvanceTransformRevision()
    {
        _transformRevision++;
        OnPropertyChanged(nameof(TransformRevision));
    }

    private void AdvanceCompareRevision()
    {
        _compareRevision++;
        OnPropertyChanged(nameof(CompareRevision));
    }

    private static EditorCanvasRectangle ToRectangle(ViewportPoint first, ViewportPoint second) =>
        new(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y));

    private static EditorCanvasRectangle ProjectCrop(
        EditorCanvasRectangle imageBounds,
        NormalizedImageRectangle crop) => new(
            imageBounds.X + crop.X * imageBounds.Width,
            imageBounds.Y + crop.Y * imageBounds.Height,
            crop.Width * imageBounds.Width,
            crop.Height * imageBounds.Height);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
