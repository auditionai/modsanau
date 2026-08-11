using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.Images;

namespace AuditionModStudio.App.Editor;

public sealed class ImageEditorViewModel : INotifyPropertyChanged
{
    private readonly IWorkspaceTextureSelection _selection;
    private readonly IImageTransformService _transformService;
    private InternalImage? _sourceImage;
    private InteractiveImageTransformState? _transform;
    private EditorResizeModeOption _selectedMode = ImageEditorModes.Supported[0];
    private string _loadedRelativePath = string.Empty;
    private string _statusMessage = "Select a project texture before opening the Image Editor.";
    private bool _isLoading;
    private int _transformRevision;
    private long _activationVersion;

    public ImageEditorViewModel(
        IWorkspaceTextureSelection selection,
        IImageTransformService transformService)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _transformService = transformService ?? throw new ArgumentNullException(nameof(transformService));
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
        }
    }

    public InternalImage? SourceImage => _sourceImage;

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
        _loadedRelativePath = selected.RelativePath;
        SetLoading(false, "Texture ready. Changes remain a preview until a later Apply workflow.");
        RaiseSelectionProperties();
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PanSummary));
        OnPropertyChanged(nameof(CropSummary));
        AdvanceTransformRevision();
    }

    public bool CancelLoading() => _selection.CancelSelectedImageLoading();

    public void Unload()
    {
        Interlocked.Increment(ref _activationVersion);
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
        return true;
    }

    private void ResetEditor(string message)
    {
        _sourceImage = null;
        _transform = null;
        _loadedRelativePath = string.Empty;
        SetLoading(false, message);
        RaiseSelectionProperties();
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PanSummary));
        OnPropertyChanged(nameof(CropSummary));
        AdvanceTransformRevision();
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
