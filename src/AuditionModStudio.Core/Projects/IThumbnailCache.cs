using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Projects;

public interface IThumbnailCache
{
    Task<ThumbnailCacheResult> GetOrCreateAsync(
        ThumbnailCacheRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ThumbnailCacheRequest(
    IProjectArchiveWorkspace Workspace,
    ModRelativePath SourceRelativePath,
    Sha256Digest SourceSha256,
    int MaximumDimension);

public enum ThumbnailCacheSource
{
    None,
    Memory,
    Disk,
    Generated
}

public enum ThumbnailCacheFailureReason
{
    None,
    InvalidRequest,
    SourcePreviewFailed,
    ImageImportFailed,
    ResizeFailed,
    Cancelled
}

public sealed record ThumbnailCacheResult(
    bool Succeeded,
    bool Cancelled,
    ThumbnailCacheFailureReason FailureReason,
    string? DiagnosticCode,
    InternalImage? Image,
    ThumbnailCacheSource Source,
    bool CorruptEntryRecovered,
    bool CacheWriteFailed)
{
    public static ThumbnailCacheResult Success(
        InternalImage image,
        ThumbnailCacheSource source,
        bool corruptEntryRecovered = false,
        bool cacheWriteFailed = false) =>
        new(true, false, ThumbnailCacheFailureReason.None, null, image, source,
            corruptEntryRecovered, cacheWriteFailed);

    public static ThumbnailCacheResult Failure(ThumbnailCacheFailureReason reason, string code) =>
        new(false, reason == ThumbnailCacheFailureReason.Cancelled, reason, code, null,
            ThumbnailCacheSource.None, false, false);
}

public sealed record ThumbnailCacheOptions(
    int MaximumMemoryEntries,
    int MaximumDiskEntries,
    long MaximumDiskBytes)
{
    public static ThumbnailCacheOptions Default { get; } = new(256, 4_096, 512L * 1024 * 1024);

    public bool IsValid => MaximumMemoryEntries is > 0 and <= 4_096
                           && MaximumDiskEntries is > 0 and <= 100_000
                           && MaximumDiskBytes is >= 1024 * 1024 and <= 4L * 1024 * 1024 * 1024;
}
