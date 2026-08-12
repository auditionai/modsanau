using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Security;

namespace Security.Tests;

public sealed class ClientIntegrityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"client-integrity-{Guid.NewGuid():N}");
    private readonly FakeTrustVerifier _trust = new();

    [Fact]
    public async Task Valid_development_manifest_enables_capabilities_without_claiming_production_verification()
    {
        var service = CreateService(requireSignature: false);

        var result = await service.VerifyAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.ProductionVerified);
        Assert.Equal(ClientIntegrityCapabilities.Full, result.Capabilities);
        Assert.Equal(0, _trust.CallCount);
    }

    [Fact]
    public async Task Required_executable_signature_failure_disables_every_risky_capability()
    {
        _trust.Result = ClientExecutableTrustResult.Failure("AUTHENTICODE_TRUST_REJECTED");
        var service = CreateService(requireSignature: true);

        var result = await service.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ClientIntegrityFailureReason.ExecutableSignatureRejected, result.FailureReason);
        Assert.Equal(ClientIntegrityCapabilities.DiagnosticsOnly, result.Capabilities);
        Assert.Equal("CLIENT_EXECUTABLE_SIGNATURE_REJECTED", result.DiagnosticCode);
        Assert.Equal(1, _trust.CallCount);
    }

    [Fact]
    public async Task Exact_signer_and_manifest_can_produce_production_verified_state()
    {
        var service = CreateService(requireSignature: true);

        var result = await service.VerifyAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.ProductionVerified);
        Assert.Equal("CLIENT_INTEGRITY_VALID", result.DiagnosticCode);
    }

    [Fact]
    public async Task Modified_manifest_is_rejected_before_artifact_use()
    {
        var service = CreateService(requireSignature: false);
        await File.AppendAllTextAsync(Path.Combine(_root, "client-integrity.json"), " ");

        var result = await service.VerifyAsync();

        Assert.Equal(ClientIntegrityFailureReason.ManifestHashMismatch, result.FailureReason);
        Assert.Equal(ClientIntegrityCapabilities.DiagnosticsOnly, result.Capabilities);
    }

    [Fact]
    public async Task Same_length_resource_modification_is_detected()
    {
        var service = CreateService(requireSignature: false);
        await File.WriteAllTextAsync(Path.Combine(_root, "resources.pri"), "tamper!");

        var result = await service.VerifyAsync();

        Assert.Equal(ClientIntegrityFailureReason.ArtifactHashMismatch, result.FailureReason);
        Assert.Equal("CLIENT_RESOURCE_HASH_MISMATCH", result.DiagnosticCode);
        Assert.False(result.Capabilities.ResourceDependentMutationEnabled);
    }

    [Fact]
    public async Task Companion_modification_disables_archive_build_export()
    {
        var service = CreateService(requireSignature: false);
        await File.AppendAllTextAsync(Path.Combine(_root, "acv.exe"), "changed");

        var result = await service.VerifyAsync();

        Assert.Equal(ClientIntegrityFailureReason.ArtifactLengthMismatch, result.FailureReason);
        Assert.Equal("CLIENT_COMPANION_LENGTH_MISMATCH", result.DiagnosticCode);
        Assert.False(result.Capabilities.ArchiveBuildExportEnabled);
        Assert.False(result.Capabilities.UpdateInstallEnabled);
    }

    [Fact]
    public async Task Traversal_entry_is_rejected_with_stable_diagnostic_without_path()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "AuditionModStudio.App.exe"), "app");
        var manifestBytes = ManifestBytes(new Entry("../outside.bin", "resource_bundle", 1, new string('A', 64)));
        await File.WriteAllBytesAsync(Path.Combine(_root, "client-integrity.json"), manifestBytes);
        var service = ServiceFor(manifestBytes, requireSignature: false);

        var result = await service.VerifyAsync();

        Assert.Equal(ClientIntegrityFailureReason.ArtifactPathRejected, result.FailureReason);
        Assert.Equal("CLIENT_INTEGRITY_PATH_REJECTED", result.DiagnosticCode);
        Assert.DoesNotContain(_root, result.DiagnosticCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_is_propagated_instead_of_reported_as_integrity_failure()
    {
        var service = CreateService(requireSignature: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.VerifyAsync(cancellation.Token));
    }

    private ClientIntegrityService CreateService(bool requireSignature)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "AuditionModStudio.App.exe"), "app");
        var resource = Encoding.UTF8.GetBytes("trusted");
        var companion = Encoding.UTF8.GetBytes("approved-tool");
        File.WriteAllBytes(Path.Combine(_root, "resources.pri"), resource);
        File.WriteAllBytes(Path.Combine(_root, "acv.exe"), companion);
        var manifestBytes = ManifestBytes(
            EntryFor("resources.pri", "resource_bundle", resource),
            EntryFor("acv.exe", "companion_tool", companion));
        File.WriteAllBytes(Path.Combine(_root, "client-integrity.json"), manifestBytes);
        return ServiceFor(manifestBytes, requireSignature);
    }

    private ClientIntegrityService ServiceFor(byte[] manifestBytes, bool requireSignature) => new(
        new(
            Path.GetFullPath(_root),
            "AuditionModStudio.App.exe",
            "client-integrity.json",
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            requireSignature,
            "CN=Audition AI Mod Studio",
            new string('A', 40)),
        _trust);

    private static Entry EntryFor(string path, string kind, byte[] bytes) =>
        new(path, kind, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));

    private static byte[] ManifestBytes(params Entry[] entries) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        artifacts = entries.Select(entry => new
        {
            relativePath = entry.Path,
            kind = entry.Kind,
            contentLength = entry.Length,
            sha256 = entry.Sha256
        })
    });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed record Entry(string Path, string Kind, long Length, string Sha256);

    private sealed class FakeTrustVerifier : IClientExecutableTrustVerifier
    {
        public int CallCount { get; private set; }
        public ClientExecutableTrustResult Result { get; set; } = ClientExecutableTrustResult.Success();

        public Task<ClientExecutableTrustResult> VerifyAsync(string executablePath, string expectedPublisherSubject,
            string expectedPublisherThumbprint, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }
}
