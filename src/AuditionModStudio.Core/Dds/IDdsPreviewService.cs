namespace AuditionModStudio.Core.Dds;

public interface IDdsPreviewService
{
    Task<DdsPreviewResult> CreateAsync(
        DdsPreviewRequest request,
        CancellationToken cancellationToken = default);
}
