using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class ThumbnailCache(
    IAppPaths appPaths,
    IPathSecurity pathSecurity,
    IDdsPreviewService previewService,
    IImageImportService imageImportService,
    IImageResizeService imageResizeService,
    ThumbnailCacheOptions options) : IThumbnailCache
{
    private const int SchemaVersion = 1;
    private const int MaximumThumbnailDimension = 1_024;
    private const int HeaderBytes = 82;
    private static readonly byte[] Magic = "AMSTHMB1"u8.ToArray();
    private readonly ConcurrentDictionary<string, InternalImage> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _memoryOrder = new();
    private readonly Dictionary<string, KeyGate> _gates = new(StringComparer.Ordinal);
    private readonly object _gateSync = new();

    public async Task<ThumbnailCacheResult> GetOrCreateAsync(
        ThumbnailCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.Workspace is null || !request.SourceRelativePath.IsValid
            || !request.SourceSha256.IsValid || request.MaximumDimension is <= 0 or > MaximumThumbnailDimension
            || options is null || !options.IsValid)
        {
            return ThumbnailCacheResult.Failure(
                ThumbnailCacheFailureReason.InvalidRequest, "THUMBNAIL_CACHE_REQUEST_INVALID");
        }

        var key = CreateKey(request.SourceSha256, request.MaximumDimension);
        if (_memory.TryGetValue(key, out var memoryImage))
        {
            return ThumbnailCacheResult.Success(memoryImage, ThumbnailCacheSource.Memory);
        }

        KeyGateLease? gate = null;
        try
        {
            gate = await EnterGateAsync(key, cancellationToken).ConfigureAwait(false);
            if (_memory.TryGetValue(key, out memoryImage))
            {
                return ThumbnailCacheResult.Success(memoryImage, ThumbnailCacheSource.Memory);
            }

            var cacheDirectory = GetCacheDirectory();
            var cachePath = pathSecurity.ResolvePathWithinRoot(cacheDirectory, $"{key}.thumb");
            pathSecurity.EnsureNoReparsePoints(cacheDirectory, cachePath);
            var corruptRecovered = false;
            if (File.Exists(cachePath))
            {
                try
                {
                    var diskImage = await ReadEntryAsync(cachePath, request, cancellationToken).ConfigureAwait(false);
                    AddMemory(key, diskImage);
                    File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
                    return ThumbnailCacheResult.Success(diskImage, ThumbnailCacheSource.Disk);
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or InvalidDataException
                                                  or ArgumentException
                                                  or OverflowException)
                {
                    corruptRecovered = true;
                    TryDelete(cachePath);
                }
            }

            var generated = await GenerateAsync(request, cancellationToken).ConfigureAwait(false);
            if (!generated.Succeeded)
            {
                return generated;
            }

            var writeFailed = false;
            if (HeaderBytes + generated.Image!.Pixels.Length > options.MaximumDiskBytes)
            {
                writeFailed = true;
            }
            else
            {
                try
                {
                    await WriteEntryAsync(cacheDirectory, cachePath, request, generated.Image!, cancellationToken)
                        .ConfigureAwait(false);
                    EnforceDiskBudget(cacheDirectory, cachePath);
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or InvalidDataException
                                                  or InvalidOperationException
                                                  or ArgumentException)
                {
                    writeFailed = true;
                }
            }

            AddMemory(key, generated.Image!);
            return ThumbnailCacheResult.Success(
                generated.Image!, ThumbnailCacheSource.Generated, corruptRecovered, writeFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ThumbnailCacheResult.Failure(
                ThumbnailCacheFailureReason.Cancelled, "THUMBNAIL_CACHE_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return ThumbnailCacheResult.Failure(
                ThumbnailCacheFailureReason.SourcePreviewFailed, "THUMBNAIL_CACHE_FAILED");
        }
        finally
        {
            gate?.Dispose();
        }
    }

    private string GetCacheDirectory()
    {
        pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, appPaths.CacheDirectory);
        var thumbnails = pathSecurity.ResolvePathWithinRoot(appPaths.CacheDirectory, "Thumbnails");
        Directory.CreateDirectory(thumbnails);
        pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, thumbnails);
        var version = pathSecurity.ResolvePathWithinRoot(thumbnails, $"v{SchemaVersion}");
        Directory.CreateDirectory(version);
        pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, version);
        return version;
    }

    private async Task<ThumbnailCacheResult> GenerateAsync(
        ThumbnailCacheRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = request.Workspace.ArchiveWorkspace;
        var sourceRelativePath = Path.Combine(
            "Extracted",
            workspace.ExtractDirectoryRelativePath,
            request.SourceRelativePath.Value.Replace('/', Path.DirectorySeparatorChar));
        var preview = await previewService.CreateAsync(
            new(workspace.SecureWorkspace, sourceRelativePath), cancellationToken).ConfigureAwait(false);
        if (!preview.Succeeded)
        {
            return ThumbnailCacheResult.Failure(
                preview.Cancelled ? ThumbnailCacheFailureReason.Cancelled : ThumbnailCacheFailureReason.SourcePreviewFailed,
                preview.DiagnosticCode ?? "THUMBNAIL_CACHE_PREVIEW_FAILED");
        }

        var imported = await imageImportService.ImportMemoryAsync(
            new(preview.Image!.EncodedPng), cancellationToken).ConfigureAwait(false);
        if (!imported.Succeeded)
        {
            return ThumbnailCacheResult.Failure(
                imported.Cancelled ? ThumbnailCacheFailureReason.Cancelled : ThumbnailCacheFailureReason.ImageImportFailed,
                imported.DiagnosticCode ?? "THUMBNAIL_CACHE_IMPORT_FAILED");
        }

        var image = imported.Image!;
        if (image.Width <= request.MaximumDimension && image.Height <= request.MaximumDimension)
        {
            return ThumbnailCacheResult.Success(image, ThumbnailCacheSource.Generated);
        }

        var scale = Math.Min(
            (double)request.MaximumDimension / image.Width,
            (double)request.MaximumDimension / image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale, MidpointRounding.AwayFromZero));
        var resized = await imageResizeService.ResizeAsync(
            new(image, width, height, new(ImageResizeMode.Stretch, ImageInterpolationMode.Linear)),
            cancellationToken).ConfigureAwait(false);
        return resized.Succeeded
            ? ThumbnailCacheResult.Success(resized.Image!, ThumbnailCacheSource.Generated)
            : ThumbnailCacheResult.Failure(
                resized.Cancelled ? ThumbnailCacheFailureReason.Cancelled : ThumbnailCacheFailureReason.ResizeFailed,
                resized.DiagnosticCode ?? "THUMBNAIL_CACHE_RESIZE_FAILED");
    }

    private static async Task<InternalImage> ReadEntryAsync(
        string path,
        ThumbnailCacheRequest request,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var maximumPixelBytes = checked(request.MaximumDimension * request.MaximumDimension * 4L);
        if (stream.Length is < HeaderBytes || stream.Length > HeaderBytes + maximumPixelBytes)
        {
            throw new InvalidDataException("Thumbnail cache entry length is invalid.");
        }

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)
            || reader.ReadInt32() != SchemaVersion
            || reader.ReadInt32() != request.MaximumDimension
            || !reader.ReadBytes(32).AsSpan().SequenceEqual(Convert.FromHexString(request.SourceSha256.Value)))
        {
            throw new InvalidDataException("Thumbnail cache identity is invalid.");
        }

        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var stride = reader.ReadInt32();
        var format = (ImageSourceFormat)reader.ReadInt32();
        var originalWidth = reader.ReadInt32();
        var originalHeight = reader.ReadInt32();
        var orientation = (ImageSourceOrientation)reader.ReadInt32();
        var orientationNormalized = reader.ReadBoolean();
        var hasIccProfile = reader.ReadBoolean();
        var pixelLength = reader.ReadInt32();
        if (width <= 0 || height <= 0 || width > request.MaximumDimension || height > request.MaximumDimension
            || stride != checked(width * 4) || pixelLength != checked(stride * height)
            || pixelLength > maximumPixelBytes || !Enum.IsDefined(format) || !Enum.IsDefined(orientation)
            || originalWidth <= 0 || originalHeight <= 0
            || stream.Length != stream.Position + pixelLength)
        {
            throw new InvalidDataException("Thumbnail cache payload metadata is invalid.");
        }

        var pixels = new byte[pixelLength];
        await stream.ReadExactlyAsync(pixels, cancellationToken).ConfigureAwait(false);
        return new(
            width, height, stride, pixels,
            new(format, originalWidth, originalHeight, orientation, orientationNormalized, hasIccProfile));
    }

    private static async Task WriteEntryAsync(
        string directory,
        string destination,
        ThumbnailCacheRequest request,
        InternalImage image,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    writer.Write(SchemaVersion);
                    writer.Write(request.MaximumDimension);
                    writer.Write(Convert.FromHexString(request.SourceSha256.Value));
                    writer.Write(image.Width);
                    writer.Write(image.Height);
                    writer.Write(image.Stride);
                    writer.Write((int)image.SourceMetadata.Format);
                    writer.Write(image.SourceMetadata.OriginalWidth);
                    writer.Write(image.SourceMetadata.OriginalHeight);
                    writer.Write((int)image.SourceMetadata.OriginalOrientation);
                    writer.Write(image.SourceMetadata.OrientationNormalized);
                    writer.Write(image.SourceMetadata.HasIccProfile);
                    writer.Write(image.Pixels.Length);
                    writer.Flush();
                }

                await stream.WriteAsync(image.Pixels.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void AddMemory(string key, InternalImage image)
    {
        if (_memory.TryAdd(key, image))
        {
            _memoryOrder.Enqueue(key);
        }

        while (_memory.Count > options.MaximumMemoryEntries && _memoryOrder.TryDequeue(out var oldest))
        {
            _memory.TryRemove(oldest, out _);
        }
    }

    private void EnforceDiskBudget(string directory, string protectedPath)
    {
        var entries = Directory.EnumerateFiles(directory, "*.thumb", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToList();
        var totalBytes = entries.Sum(file => file.Length);
        var totalEntries = entries.Count;
        foreach (var entry in entries)
        {
            if (totalEntries <= options.MaximumDiskEntries && totalBytes <= options.MaximumDiskBytes)
            {
                break;
            }

            if (string.Equals(entry.FullName, protectedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryDelete(entry.FullName))
            {
                totalBytes -= entry.Length;
                totalEntries--;
            }
        }
    }

    private async Task<KeyGateLease> EnterGateAsync(string key, CancellationToken cancellationToken)
    {
        KeyGate gate;
        lock (_gateSync)
        {
            if (!_gates.TryGetValue(key, out gate!))
            {
                gate = new();
                _gates.Add(key, gate);
            }

            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(this, key, gate);
        }
        catch
        {
            ReleaseGateReference(key, gate, releaseSemaphore: false);
            throw;
        }
    }

    private void ReleaseGateReference(string key, KeyGate gate, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        lock (_gateSync)
        {
            gate.Users--;
            if (gate.Users == 0)
            {
                _gates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    private static string CreateKey(Sha256Digest sourceSha256, int maximumDimension)
    {
        var material = Encoding.ASCII.GetBytes($"{SchemaVersion}:{sourceSha256.Value}:{maximumDimension}");
        return Convert.ToHexStringLower(SHA256.HashData(material));
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class KeyGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class KeyGateLease(ThumbnailCache owner, string key, KeyGate gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseGateReference(key, gate, releaseSemaphore: true);
            }
        }
    }
}
