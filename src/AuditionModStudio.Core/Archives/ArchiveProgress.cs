namespace AuditionModStudio.Core.Archives;

public sealed record ArchiveProgress(
    ArchiveOperation Operation,
    ArchiveOperationState State,
    int ProcessedItemCount,
    string? CurrentItemRelativePath = null);
