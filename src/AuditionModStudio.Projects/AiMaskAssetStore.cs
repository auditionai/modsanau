using System.Buffers.Binary;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Projects;
using Microsoft.Extensions.Logging;

namespace AuditionModStudio.Projects;

public sealed class AiMaskAssetStore(ILogger<AiMaskAssetStore>? logger = null) : IAiMaskAssetStore
{
    public const string MaskRelativePath = "ai/masks/current.amsmask";
    private static ReadOnlySpan<byte> Magic => "AMSMASK1"u8;

    public async Task<AiMaskAssetResult> SaveAsync(
        IProjectArchiveWorkspace workspace,
        AiMask mask,
        CancellationToken cancellationToken = default)
    {
        if (workspace is null || mask is null)
            return new(false, "AI_MASK_SAVE_INVALID", null);
        string targetPath;
        try
        {
            targetPath = workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(MaskRelativePath);
        }
        catch (ArgumentException)
        {
            return new(false, "AI_MASK_PATH_INVALID", null);
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory)) return new(false, "AI_MASK_PATH_INVALID", null);
        var temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var header = new byte[20];
                Magic.CopyTo(header);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), mask.Width);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), mask.Height);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), mask.Opacity.Length);
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(mask.Opacity.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, true);
            return new(true, "AI_MASK_SAVED", MaskRelativePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return new(false, "AI_MASK_SAVE_FAILED", null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(false, "AI_MASK_SAVE_FAILED", null);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning("AI mask temporary cleanup failed with {ExceptionType}",
                    exception.GetType().Name);
            }
        }
    }
}
