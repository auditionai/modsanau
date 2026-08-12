using System.ComponentModel;
using AuditionModStudio.App.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AuditionModStudio.Core.Images;
using Microsoft.UI.Xaml.Input;

namespace AuditionModStudio.App.AiStudio;

public sealed partial class AiStudioPage : Page
{
    private readonly List<AiMaskPoint> _strokePoints = [];
    private bool _drawingMask;

    public AiStudioPage(AiStudioViewModel viewModel, AiMaskEditorViewModel maskViewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        MaskViewModel = maskViewModel ?? throw new ArgumentNullException(nameof(maskViewModel));
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        MaskViewModel.PropertyChanged += OnMaskViewModelPropertyChanged;
    }

    public AiStudioViewModel ViewModel { get; }
    public AiMaskEditorViewModel MaskViewModel { get; }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await ViewModel.ActivateAsync(cancellationToken);
    }

    public void Deactivate() => ViewModel.Deactivate();

    public void FocusPrimaryHeading() => StudioHeading.Focus(FocusState.Programmatic);

    private async void OnSubmitClicked(object sender, RoutedEventArgs e) => await ViewModel.SubmitAsync();
    private void OnCancelClicked(object sender, RoutedEventArgs e) => ViewModel.CancelCurrent();
    private async void OnRefreshHistoryClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshHistoryAsync();

    private async void OnCancelJobClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid jobId }) await ViewModel.CancelJobAsync(jobId);
    }

    private void OnInitializeMaskClicked(object sender, RoutedEventArgs e) => MaskViewModel.InitializeFromPreview();
    private void OnUndoMaskClicked(object sender, RoutedEventArgs e) => MaskViewModel.Undo();
    private void OnRedoMaskClicked(object sender, RoutedEventArgs e) => MaskViewModel.Redo();
    private void OnClearMaskClicked(object sender, RoutedEventArgs e) => MaskViewModel.Clear();
    private void OnInvertMaskClicked(object sender, RoutedEventArgs e) => MaskViewModel.Invert();
    private void OnStampCenterClicked(object sender, RoutedEventArgs e) => MaskViewModel.StampCenter();
    private void OnPanLeftClicked(object sender, RoutedEventArgs e) => MaskViewModel.PanBy(-20, 0);
    private void OnPanRightClicked(object sender, RoutedEventArgs e) => MaskViewModel.PanBy(20, 0);
    private void OnPanUpClicked(object sender, RoutedEventArgs e) => MaskViewModel.PanBy(0, -20);
    private void OnPanDownClicked(object sender, RoutedEventArgs e) => MaskViewModel.PanBy(0, 20);
    private void OnResetMaskViewClicked(object sender, RoutedEventArgs e) => MaskViewModel.ResetView();
    private void OnCancelMaskCompositionClicked(object sender, RoutedEventArgs e) => MaskViewModel.CancelComposition();
    private async void OnSaveMaskClicked(object sender, RoutedEventArgs e) => await MaskViewModel.SaveAsync();

    private void OnMaskPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!MaskViewModel.HasMask || sender is not UIElement element) return;
        _strokePoints.Clear();
        _drawingMask = true;
        element.CapturePointer(e.Pointer);
        AddMaskPoint(e);
        e.Handled = true;
    }

    private void OnMaskPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drawingMask) AddMaskPoint(e);
    }

    private void OnMaskPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawingMask || sender is not UIElement element) return;
        AddMaskPoint(e);
        element.ReleasePointerCapture(e.Pointer);
        _drawingMask = false;
        MaskViewModel.ApplySourceStroke(_strokePoints.ToArray());
        _strokePoints.Clear();
        e.Handled = true;
    }

    private void AddMaskPoint(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MaskViewport).Position;
        var source = MaskViewModel.ViewportToSource(
            point.X, point.Y, MaskViewport.ActualWidth, MaskViewport.ActualHeight);
        if (source is { } value && (_strokePoints.Count == 0 || _strokePoints[^1] != value))
            _strokePoints.Add(value);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.PreviewImage))
        {
            PreviewImage.Source = ViewModel.PreviewImage is null
                ? null
                : InternalImageBitmapAdapter.CreateBitmap(ViewModel.PreviewImage);
        }
    }

    private void OnMaskViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MaskViewModel.SourceImage))
            MaskSourceImage.Source = MaskViewModel.SourceImage is null ? null
                : InternalImageBitmapAdapter.CreateBitmap(MaskViewModel.SourceImage);
        if (args.PropertyName is nameof(MaskViewModel.OverlayImage) or nameof(MaskViewModel.ShowMask))
            MaskOverlayImage.Source = MaskViewModel.OverlayImage is null ? null
                : InternalImageBitmapAdapter.CreateBitmap(MaskViewModel.OverlayImage);
        if (args.PropertyName is nameof(MaskViewModel.Zoom) or nameof(MaskViewModel.Pan))
        {
            MaskSourceTransform.ScaleX = MaskOverlayTransform.ScaleX = MaskViewModel.Zoom;
            MaskSourceTransform.ScaleY = MaskOverlayTransform.ScaleY = MaskViewModel.Zoom;
            MaskSourceTransform.TranslateX = MaskOverlayTransform.TranslateX = MaskViewModel.Pan.X;
            MaskSourceTransform.TranslateY = MaskOverlayTransform.TranslateY = MaskViewModel.Pan.Y;
        }
    }
}
