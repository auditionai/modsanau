using System.Diagnostics;

namespace AuditionModStudio.Updater;

public sealed class ConfiguredPortableUpdateCoordinator : IPortableUpdateCoordinator, IDisposable
{
    private static readonly TimeSpan AutomaticCadence = TimeSpan.FromHours(6);
    private readonly HttpClient _httpClient;
    private readonly Uri _manifestUri;
    private readonly IAppUpdateManifestVerifier _manifestVerifier;
    private readonly PortableUpdateStager _stager;
    private readonly IPortableUpdaterProcessLauncher _processLauncher;
    private readonly string _statePath;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private byte[]? _lastVerifiedEnvelope;
    private Version? _lastCheckedCurrentVersion;

    public ConfiguredPortableUpdateCoordinator(
        HttpClient httpClient,
        Uri manifestUri,
        IAppUpdateManifestVerifier manifestVerifier,
        IEnumerable<string> allowedHosts,
        string updatesRoot,
        IPortableUpdaterProcessLauncher processLauncher)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _manifestVerifier = manifestVerifier ?? throw new ArgumentNullException(nameof(manifestVerifier));
        if (manifestUri is null || !manifestUri.IsAbsoluteUri || manifestUri.Scheme != Uri.UriSchemeHttps
            || manifestUri.Port != 443 || !string.IsNullOrEmpty(manifestUri.UserInfo)
            || !string.IsNullOrEmpty(manifestUri.Fragment))
            throw new ArgumentException("PORTABLE_UPDATE_MANIFEST_URI_INVALID", nameof(manifestUri));
        var hosts = allowedHosts.Select(static host => host.Trim().TrimEnd('.').ToLowerInvariant()).ToArray();
        if (!hosts.Contains(manifestUri.IdnHost.TrimEnd('.').ToLowerInvariant(), StringComparer.Ordinal))
            throw new ArgumentException("PORTABLE_UPDATE_MANIFEST_HOST_REJECTED", nameof(manifestUri));
        _manifestUri = manifestUri;
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _stager = new(httpClient, manifestVerifier, hosts, updatesRoot);
        _statePath = Path.Combine(Path.GetFullPath(updatesRoot), "last-check.txt");
        LastCheckAt = ReadLastCheck();
    }

    public bool IsAvailable => true;
    public DateTimeOffset? LastCheckAt { get; private set; }

    public async Task<PortableUpdateCheckResult> CheckAsync(
        Version currentVersion,
        bool manual,
        CancellationToken cancellationToken = default)
    {
        if (!manual && LastCheckAt is DateTimeOffset last && DateTimeOffset.UtcNow - last < AutomaticCadence)
            return new(PortableUpdateAvailability.Current, "PORTABLE_UPDATE_CHECK_THROTTLED",
                currentVersion, null, last);
        if (!await _singleFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(PortableUpdateAvailability.Failed, "PORTABLE_UPDATE_ALREADY_RUNNING",
                currentVersion, null, DateTimeOffset.UtcNow);
        try
        {
            var attemptedAt = DateTimeOffset.UtcNow;
            LastCheckAt = attemptedAt;
            WriteLastCheck(attemptedAt);
            using var request = new HttpRequestMessage(HttpMethod.Get, _manifestUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != _manifestUri
                || response.Content.Headers.ContentLength is > UpdateManifestCodec.MaximumEnvelopeBytes)
                return Failure(currentVersion, "PORTABLE_UPDATE_MANIFEST_RESPONSE_INVALID", attemptedAt);
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!UpdateManifestCodec.TryReadEnvelope(bytes, out var envelope)
                || !_manifestVerifier.Verify(envelope!.Payload.AsSpan(), envelope.Signature.AsSpan())
                || !PortableUpdateManifestCodec.TryReadVerifiedPayload(envelope.Payload.AsSpan(), out var manifest))
                return Failure(currentVersion, "PORTABLE_UPDATE_MANIFEST_REJECTED", attemptedAt);
            _lastVerifiedEnvelope = bytes;
            _lastCheckedCurrentVersion = currentVersion;
            var comparison = manifest!.Version.CompareTo(currentVersion);
            if (comparison < 0) return Failure(currentVersion, "PORTABLE_UPDATE_DOWNGRADE_REJECTED", attemptedAt);
            return new(comparison == 0 ? PortableUpdateAvailability.Current : PortableUpdateAvailability.Available,
                comparison == 0 ? "PORTABLE_UPDATE_CURRENT" : "PORTABLE_UPDATE_AVAILABLE",
                currentVersion, comparison == 0 ? null : manifest, attemptedAt);
        }
        catch (OperationCanceledException)
        {
            return Failure(currentVersion, "PORTABLE_UPDATE_CANCELLED", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return Failure(currentVersion, "PORTABLE_UPDATE_NETWORK_FAILED", DateTimeOffset.UtcNow);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    public Task<PortableUpdateStageResult> DownloadAsync(
        PortableUpdateManifest manifest,
        IProgress<PortableUpdateStageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_lastVerifiedEnvelope is null || _lastCheckedCurrentVersion is null)
            return Task.FromResult(PortableUpdateStageResult.Failure("PORTABLE_UPDATE_CHECK_REQUIRED"));
        return _stager.StageAsync(_lastVerifiedEnvelope, _lastCheckedCurrentVersion, progress, cancellationToken);
    }

    public bool TryLaunchUpdater(
        PortableStagedUpdate update,
        int parentProcessId,
        string installDirectory,
        out string diagnosticCode)
    {
        diagnosticCode = "PORTABLE_UPDATE_HANDOFF_FAILED";
        try
        {
            var installRoot = Path.GetFullPath(installDirectory);
            if (!PortableUpdateInstallEngine.IsInstallDirectoryWritable(installRoot))
            {
                diagnosticCode = "PORTABLE_UPDATE_INSTALL_DIRECTORY_NOT_WRITABLE";
                return false;
            }
            var sourceUpdater = PortableUpdateStager.ResolveUnderRoot(update.ExtractedRoot,
                PortableUpdateProduct.UpdaterExecutable);
            var handoffUpdater = Path.Combine(update.OperationRoot, PortableUpdateProduct.UpdaterExecutable);
            File.Copy(sourceUpdater, handoffUpdater, false);
            var expected = update.Manifest.Package.Inventory.Single(static file =>
                file.Path.Equals(PortableUpdateProduct.UpdaterExecutable, StringComparison.OrdinalIgnoreCase));
            using var stream = File.OpenRead(handoffUpdater);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            if (stream.Length != expected.Length || hash != expected.Sha256)
            {
                diagnosticCode = "PORTABLE_UPDATE_HANDOFF_INTEGRITY_FAILED";
                return false;
            }
            var info = new ProcessStartInfo
            {
                FileName = handoffUpdater,
                WorkingDirectory = update.OperationRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("--parent-pid");
            info.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--staging-dir");
            info.ArgumentList.Add(update.OperationRoot);
            info.ArgumentList.Add("--install-dir");
            info.ArgumentList.Add(installRoot);
            info.ArgumentList.Add("--expected-version");
            info.ArgumentList.Add(update.Manifest.Version.ToString());
            info.ArgumentList.Add("--restart-exe");
            info.ArgumentList.Add(PortableUpdateProduct.PrimaryExecutable);
            if (!_processLauncher.TryStart(info)) return false;
            diagnosticCode = "PORTABLE_UPDATE_HANDOFF_STARTED";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > UpdateManifestCodec.MaximumEnvelopeBytes)
                throw new IOException("PORTABLE_UPDATE_MANIFEST_TOO_LARGE");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private DateTimeOffset? ReadLastCheck()
    {
        try
        {
            return File.Exists(_statePath)
                   && DateTimeOffset.TryParse(File.ReadAllText(_statePath), out var value)
                ? value.ToUniversalTime()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void WriteLastCheck(DateTimeOffset value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, value.ToUniversalTime().ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static PortableUpdateCheckResult Failure(Version currentVersion, string code, DateTimeOffset attemptedAt) =>
        new(PortableUpdateAvailability.Failed, code, currentVersion, null, attemptedAt);

    public void Dispose() => _singleFlight.Dispose();
}
