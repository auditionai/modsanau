using AuditionModStudio.Core.Dds;
using AuditionModStudio.Dds;

namespace Dds.Tests;

public sealed class DirectXTexCommandBuilderTests
{
    [Fact]
    public void Decode_uses_structured_png_arguments_and_option_terminator()
    {
        var request = CreateRequest(DirectXTexEvaluationOperation.DecodeToPng);

        var info = DirectXTexCommandBuilder.Create(
            @"C:\trusted tool\texconv.exe",
            @"C:\isolated workspace",
            request,
            @"C:\isolated workspace\Extracted\ảnh.dds",
            @"C:\isolated workspace\BuildOutput\preview");

        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(@"C:\trusted tool\texconv.exe", info.FileName);
        Assert.Equal(
            [
                "-nologo", "-y", "-ft", "png", "-o",
                @"C:\isolated workspace\BuildOutput\preview", "--",
                @"C:\isolated workspace\Extracted\ảnh.dds",
            ],
            info.ArgumentList);
    }

    [Fact]
    public void Encode_uses_bc3_exact_size_mips_and_legacy_header_switch()
    {
        var request = CreateRequest(DirectXTexEvaluationOperation.EncodeBc3) with
        {
            TargetWidth = 6000,
            TargetHeight = 1801,
            MipLevelCount = 1,
            ForceLegacyHeader = true,
        };

        var info = DirectXTexCommandBuilder.Create(
            @"C:\tool\texconv.exe",
            @"C:\work",
            request,
            @"C:\work\input.png",
            @"C:\work\out");

        Assert.Equal(
            [
                "-nologo", "-y", "-ft", "dds", "-f", "BC3_UNORM",
                "-w", "6000", "-h", "1801", "-m", "1", "-dx9",
                "-o", @"C:\work\out", "--", @"C:\work\input.png",
            ],
            info.ArgumentList);
        Assert.DoesNotContain("-pow2", info.ArgumentList);
    }

    private static DirectXTexEvaluationRequest CreateRequest(DirectXTexEvaluationOperation operation) => new(
        operation,
        null!,
        @"C:\tool\texconv.exe",
        @"Extracted\input.dds",
        "evaluation",
        null,
        null,
        1,
        false,
        TimeSpan.FromSeconds(30));
}
