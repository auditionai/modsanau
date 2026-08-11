using System.Collections.Immutable;
using AuditionModStudio.Core.Images;

namespace AuditionModStudio.App.Editor;

public sealed record EditorResizeModeOption(ImageResizeMode Mode, string Label);

public readonly record struct EditorCanvasRectangle(double X, double Y, double Width, double Height);

public sealed record EditorCanvasProjection(
    EditorCanvasRectangle ImageBounds,
    EditorCanvasRectangle CropBounds);

public enum ImageCompareMode
{
    SideBySide,
    Slider,
    Toggle
}

public enum ImageCompareToggleState
{
    Before,
    After
}

public sealed record ImageCompareModeOption(ImageCompareMode Mode, string Label);

public static class ImageCompareModes
{
    public static ImmutableArray<ImageCompareModeOption> Supported { get; } =
    [
        new(ImageCompareMode.SideBySide, "Side-by-side"),
        new(ImageCompareMode.Slider, "Slider"),
        new(ImageCompareMode.Toggle, "Toggle")
    ];
}

public static class ImageEditorModes
{
    public static ImmutableArray<EditorResizeModeOption> Supported { get; } =
    [
        new(ImageResizeMode.ManualCrop, "Crop"),
        new(ImageResizeMode.Fit, "Fit"),
        new(ImageResizeMode.Fill, "Fill"),
        new(ImageResizeMode.Stretch, "Stretch"),
        new(ImageResizeMode.CanvasResize, "Canvas"),
        new(ImageResizeMode.TransparentPadding, "Padding")
    ];
}
