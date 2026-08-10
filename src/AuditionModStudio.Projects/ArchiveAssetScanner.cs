using System.Buffers;
using System.Security.Cryptography;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class ArchiveAssetScanner(IPathSecurity pathSecurity) : IArchiveAssetScanner
{
    private static readonly StringComparer IdentityComparer = StringComparer.OrdinalIgnoreCase;

    public async Task<ArchiveAssetScanResult> ScanAsync(
        IProjectArchiveWorkspace workspace,
        IProgress<ArchiveAssetScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var extractedBase = workspace.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory;
        string extractedRoot;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            extractedRoot = pathSecurity.ResolvePathWithinRoot(
                extractedBase,
                workspace.ArchiveWorkspace.ExtractDirectoryRelativePath);
            if (!Directory.Exists(extractedRoot))
            {
                return Failure(ArchiveAssetScanFailureReason.ExtractedDirectoryMissing, "asset_scan.extracted_missing");
            }

            pathSecurity.EnsureNoReparsePoints(extractedBase, extractedRoot);
            var files = new List<(string FullPath, string RelativePath)>();
            var directoryCount = Enumerate(extractedRoot, extractedRoot, files, cancellationToken);
            files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));

            var assets = new List<ArchiveAsset>(files.Count);
            var identities = new HashSet<string>(IdentityComparer);
            long totalBytes = 0;
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                if (!identities.Add(file.RelativePath))
                {
                    return Failure(ArchiveAssetScanFailureReason.DuplicateIdentity, "asset_scan.duplicate_identity");
                }

                progress?.Report(new(files.Count, index, file.RelativePath));
                pathSecurity.EnsureNoReparsePoints(extractedRoot, file.FullPath);
                var info = new FileInfo(file.FullPath);
                var hash = await ComputeSha256Async(file.FullPath, cancellationToken).ConfigureAwait(false);
                var kind = Classify(info.Extension);
                var directory = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
                ArchiveAsset asset = kind == ArchiveAssetKind.Dds
                    ? new TextureAsset(file.RelativePath, info.Name, info.Extension, directory, info.Length, info.LastWriteTimeUtc, hash)
                    : new ArchiveAsset(file.RelativePath, info.Name, info.Extension, directory, info.Length, info.LastWriteTimeUtc, hash, kind);
                assets.Add(asset);
                totalBytes = checked(totalBytes + info.Length);
                progress?.Report(new(files.Count, index + 1, file.RelativePath));
            }

            return ArchiveAssetScanResult.Success(new(assets, directoryCount, totalBytes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(ArchiveAssetScanFailureReason.Cancelled, "asset_scan.cancelled");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(ArchiveAssetScanFailureReason.AccessDenied, "asset_scan.access_denied");
        }
        catch (InvalidOperationException)
        {
            return Failure(ArchiveAssetScanFailureReason.ReparsePointRejected, "asset_scan.reparse_point");
        }
        catch (ArgumentException)
        {
            return Failure(ArchiveAssetScanFailureReason.InvalidExtractedRoot, "asset_scan.invalid_root");
        }
        catch (IOException)
        {
            return Failure(ArchiveAssetScanFailureReason.FileReadFailed, "asset_scan.file_read_failed");
        }
    }

    private int Enumerate(
        string root,
        string directory,
        List<(string FullPath, string RelativePath)> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pathSecurity.EnsureNoReparsePoints(root, directory);
        var count = directory == root ? 0 : 1;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Reparse points are not permitted while scanning assets.");
            }

            var relative = NormalizeRelativePath(Path.GetRelativePath(root, entry));
            var resolved = pathSecurity.ResolvePathWithinRoot(root, relative);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                count += Enumerate(root, resolved, files, cancellationToken);
            }
            else
            {
                files.Add((resolved, relative));
            }
        }

        return count;
    }

    private static string NormalizeRelativePath(string path) => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static ArchiveAssetKind Classify(string extension) => extension.ToUpperInvariant() switch
    {
        ".DDS" => ArchiveAssetKind.Dds,
        ".PNG" => ArchiveAssetKind.Png,
        ".SLK" => ArchiveAssetKind.Slk,
        ".RGM" => ArchiveAssetKind.Rgm,
        _ => ArchiveAssetKind.Other,
    };

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ArchiveAssetScanResult Failure(ArchiveAssetScanFailureReason reason, string code) =>
        ArchiveAssetScanResult.Failure(reason, code);
}
