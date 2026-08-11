namespace AuditionModStudio.Core.Images;

public interface IImageImportService
{
    Task<ImageImportResult> ImportAsync(
        ImageImportRequest request,
        CancellationToken cancellationToken = default);
}
