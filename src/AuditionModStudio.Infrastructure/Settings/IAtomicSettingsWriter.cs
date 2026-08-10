namespace AuditionModStudio.Infrastructure.Settings;

public interface IAtomicSettingsWriter
{
    Task WriteAsync(
        string destinationFileName,
        string? backupFileName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);
}
