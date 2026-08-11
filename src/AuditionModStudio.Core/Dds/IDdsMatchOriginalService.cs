namespace AuditionModStudio.Core.Dds;

public interface IDdsMatchOriginalService
{
    DdsMatchOriginalProfileResult DeriveProfile(DdsMetadata metadata);

    Task<DdsMatchOriginalResult> MatchAsync(
        DdsMatchOriginalRequest request,
        CancellationToken cancellationToken = default);
}
