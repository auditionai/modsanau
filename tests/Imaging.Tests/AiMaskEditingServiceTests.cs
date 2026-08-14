using AuditionModStudio.Core.Images;
using AuditionModStudio.Imaging;

namespace Imaging.Tests;

public sealed class AiMaskEditingServiceTests
{
    private readonly AiMaskEditingService _service = new();

    [Fact]
    public void Create_uses_exact_source_dimensions_and_zero_mask()
    {
        var result = _service.Create(Image(7, 5));

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.Mask!.Width);
        Assert.Equal(5, result.Mask.Height);
        Assert.All(result.Mask.Opacity, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Identical_stroke_is_deterministic_and_clipped_to_source_bounds()
    {
        var mask = Mask(16, 12);
        var stroke = new AiMaskStroke([new(-2, 1), new(8, 6), new(20, 10)],
            new(AiMaskBrushMode.Paint, 5, 0.6, 0.75));

        var first = _service.ApplyStroke(mask, stroke);
        var second = _service.ApplyStroke(mask, stroke);

        Assert.True(first.Succeeded);
        Assert.True(first.Mask!.Opacity.SequenceEqual(second.Mask!.Opacity));
        Assert.Contains(first.Mask.Opacity, value => value > 0);
    }

    [Fact]
    public void Paint_then_erase_respects_opacity_and_does_not_mutate_input()
    {
        var original = Mask(5, 5);
        var paint = _service.ApplyStroke(original,
            new([new(2.5, 2.5)], new(AiMaskBrushMode.Paint, 4, 1, 1))).Mask!;
        var erased = _service.ApplyStroke(paint,
            new([new(2.5, 2.5)], new(AiMaskBrushMode.Erase, 4, 1, 0.5))).Mask!;

        Assert.All(original.Opacity, value => Assert.Equal(0, value));
        Assert.True(paint.Opacity[12] > erased.Opacity[12]);
        Assert.True(erased.Opacity[12] > 0);
    }

    [Fact]
    public void Clear_and_invert_return_new_exact_masks()
    {
        var mask = new AiMask(2, 2, [0, 64, 128, 255]);

        var inverted = _service.Invert(mask).Mask!;
        var cleared = _service.Clear(mask).Mask!;

        Assert.True(inverted.Opacity.SequenceEqual(new byte[] { 255, 191, 127, 0 }));
        Assert.True(cleared.Opacity.SequenceEqual(new byte[] { 0, 0, 0, 0 }));
        Assert.True(mask.Opacity.SequenceEqual(new byte[] { 0, 64, 128, 255 }));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, -0.1, 1)]
    [InlineData(1, 1, 0)]
    public void Invalid_brush_is_rejected_without_mask_mutation(double size, double hardness, double opacity)
    {
        var mask = Mask(2, 2);
        var result = _service.ApplyStroke(mask,
            new([new(1, 1)], new(AiMaskBrushMode.Paint, size, hardness, opacity)));

        Assert.False(result.Succeeded);
        Assert.All(mask.Opacity, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Unbounded_coordinate_is_rejected_without_allocating_or_mutating()
    {
        var mask = Mask(2, 2);

        var result = _service.ApplyStroke(mask,
            new([new(double.MaxValue, 1)], new(AiMaskBrushMode.Paint, 1, 1, 1)));

        Assert.False(result.Succeeded);
        Assert.All(mask.Opacity, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Overlay_has_exact_alignment_and_cancellation_is_observed()
    {
        var mask = new AiMask(2, 1, [0, 255]);
        var overlay = await _service.ComposeOverlayAsync(mask);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(2, overlay!.Width);
        Assert.Equal(1, overlay.Height);
        Assert.Equal(0, overlay.Pixels[3]);
        Assert.Equal(255, overlay.Pixels[7]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.ComposeOverlayAsync(mask, cancellation.Token));
    }

    private static AiMask Mask(int width, int height) => new(width, height, new byte[width * height]);
    private static InternalImage Image(int width, int height) => new(
        width, height, width * 4, new byte[width * height * 4],
        new(ImageSourceFormat.Png, width, height, ImageSourceOrientation.Normal, true, false));
}
