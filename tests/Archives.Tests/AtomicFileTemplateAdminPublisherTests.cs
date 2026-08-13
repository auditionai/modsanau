using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Infrastructure.Paths;

namespace Archives.Tests;

public sealed class AtomicFileTemplateAdminPublisherTests
{
    [Fact]
    public async Task Publish_encrypts_signs_audits_and_commits_immutable_version_atomically()
    {
        using var context = new Context();
        var request = context.Request("v1");

        var result = await context.Publisher.PublishAtomicallyAsync(request);
        var conflict = await context.Publisher.PublishAtomicallyAsync(request);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(conflict.VersionConflict);
        var versionRoot = context.VersionRoot("v1");
        Assert.Equal(["audit.json", "metadata.json", "package.amtenc"],
            Directory.GetFiles(versionRoot).Select(path => Path.GetFileName(path)!).Order().ToArray());
        var encrypted = await File.ReadAllBytesAsync(Path.Combine(versionRoot, "package.amtenc"));
        Assert.DoesNotContain(Encoding.UTF8.GetString(context.Bytes), Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(versionRoot, "metadata.json")));
        var unsigned = metadata.RootElement.GetProperty("metadata");
        Assert.Equal("AES-256-CBC-HMAC-SHA256", unsigned.GetProperty("encryption").GetString());
        Assert.Equal("AMS-TEMPLATE-METADATA-V1", unsigned.GetProperty("signatureScope").GetString());
        Assert.Equal("texture.dds", Assert.Single(unsigned.GetProperty("textureSlots").EnumerateArray())
            .GetProperty("relativePath").GetString());
        Assert.Equal(6000, Assert.Single(unsigned.GetProperty("ddsRecords").EnumerateArray())
            .GetProperty("width").GetInt32());
        var signature = Convert.FromBase64String(metadata.RootElement.GetProperty("metadataSignature").GetString()!);
        var auditBytes = await File.ReadAllBytesAsync(Path.Combine(versionRoot, "audit.json"));
        var signingBytes = AtomicFileTemplateAdminPublisher.CreateMetadataSigningBytes(request, auditBytes);
        Assert.True(context.SigningKey.VerifyData(signingBytes, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var auditText = Encoding.UTF8.GetString(auditBytes);
        Assert.DoesNotContain(context.ArchivePath, auditText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(context.PublishRoot, ".stage-*"));
    }

    [Fact]
    public async Task Concurrent_same_version_has_exactly_one_commit_and_tampered_identity_never_publishes()
    {
        using var context = new Context();
        var request = context.Request("v2");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => context.Publisher.PublishAtomicallyAsync(request)));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Succeeded));
        Assert.Equal(5, outcomes.Count(outcome => outcome.VersionConflict));

        var v3 = context.Request("v3");
        var wrongIdentity = new TemplateIdentity(new("pointer"), new("v3"),
            new(new string('B', 64)), new("build-1"));
        var tampered = v3 with
        {
            PackageManifest = new(wrongIdentity,
                new("audition"), new("pointer_mod"), context.Bytes.Length,
                PremiumTemplatePackageManifest.PackageMediaType),
            AuditEvent = v3.AuditEvent with { Identity = wrongIdentity },
        };
        var rejected = await context.Publisher.PublishAtomicallyAsync(tampered);
        Assert.False(rejected.Succeeded);
        Assert.Equal("TEMPLATE_ADMIN_PUBLISH_CRYPTO_FAILED", rejected.DiagnosticCode);
        Assert.False(Directory.Exists(context.VersionRoot("v3")));
    }

    [Fact]
    public async Task Inconsistent_audit_or_manifest_is_rejected_before_creating_a_version()
    {
        using var context = new Context();
        var request = context.Request("v4");
        var wrongAudit = request with
        {
            AuditEvent = request.AuditEvent with { AdminSubjectId = Guid.Empty },
        };

        var rejectedAudit = await context.Publisher.PublishAtomicallyAsync(wrongAudit);
        var wrongManifest = request with
        {
            TextureManifest = TextureManifest.Create(new("other-game"), new("pointer_mod"),
            [
                new TextureSlot(new("logo"), new("texture.dds"), "Logo", new("logo"),
                    "Main logo", ["logo"], true, true, new("fit")),
            ]).Manifest!,
        };
        var rejectedManifest = await context.Publisher.PublishAtomicallyAsync(wrongManifest);

        Assert.Equal("TEMPLATE_ADMIN_PUBLISH_REQUEST_INVALID", rejectedAudit.DiagnosticCode);
        Assert.Equal("TEMPLATE_ADMIN_PUBLISH_REQUEST_INVALID", rejectedManifest.DiagnosticCode);
        Assert.False(Directory.Exists(context.VersionRoot("v4")));

        var wrongDds = request with
        {
            DdsRecords = [request.DdsRecords[0] with { RelativePath = new("other.dds") }],
        };
        var rejectedDds = await context.Publisher.PublishAtomicallyAsync(wrongDds);
        Assert.Equal("TEMPLATE_ADMIN_PUBLISH_REQUEST_INVALID", rejectedDds.DiagnosticCode);
        Assert.False(Directory.Exists(context.VersionRoot("v4")));
    }

    private sealed class Context : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ams-admin-publisher-" + Guid.NewGuid().ToString("N"));
        private readonly TemplateAdminPublisherKeys _keys;
        public Context()
        {
            Directory.CreateDirectory(_root);
            ArchivePath = Path.Combine(_root, "source.ab");
            Bytes = Encoding.UTF8.GetBytes("sensitive-template-package-content");
            File.WriteAllBytes(ArchivePath, Bytes);
            PublishRoot = Path.Combine(_root, "published");
            SigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keys = new(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32),
                SigningKey.ExportECPrivateKeyPem());
            Publisher = new(PublishRoot, _keys, new PathSecurity());
        }
        public byte[] Bytes { get; }
        public string ArchivePath { get; }
        public string PublishRoot { get; }
        public ECDsa SigningKey { get; }
        public AtomicFileTemplateAdminPublisher Publisher { get; }
        public string VersionRoot(string version) => Path.Combine(PublishRoot, "pointer", version);
        public TemplateAdminPublishRequest Request(string version)
        {
            var sha = Convert.ToHexString(SHA256.HashData(Bytes));
            var identity = new TemplateIdentity(new("pointer"), new(version), new(sha), new("build-1"));
            var package = new PremiumTemplatePackageManifest(identity, new("audition"), new("pointer_mod"),
                Bytes.Length, PremiumTemplatePackageManifest.PackageMediaType);
            var manifest = TextureManifest.Create(new("audition"), new("pointer_mod"),
            [
                new TextureSlot(new("logo"), new("texture.dds"), "Logo", new("logo"),
                    "Main logo", ["logo"], true, true, new("fit")),
            ]).Manifest!;
            var audit = new TemplateAdminAuditEvent(Guid.NewGuid(), Guid.NewGuid(), identity,
                new("audition"), new("pointer_mod"), 1, DateTimeOffset.UtcNow, "template_version_published");
            TemplateAdminDdsRecord dds = new(new("texture.dds"), new(sha), 6000, 1801,
                DdsFormat.BC3, 1, true, DdsHeaderType.Legacy);
            return new(ArchivePath, package, manifest, [dds], audit, TemplateAdminStorageEncryption.ServerManaged);
        }
        public void Dispose()
        {
            _keys.Dispose();
            SigningKey.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
