namespace AuditionModStudio.Core.Images;

public interface IImageImportService
{
    Task<ImageImportResult> ImportAsync(
        ImageImportRequest request,
        CancellationToken cancellationToken = default);

    Task<ImageImportResult> ImportMemoryAsync(
        ImageImportMemoryRequest request,
        CancellationToken cancellationToken = default);
}
