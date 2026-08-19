using System.ComponentModel;
using AuditionModStudio.App.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AuditionModStudio.Core.Images;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;

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

    private async void OnSubmitClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.SubmitAsync(MaskViewModel.Mask);
    private void OnCancelClicked(object sender, RoutedEventArgs e) => ViewModel.CancelCurrent();
    private async void OnApprovePreviewClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.ApprovePreviewAsync();
    private async void OnRefreshHistoryClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshHistoryAsync();

    private async void OnAddReferencesClicked(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App { ActiveWindow: { } window }) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await ViewModel.AddReferenceImagesAsync(files.Select(file => file.Path));
    }

    private void OnClearReferencesClicked(object sender, RoutedEventArgs e) => ViewModel.ClearReferenceImages();

    private void OnRemoveReferenceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int index }) ViewModel.RemoveReferenceAt(index);
    }

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
        if (args.PropertyName == nameof(ViewModel.AdditionalReferences)) UpdateReferenceThumbnails();
        if (args.PropertyName == nameof(ViewModel.PreviewImage))
        {
            PreviewImage.Source = ViewModel.PreviewImage is null
                ? null
                : InternalImageBitmapAdapter.CreateBitmap(ViewModel.PreviewImage);
        }
    }

    private void UpdateReferenceThumbnails()
    {
        ReferenceThumbnails.Children.Clear();
        for (var index = 0; index < ViewModel.AdditionalReferences.Count; index++)
        {
            var image = ViewModel.AdditionalReferences[index];
            var tile = new Grid { Width = 72, Height = 72 };
            var preview = new Image
            {
                Source = InternalImageBitmapAdapter.CreateBitmap(image),
                Stretch = Stretch.UniformToFill,
            };
            AutomationProperties.SetName(preview, $"Ảnh tham chiếu {index + 1}");
            var frame = new Border
            {
                Width = 72,
                Height = 72,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["AmsCardBorderBrush"],
                Child = preview,
            };
            var remove = new Button
            {
                Content = "×",
                Tag = index,
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2),
                FontSize = 16,
            };
            AutomationProperties.SetName(remove, $"Xóa ảnh tham chiếu {index + 1}");
            remove.Click += OnRemoveReferenceClicked;
            tile.Children.Add(frame);
            tile.Children.Add(remove);
            ReferenceThumbnails.Children.Add(tile);
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
