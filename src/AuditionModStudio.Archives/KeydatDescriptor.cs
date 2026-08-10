namespace AuditionModStudio.Archives;

public sealed record KeydatDescriptor(
    string ArchivePath,
    string KeydatPath,
    KeydatStatus Status,
    long? Length,
    string? Sha256);
