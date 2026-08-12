using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Shell;
using AuditionModStudio.Core.Images;

namespace AuditionModStudio.App.AiStudio;

public sealed class AiMaskEditorViewModel : INotifyPropertyChanged
{
    private readonly AiStudioViewModel _studio;
    private readonly IAiMaskEditingService _maskService;
    private readonly IEditHistoryService _historyService;
    private readonly IAiMaskAssetStore _assetStore;
    private readonly IApplicationProjectSession _projectSession;
    private IEditHistorySession? _history;
    private InternalImage? _source;
    private AiMask? _mask;
    private InternalImage? _overlay;
    private AiMaskBrushMode _mode = AiMaskBrushMode.Paint;
    private double _brushSize = 48;
    private double _hardness = 0.8;
    private double _opacity = 1;
    private double _zoom = 1;
    private ViewportVector _pan;
    private bool _showMask = true;
    private bool _isComposing;
    private string _statusMessage = "Create an AI preview, then initialize a source-aligned mask.";
    private CancellationTokenSource? _compositionCancellation;

    public AiMaskEditorViewModel(
        AiStudioViewModel studio,
        IAiMaskEditingService maskService,
        IEditHistoryService historyService,
        IAiMaskAssetStore assetStore,
        IApplicationProjectSession projectSession)
    {
        _studio = studio ?? throw new ArgumentNullException(nameof(studio));
        _maskService = maskService ?? throw new ArgumentNullException(nameof(maskService));
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        _assetStore = assetStore ?? throw new ArgumentNullException(nameof(assetStore));
        _projectSession = projectSession ?? throw new ArgumentNullException(nameof(projectSession));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public InternalImage? SourceImage => _source;
    public IReadOnlyList<AiMaskBrushMode> BrushModes { get; } = Enum.GetValues<AiMaskBrushMode>();
    public InternalImage? OverlayImage => ShowMask ? _overlay : null;
    public AiMask? Mask => _mask;
    public bool HasMask => _mask is not null;
    public AiMaskBrushMode Mode { get => _mode; set => Set(ref _mode, value); }
    public double BrushSize { get => _brushSize; set => Set(ref _brushSize, Math.Clamp(value, 1, 512)); }
    public double Hardness { get => _hardness; set => Set(ref _hardness, Math.Clamp(value, 0, 1)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.01, 1)); }
    public double Zoom { get => _zoom; set => Set(ref _zoom, Math.Clamp(value, 0.1, 8)); }
    public ViewportVector Pan => _pan;
    public bool ShowMask
    {
        get => _showMask;
        set
        {
            if (Set(ref _showMask, value)) OnPropertyChanged(nameof(OverlayImage));
        }
    }
    public bool IsComposing { get => _isComposing; private set => Set(ref _isComposing, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public bool CanUndo => _history?.State.CanUndo == true;
    public bool CanRedo => _history?.State.CanRedo == true;
    public string MemoryStatus => _history is null
        ? "History: 0 MiB"
        : $"History: {_history.State.EstimatedMemoryBytes / (1024d * 1024):0.0} MiB";

    public bool InitializeFromPreview()
    {
        var preview = _studio.PreviewImage;
        if (preview is null)
        {
            StatusMessage = "No AI preview is available. Mask state was not changed.";
            return false;
        }
        var created = _maskService.Create(preview);
        if (!created.Succeeded || created.Mask is null)
        {
            StatusMessage = "The preview dimensions exceed mask limits.";
            return false;
        }
        var history = _historyService.CreateSession(ToHistoryState(created.Mask),
            new EditHistoryOptions(64, 256L * 1024 * 1024));
        if (!history.Succeeded || history.Session is null)
        {
            StatusMessage = "Mask history could not be initialized within the memory limit.";
            return false;
        }
        _source = preview;
        _mask = created.Mask;
        _history = history.Session;
        _pan = default;
        _zoom = 1;
        NotifyMaskChanged();
        StatusMessage = $"Mask initialized at {_mask.Width} × {_mask.Height} source pixels.";
        StartOverlayComposition();
        return true;
    }

    public bool ApplySourceStroke(IReadOnlyList<AiMaskPoint> points)
    {
        if (_mask is null) return false;
        var result = _maskService.ApplyStroke(_mask,
            new(points, new(Mode, BrushSize, Hardness, Opacity)));
        return Commit(result, EditOperationKind.Alpha, "Mask stroke added.");
    }

    public bool StampCenter() => _source is not null
        && ApplySourceStroke([new(_source.Width / 2d, _source.Height / 2d)]);

    public bool Clear() => _mask is not null
        && Commit(_maskService.Clear(_mask), EditOperationKind.Alpha, "Mask cleared.");

    public bool Invert() => _mask is not null
        && Commit(_maskService.Invert(_mask), EditOperationKind.Alpha, "Mask inverted.");

    public bool Undo()
    {
        if (_history?.Undo() is not { Succeeded: true } result) return false;
        Restore(result.State.Current.Image);
        StatusMessage = "Mask undo completed.";
        return true;
    }

    public bool Redo()
    {
        if (_history?.Redo() is not { Succeeded: true } result) return false;
        Restore(result.State.Current.Image);
        StatusMessage = "Mask redo completed.";
        return true;
    }

    public void PanBy(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        _pan = new(_pan.X + x, _pan.Y + y);
        OnPropertyChanged(nameof(Pan));
    }

    public void ResetView()
    {
        _pan = default;
        _zoom = 1;
        OnPropertyChanged(nameof(Pan));
        OnPropertyChanged(nameof(Zoom));
    }

    public AiMaskPoint? ViewportToSource(
        double viewportX, double viewportY, double viewportWidth, double viewportHeight)
    {
        if (_source is null || viewportWidth <= 0 || viewportHeight <= 0
            || !double.IsFinite(viewportX) || !double.IsFinite(viewportY)) return null;
        var scale = Math.Min(viewportWidth / _source.Width, viewportHeight / _source.Height) * Zoom;
        var renderedWidth = _source.Width * scale;
        var renderedHeight = _source.Height * scale;
        var left = (viewportWidth - renderedWidth) / 2 + Pan.X;
        var top = (viewportHeight - renderedHeight) / 2 + Pan.Y;
        var sourceX = (viewportX - left) / scale;
        var sourceY = (viewportY - top) / scale;
        return sourceX is >= 0 && sourceX < double.MaxValue
            && sourceY is >= 0 && sourceY < double.MaxValue
            && sourceX < _source.Width && sourceY < _source.Height
            ? new(sourceX, sourceY)
            : null;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (_mask is null || _projectSession.Workspace is null)
        {
            StatusMessage = "Open a project and initialize a mask before saving.";
            return;
        }
        var result = await _assetStore.SaveAsync(_projectSession.Workspace, _mask, cancellationToken);
        StatusMessage = result.Succeeded
            ? "Mask asset saved atomically in the project workspace."
            : "Mask asset could not be saved; the previous asset remains unchanged.";
        if (result.Succeeded) _history?.MarkSavedCheckpoint();
    }

    public void CancelComposition() => _compositionCancellation?.Cancel();

    private bool Commit(AiMaskEditResult result, EditOperationKind kind, string message)
    {
        if (!result.Succeeded || result.Mask is null || _history is null) return false;
        var pushed = _history.Push(ToHistoryState(result.Mask), new(kind));
        if (!pushed.Succeeded)
        {
            StatusMessage = "Mask history limit reached; the edit was not committed.";
            return false;
        }
        _mask = result.Mask;
        NotifyMaskChanged();
        StatusMessage = message;
        StartOverlayComposition();
        return true;
    }

    private void Restore(InternalImage historyImage)
    {
        var opacity = new byte[checked(historyImage.Width * historyImage.Height)];
        for (var index = 0; index < opacity.Length; index++) opacity[index] = historyImage.Pixels[index * 4];
        _mask = new(historyImage.Width, historyImage.Height, opacity);
        NotifyMaskChanged();
        StartOverlayComposition();
    }

    private async void StartOverlayComposition()
    {
        _compositionCancellation?.Cancel();
        _compositionCancellation?.Dispose();
        if (_mask is null) return;
        var cancellation = new CancellationTokenSource();
        _compositionCancellation = cancellation;
        IsComposing = true;
        try
        {
            var overlay = await _maskService.ComposeOverlayAsync(_mask, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                _overlay = overlay;
                OnPropertyChanged(nameof(OverlayImage));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusMessage = "Mask preview composition cancelled.";
        }
        finally
        {
            if (ReferenceEquals(_compositionCancellation, cancellation))
            {
                _compositionCancellation = null;
                IsComposing = false;
                cancellation.Dispose();
            }
        }
    }

    private static ImageEditorState ToHistoryState(AiMask mask)
    {
        var pixels = new byte[checked(mask.Width * mask.Height * 4)];
        for (var index = 0; index < mask.Opacity.Length; index++)
        {
            var pixel = index * 4;
            pixels[pixel] = mask.Opacity[index];
            pixels[pixel + 1] = mask.Opacity[index];
            pixels[pixel + 2] = mask.Opacity[index];
            pixels[pixel + 3] = 255;
        }
        var image = new InternalImage(mask.Width, mask.Height, mask.Width * 4, pixels,
            new(ImageSourceFormat.Png, mask.Width, mask.Height,
                ImageSourceOrientation.Normal, true, false));
        return new(image,
            new(mask.Width, mask.Height, new(0, 0, 1, 1), 1, new(0, 0), ImageScale.Identity,
                ImageQuarterTurn.None, false, false, new(0, 0), ImageTransformConstraints.Default),
            new());
    }

    private void NotifyMaskChanged()
    {
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(Mask));
        OnPropertyChanged(nameof(HasMask));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(MemoryStatus));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
