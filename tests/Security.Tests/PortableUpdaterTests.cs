using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuditionModStudio.Updater;

namespace Security.Tests;

public sealed class PortableUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"portable-updater-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Valid_signed_portable_manifest_is_parsed_after_signature_verification()
    {
        using var fixture = Fixture.Valid();
        Assert.True(UpdateManifestCodec.TryReadEnvelope(fixture.Envelope, out var envelope));
        Assert.True(fixture.Verifier.Verify(envelope!.Payload.AsSpan(), envelope.Signature.AsSpan()));
        Assert.True(PortableUpdateManifestCodec.TryReadVerifiedPayload(envelope.Payload.AsSpan(), out var manifest));
        Assert.Equal(new Version(1, 0, 1), manifest!.Version);
        Assert.Equal(PortableUpdatePolicy.Optional, manifest.UpdatePolicy);
        Assert.Equal(2, manifest.Package.Inventory.Length);
    }

    [Theory]
    [InlineData("\"Product\":\"AuditionAI.ModStudio\"", "\"Product\":\"Other\"")]
    [InlineData("\"Channel\":\"stable\"", "\"Channel\":\"beta\"")]
    [InlineData("\"Version\":\"1.0.1\"", "\"Version\":\"bad\"")]
    [InlineData("https://release.example.invalid/", "http://release.example.invalid/")]
    [InlineData("\"Architecture\":\"win-x64\"", "\"Architecture\":\"win-arm64\"")]
    [InlineData("\"Distribution\":\"portable\"", "\"Distribution\":\"installer\"")]
    public void Signed_but_invalid_portable_authority_is_rejected(string oldValue, string newValue)
    {
        using var fixture = Fixture.Valid(payloadTransform: payload =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace(oldValue, newValue, StringComparison.Ordinal)));
        Assert.True(UpdateManifestCodec.TryReadEnvelope(fixture.Envelope, out var envelope));
        Assert.True(fixture.Verifier.Verify(envelope!.Payload.AsSpan(), envelope.Signature.AsSpan()));
        Assert.False(PortableUpdateManifestCodec.TryReadVerifiedPayload(envelope.Payload.AsSpan(), out _));
    }

    [Fact]
    public void Modified_payload_or_wrong_key_is_rejected()
    {
        using var fixture = Fixture.Valid();
        Assert.True(UpdateManifestCodec.TryReadEnvelope(fixture.Envelope, out var envelope));
        var modified = envelope!.Payload.ToArray();
        modified[^2] ^= 0x01;
        Assert.False(fixture.Verifier.Verify(modified, envelope.Signature.AsSpan()));
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongVerifier = new EcdsaUpdateManifestVerifier(wrongKey.ExportSubjectPublicKeyInfoPem());
        Assert.False(wrongVerifier.Verify(envelope.Payload.AsSpan(), envelope.Signature.AsSpan()));
    }

    [Theory]
    [InlineData("\\\"Size\\\":", "oversized")]
    [InlineData("AuditionModStudio.App.exe", "missing-primary")]
    public void Oversized_package_and_missing_primary_executable_are_rejected(string marker, string scenario)
    {
        using var fixture = Fixture.Valid(payloadTransform: payload =>
        {
            var json = Encoding.UTF8.GetString(payload);
            json = scenario == "oversized"
                ? new Regex("\\\"Size\\\":\\d+").Replace(json,
                    $"\\\"Size\\\":{PortableUpdateManifestCodec.MaximumPackageBytes + 1}", 1)
                : json.Replace(marker, "missing-primary.exe", StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(json);
        });
        Assert.True(UpdateManifestCodec.TryReadEnvelope(fixture.Envelope, out var envelope));
        Assert.True(fixture.Verifier.Verify(envelope!.Payload.AsSpan(), envelope.Signature.AsSpan()));
        Assert.False(PortableUpdateManifestCodec.TryReadVerifiedPayload(envelope.Payload.AsSpan(), out _));
    }

    [Fact]
    public async Task Download_streams_verifies_promotes_extracts_and_validates_inventory()
    {
        using var fixture = Fixture.Valid();
        using var client = new HttpClient(new SequenceHandler(fixture.ZipBytes));
        var stager = new PortableUpdateStager(client, fixture.Verifier, ["release.example.invalid"], Root("updates"));

        var result = await stager.StageAsync(fixture.Envelope, new Version(1, 0, 0));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.NotNull(result.Update);
        Assert.True(File.Exists(result.Update!.PackagePath));
        Assert.False(File.Exists(result.Update.PackagePath + ".partial"));
        Assert.Equal(fixture.NewApp, await File.ReadAllBytesAsync(Path.Combine(result.Update.ExtractedRoot,
            PortableUpdateProduct.PrimaryExecutable)));
    }

    [Fact]
    public async Task Length_hash_partial_and_transient_retry_fail_closed()
    {
        using var fixture = Fixture.Valid(declaredLengthDelta: 1);
        var handler = new SequenceHandler(fixture.ZipBytes, HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var stager = new PortableUpdateStager(client, fixture.Verifier, ["release.example.invalid"], Root("updates"));

        var result = await stager.StageAsync(fixture.Envelope, new Version(1, 0, 0));

        Assert.False(result.Succeeded);
        Assert.Equal(2, handler.CallCount);
        Assert.Empty(Directory.EnumerateFiles(Root("updates"), "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cancellation_and_network_timeout_leave_no_partial_authority()
    {
        using var fixture = Fixture.Valid();
        using var cancelledClient = new HttpClient(new SequenceHandler(fixture.ZipBytes));
        var cancelledRoot = Root("cancelled");
        var cancelledStager = new PortableUpdateStager(cancelledClient, fixture.Verifier,
            ["release.example.invalid"], cancelledRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await cancelledStager.StageAsync(fixture.Envelope, new Version(1, 0, 0),
            cancellationToken: cancellation.Token);

        Assert.Equal("PORTABLE_UPDATE_CANCELLED", cancelled.DiagnosticCode);
        Assert.Empty(Directory.EnumerateFiles(cancelledRoot, "*.partial", SearchOption.AllDirectories));

        using var timeoutClient = new HttpClient(new TimeoutHandler());
        var timeoutRoot = Root("timeout");
        var timeoutStager = new PortableUpdateStager(timeoutClient, fixture.Verifier,
            ["release.example.invalid"], timeoutRoot);

        var timedOut = await timeoutStager.StageAsync(fixture.Envelope, new Version(1, 0, 0));

        Assert.Equal("PORTABLE_UPDATE_NETWORK_FAILED", timedOut.DiagnosticCode);
        Assert.Empty(Directory.EnumerateFiles(timeoutRoot, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Partial_connection_and_stale_partial_are_cleaned_without_authority()
    {
        using var fixture = Fixture.Valid();
        var updates = Root("partial");
        var stale = Path.Combine(updates, "1.0.0", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale);
        var stalePartial = Path.Combine(stale, "package.zip.partial");
        await File.WriteAllTextAsync(stalePartial, "old-partial");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-15));

        using var partialClient = new HttpClient(new PartialConnectionHandler(fixture.ZipBytes));
        var stager = new PortableUpdateStager(partialClient, fixture.Verifier,
            ["release.example.invalid"], updates);

        var result = await stager.StageAsync(fixture.Envelope, new Version(1, 0, 0));

        Assert.False(result.Succeeded);
        Assert.Equal("PORTABLE_UPDATE_LENGTH_MISMATCH", result.DiagnosticCode);
        Assert.False(File.Exists(stalePartial));
        Assert.Empty(Directory.EnumerateFiles(updates, "*.partial", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("1.0.1", "PORTABLE_UPDATE_CURRENT")]
    [InlineData("1.0.2", "PORTABLE_UPDATE_DOWNGRADE_REJECTED")]
    public async Task Same_version_and_downgrade_are_rejected_before_download(string current, string expectedCode)
    {
        using var fixture = Fixture.Valid();
        var handler = new SequenceHandler(fixture.ZipBytes);
        using var client = new HttpClient(handler);
        var stager = new PortableUpdateStager(client, fixture.Verifier, ["release.example.invalid"], Root("updates"));

        var result = await stager.StageAsync(fixture.Envelope, Version.Parse(current));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.DiagnosticCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Wrong_hash_and_corrupt_zip_are_rejected_without_partial_authority()
    {
        using (var wrongHash = Fixture.Valid(payloadTransform: payload =>
                   Encoding.UTF8.GetBytes(new Regex("\\\"Sha256\\\":\\\"[A-F0-9]{64}\\\"")
                       .Replace(Encoding.UTF8.GetString(payload), $"\"Sha256\":\"{new string('A', 64)}\"", 1))))
        using (var client = new HttpClient(new SequenceHandler(wrongHash.ZipBytes)))
        {
            var stager = new PortableUpdateStager(client, wrongHash.Verifier, ["release.example.invalid"], Root("hash"));
            var result = await stager.StageAsync(wrongHash.Envelope, new Version(1, 0, 0));
            Assert.False(result.Succeeded);
            Assert.Equal("PORTABLE_UPDATE_HASH_MISMATCH", result.DiagnosticCode);
        }

        using var corrupt = Fixture.Valid(zipTransform: bytes => bytes[..(bytes.Length / 2)]);
        using var corruptClient = new HttpClient(new SequenceHandler(corrupt.ZipBytes));
        var corruptStager = new PortableUpdateStager(corruptClient, corrupt.Verifier,
            ["release.example.invalid"], Root("corrupt"));
        var corruptResult = await corruptStager.StageAsync(corrupt.Envelope, new Version(1, 0, 0));
        Assert.False(corruptResult.Succeeded);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("C:/absolute.dll")]
    [InlineData("/rooted.dll")]
    public async Task Zip_slip_and_absolute_entries_are_rejected(string maliciousPath)
    {
        using var fixture = Fixture.Valid(extraZipEntry: maliciousPath, includeExtraInInventory: false);
        using var client = new HttpClient(new SequenceHandler(fixture.ZipBytes));
        var stager = new PortableUpdateStager(client, fixture.Verifier, ["release.example.invalid"], Root("updates"));

        var result = await stager.StageAsync(fixture.Envelope, new Version(1, 0, 0));

        Assert.False(result.Succeeded);
        Assert.Equal("PORTABLE_UPDATE_ZIP_STRUCTURE_INVALID", result.DiagnosticCode);
        Assert.False(File.Exists(Path.Combine(_root, "escape.dll")));
    }

    [Fact]
    public async Task Duplicate_case_insensitive_entry_is_rejected()
    {
        using var fixture = Fixture.Valid(extraZipEntry: "auditionmodstudio.app.exe", includeExtraInInventory: false);
        using var client = new HttpClient(new SequenceHandler(fixture.ZipBytes));
        var stager = new PortableUpdateStager(client, fixture.Verifier, ["release.example.invalid"], Root("updates"));

        var result = await stager.StageAsync(fixture.Envelope, new Version(1, 0, 0));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task In_place_install_preserves_unknown_files_and_removes_only_signed_owned_files()
    {
        using var fixture = Fixture.Valid(removeOwnedFiles: ["obsolete-owned.dll"]);
        var install = Root("Portable Việt Nam");
        Directory.CreateDirectory(install);
        await File.WriteAllBytesAsync(Path.Combine(install, PortableUpdateProduct.PrimaryExecutable), "old-app"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(install, PortableUpdateProduct.UpdaterExecutable), "old-updater"u8.ToArray());
        await File.WriteAllTextAsync(Path.Combine(install, "obsolete-owned.dll"), "obsolete");
        await File.WriteAllTextAsync(Path.Combine(install, "user-note.txt"), "preserve");
        var staging = await fixture.CreateStagingAsync(Root("staging"));
        var engine = new PortableUpdateInstallEngine(fixture.Verifier);

        var result = await engine.InstallAsync(new(int.MaxValue, install, staging, new Version(1, 0, 1),
            PortableUpdateProduct.PrimaryExecutable), TimeSpan.FromSeconds(2));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(fixture.NewApp, await File.ReadAllBytesAsync(Path.Combine(install,
            PortableUpdateProduct.PrimaryExecutable)));
        Assert.False(File.Exists(Path.Combine(install, "obsolete-owned.dll")));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(install, "user-note.txt")));
    }

    [Fact]
    public async Task Failure_during_replacement_restores_exact_previous_version_and_user_data()
    {
        using var fixture = Fixture.Valid();
        var install = Root("rollback install");
        Directory.CreateDirectory(install);
        var oldApp = "old-app-exact"u8.ToArray();
        var oldUpdater = "old-updater-exact"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(install, PortableUpdateProduct.PrimaryExecutable), oldApp);
        await File.WriteAllBytesAsync(Path.Combine(install, PortableUpdateProduct.UpdaterExecutable), oldUpdater);
        await File.WriteAllTextAsync(Path.Combine(install, "project.audproj"), "user-data");
        var staging = await fixture.CreateStagingAsync(Root("rollback staging"));
        var engine = new PortableUpdateInstallEngine(fixture.Verifier,
            new ThrowAtCheckpoint("FILE_REPLACED", PortableUpdateProduct.UpdaterExecutable));

        var result = await engine.InstallAsync(new(int.MaxValue, install, staging, new Version(1, 0, 1),
            PortableUpdateProduct.PrimaryExecutable), TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal(oldApp, await File.ReadAllBytesAsync(Path.Combine(install, PortableUpdateProduct.PrimaryExecutable)));
        Assert.Equal(oldUpdater, await File.ReadAllBytesAsync(Path.Combine(install, PortableUpdateProduct.UpdaterExecutable)));
        Assert.Equal("user-data", await File.ReadAllTextAsync(Path.Combine(install, "project.audproj")));
    }

    [Theory]
    [InlineData("BACKUP_COMPLETE", "")]
    [InlineData("FILE_REPLACED", PortableUpdateProduct.PrimaryExecutable)]
    [InlineData("REPLACEMENT_COMPLETE", "")]
    [InlineData("POST_INSTALL_VERIFIED", "")]
    public async Task Deterministic_crash_checkpoints_never_authorize_a_partial_install(
        string checkpoint, string relativePath)
    {
        using var fixture = Fixture.Valid();
        var install = Root($"crash-{checkpoint}");
        var oldApp = "recoverable-old-app"u8.ToArray();
        var oldUpdater = "recoverable-old-updater"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(install, PortableUpdateProduct.PrimaryExecutable), oldApp);
        await File.WriteAllBytesAsync(Path.Combine(install, PortableUpdateProduct.UpdaterExecutable), oldUpdater);
        var staging = await fixture.CreateStagingAsync(Root($"staging-{checkpoint}"));
        var engine = new PortableUpdateInstallEngine(fixture.Verifier,
            new ThrowAtCheckpoint(checkpoint, relativePath));

        var result = await engine.InstallAsync(new(int.MaxValue, install, staging, new Version(1, 0, 1),
            PortableUpdateProduct.PrimaryExecutable), TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Equal(oldApp, await File.ReadAllBytesAsync(Path.Combine(install,
            PortableUpdateProduct.PrimaryExecutable)));
        Assert.Equal(oldUpdater, await File.ReadAllBytesAsync(Path.Combine(install,
            PortableUpdateProduct.UpdaterExecutable)));
        Assert.False(File.Exists(Path.Combine(install, PortableUpdateProduct.PrimaryExecutable + ".update-new")));
    }

    [Fact]
    public async Task Locked_owned_file_fails_safely_without_partial_replacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = Fixture.Valid();
        var install = Root("locked install");
        var oldApp = "locked-old-app"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(install, PortableUpdateProduct.PrimaryExecutable), oldApp);
        await File.WriteAllBytesAsync(Path.Combine(install, PortableUpdateProduct.UpdaterExecutable), "old-updater"u8.ToArray());
        var staging = await fixture.CreateStagingAsync(Root("locked staging"));
        await using var locked = new FileStream(Path.Combine(install, PortableUpdateProduct.UpdaterExecutable),
            FileMode.Open, FileAccess.Read, FileShare.None);
        var engine = new PortableUpdateInstallEngine(fixture.Verifier);

        var result = await engine.InstallAsync(new(int.MaxValue, install, staging, new Version(1, 0, 1),
            PortableUpdateProduct.PrimaryExecutable), TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Equal(oldApp, await File.ReadAllBytesAsync(Path.Combine(install, PortableUpdateProduct.PrimaryExecutable)));
        Assert.False(File.Exists(Path.Combine(install, PortableUpdateProduct.PrimaryExecutable + ".update-new")));
    }

    [Fact]
    public void Unsafe_relative_paths_and_non_writable_targets_are_rejected()
    {
        Assert.False(PortableUpdateManifestCodec.ValidRelativePath("../file.dll"));
        Assert.False(PortableUpdateManifestCodec.ValidRelativePath("C:\\file.dll"));
        Assert.False(PortableUpdateInstallEngine.IsInstallDirectoryWritable(Path.Combine(_root, "missing")));
    }

    private string Root(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ThrowAtCheckpoint(string checkpoint, string path) : IPortableInstallFailureInjector
    {
        public void OnCheckpoint(string actualCheckpoint, string relativePath)
        {
            if (actualCheckpoint == checkpoint && relativePath == path)
                throw new IOException("INJECTED_FAILURE");
        }
    }

    private sealed class SequenceHandler(byte[] bytes, params HttpStatusCode[] firstStatuses) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = CallCount++;
            var status = call < firstStatuses.Length ? firstStatuses[call] : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
                Content = new ByteArrayContent(bytes)
            });
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("TEST_NETWORK_TIMEOUT"));
    }

    private sealed class PartialConnectionHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var truncated = bytes[..Math.Max(1, bytes.Length / 2)];
            var content = new ByteArrayContent(truncated);
            content.Headers.ContentLength = bytes.LongLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
                Content = content
            });
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _signer;
        public byte[] Envelope { get; }
        public byte[] Payload { get; }
        public byte[] ZipBytes { get; }
        public byte[] NewApp { get; } = "new-app-1.0.1"u8.ToArray();
        public byte[] NewUpdater { get; } = "new-updater-1.0.1"u8.ToArray();
        public EcdsaUpdateManifestVerifier Verifier { get; }

        private Fixture(Func<byte[], byte[]>? payloadTransform, Func<byte[], byte[]>? zipTransform,
            long declaredLengthDelta, string? extraZipEntry, bool includeExtraInInventory,
            string[] removeOwnedFiles)
        {
            ZipBytes = zipTransform?.Invoke(CreateZip(NewApp, NewUpdater, extraZipEntry))
                       ?? CreateZip(NewApp, NewUpdater, extraZipEntry);
            var inventory = new List<object>
            {
                Entry(PortableUpdateProduct.PrimaryExecutable, NewApp),
                Entry(PortableUpdateProduct.UpdaterExecutable, NewUpdater)
            };
            if (includeExtraInInventory && extraZipEntry is not null)
                inventory.Add(Entry(extraZipEntry, "extra"u8.ToArray()));
            Payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 3,
                Product = PortableUpdateProduct.Identity,
                Channel = "stable",
                Version = "1.0.1",
                MinimumSupportedVersion = "1.0.0",
                PublishedAt = "2026-08-14T00:00:00Z",
                UpdatePolicy = "optional",
                Package = new
                {
                    Url = "https://release.example.invalid/AuditionAI-Mod-Studio-1.0.1-win-x64.zip",
                    FileName = "AuditionAI-Mod-Studio-1.0.1-win-x64.zip",
                    Size = ZipBytes.LongLength + declaredLengthDelta,
                    Sha256 = Convert.ToHexString(SHA256.HashData(ZipBytes)),
                    Product = PortableUpdateProduct.Identity,
                    Architecture = PortableUpdateProduct.Architecture,
                    Distribution = PortableUpdateProduct.Distribution,
                    Inventory = inventory,
                    RemoveOwnedFiles = removeOwnedFiles
                },
                ReleaseNotes = new[] { "Sửa lỗi Build", "Cải thiện hiệu năng" }
            });
            if (payloadTransform is not null) Payload = payloadTransform(Payload);
            _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var domain = Encoding.UTF8.GetBytes("AUDITION_APP_UPDATE_MANIFEST_V1\0");
            var signature = _signer.SignData(domain.Concat(Payload).ToArray(), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            Envelope = JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1,
                Payload = Convert.ToBase64String(Payload),
                Signature = Convert.ToBase64String(signature)
            });
            Verifier = new(_signer.ExportSubjectPublicKeyInfoPem());
        }

        public static Fixture Valid(Func<byte[], byte[]>? payloadTransform = null,
            Func<byte[], byte[]>? zipTransform = null, long declaredLengthDelta = 0,
            string? extraZipEntry = null, bool includeExtraInInventory = false, string[]? removeOwnedFiles = null) =>
            new(payloadTransform, zipTransform, declaredLengthDelta, extraZipEntry, includeExtraInInventory,
                removeOwnedFiles ?? []);

        public async Task<string> CreateStagingAsync(string staging)
        {
            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.signed.json"), Envelope);
            await File.WriteAllBytesAsync(Path.Combine(staging, "package.zip"), ZipBytes);
            var extracted = Path.Combine(staging, "extracted");
            Directory.CreateDirectory(extracted);
            await File.WriteAllBytesAsync(Path.Combine(extracted, PortableUpdateProduct.PrimaryExecutable), NewApp);
            await File.WriteAllBytesAsync(Path.Combine(extracted, PortableUpdateProduct.UpdaterExecutable), NewUpdater);
            return staging;
        }

        private static object Entry(string path, byte[] bytes) => new
        {
            Path = path,
            Length = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };

        private static byte[] CreateZip(byte[] app, byte[] updater, string? extra)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                Write(zip, PortableUpdateProduct.PrimaryExecutable, app);
                Write(zip, PortableUpdateProduct.UpdaterExecutable, updater);
                if (extra is not null) Write(zip, extra, "extra"u8.ToArray());
            }
            return output.ToArray();
        }

        private static void Write(ZipArchive zip, string path, byte[] bytes)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        public void Dispose()
        {
            Verifier.Dispose();
            _signer.Dispose();
        }
    }
}
