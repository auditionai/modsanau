using AuditionModStudio.AI;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Images;

namespace IntegrationTests;

public sealed class AiProviderAbstractionTests
{
    [Fact]
    public async Task All_operations_fail_closed_without_a_trusted_backend()
    {
        IAiService service = new UnavailableAiService();
        var image = Image();
        var mask = Image();
        var prompt = new AiPrompt("edit safely");
        var target = new AiTargetSize(3, 5);
        var operations = new Task<AiImageResult>[]
        {
            service.GenerateAsync(new(prompt, target)),
            service.EditAsync(new(image, prompt)),
            service.InpaintAsync(new(image, mask, prompt)),
            service.OutpaintAsync(new(image, prompt, target)),
            service.RemoveObjectAsync(new(image, mask)),
            service.ReplaceObjectAsync(new(image, mask, prompt)),
            service.UpscaleAsync(new(image, target)),
        };

        var results = await Task.WhenAll(operations);

        Assert.All(results, result =>
        {
            Assert.False(result.Succeeded);
            Assert.False(result.Cancelled);
            Assert.Equal(AiServiceFailureReason.Unavailable, result.FailureReason);
            Assert.Equal("AI_TRUSTED_BACKEND_UNAVAILABLE", result.DiagnosticCode);
            Assert.Null(result.Image);
        });
    }

    [Fact]
    public async Task Pre_cancelled_operation_returns_typed_cancellation()
    {
        IAiService service = new UnavailableAiService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.GenerateAsync(
            new(new("generate"), new(1, 1)),
            cancellationToken: cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(AiServiceFailureReason.Cancelled, result.FailureReason);
        Assert.Equal("AI_OPERATION_CANCELLED", result.DiagnosticCode);
    }

    [Fact]
    public void Contract_exposes_exact_required_operation_set()
    {
        var operations = typeof(IAiService).GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "EditAsync",
                "GenerateAsync",
                "InpaintAsync",
                "OutpaintAsync",
                "RemoveObjectAsync",
                "ReplaceObjectAsync",
                "UpscaleAsync",
            ],
            operations);
    }

    private static InternalImage Image() => new(
        1,
        1,
        4,
        [10, 20, 30, 255],
        new(ImageSourceFormat.Png, 1, 1, ImageSourceOrientation.Normal, true, false));
}
