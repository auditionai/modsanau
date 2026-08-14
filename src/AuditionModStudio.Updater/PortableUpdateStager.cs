using System.Buffers;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace AuditionModStudio.Updater;

public sealed class PortableUpdateStager
{
    private const int BufferSize = 64 * 1024;
    private readonly HttpClient _httpClient;
    private readonly IAppUpdateManifestVerifier _manifestVerifier;
    private readonly ImmutableHashSet<string> _allowedHosts;
    private readonly string _updatesRoot;

    public PortableUpdateStager(
        HttpClient httpClient,
        IAppUpdateManifestVerifier manifestVerifier,
        IEnumerable<string> allowedHosts,
        string updatesRoot)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _manifestVerifier = manifestVerifier ?? throw new ArgumentNullException(nameof(manifestVerifier));
        _allowedHosts = allowedHosts?.Select(static host => host.Trim().TrimEnd('.').ToLowerInvariant())
            .ToImmutableHashSet(StringComparer.Ordinal) ?? throw new ArgumentNullException(nameof(allowedHosts));
        if (_allowedHosts is not { Count: > 0 and <= 8 }
            || _allowedHosts.Any(static host => !UpdateUriPolicy.IsSafeHost(host)))
            throw new ArgumentException("PORTABLE_UPDATE_HOST_POLICY_INVALID", nameof(allowedHosts));
        if (_httpClient.Timeout == Timeout.InfiniteTimeSpan || _httpClient.Timeout <= TimeSpan.Zero
            || _httpClient.Timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentException("PORTABLE_UPDATE_TIMEOUT_INVALID", nameof(httpClient));
        if (!Path.IsPathFullyQualified(updatesRoot))
            throw new ArgumentException("PORTABLE_UPDATE_ROOT_INVALID", nameof(updatesRoot));
        _updatesRoot = Path.GetFullPath(updatesRoot);
    }

    public async Task<PortableUpdateStageResult> StageAsync(
        ReadOnlyMemory<byte> signedEnvelope,
        Version currentVersion,
        IProgress<PortableUpdateStageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? operationRoot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (currentVersion is null || currentVersion.Build < 0 || signedEnvelope.IsEmpty)
                return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_REQUEST_INVALID");
            if (!UpdateManifestCodec.TryReadEnvelope(signedEnvelope.Span, out var envelope))
                return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_ENVELOPE_INVALID");
            if (!_manifestVerifier.Verify(envelope!.Payload.AsSpan(), envelope.Signature.AsSpan()))
                return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_SIGNATURE_INVALID");
            if (!PortableUpdateManifestCodec.TryReadVerifiedPayload(envelope.Payload.AsSpan(), out var manifest))
                return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_MANIFEST_INVALID");
            if (manifest!.Version.CompareTo(currentVersion) <= 0)
                return PortableUpdateStageResult.Failure(manifest.Version == currentVersion
                    ? "PORTABLE_UPDATE_CURRENT"
                    : "PORTABLE_UPDATE_DOWNGRADE_REJECTED");
            if (!_allowedHosts.Contains(manifest.Package.Uri.IdnHost.TrimEnd('.').ToLowerInvariant())
                || manifest.Package.Uri.Port != 443)
                return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_URI_REJECTED");

            EnsureSafeDirectory(_updatesRoot);
            CleanupStaleOperations();
            var versionRoot = Path.Combine(_updatesRoot, manifest.Version.ToString());
            EnsureSafeDirectory(versionRoot);
            var expandedBytes = ExpandedLength(manifest.Package.Inventory);
            var requiredBytes = checked(manifest.Package.Length + expandedBytes + 64L * 1024 * 1024);
            var drive = new DriveInfo(Path.GetPathRoot(versionRoot)!);
            if (drive.AvailableFreeSpace < requiredBytes)
                return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_DISK_SPACE_INSUFFICIENT");
            operationRoot = Path.Combine(versionRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationRoot);
            EnsureNoReparse(operationRoot);

            var envelopePath = Path.Combine(operationRoot, "manifest.signed.json");
            await File.WriteAllBytesAsync(envelopePath, signedEnvelope.ToArray(), cancellationToken).ConfigureAwait(false);
            var partialPath = Path.Combine(operationRoot, "package.zip.partial");
            var packagePath = Path.Combine(operationRoot, "package.zip");
            var download = await DownloadAsync(manifest.Package, partialPath, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!download.Succeeded)
            {
                TryDelete(operationRoot);
                return PortableUpdateStageResult.Failure(download.Code);
            }
            File.Move(partialPath, packagePath, false);

            var extractedRoot = Path.Combine(operationRoot, "extracted");
            Directory.CreateDirectory(extractedRoot);
            progress?.Report(new(PortableUpdateStage.Extracting, 0, expandedBytes));
            var extraction = await ExtractAndVerifyAsync(packagePath, extractedRoot, manifest.Package,
                progress, cancellationToken).ConfigureAwait(false);
            if (!extraction.Succeeded)
            {
                TryDelete(operationRoot);
                return PortableUpdateStageResult.Failure(extraction.Code);
            }

            progress?.Report(new(PortableUpdateStage.Ready, manifest.Package.Length, manifest.Package.Length));
            return PortableUpdateStageResult.Success(new(manifest, operationRoot, envelopePath, packagePath,
                extractedRoot, download.Sha256!));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (operationRoot is not null) TryDelete(operationRoot);
            return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_CANCELLED");
        }
        catch (OperationCanceledException)
        {
            if (operationRoot is not null) TryDelete(operationRoot);
            return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_NETWORK_FAILED");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                   or InvalidDataException or OverflowException)
        {
            if (operationRoot is not null) TryDelete(operationRoot);
            return PortableUpdateStageResult.Failure("PORTABLE_UPDATE_STAGE_FAILED");
        }
    }

