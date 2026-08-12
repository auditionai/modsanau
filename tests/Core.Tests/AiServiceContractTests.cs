using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Images;

namespace Core.Tests;

public sealed class AiServiceContractTests
{
    [Fact]
    public void Prompt_is_trimmed_and_rejects_empty_oversized_or_unsafe_control_text()
    {
        Assert.Equal("make it blue", new AiPrompt("  make it blue  ").Value);
        Assert.Throws<ArgumentException>(() => new AiPrompt("   "));
        Assert.Throws<ArgumentException>(() => new AiPrompt(new string('a', AiPrompt.MaximumLength + 1)));
        Assert.Throws<ArgumentException>(() => new AiPrompt("unsafe\0prompt"));
        Assert.True(new AiPrompt("line one\nline two").IsValid);
    }

    [Fact]
    public void Target_size_is_bounded_without_power_of_two_requirement()
    {
        var target = new AiTargetSize(6000, 1801);

        Assert.True(target.IsValid);
        Assert.Equal(6000, target.Width);
        Assert.Equal(1801, target.Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiTargetSize(0, 128));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiTargetSize(128, AiTargetSize.MaximumDimension + 1));
    }

    [Fact]
    public void Success_and_terminal_failures_have_consistent_payloads()
    {
        var image = Image();

        var success = AiImageResult.Success(image);
        var failure = AiImageResult.Failure(AiServiceFailureReason.Rejected, "AI_REJECTED");
        var cancelled = AiImageResult.CancelledResult();

        Assert.True(success.Succeeded);
        Assert.Same(image, success.Image);
        Assert.Null(failure.Image);
        Assert.False(failure.Cancelled);
        Assert.True(cancelled.Cancelled);
        Assert.False(cancelled.Succeeded);
        Assert.Null(cancelled.Image);
    }

    private static InternalImage Image() => new(
        1,
        1,
        4,
        [10, 20, 30, 255],
        new(ImageSourceFormat.Png, 1, 1, ImageSourceOrientation.Normal, true, false));
}
