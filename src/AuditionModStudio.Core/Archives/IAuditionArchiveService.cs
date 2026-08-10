namespace AuditionModStudio.Core.Archives;

public interface IAuditionArchiveService
{
    Task<ArchiveExtractResult> ExtractAsync(
        ArchiveExtractRequest request,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ArchivePackResult> PackAsync(
        ArchivePackRequest request,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
