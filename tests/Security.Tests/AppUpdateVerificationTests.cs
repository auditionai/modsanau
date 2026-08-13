using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Updater;

namespace Security.Tests;

public sealed class AppUpdateVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"audition-update-{Guid.NewGuid():N}");

    [Fact]
    public async Task Valid_newer_signed_release_is_downloaded_verified_then_installed()
    {
        var bytes = "signed-release-bytes"u8.ToArray();
        using var context = Create(bytes);

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.True(result.Succeeded);
        Assert.Equal(new Version(2, 0, 0, 0), result.InstalledVersion);
        Assert.Equal(1, context.Handler.CallCount);
        Assert.Equal(1, context.Authenticode.CallCount);
        Assert.Equal(1, context.Installer.CallCount);
        Assert.Equal(bytes, context.Installer.InstalledBytes);
        Assert.False(Directory.EnumerateFileSystemEntries(_root).Any());
    }

    [Fact]
    public async Task Replaced_manifest_metadata_is_rejected_before_network_or_install()
    {
        var bytes = "release"u8.ToArray();
        using var context = Create(bytes);
        var json = JsonNode(context.Request.SignedManifestEnvelope.ToArray());
        var payload = Convert.FromBase64String(json.Payload);
        var text = Encoding.UTF8.GetString(payload).Replace("2.0.0.0", "3.0.0.0", StringComparison.Ordinal);
        var tampered = Envelope(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(json.Signature));

        var result = await context.Service.VerifyAndInstallAsync(new(new Version(1, 0, 0, 0), tampered));

        Assert.Equal(AppUpdateFailureReason.InvalidManifestSignature, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
        Assert.Equal(0, context.Installer.CallCount);
    }

    [Fact]
    public async Task Equal_version_is_a_typed_no_update_without_network()
    {
        using var context = Create("release"u8.ToArray(), version: "1.0.0.0");

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateStatus.NoUpdate, result.Status);
        Assert.Equal(AppUpdateFailureReason.NoUpdate, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
    }

    [Fact]
    public async Task Downgrade_is_rejected_with_typed_version_comparison()
    {
        using var context = Create("release"u8.ToArray(), version: "0.9.9.9");

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.DowngradeRejected, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
        Assert.Equal(0, context.Installer.CallCount);
    }

    [Fact]
    public async Task Hash_mismatch_never_reaches_authenticode_or_installer()
    {
        using var context = Create("actual!!"u8.ToArray(), declaredBytes: "expected"u8.ToArray());

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.HashMismatch, result.FailureReason);
        Assert.Equal(0, context.Authenticode.CallCount);
        Assert.Equal(0, context.Installer.CallCount);
    }

    [Fact]
    public async Task Unsigned_wrong_publisher_or_altered_artifact_is_rejected_before_install()
    {
        using var context = Create("release"u8.ToArray(), authenticodeSuccess: false);

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.AuthenticodeRejected, result.FailureReason);
        Assert.Equal("AUTHENTICODE_PUBLISHER_MISMATCH", result.DiagnosticCode);
        Assert.Equal(1, context.Authenticode.CallCount);
        Assert.Equal(0, context.Installer.CallCount);
    }

    [Fact]
    public async Task Redirected_response_is_rejected_even_when_handler_follows_it()
    {
        using var context = Create("release"u8.ToArray(), finalUri: new("https://cdn.example.invalid/replaced.msix"));

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.DownloadInvalid, result.FailureReason);
        Assert.Equal(0, context.Authenticode.CallCount);
        Assert.Equal(0, context.Installer.CallCount);
    }

    [Theory]
    [InlineData("http://release.example.invalid/app.msix", "AuditionAI-Mod-Studio-2.0.0.0-win-x64.msix")]
    [InlineData("https://release.example.invalid/app.msix", "../app.msix")]
    [InlineData("https://user:password@release.example.invalid/app.msix", "AuditionAI-Mod-Studio-2.0.0.0-win-x64.msix")]
    public async Task Unsafe_signed_url_or_filename_is_still_rejected(string uri, string fileName)
    {
        using var context = Create("release"u8.ToArray(), artifactUri: uri, artifactFileName: fileName);

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.InvalidManifest, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
    }

    [Fact]
    public async Task Cancellation_cleans_staging_and_never_installs()
    {
        using var cancellation = new CancellationTokenSource();
        using var context = Create(new byte[256 * 1024], cancelDuringDownload: cancellation);

        var result = await context.Service.VerifyAndInstallAsync(context.Request, cancellationToken: cancellation.Token);

        Assert.Equal(AppUpdateFailureReason.Cancelled, result.FailureReason);
        Assert.Equal(1, context.Handler.CallCount);
        Assert.Equal(0, context.Installer.CallCount);
        if (Directory.Exists(_root)) Assert.False(Directory.EnumerateFileSystemEntries(_root).Any());
    }

    [Fact]
    public async Task Wrong_product_is_rejected_before_network()
    {
        using var context = Create("release"u8.ToArray(), productId: "DifferentProduct");

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.ProductRejected, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
    }

    [Fact]
    public async Task Development_publisher_cannot_satisfy_pinned_production_policy()
    {
        using var context = Create("release"u8.ToArray(),
            manifestPublisher: "CN=Audition AI Mod Studio Development");

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.AuthenticodeRejected, result.FailureReason);
        Assert.Equal("UPDATE_PUBLISHER_POLICY_MISMATCH", result.DiagnosticCode);
        Assert.Equal(0, context.Handler.CallCount);
    }

    [Fact]
    public async Task Unsupported_channel_is_rejected_as_invalid_signed_manifest()
    {
        using var context = Create("release"u8.ToArray(), channel: "Development");

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.InvalidManifest, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
    }

    [Fact]
    public async Task Unsupported_payload_schema_is_rejected_before_network()
    {
        using var context = Create("release"u8.ToArray(), payloadSchemaVersion: 99);

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.InvalidManifest, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
    }

    [Fact]
    public async Task Candidate_outside_signed_rollout_is_deferred_before_network()
    {
        using var context = Create("release"u8.ToArray(), rolloutBasisPoints: 2500);

        var result = await context.Service.VerifyAndInstallAsync(context.Request with { RolloutBucket = 2500 });

        Assert.Equal(AppUpdateStatus.Deferred, result.Status);
        Assert.Equal(AppUpdateFailureReason.RolloutDeferred, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
    }

    [Fact]
    public async Task Active_build_or_export_defers_before_download()
    {
        using var context = Create("release"u8.ToArray(), activityBlocked: true);

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.ActivityDeferred, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
        Assert.Equal(0, context.Installer.CallCount);
    }

    [Theory]
    [InlineData("https://localhost/app.msix")]
    [InlineData("https://127.0.0.1/app.msix")]
    [InlineData("https://10.1.2.3/app.msix")]
    [InlineData("ftp://release.example.invalid/app.msix")]
    [InlineData("file:///C:/app.msix")]
    public async Task Arbitrary_or_local_artifact_uri_is_rejected(string uri)
    {
        using var context = Create("release"u8.ToArray(), artifactUri: uri);

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.InvalidManifest, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
    }

    [Fact]
    public async Task Oversized_declared_package_is_rejected_before_network()
    {
        using var context = Create("release"u8.ToArray(),
            declaredLength: UpdateManifestCodec.MaximumArtifactBytes + 1);

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.InvalidManifest, result.FailureReason);
        Assert.Equal(0, context.Handler.CallCount);
    }

    [Fact]
    public async Task Truncated_package_is_rejected_and_partial_is_cleaned()
    {
        using var context = Create("short"u8.ToArray(), declaredLength: 100);

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.DownloadInvalid, result.FailureReason);
        Assert.Equal(0, context.Installer.CallCount);
        Assert.False(Directory.EnumerateFileSystemEntries(_root).Any());
    }

    [Fact]
    public async Task Package_identity_failure_never_reaches_authenticode_or_installer()
    {
        using var context = Create("release"u8.ToArray(), packageIdentitySuccess: false);

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.PackageIdentityRejected, result.FailureReason);
        Assert.Equal(0, context.Authenticode.CallCount);
        Assert.Equal(0, context.Installer.CallCount);
    }

    [Fact]
    public async Task Concurrent_update_operations_are_single_flight()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = Create("release"u8.ToArray(), responseGate: gate);
        var first = context.Service.VerifyAndInstallAsync(context.Request);
        await context.Handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await context.Service.VerifyAndInstallAsync(context.Request);
        gate.SetResult(true);
        var firstResult = await first;

        Assert.True(firstResult.Succeeded);
        Assert.Equal(AppUpdateFailureReason.ConcurrentOperation, second.FailureReason);
        Assert.Equal(1, context.Handler.CallCount);
    }

    [Fact]
    public async Task Crash_residue_is_never_reused_as_a_verified_candidate()
    {
        var trusted = "trusted-release"u8.ToArray();
        using var context = Create(trusted);
        var residue = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(residue);
        await File.WriteAllBytesAsync(Path.Combine(residue, "candidate.msix"), "stale-attacker-bytes"u8.ToArray());

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.True(result.Succeeded);
        Assert.Equal(trusted, context.Installer.InstalledBytes);
        Assert.True(Directory.Exists(residue));
    }

    [Fact]
    public async Task Windows_trust_verifier_rejects_actual_unsigned_test_binary()
    {
        if (!OperatingSystem.IsWindows()) return;
        var verifier = new WindowsAuthenticodeUpdateVerifier();
        var result = await verifier.VerifyAsync(typeof(AppUpdateVerificationTests).Assembly.Location,
            "CN=Audition AI Mod Studio Test", new string('A', 40));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Rollover_verifier_accepts_either_bounded_trusted_public_key()
    {
        var payload = "manifest"u8.ToArray();
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var firstVerifier = new EcdsaUpdateManifestVerifier(first.ExportSubjectPublicKeyInfoPem());
        using var secondVerifier = new EcdsaUpdateManifestVerifier(second.ExportSubjectPublicKeyInfoPem());
        var signed = Encoding.UTF8.GetBytes("AUDITION_APP_UPDATE_MANIFEST_V1\0").Concat(payload).ToArray();
        var signature = second.SignData(signed, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var rollover = new AnyTrustedUpdateManifestVerifier([firstVerifier, secondVerifier]);

        Assert.True(rollover.Verify(payload, signature));
    }

    private Context Create(byte[] responseBytes, byte[]? declaredBytes = null, string version = "2.0.0.0",
        bool authenticodeSuccess = true, Uri? finalUri = null,
        string artifactUri = "https://release.example.invalid/AuditionAI-Mod-Studio-2.0.0.0-win-x64.msix",
        string artifactFileName = "AuditionAI-Mod-Studio-2.0.0.0-win-x64.msix",
        string productId = AppUpdateProduct.Identity, string channel = "Stable", int rolloutBasisPoints = 10_000,
        bool activityBlocked = false, bool packageIdentitySuccess = true, long? declaredLength = null,
        TaskCompletionSource<bool>? responseGate = null, int payloadSchemaVersion = 2,
        string manifestPublisher = "CN=Audition AI Mod Studio Test",
        CancellationTokenSource? cancelDuringDownload = null)
    {
        Directory.CreateDirectory(_root);
        var authoritative = declaredBytes ?? responseBytes;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = payloadSchemaVersion,
            ProductId = productId,
            Channel = channel,
            RolloutBasisPoints = rolloutBasisPoints,
            Version = version,
            ArtifactFileName = artifactFileName,
            ArtifactUri = artifactUri,
            ContentLength = declaredLength ?? authoritative.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(authoritative)),
            PublisherSubject = manifestPublisher,
            PublisherThumbprint = new string('A', 40)
        });
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Encoding.UTF8.GetBytes("AUDITION_APP_UPDATE_MANIFEST_V1\0").Concat(payload).ToArray();
        var signature = ecdsa.SignData(signed, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using var publicKey = ECDsa.Create(ecdsa.ExportParameters(false));
        var verifier = new EcdsaUpdateManifestVerifier(publicKey.ExportSubjectPublicKeyInfoPem());
        var handler = new FakeHandler(responseBytes, finalUri, responseGate, cancelDuringDownload);
        var authenticode = new FakeAuthenticode(authenticodeSuccess);
        var package = new FakePackageVerifier(packageIdentitySuccess);
        var activity = new FakeActivityGuard(activityBlocked);
        var installer = new FakeInstaller();
        var client = new HttpClient(handler);
        var policy = AppUpdatePolicy.CreateStable(["release.example.invalid"],
            "CN=Audition AI Mod Studio Test", new string('A', 40));
        var service = new AppUpdateService(client, verifier, authenticode, package, activity, installer, policy, _root);
        return new(service, new(new Version(1, 0, 0, 0), Envelope(payload, signature)), handler,
            authenticode, package, activity, installer, verifier, ecdsa, client);
    }

    private static byte[] Envelope(byte[] payload, byte[] signature) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        SchemaVersion = 1,
        Payload = Convert.ToBase64String(payload),
        Signature = Convert.ToBase64String(signature)
    });

    private static (string Payload, string Signature) JsonNode(byte[] envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        return (document.RootElement.GetProperty("Payload").GetString()!,
            document.RootElement.GetProperty("Signature").GetString()!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeHandler(byte[] bytes, Uri? finalUri, TaskCompletionSource<bool>? responseGate,
        CancellationTokenSource? cancelDuringDownload) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public TaskCompletionSource<bool> RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestStarted.TrySetResult(true);
            if (responseGate is not null) await responseGate.Task.WaitAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri ?? request.RequestUri),
                Content = cancelDuringDownload is null
                    ? new ByteArrayContent(bytes)
                    : new StreamContent(new CancellingReadStream(bytes, cancelDuringDownload))
            };
            return response;
        }
    }

    private sealed class CancellingReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        private bool _cancelled;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = base.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], cancellationToken);
            if (!_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }
            return read;
        }
    }

    private sealed class FakeAuthenticode(bool succeed) : IAuthenticodeUpdateVerifier
    {
        public int CallCount { get; private set; }
        public Task<AuthenticodeVerificationResult> VerifyAsync(string artifactPath, string expectedPublisherSubject,
            string expectedPublisherThumbprint, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.True(File.Exists(artifactPath));
            Assert.Equal("CN=Audition AI Mod Studio Test", expectedPublisherSubject);
            Assert.Equal(new string('A', 40), expectedPublisherThumbprint);
            return Task.FromResult(succeed ? AuthenticodeVerificationResult.Success()
                : AuthenticodeVerificationResult.Failure("AUTHENTICODE_PUBLISHER_MISMATCH"));
        }
    }

    private sealed class FakeInstaller : IVerifiedAppUpdateInstaller
    {
        public int CallCount { get; private set; }
        public byte[]? InstalledBytes { get; private set; }
        public async Task<bool> InstallAsync(VerifiedUpdateCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Equal("candidate.msix", Path.GetFileName(candidate.StagedArtifactPath));
            Assert.False(File.Exists(candidate.StagedArtifactPath + ".partial"));
            InstalledBytes = await File.ReadAllBytesAsync(candidate.StagedArtifactPath, cancellationToken);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(InstalledBytes)), candidate.VerifiedSha256);
            return true;
        }
    }

    private sealed class FakePackageVerifier(bool succeed) : IAppUpdatePackageVerifier
    {
        public int CallCount { get; private set; }
        public Task<PackageIdentityVerificationResult> VerifyAsync(string artifactPath,
            AppUpdateManifest manifest, AppUpdatePolicy policy, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Equal("candidate.msix", Path.GetFileName(artifactPath));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(artifactPath)!, "candidate.msix.partial")));
            return Task.FromResult(succeed ? PackageIdentityVerificationResult.Success()
                : PackageIdentityVerificationResult.Failure("MSIX_PRODUCT_IDENTITY_MISMATCH"));
        }
    }

    private sealed class FakeActivityGuard(bool blocked) : IAppUpdateActivityGuard
    {
        public bool MustDeferInstallation() => blocked;
    }

    private sealed record Context(AppUpdateService Service, AppUpdateRequest Request, FakeHandler Handler,
        FakeAuthenticode Authenticode, FakePackageVerifier Package, FakeActivityGuard Activity,
        FakeInstaller Installer, EcdsaUpdateManifestVerifier Verifier, ECDsa Signer, HttpClient Client)
        : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Service.Dispose();
            Verifier.Dispose();
            Signer.Dispose();
        }
    }
}
