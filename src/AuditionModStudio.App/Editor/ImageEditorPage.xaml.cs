using System.ComponentModel;
using AuditionModStudio.App.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace AuditionModStudio.App.Editor;

public sealed partial class ImageEditorPage : Page
{
    private Point? _lastPointerPosition;
    private Point? _lastComparePointerPosition;
    private UIElement? _comparePointerSurface;
    private WriteableBitmap? _beforeBitmap;
    private WriteableBitmap? _afterBitmap;
    private AuditionModStudio.Core.Images.InternalImage? _beforeBitmapSource;
    private AuditionModStudio.Core.Images.InternalImage? _afterBitmapSource;

    public ImageEditorPage(ImageEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        PopulateCheckerboard(BeforeSideCheckerboard);
        PopulateCheckerboard(AfterSideCheckerboard);
        PopulateCheckerboard(SliderCheckerboard);
        PopulateCheckerboard(ToggleCheckerboard);
    }

    public ImageEditorViewModel ViewModel { get; }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await ViewModel.ActivateAsync(cancellationToken);
        UpdateCompareBitmaps();
        ZoomSlider.Value = ViewModel.Zoom;
        CompareZoomSlider.Value = ViewModel.CompareZoom;
        CompareDividerSlider.Value = ViewModel.CompareDivider;
        CheckerboardToggle.IsChecked = ViewModel.ShowCheckerboard;
        BeforeAfterToggle.IsOn = ViewModel.CompareToggleState == ImageCompareToggleState.After;
        UpdateCompareBitmaps();
        UpdateCanvasProjection();
        UpdateComparePresentation();
    }

    public bool FocusPrimaryHeading() => EditorHeading.Focus(FocusState.Programmatic);

    public void Deactivate()
    {
        ViewModel.Unload();
        EditorImage.Source = null;
        ReleaseCompareBitmaps();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => ViewModel.CancelLoading();

    private async void OnApplyClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.ApplyAsync();

    private void OnCancelApplyClicked(object sender, RoutedEventArgs e) => ViewModel.CancelApply();

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
        if (!ViewModel.CanEdit)
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
        if (!ViewModel.CanEdit)
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

        if (args.PropertyName == nameof(ViewModel.AfterImage))
        {
            UpdateCompareBitmaps();
        }

        if (args.PropertyName == nameof(ViewModel.SourceImage))
        {
            UpdateCompareBitmaps();
        }

        if (args.PropertyName == nameof(ViewModel.CompareRevision))
        {
            UpdateComparePresentation();
        }
    }

    private void OnCheckerboardClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetCheckerboard(CheckerboardToggle.IsChecked == true);
    }

    private void OnCompareZoomChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.SetCompareZoom(e.NewValue))
        {
            UpdateComparePresentation();
        }
    }

    private void OnCompareDividerChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.SetCompareDivider(e.NewValue))
        {
            UpdateCompareSliderClip();
        }
    }

    private void OnBeforeAfterToggled(object sender, RoutedEventArgs e)
    {
        ViewModel.SetCompareToggleState(
            BeforeAfterToggle.IsOn
                ? ImageCompareToggleState.After
                : ImageCompareToggleState.Before);
    }

    private void OnComparePanClicked(object sender, RoutedEventArgs e)
    {
        var moved = ((sender as Button)?.Tag as string) switch
        {
            "up" => ViewModel.PanCompareBy(0, -20),
            "down" => ViewModel.PanCompareBy(0, 20),
            "left" => ViewModel.PanCompareBy(-20, 0),
            "right" => ViewModel.PanCompareBy(20, 0),
            _ => false
        };
        if (moved)
        {
            UpdateComparePresentation();
        }
    }

    private void OnCompareCameraResetClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetCompareCamera();
        CompareZoomSlider.Value = ViewModel.CompareZoom;
        UpdateComparePresentation();
    }

    private void OnComparePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasImage || sender is not UIElement surface)
        {
            return;
        }

        _lastComparePointerPosition = e.GetCurrentPoint(surface).Position;
        _comparePointerSurface = surface;
        surface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnComparePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_lastComparePointerPosition is not { } previous
            || sender is not UIElement surface
            || !ReferenceEquals(surface, _comparePointerSurface)
            || !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetCurrentPoint(surface).Position;
        if (ViewModel.PanCompareBy(current.X - previous.X, current.Y - previous.Y))
        {
            _lastComparePointerPosition = current;
            UpdateComparePresentation();
        }

        e.Handled = true;
    }

    private void OnComparePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement surface)
        {
            surface.ReleasePointerCapture(e.Pointer);
        }

        _lastComparePointerPosition = null;
        _comparePointerSurface = null;
        e.Handled = true;
    }

    private void OnComparePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        var delta = e.GetCurrentPoint(sender as UIElement).Properties.MouseWheelDelta;
        CompareZoomSlider.Value = Math.Clamp(
            ViewModel.CompareZoom * (delta > 0 ? 1.1 : 1 / 1.1),
            0.1,
            8);
        e.Handled = true;
    }

    private void OnCompareSurfaceSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateComparePresentation();

    private void UpdateCompareBitmaps()
    {
        if (ViewModel.BeforeImage is { } before
            && !ReferenceEquals(_beforeBitmapSource, before))
        {
            _beforeBitmap = InternalImageBitmapAdapter.CreateBitmap(before);
            _beforeBitmapSource = before;
        }
        else if (ViewModel.BeforeImage is null)
        {
            _beforeBitmap = null;
            _beforeBitmapSource = null;
        }

        if (ViewModel.AfterImage is { } after)
        {
            if (!ReferenceEquals(_afterBitmapSource, after))
            {
                _afterBitmap = InternalImageBitmapAdapter.CreateBitmap(after);
                _afterBitmapSource = after;
            }
        }
        else
        {
            _afterBitmap = null;
            _afterBitmapSource = null;
        }

        BeforeSideImage.Source = _beforeBitmap;
        BeforeSliderImage.Source = _beforeBitmap;
        EditorImage.Source = _beforeBitmap;
        AfterSideImage.Source = _afterBitmap;
        AfterSliderImage.Source = _afterBitmap;
        UpdateToggleImage();
    }

    private void ReleaseCompareBitmaps()
    {
        BeforeSideImage.Source = null;
        BeforeSliderImage.Source = null;
        AfterSideImage.Source = null;
        AfterSliderImage.Source = null;
        ToggleImage.Source = null;
        _beforeBitmap = null;
        _afterBitmap = null;
        _beforeBitmapSource = null;
        _afterBitmapSource = null;
    }

    private void UpdateComparePresentation()
    {
        SideBySideCompare.Visibility = ViewModel.SelectedCompareMode.Mode == ImageCompareMode.SideBySide
            ? Visibility.Visible
            : Visibility.Collapsed;
        SliderCompare.Visibility = ViewModel.SelectedCompareMode.Mode == ImageCompareMode.Slider
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToggleCompare.Visibility = ViewModel.SelectedCompareMode.Mode == ImageCompareMode.Toggle
            ? Visibility.Visible
            : Visibility.Collapsed;

        var checkerVisibility = ViewModel.ShowCheckerboard ? Visibility.Visible : Visibility.Collapsed;
        BeforeSideCheckerboard.Visibility = checkerVisibility;
        AfterSideCheckerboard.Visibility = checkerVisibility;
        SliderCheckerboard.Visibility = checkerVisibility;
        ToggleCheckerboard.Visibility = checkerVisibility;

        UpdateCompareImageBounds(BeforeSideImage, ViewModel.BeforeImage, BeforeSideViewport);
        UpdateCompareImageBounds(AfterSideImage, ViewModel.AfterImage, AfterSideViewport);
        UpdateCompareImageBounds(BeforeSliderImage, ViewModel.BeforeImage, SliderViewport);
        UpdateCompareImageBounds(AfterSliderImage, ViewModel.AfterImage, SliderViewport);
        UpdateToggleImage();
        UpdateCompareImageBounds(
            ToggleImage,
            ViewModel.CompareToggleState == ImageCompareToggleState.After
                ? ViewModel.AfterImage
                : ViewModel.BeforeImage,
            ToggleViewport);
        UpdateCompareSliderClip();
    }

    private void UpdateToggleImage()
    {
        var showAfter = ViewModel.CompareToggleState == ImageCompareToggleState.After;
        ToggleImage.Source = showAfter ? _afterBitmap : _beforeBitmap;
        ToggleStateLabel.Text = showAfter ? "After" : "Before";
    }

    private void UpdateCompareSliderClip()
    {
        var width = Math.Max(0, SliderViewport.ActualWidth);
        var height = Math.Max(0, SliderViewport.ActualHeight);
        var dividerX = width * ViewModel.CompareDivider;
        AfterSliderLayer.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, dividerX, height)
        };
        Canvas.SetLeft(SliderDivider, dividerX - SliderDivider.Width / 2);
        SliderDivider.Height = height;
    }

    private void UpdateCompareImageBounds(Image element, AuditionModStudio.Core.Images.InternalImage? image, FrameworkElement viewport)
    {
        if (image is null
            || viewport.ActualWidth <= 0
            || viewport.ActualHeight <= 0
            || ViewModel.GetCompareProjection(image, viewport.ActualWidth, viewport.ActualHeight) is not { } projection)
        {
            SetElementBounds(element, 0, 0, 0, 0);
            return;
        }

        SetElementBounds(element, projection.X, projection.Y, projection.Width, projection.Height);
    }

    private static void PopulateCheckerboard(Grid grid)
    {
        const int columns = 12;
        const int rows = 8;
        for (var column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (var column = 0; column < columns; column++)
            {
                var square = new Border
                {
                    Background = (Brush)Application.Current.Resources[
                        (row + column) % 2 == 0 ? "AmsCardBrush" : "AmsPanelBrush"]
                };
                Grid.SetRow(square, row);
                Grid.SetColumn(square, column);
                grid.Children.Add(square);
            }
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
