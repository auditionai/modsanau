namespace AuditionModStudio.Core.Dds;

public interface IDdsEncoder
{
    Task<DdsEncodeResult> EncodeAsync(
        DdsEncodeRequest request,
        CancellationToken cancellationToken = default);
}
