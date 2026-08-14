namespace AuditionModStudio.Core.Images;

public interface IAlphaChannelService
{
    Task<AlphaChannelResult> ProcessAsync(
        AlphaChannelRequest request,
        CancellationToken cancellationToken = default);
}
