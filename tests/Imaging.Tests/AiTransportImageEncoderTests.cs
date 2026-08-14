using AuditionModStudio.Core.Images;
using AuditionModStudio.Imaging;

namespace Imaging.Tests;

public sealed class AiTransportImageEncoderTests
{
    [Fact]
    public async Task Source_png_roundtrips_through_existing_bounded_importer()
    {
        var encoder = new AiTransportImageEncoder();
        var importer = new ImageImportService(ImageImportResourcePolicy.Default);
        var source = new InternalImage(2, 1, 8,
            new byte[] { 255, 0, 0, 255, 0, 255, 0, 128 },
            new(ImageSourceFormat.Png, 2, 1, ImageSourceOrientation.Normal, true, false));

        var encoded = await encoder.EncodePngAsync(source);
        var imported = await importer.ImportMemoryAsync(new(encoded!));

        Assert.True(imported.Succeeded);
        Assert.Equal(2, imported.Image!.Width);
        Assert.Equal(1, imported.Image.Height);
    }

    [Fact]
    public async Task Mask_png_preserves_exact_source_dimensions_and_opacity_values()
    {
        var encoder = new AiTransportImageEncoder();
        var importer = new ImageImportService(ImageImportResourcePolicy.Default);
        var mask = new AiMask(2, 1, new byte[] { 0, 255 });

        var encoded = await encoder.EncodeMaskPngAsync(mask);
        var imported = await importer.ImportMemoryAsync(new(encoded!));

        Assert.True(imported.Succeeded);
        Assert.Equal(2, imported.Image!.Width);
        Assert.Equal(1, imported.Image.Height);
        Assert.Equal(0, imported.Image.Pixels[0]);
        Assert.Equal(255, imported.Image.Pixels[4]);
    }
}
