using System.Diagnostics;
using System.Security.Cryptography;

namespace AuditionModStudio.Updater;

public sealed class PortableUpdateInstallEngine
{
    private readonly IAppUpdateManifestVerifier _manifestVerifier;
    private readonly IPortableInstallFailureInjector _failureInjector;

    public PortableUpdateInstallEngine(
        IAppUpdateManifestVerifier manifestVerifier,
        IPortableInstallFailureInjector? failureInjector = null)
    {
        _manifestVerifier = manifestVerifier ?? throw new ArgumentNullException(nameof(manifestVerifier));
        _failureInjector = failureInjector ?? NoPortableInstallFailureInjector.Instance;
    }

    public async Task<PortableInstallResult> InstallAsync(
        PortableInstallRequest request,
        TimeSpan parentExitTimeout,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateRequest(request, out var installRoot, out var stagingRoot))
            return new(false, false, "PORTABLE_INSTALL_REQUEST_INVALID");

        FileStream? updateLock = null;
        var lockPath = Path.Combine(installRoot, ".portable-update.lock");
        try
        {
            try
            {
                updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                return new(false, false, "PORTABLE_INSTALL_ALREADY_RUNNING");
            }

            if (!await WaitForExitAsync(request.ParentProcessId, parentExitTimeout, cancellationToken).ConfigureAwait(false))
                return new(false, false, "PORTABLE_INSTALL_PARENT_TIMEOUT");
            cancellationToken.ThrowIfCancellationRequested();

            var envelopePath = Path.Combine(stagingRoot, "manifest.signed.json");
            var packagePath = Path.Combine(stagingRoot, "package.zip");
            var extractedRoot = Path.Combine(stagingRoot, "extracted");
            var envelopeBytes = await File.ReadAllBytesAsync(envelopePath, cancellationToken).ConfigureAwait(false);
            if (!UpdateManifestCodec.TryReadEnvelope(envelopeBytes, out var envelope)
                || !_manifestVerifier.Verify(envelope!.Payload.AsSpan(), envelope.Signature.AsSpan())
                || !PortableUpdateManifestCodec.TryReadVerifiedPayload(envelope.Payload.AsSpan(), out var manifest)
                || !VersionsEqual(manifest!.Version, request.ExpectedVersion))
                return new(false, false, "PORTABLE_INSTALL_MANIFEST_REJECTED");
            if (!await VerifyFileAsync(packagePath, manifest.Package.Length, manifest.Package.Sha256, cancellationToken)
                    .ConfigureAwait(false)
                || !await VerifyInventoryAsync(extractedRoot, manifest.Package.Inventory, cancellationToken)
                    .ConfigureAwait(false))
                return new(false, false, "PORTABLE_INSTALL_STAGING_REJECTED");

            var backupRoot = Path.Combine(stagingRoot, "rollback");
            Directory.CreateDirectory(backupRoot);
            var touched = manifest.Package.Inventory.Select(static file => file.Path)
                .Concat(manifest.Package.RemoveOwnedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var originallyPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relativePath in touched)
            {
                var destination = PortableUpdateStager.ResolveUnderRoot(installRoot, relativePath);
                if (!File.Exists(destination)) continue;
                RejectReparse(destination);
                var backup = PortableUpdateStager.ResolveUnderRoot(backupRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(destination, backup, false);
                originallyPresent.Add(relativePath);
            }
            _failureInjector.OnCheckpoint("BACKUP_COMPLETE", string.Empty);

            try
            {
                foreach (var file in manifest.Package.Inventory)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = PortableUpdateStager.ResolveUnderRoot(extractedRoot, file.Path);
                    var destination = PortableUpdateStager.ResolveUnderRoot(installRoot, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    RejectExistingAncestorReparse(installRoot, destination);
                    var temporary = destination + ".update-new";
                    if (File.Exists(temporary)) File.Delete(temporary);
                    File.Copy(source, temporary, false);
                    File.Move(temporary, destination, true);
                    _failureInjector.OnCheckpoint("FILE_REPLACED", file.Path);
                }

                foreach (var relativePath in manifest.Package.RemoveOwnedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var obsolete = PortableUpdateStager.ResolveUnderRoot(installRoot, relativePath);
                    if (File.Exists(obsolete)) File.Delete(obsolete);
                    _failureInjector.OnCheckpoint("OWNED_FILE_REMOVED", relativePath);
                }

                _failureInjector.OnCheckpoint("REPLACEMENT_COMPLETE", string.Empty);
                if (!await VerifyInventoryAsync(installRoot, manifest.Package.Inventory, cancellationToken)
                        .ConfigureAwait(false))
                    throw new IOException("PORTABLE_INSTALL_POST_VERIFY_FAILED");
                var primary = PortableUpdateStager.ResolveUnderRoot(installRoot, PortableUpdateProduct.PrimaryExecutable);
                if (!File.Exists(primary)) throw new IOException("PORTABLE_INSTALL_PRIMARY_MISSING");
                _failureInjector.OnCheckpoint("POST_INSTALL_VERIFIED", string.Empty);
                return new(true, false, "PORTABLE_INSTALL_SUCCESS", primary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or OperationCanceledException)
            {
                var restored = Restore(installRoot, backupRoot, touched, originallyPresent);
                return new(false, restored, restored
                    ? "PORTABLE_INSTALL_FAILED_ROLLED_BACK"
                    : "PORTABLE_INSTALL_FAILED_ROLLBACK_INCOMPLETE");
            }
        }
        catch (OperationCanceledException)
        {
            return new(false, false, "PORTABLE_INSTALL_CANCELLED_BEFORE_CRITICAL_SECTION");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or CryptographicException or InvalidDataException)
        {
            return new(false, false, "PORTABLE_INSTALL_FAILED");
        }
        finally
        {
            updateLock?.Dispose();
            try { if (File.Exists(lockPath)) File.Delete(lockPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static bool IsInstallDirectoryWritable(string installDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(installDirectory)) return false;
            var root = Path.GetFullPath(installDirectory);
            var probe = Path.Combine(root, $".update-write-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1, FileOptions.DeleteOnClose)) { }
            return !File.Exists(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryValidateRequest(PortableInstallRequest request, out string installRoot, out string stagingRoot)
    {
        installRoot = string.Empty;
        stagingRoot = string.Empty;
        try
        {
            if (request.ParentProcessId <= 0 || request.ExpectedVersion is null
                || request.ExpectedVersion.Build < 0
                || request.RestartExecutableName != PortableUpdateProduct.PrimaryExecutable
                || !Path.IsPathFullyQualified(request.InstallDirectory)
                || !Path.IsPathFullyQualified(request.StagingDirectory)) return false;
            installRoot = Path.GetFullPath(request.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
            stagingRoot = Path.GetFullPath(request.StagingDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (installRoot.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase)
                || stagingRoot.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(installRoot) || !Directory.Exists(stagingRoot)) return false;
            RejectExistingAncestorReparse(installRoot, Path.Combine(installRoot, PortableUpdateProduct.PrimaryExecutable));
            RejectExistingAncestorReparse(stagingRoot, Path.Combine(stagingRoot, "manifest.signed.json"));
            return File.Exists(Path.Combine(installRoot, PortableUpdateProduct.PrimaryExecutable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (TimeoutException) { return false; }
    }

    private static async Task<bool> VerifyInventoryAsync(
        string root,
        IEnumerable<PortablePackageFile> inventory,
        CancellationToken cancellationToken)
    {
        foreach (var file in inventory)
        {
            var path = PortableUpdateStager.ResolveUnderRoot(root, file.Path);
            if (!await VerifyFileAsync(path, file.Length, file.Sha256, cancellationToken).ConfigureAwait(false))
                return false;
        }
        return true;
    }

    private static async Task<bool> VerifyFileAsync(
        string path,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expectedLength || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            return false;
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(actual, expectedSha256, StringComparison.Ordinal);
    }

    private static bool Restore(
        string installRoot,
        string backupRoot,
        IEnumerable<string> touched,
        HashSet<string> originallyPresent)
    {
        var restored = true;
        foreach (var relativePath in touched.Reverse())
        {
            try
            {
                var destination = PortableUpdateStager.ResolveUnderRoot(installRoot, relativePath);
                var temporary = destination + ".update-new";
                if (File.Exists(temporary)) File.Delete(temporary);
                if (originallyPresent.Contains(relativePath))
                {
                    var backup = PortableUpdateStager.ResolveUnderRoot(backupRoot, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(backup, destination, true);
                }
                else if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { restored = false; }
        }
        return restored;
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("PORTABLE_INSTALL_REPARSE_REJECTED");
    }

    private static void RejectExistingAncestorReparse(string root, string path)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = new FileInfo(path).Directory;
        while (current is not null && current.FullName.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("PORTABLE_INSTALL_REPARSE_REJECTED");
            if (current.FullName.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent;
        }
    }

    private static bool VersionsEqual(Version left, Version right) =>
        left.Major == right.Major && left.Minor == right.Minor && left.Build == right.Build
        && Math.Max(left.Revision, 0) == Math.Max(right.Revision, 0);
}
