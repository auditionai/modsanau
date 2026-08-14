namespace AuditionModStudio.Core.Images;

public interface IImageAdjustmentService
{
    Task<ImageAdjustmentResult> AdjustAsync(
        ImageAdjustmentRequest request,
        CancellationToken cancellationToken = default);
}
