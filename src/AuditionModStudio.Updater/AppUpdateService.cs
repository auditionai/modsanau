using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace AuditionModStudio.Updater;

public sealed class AppUpdateService : IAppUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly IAppUpdateManifestVerifier _manifestVerifier;
    private readonly IAuthenticodeUpdateVerifier _authenticodeVerifier;
    private readonly IVerifiedAppUpdateInstaller _installer;
    private readonly string _stagingRoot;

    public AppUpdateService(HttpClient httpClient, IAppUpdateManifestVerifier manifestVerifier,
        IAuthenticodeUpdateVerifier authenticodeVerifier, IVerifiedAppUpdateInstaller installer,
        string stagingRoot)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _manifestVerifier = manifestVerifier ?? throw new ArgumentNullException(nameof(manifestVerifier));
        _authenticodeVerifier = authenticodeVerifier ?? throw new ArgumentNullException(nameof(authenticodeVerifier));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        if (!Path.IsPathFullyQualified(stagingRoot)) throw new ArgumentException("UPDATE_STAGING_ROOT_INVALID", nameof(stagingRoot));
        _stagingRoot = Path.GetFullPath(stagingRoot);
    }

    public async Task<AppUpdateResult> VerifyAndInstallAsync(AppUpdateRequest request,
        IProgress<AppUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string? operationRoot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(AppUpdatePhase.ValidatingManifest, 0, null));
            if (request.CurrentVersion is null || request.CurrentVersion.Build < 0 || request.CurrentVersion.Revision < 0
                || request.SignedManifestEnvelope.IsEmpty)
                return AppUpdateResult.Failure(AppUpdateFailureReason.InvalidRequest, "APP_UPDATE_REQUEST_INVALID");
            if (!UpdateManifestCodec.TryReadEnvelope(request.SignedManifestEnvelope.Span, out var envelope))
                return AppUpdateResult.Failure(AppUpdateFailureReason.InvalidManifestEnvelope, "UPDATE_MANIFEST_ENVELOPE_INVALID");
            if (!_manifestVerifier.Verify(envelope!.Payload.AsSpan(), envelope.Signature.AsSpan()))
                return AppUpdateResult.Failure(AppUpdateFailureReason.InvalidManifestSignature, "UPDATE_MANIFEST_SIGNATURE_INVALID");
            if (!UpdateManifestCodec.TryReadVerifiedPayload(envelope.Payload.AsSpan(), out var manifest))
                return AppUpdateResult.Failure(AppUpdateFailureReason.InvalidManifest, "UPDATE_MANIFEST_PAYLOAD_INVALID");
            if (manifest!.Version.CompareTo(request.CurrentVersion) <= 0)
                return AppUpdateResult.Failure(AppUpdateFailureReason.DowngradeRejected, "UPDATE_VERSION_NOT_NEWER");

            EnsureSafeStagingRoot();
            operationRoot = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationRoot);
            var candidatePath = Path.Combine(operationRoot, manifest.ArtifactFileName);
            var download = await DownloadAsync(manifest, candidatePath, progress, cancellationToken).ConfigureAwait(false);
            if (!download.Succeeded) return download.Result!;

            progress?.Report(new(AppUpdatePhase.VerifyingAuthenticode, manifest.ContentLength, manifest.ContentLength));
            var signature = await _authenticodeVerifier.VerifyAsync(candidatePath, manifest.PublisherSubject,
                manifest.PublisherThumbprint, cancellationToken).ConfigureAwait(false);
            if (!signature.Succeeded)
                return AppUpdateResult.Failure(AppUpdateFailureReason.AuthenticodeRejected, signature.DiagnosticCode);

            progress?.Report(new(AppUpdatePhase.Installing, manifest.ContentLength, manifest.ContentLength));
            var candidate = new VerifiedUpdateCandidate(manifest, candidatePath, download.Sha256!);
            if (!await _installer.InstallAsync(candidate, cancellationToken).ConfigureAwait(false))
                return AppUpdateResult.Failure(AppUpdateFailureReason.InstallationFailed, "APP_UPDATE_INSTALL_FAILED");
            progress?.Report(new(AppUpdatePhase.Completed, manifest.ContentLength, manifest.ContentLength));
            return AppUpdateResult.Success(manifest.Version);
        }
        catch (OperationCanceledException)
        {
            return AppUpdateResult.Failure(AppUpdateFailureReason.Cancelled, "APP_UPDATE_CANCELLED");
        }
        catch (HttpRequestException)
        {
            return AppUpdateResult.Failure(AppUpdateFailureReason.NetworkFailure, "APP_UPDATE_NETWORK_FAILED");
        }
        catch (IOException)
        {
            return AppUpdateResult.Failure(AppUpdateFailureReason.DownloadInvalid, "APP_UPDATE_IO_FAILED");
        }
        catch (UnauthorizedAccessException)
        {
            return AppUpdateResult.Failure(AppUpdateFailureReason.DownloadInvalid, "APP_UPDATE_ACCESS_DENIED");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException
                                   and not AccessViolationException)
        {
            Trace.TraceError("APP_UPDATE_UNEXPECTED_FAILURE: {0}", ex.GetType().Name);
            return AppUpdateResult.Failure(AppUpdateFailureReason.UnexpectedFailure, "APP_UPDATE_UNEXPECTED_FAILURE");
        }
        finally
        {
            if (operationRoot is not null)
            {
                try { Directory.Delete(operationRoot, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("APP_UPDATE_STAGING_CLEANUP_FAILED: {0}", ex.GetType().Name);
                }
            }
        }
    }

    private async Task<(bool Succeeded, string? Sha256, AppUpdateResult? Result)> DownloadAsync(
        AppUpdateManifest manifest, string candidatePath, IProgress<AppUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(AppUpdatePhase.Downloading, 0, manifest.ContentLength));
        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.ArtifactUri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != manifest.ArtifactUri
            || response.Content.Headers.ContentLength is long length && length != manifest.ContentLength)
            return (false, null, AppUpdateResult.Failure(AppUpdateFailureReason.DownloadInvalid, "UPDATE_DOWNLOAD_RESPONSE_INVALID"));

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > manifest.ContentLength)
                    return (false, null, AppUpdateResult.Failure(AppUpdateFailureReason.DownloadInvalid, "UPDATE_DOWNLOAD_LENGTH_INVALID"));
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(new(AppUpdatePhase.Downloading, total, manifest.ContentLength));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (total != manifest.ContentLength)
            return (false, null, AppUpdateResult.Failure(AppUpdateFailureReason.DownloadInvalid, "UPDATE_DOWNLOAD_LENGTH_INVALID"));
        progress?.Report(new(AppUpdatePhase.VerifyingHash, total, total));
        var sha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(sha256, manifest.Sha256, StringComparison.Ordinal))
            return (false, null, AppUpdateResult.Failure(AppUpdateFailureReason.HashMismatch, "UPDATE_ARTIFACT_HASH_MISMATCH"));
        return (true, sha256, null);
    }

    private void EnsureSafeStagingRoot()
    {
        Directory.CreateDirectory(_stagingRoot);
        var current = new DirectoryInfo(_stagingRoot);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("UPDATE_STAGING_REPARSE_REJECTED");
            current = current.Parent;
        }
    }
}
