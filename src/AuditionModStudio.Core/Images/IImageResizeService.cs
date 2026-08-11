namespace AuditionModStudio.Core.Images;

public interface IImageResizeService
{
    Task<ImageResizeResult> ResizeAsync(
        ImageResizeRequest request,
        CancellationToken cancellationToken = default);
}
