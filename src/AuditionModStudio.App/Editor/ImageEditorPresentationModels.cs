using System.Collections.Immutable;
using AuditionModStudio.Core.Images;

namespace AuditionModStudio.App.Editor;

public sealed record EditorResizeModeOption(ImageResizeMode Mode, string Label);

public readonly record struct EditorCanvasRectangle(double X, double Y, double Width, double Height);

public sealed record EditorCanvasProjection(
    EditorCanvasRectangle ImageBounds,
    EditorCanvasRectangle CropBounds);

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
