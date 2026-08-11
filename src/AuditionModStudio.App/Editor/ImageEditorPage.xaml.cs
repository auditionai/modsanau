using System.ComponentModel;
using AuditionModStudio.App.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AuditionModStudio.App.Editor;

public sealed partial class ImageEditorPage : Page
{
    private Point? _lastPointerPosition;

    public ImageEditorPage(ImageEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public ImageEditorViewModel ViewModel { get; }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await ViewModel.ActivateAsync(cancellationToken);
        if (ViewModel.SourceImage is { } image)
        {
            EditorImage.Source = InternalImageBitmapAdapter.CreateBitmap(image);
        }
        else
        {
            EditorImage.Source = null;
        }

        ZoomSlider.Value = ViewModel.Zoom;
        UpdateCanvasProjection();
    }

    public bool FocusPrimaryHeading() => EditorHeading.Focus(FocusState.Programmatic);

    public void Deactivate()
    {
        ViewModel.Unload();
        EditorImage.Source = null;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => ViewModel.CancelLoading();

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.ResetTransform())
        {
            return;
        }

        CropX.Value = 0;
        CropY.Value = 0;
        CropWidth.Value = 100;
        CropHeight.Value = 100;
        ZoomSlider.Value = 1;
        UpdateCanvasProjection();
    }

    private void OnUpdateCropClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetCropPercent(CropX.Value, CropY.Value, CropWidth.Value, CropHeight.Value);
        UpdateCanvasProjection();
    }

    private void OnPanClicked(object sender, RoutedEventArgs e)
    {
        var moved = ((sender as Button)?.Tag as string) switch
        {
            "up" => ViewModel.PanBy(0, -20),
            "down" => ViewModel.PanBy(0, 20),
            "left" => ViewModel.PanBy(-20, 0),
            "right" => ViewModel.PanBy(20, 0),
            _ => false
        };
        if (moved)
        {
            UpdateCanvasProjection();
        }
    }

    private void OnZoomChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.SetZoom(e.NewValue))
        {
            UpdateCanvasProjection();
        }
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        _lastPointerPosition = e.GetCurrentPoint(EditorCanvas).Position;
        EditorCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_lastPointerPosition is not { } previous || !e.GetCurrentPoint(EditorCanvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetCurrentPoint(EditorCanvas).Position;
        if (ViewModel.PanBy(current.X - previous.X, current.Y - previous.Y))
        {
            _lastPointerPosition = current;
            UpdateCanvasProjection();
        }

        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _lastPointerPosition = null;
        EditorCanvas.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        var delta = e.GetCurrentPoint(EditorCanvas).Properties.MouseWheelDelta;
        var nextZoom = Math.Clamp(ViewModel.Zoom * (delta > 0 ? 1.1 : 1 / 1.1), 0.1, 8);
        ZoomSlider.Value = nextZoom;
        e.Handled = true;
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EditorCanvas.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
        };
        UpdateCanvasProjection();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.TransformRevision))
        {
            UpdateCanvasProjection();
        }
    }

    private void UpdateCanvasProjection()
    {
        var targetWidth = ViewModel.TargetWidth;
        var targetHeight = ViewModel.TargetHeight;
        var availableWidth = Math.Max(0, EditorCanvas.ActualWidth - 48);
        var availableHeight = Math.Max(0, EditorCanvas.ActualHeight - 48);
        if (!ViewModel.HasImage
            || targetWidth <= 0
            || targetHeight <= 0
            || availableWidth <= 0
            || availableHeight <= 0)
        {
            SetElementBounds(TargetFrame, 0, 0, 0, 0);
            SetElementBounds(CropFrame, 0, 0, 0, 0);
            SetElementBounds(EditorImage, 0, 0, 0, 0);
            return;
        }

        var targetScale = Math.Min(availableWidth / targetWidth, availableHeight / targetHeight);
        var frameWidth = targetWidth * targetScale;
        var frameHeight = targetHeight * targetScale;
        var frameX = (EditorCanvas.ActualWidth - frameWidth) / 2;
        var frameY = (EditorCanvas.ActualHeight - frameHeight) / 2;
        SetElementBounds(TargetFrame, frameX, frameY, frameWidth, frameHeight);

        var projection = ViewModel.GetProjection(frameWidth, frameHeight);
        if (projection is null)
        {
            SetElementBounds(CropFrame, 0, 0, 0, 0);
            SetElementBounds(EditorImage, 0, 0, 0, 0);
            return;
        }

        SetElementBounds(
            EditorImage,
            frameX + projection.ImageBounds.X,
            frameY + projection.ImageBounds.Y,
            projection.ImageBounds.Width,
            projection.ImageBounds.Height);
        SetElementBounds(
            CropFrame,
            frameX + projection.CropBounds.X,
            frameY + projection.CropBounds.Y,
            projection.CropBounds.Width,
            projection.CropBounds.Height);
    }

    private static void SetElementBounds(
        FrameworkElement element,
        double x,
        double y,
        double width,
        double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }
}
