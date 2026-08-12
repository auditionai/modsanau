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

    [Theory]
    [InlineData("1.0.0.0")]
    [InlineData("0.9.9.9")]
    public async Task Equal_version_and_downgrade_are_rejected_with_typed_version_comparison(string candidate)
    {
        using var context = Create("release"u8.ToArray(), version: candidate);

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
        using var context = Create("release"u8.ToArray(), finalUri: new("https://cdn.example.invalid/replaced.exe"));

        var result = await context.Service.VerifyAndInstallAsync(context.Request);

        Assert.Equal(AppUpdateFailureReason.DownloadInvalid, result.FailureReason);
        Assert.Equal(0, context.Authenticode.CallCount);
        Assert.Equal(0, context.Installer.CallCount);
    }

    [Theory]
    [InlineData("http://release.example.invalid/app.exe", "AuditionModStudio.App.exe")]
    [InlineData("https://release.example.invalid/app.exe", "../app.exe")]
    [InlineData("https://user:password@release.example.invalid/app.exe", "AuditionModStudio.App.exe")]
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
        using var context = Create(new byte[256 * 1024]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await context.Service.VerifyAndInstallAsync(context.Request, cancellationToken: cancellation.Token);

        Assert.Equal(AppUpdateFailureReason.Cancelled, result.FailureReason);
        Assert.Equal(0, context.Installer.CallCount);
        if (Directory.Exists(_root)) Assert.False(Directory.EnumerateFileSystemEntries(_root).Any());
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
        string artifactUri = "https://release.example.invalid/AuditionModStudio.App.exe",
        string artifactFileName = "AuditionModStudio.App.exe")
    {
        Directory.CreateDirectory(_root);
        var authoritative = declaredBytes ?? responseBytes;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            Version = version,
            ArtifactFileName = artifactFileName,
            ArtifactUri = artifactUri,
            ContentLength = authoritative.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(authoritative)),
            PublisherSubject = "CN=Audition AI Mod Studio Test",
            PublisherThumbprint = new string('A', 40)
        });
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Encoding.UTF8.GetBytes("AUDITION_APP_UPDATE_MANIFEST_V1\0").Concat(payload).ToArray();
        var signature = ecdsa.SignData(signed, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using var publicKey = ECDsa.Create(ecdsa.ExportParameters(false));
        var verifier = new EcdsaUpdateManifestVerifier(publicKey.ExportSubjectPublicKeyInfoPem());
        var handler = new FakeHandler(responseBytes, finalUri);
        var authenticode = new FakeAuthenticode(authenticodeSuccess);
        var installer = new FakeInstaller();
        var client = new HttpClient(handler);
        var service = new AppUpdateService(client, verifier, authenticode, installer, _root);
        return new(service, new(new Version(1, 0, 0, 0), Envelope(payload, signature)), handler,
            authenticode, installer, verifier, ecdsa, client);
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

    private sealed class FakeHandler(byte[] bytes, Uri? finalUri) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri ?? request.RequestUri),
                Content = new ByteArrayContent(bytes)
            };
            return Task.FromResult(response);
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
            InstalledBytes = await File.ReadAllBytesAsync(candidate.StagedArtifactPath, cancellationToken);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(InstalledBytes)), candidate.VerifiedSha256);
            return true;
        }
    }

    private sealed record Context(AppUpdateService Service, AppUpdateRequest Request, FakeHandler Handler,
        FakeAuthenticode Authenticode, FakeInstaller Installer, EcdsaUpdateManifestVerifier Verifier, ECDsa Signer,
        HttpClient Client)
        : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Verifier.Dispose();
            Signer.Dispose();
        }
    }
}
