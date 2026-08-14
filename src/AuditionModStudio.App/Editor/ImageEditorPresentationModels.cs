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
        new(ImageCompareMode.SideBySide, "Song song"),
        new(ImageCompareMode.Slider, "Chia đôi"),
        new(ImageCompareMode.Toggle, "Chuyển đổi")
    ];
}

public static class ImageEditorModes
{
    public static ImmutableArray<EditorResizeModeOption> Supported { get; } =
    [
        new(ImageResizeMode.ManualCrop, "Cắt ảnh"),
        new(ImageResizeMode.Fit, "Vừa khung"),
        new(ImageResizeMode.Fill, "Lấp đầy"),
        new(ImageResizeMode.Stretch, "Kéo giãn"),
        new(ImageResizeMode.CanvasResize, "Đổi khung"),
        new(ImageResizeMode.TransparentPadding, "Thêm khoảng trong suốt")
    ];
}
