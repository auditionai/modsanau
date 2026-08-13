using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace AuditionModStudio.Updater;

public sealed class AppUpdateService : IAppUpdateService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IAppUpdateManifestVerifier _manifestVerifier;
    private readonly IAuthenticodeUpdateVerifier _authenticodeVerifier;
    private readonly IAppUpdatePackageVerifier _packageVerifier;
    private readonly IAppUpdateActivityGuard _activityGuard;
    private readonly IVerifiedAppUpdateInstaller _installer;
    private readonly AppUpdatePolicy _policy;
    private readonly string _stagingRoot;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    public AppUpdateService(
        HttpClient httpClient,
        IAppUpdateManifestVerifier manifestVerifier,
        IAuthenticodeUpdateVerifier authenticodeVerifier,
        IAppUpdatePackageVerifier packageVerifier,
        IAppUpdateActivityGuard activityGuard,
        IVerifiedAppUpdateInstaller installer,
        AppUpdatePolicy policy,
        string stagingRoot)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _manifestVerifier = manifestVerifier ?? throw new ArgumentNullException(nameof(manifestVerifier));
        _authenticodeVerifier = authenticodeVerifier ?? throw new ArgumentNullException(nameof(authenticodeVerifier));
        _packageVerifier = packageVerifier ?? throw new ArgumentNullException(nameof(packageVerifier));
        _activityGuard = activityGuard ?? throw new ArgumentNullException(nameof(activityGuard));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _policy = policy is { IsValid: true } ? policy : throw new ArgumentException("UPDATE_POLICY_INVALID", nameof(policy));
        if (_httpClient.Timeout == Timeout.InfiniteTimeSpan || _httpClient.Timeout <= TimeSpan.Zero
            || _httpClient.Timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentException("UPDATE_HTTP_TIMEOUT_INVALID", nameof(httpClient));
        if (!Path.IsPathFullyQualified(stagingRoot))
            throw new ArgumentException("UPDATE_STAGING_ROOT_INVALID", nameof(stagingRoot));
        _stagingRoot = Path.GetFullPath(stagingRoot);
    }

    public async Task<AppUpdateResult> VerifyAndInstallAsync(
        AppUpdateRequest request,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ownsFlight = false;
        string? operationRoot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ownsFlight = await _singleFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!ownsFlight)
                return AppUpdateResult.Deferred(AppUpdateFailureReason.ConcurrentOperation, "APP_UPDATE_ALREADY_RUNNING");

            progress?.Report(new(AppUpdatePhase.ValidatingManifest, 0, null));
            if (request.CurrentVersion is null || request.CurrentVersion.Build < 0 || request.CurrentVersion.Revision < 0
                || request.SignedManifestEnvelope.IsEmpty || request.RolloutBucket is < 0 or >= 10_000)
                return AppUpdateResult.Failure(AppUpdateFailureReason.InvalidRequest, "APP_UPDATE_REQUEST_INVALID");
            if (!UpdateManifestCodec.TryReadEnvelope(request.SignedManifestEnvelope.Span, out var envelope))
                return AppUpdateResult.Failure(AppUpdateFailureReason.InvalidManifestEnvelope, "UPDATE_MANIFEST_ENVELOPE_INVALID");
            if (!_manifestVerifier.Verify(envelope!.Payload.AsSpan(), envelope.Signature.AsSpan()))
                return AppUpdateResult.Failure(AppUpdateFailureReason.InvalidManifestSignature, "UPDATE_MANIFEST_SIGNATURE_INVALID");
            if (!UpdateManifestCodec.TryReadVerifiedPayload(envelope.Payload.AsSpan(), out var manifest))
                return AppUpdateResult.Failure(AppUpdateFailureReason.InvalidManifest, "UPDATE_MANIFEST_PAYLOAD_INVALID");
            if (!string.Equals(manifest!.ProductId, _policy.ProductId, StringComparison.Ordinal))
                return AppUpdateResult.Failure(AppUpdateFailureReason.ProductRejected, "UPDATE_PRODUCT_REJECTED");
            if (manifest.Channel != _policy.Channel)
                return AppUpdateResult.Failure(AppUpdateFailureReason.ChannelRejected, "UPDATE_CHANNEL_REJECTED");
            if (!string.Equals(manifest.PublisherSubject, _policy.ExpectedPublisherSubject, StringComparison.Ordinal)
                || !string.Equals(manifest.PublisherThumbprint, _policy.ExpectedPublisherThumbprint, StringComparison.Ordinal))
                return AppUpdateResult.Failure(AppUpdateFailureReason.AuthenticodeRejected, "UPDATE_PUBLISHER_POLICY_MISMATCH");
            if (!UpdateUriPolicy.IsAllowedArtifactUri(manifest.ArtifactUri, _policy))
                return AppUpdateResult.Failure(AppUpdateFailureReason.InvalidManifest, "UPDATE_ARTIFACT_URI_REJECTED");

            var comparison = manifest.Version.CompareTo(request.CurrentVersion);
            if (comparison == 0) return AppUpdateResult.NoUpdate();
            if (comparison < 0)
                return AppUpdateResult.Failure(AppUpdateFailureReason.DowngradeRejected, "UPDATE_VERSION_DOWNGRADE_REJECTED");
            if (request.RolloutBucket >= manifest.RolloutBasisPoints)
                return AppUpdateResult.Deferred(AppUpdateFailureReason.RolloutDeferred, "APP_UPDATE_ROLLOUT_DEFERRED");
            if (_activityGuard.MustDeferInstallation())
                return AppUpdateResult.Deferred(AppUpdateFailureReason.ActivityDeferred, "APP_UPDATE_ACTIVE_WORK_DEFERRED");

            EnsureSafeStagingRoot();
            CleanupStaleOperationDirectories();
            operationRoot = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationRoot);
            var partialPath = Path.Combine(operationRoot, "candidate.msix.partial");
            var candidatePath = Path.Combine(operationRoot, "candidate.msix");
            var download = await DownloadAsync(manifest, partialPath, progress, cancellationToken).ConfigureAwait(false);
            if (!download.Succeeded) return download.Result!;

            File.Move(partialPath, candidatePath, false);
            progress?.Report(new(AppUpdatePhase.VerifyingPackageIdentity, manifest.ContentLength, manifest.ContentLength));
            var package = await _packageVerifier.VerifyAsync(candidatePath, manifest, _policy, cancellationToken)
                .ConfigureAwait(false);
            if (!package.Succeeded)
                return AppUpdateResult.Failure(AppUpdateFailureReason.PackageIdentityRejected, package.DiagnosticCode);

            progress?.Report(new(AppUpdatePhase.VerifyingAuthenticode, manifest.ContentLength, manifest.ContentLength));
            var signature = await _authenticodeVerifier.VerifyAsync(candidatePath, _policy.ExpectedPublisherSubject,
                _policy.ExpectedPublisherThumbprint, cancellationToken).ConfigureAwait(false);
            if (!signature.Succeeded)
                return AppUpdateResult.Failure(AppUpdateFailureReason.AuthenticodeRejected, signature.DiagnosticCode);

            progress?.Report(new(AppUpdatePhase.ReadyToInstall, manifest.ContentLength, manifest.ContentLength));
            if (_activityGuard.MustDeferInstallation())
                return AppUpdateResult.Deferred(AppUpdateFailureReason.ActivityDeferred, "APP_UPDATE_ACTIVE_WORK_DEFERRED");

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
            if (operationRoot is not null) TryDeleteOperationRoot(operationRoot);
            if (ownsFlight) _singleFlight.Release();
        }
    }

    private async Task<(bool Succeeded, string? Sha256, AppUpdateResult? Result)> DownloadAsync(
        AppUpdateManifest manifest,
        string partialPath,
        IProgress<AppUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(AppUpdatePhase.Downloading, 0, manifest.ContentLength));
        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.ArtifactUri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri;
        if (!response.IsSuccessStatusCode || finalUri is null || finalUri != manifest.ArtifactUri
            || !UpdateUriPolicy.IsAllowedArtifactUri(finalUri, _policy)
            || response.Content.Headers.ContentLength is long length && length != manifest.ContentLength)
            return (false, null, AppUpdateResult.Failure(AppUpdateFailureReason.DownloadInvalid,
                "UPDATE_DOWNLOAD_RESPONSE_INVALID"));

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
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
                    return (false, null, AppUpdateResult.Failure(AppUpdateFailureReason.DownloadInvalid,
                        "UPDATE_DOWNLOAD_LENGTH_INVALID"));
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
            return (false, null, AppUpdateResult.Failure(AppUpdateFailureReason.DownloadInvalid,
                "UPDATE_DOWNLOAD_LENGTH_INVALID"));
        progress?.Report(new(AppUpdatePhase.VerifyingHash, total, total));
        var sha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(sha256, manifest.Sha256, StringComparison.Ordinal))
            return (false, null, AppUpdateResult.Failure(AppUpdateFailureReason.HashMismatch,
                "UPDATE_ARTIFACT_HASH_MISMATCH"));
        return (true, sha256, null);
    }

    private void EnsureSafeStagingRoot()
    {
        ValidateExistingAncestors();
        Directory.CreateDirectory(_stagingRoot);
        ValidateExistingAncestors();
    }

    private void ValidateExistingAncestors()
    {
        var current = new DirectoryInfo(_stagingRoot);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("UPDATE_STAGING_REPARSE_REJECTED");
            current = current.Parent;
        }
    }

    private void CleanupStaleOperationDirectories()
    {
        var threshold = DateTime.UtcNow.AddDays(-7);
        foreach (var directory in new DirectoryInfo(_stagingRoot).EnumerateDirectories())
        {
            if (directory.Name.Length != 32 || !Guid.TryParseExact(directory.Name, "N", out _)
                || directory.LastWriteTimeUtc >= threshold || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            TryDeleteOperationRoot(directory.FullName);
        }
    }

    private static void TryDeleteOperationRoot(string operationRoot)
    {
        try
        {
            var directory = new DirectoryInfo(operationRoot);
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is not FileInfo file
                    || file.Name is not ("candidate.msix" or "candidate.msix.partial")
                    || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    return;
                file.Delete();
            }
            directory.Delete(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("APP_UPDATE_STAGING_CLEANUP_FAILED: {0}", ex.GetType().Name);
        }
    }

    public void Dispose() => _singleFlight.Dispose();
}
