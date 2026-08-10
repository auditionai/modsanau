namespace AuditionModStudio.Core.Dds;

public interface IDdsMetadataReader
{
    Task<DdsMetadataReadResult> ReadAsync(string path, CancellationToken cancellationToken = default);
}
