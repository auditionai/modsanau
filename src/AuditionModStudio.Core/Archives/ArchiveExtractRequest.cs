namespace AuditionModStudio.Core.Archives;

public sealed record ArchiveExtractRequest(
    AuditionArchiveTemplate Archive,
    ArchiveWorkspace Workspace,
    PristineArchiveSource Source,
    TimeSpan Timeout);
