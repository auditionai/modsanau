namespace AuditionModStudio.Core.Archives;

public sealed record ArchivePackRequest(
    AuditionArchiveTemplate Archive,
    ArchiveWorkspace Workspace,
    TimeSpan Timeout);
