using AuditionModStudio.Core.Archives;

namespace AuditionModStudio.Archives;

public interface IArchiveEngine
{
    ArchiveEngineType EngineType { get; }

    Task<ArchiveCommandResult> ExtractAsync(
        ArchiveExtractRequest request,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken);

    Task<ArchiveCommandResult> PackAsync(
        ArchivePackRequest request,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken);
}
