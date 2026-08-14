namespace AuditionModStudio.Core.Dds;

public interface IDdsValidationService
{
    Task<DdsValidationResult> ValidateAsync(
        DdsValidationRequest request,
        CancellationToken cancellationToken = default);
}