    private async Task<(bool Succeeded, string Code, string? Sha256)> DownloadAsync(
        PortableUpdatePackage package,
        string partialPath,
        IProgress<PortableUpdateStageProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (File.Exists(partialPath)) File.Delete(partialPath);
                progress?.Report(new(PortableUpdateStage.Downloading, 0, package.Length));
                using var request = new HttpRequestMessage(HttpMethod.Get, package.Uri);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (IsTransient(response.StatusCode) && attempt < 3) continue;
                if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != package.Uri
                    || response.Content.Headers.ContentLength is long length && length != package.Length)
                    return (false, "PORTABLE_UPDATE_DOWNLOAD_RESPONSE_INVALID", null);

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                long total = 0;
                try
                {
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0) break;
                        total = checked(total + read);
                        if (total > package.Length) return (false, "PORTABLE_UPDATE_LENGTH_MISMATCH", null);
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        progress?.Report(new(PortableUpdateStage.Downloading, total, package.Length));
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(true);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer.AsSpan(0, BufferSize));
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (total != package.Length) return (false, "PORTABLE_UPDATE_LENGTH_MISMATCH", null);
                progress?.Report(new(PortableUpdateStage.Verifying, total, total));
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                return string.Equals(actual, package.Sha256, StringComparison.Ordinal)
                    ? (true, "PORTABLE_UPDATE_DOWNLOAD_VERIFIED", actual)
                    : (false, "PORTABLE_UPDATE_HASH_MISMATCH", null);
            }
            catch (HttpRequestException) when (attempt < 3)
            {
            }
            catch (IOException) when (attempt < 3)
            {
            }
        }
        return (false, "PORTABLE_UPDATE_NETWORK_FAILED", null);
    }

    private static async Task<(bool Succeeded, string Code)> ExtractAndVerifyAsync(
        string packagePath,
        string extractedRoot,
        PortableUpdatePackage package,
        IProgress<PortableUpdateStageProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count is < 2 or > PortableUpdateManifestCodec.MaximumInventoryEntries)
            return (false, "PORTABLE_UPDATE_ZIP_ENTRY_COUNT_INVALID");
        var expected = package.Inventory.ToDictionary(static file => file.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.FullName.Replace('\\', '/');
            if (path.EndsWith("/", StringComparison.Ordinal)) continue;
            if (!PortableUpdateManifestCodec.ValidRelativePath(path) || !seen.Add(path)
                || !expected.TryGetValue(path, out var expectedFile)
                || IsLink(entry) || entry.Length != expectedFile.Length)
                return (false, "PORTABLE_UPDATE_ZIP_STRUCTURE_INVALID");
            total = checked(total + entry.Length);
            if (total > PortableUpdateManifestCodec.MaximumExpandedBytes)
                return (false, "PORTABLE_UPDATE_ZIP_EXPANSION_LIMIT");

            var destination = ResolveUnderRoot(extractedRoot, path);
            var parent = Path.GetDirectoryName(destination)!;
            EnsureSafeDirectory(parent);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long written = 0;
            try
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    written = checked(written + read);
                    if (written > expectedFile.Length) return (false, "PORTABLE_UPDATE_FILE_LENGTH_MISMATCH");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, BufferSize));
                ArrayPool<byte>.Shared.Return(buffer);
            }
            if (written != expectedFile.Length
                || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedFile.Sha256,
                    StringComparison.Ordinal))
                return (false, "PORTABLE_UPDATE_FILE_HASH_MISMATCH");
            progress?.Report(new(PortableUpdateStage.Extracting, total, ExpandedLength(package.Inventory)));
        }
        progress?.Report(new(PortableUpdateStage.ValidatingInventory, total, total));
        return seen.SetEquals(expected.Keys)
            ? (true, "PORTABLE_UPDATE_INVENTORY_VALID")
            : (false, "PORTABLE_UPDATE_INVENTORY_MISSING");
    }

    internal static string ResolveUnderRoot(string root, string relativePath)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PORTABLE_UPDATE_PATH_ESCAPE");
        return candidate;
    }

    private static void EnsureSafeDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("PORTABLE_UPDATE_REPARSE_REJECTED");
            current = current.Parent;
        }
    }

    private static void EnsureNoReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("PORTABLE_UPDATE_REPARSE_REJECTED");
    }

    private static bool IsLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode is 0xA000 or 0x6000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsTransient(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static long ExpandedLength(ImmutableArray<PortablePackageFile> inventory) =>
        inventory.Aggregate(0L, static (total, file) => checked(total + file.Length));

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void CleanupStaleOperations()
    {
        var threshold = DateTime.UtcNow.AddDays(-14);
        foreach (var versionDirectory in new DirectoryInfo(_updatesRoot).EnumerateDirectories())
        {
            if (!Version.TryParse(versionDirectory.Name, out _)
                || (versionDirectory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var operation in versionDirectory.EnumerateDirectories())
            {
                if (operation.Name.Length != 32 || !Guid.TryParseExact(operation.Name, "N", out _)
                    || operation.LastWriteTimeUtc >= threshold
                    || (operation.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                TryDelete(operation.FullName);
            }
        }
    }
}
