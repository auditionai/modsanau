using System.Collections.Immutable;
using System.Security.Cryptography;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Gateway.Services;

namespace Gateway.Tests;

public sealed class PremiumTemplateDistributionTests
{
    private static readonly AuthenticatedGatewayUser User = new(Guid.Parse("88c686a6-615b-4ac8-a775-53286de653a7"));

    [Fact]
    public async Task Exact_authorized_package_returns_short_lived_private_access()
    {
        using var context = Create();

        var result = await context.Service.AuthorizeAsync(User, Request());

        Assert.Equal(TrustedServiceStatus.Succeeded, result.Status);
        Assert.Equal(Identity(), result.Manifest!.Identity);
        Assert.Equal("https", result.DownloadUri!.Scheme);
        Assert.True(result.ExpiresAt <= context.Time.GetUtcNow().AddMinutes(2));
        Assert.Equal(User.UserId, context.Storage.LastUser!.UserId);
        Assert.Equal("packages/pointer/1/package", context.Storage.LastReference);
    }

    [Fact]
    public async Task Missing_revoked_wrong_resource_and_bad_signature_fail_closed()
    {
        using var missing = Create(catalogStatus: PremiumTemplateCatalogStatus.Missing);
        using var revoked = Create(revoked: true);
        using var wrongResource = Create();
        using var wrongVersion = Create();
        using var badSignature = Create(tamperSignature: true);

        var results = new[]
        {
            await missing.Service.AuthorizeAsync(User, Request()),
            await revoked.Service.AuthorizeAsync(User, Request()),
            await wrongResource.Service.AuthorizeAsync(User, Request() with { ModId = new("wrong_mod") }),
            await wrongVersion.Service.AuthorizeAsync(User, Request() with { Version = new("2") }),
            await badSignature.Service.AuthorizeAsync(User, Request()),
        };

        Assert.All(results, result => Assert.Equal(TrustedServiceStatus.Rejected, result.Status));
        Assert.Equal(0, missing.Storage.CallCount);
        Assert.Equal(0, revoked.Storage.CallCount);
        Assert.Equal(0, wrongResource.Storage.CallCount);
        Assert.Equal(0, wrongVersion.Storage.CallCount);
        Assert.Equal(0, badSignature.Storage.CallCount);
    }

    [Fact]
    public async Task Entitlement_rejection_or_replay_prevents_storage_access()
    {
        using var denied = Create(grantStatus: TrustedServiceStatus.Rejected);
        using var replay = Create(consumeStatus: TrustedServiceStatus.Rejected);

        var deniedResult = await denied.Service.AuthorizeAsync(User, Request());
        var replayResult = await replay.Service.AuthorizeAsync(User, Request());

        Assert.Equal(TrustedServiceStatus.Rejected, deniedResult.Status);
        Assert.Equal(TrustedServiceStatus.Rejected, replayResult.Status);
        Assert.Equal(0, denied.Storage.CallCount);
        Assert.Equal(0, replay.Storage.CallCount);
    }

    [Fact]
    public async Task Storage_unavailable_or_invalid_access_never_returns_package_authority()
    {
        using var unavailable = Create(storageStatus: TrustedServiceStatus.Unavailable);
        using var insecure = Create(downloadUri: new Uri("http://storage.invalid/package"));
        using var tooLong = Create(accessLifetime: TimeSpan.FromMinutes(3));
        using var expired = Create(accessLifetime: TimeSpan.FromSeconds(-1));

        var results = new[]
        {
            await unavailable.Service.AuthorizeAsync(User, Request()),
            await insecure.Service.AuthorizeAsync(User, Request()),
            await tooLong.Service.AuthorizeAsync(User, Request()),
            await expired.Service.AuthorizeAsync(User, Request()),
        };

        Assert.All(results, result =>
        {
            Assert.Equal(TrustedServiceStatus.Unavailable, result.Status);
            Assert.Null(result.DownloadUri);
            Assert.Null(result.Manifest);
        });
    }

    [Fact]
    public async Task Package_bytes_require_exact_length_hash_and_manifest_signature()
    {
        var bytes = "trusted premium package"u8.ToArray();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(PremiumTemplateVerificationKey.TryCreate(signer.ExportSubjectPublicKeyInfoPem(), out var key));
        using var verificationKey = key!;
        var manifest = Manifest(bytes);
        var signingBytes = manifest.GetSigningBytes();
        var signature = signer.SignData(signingBytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        CryptographicOperations.ZeroMemory(signingBytes);
        var verifier = new PremiumTemplatePackageIntegrityVerifier(verificationKey);

        var valid = await verifier.VerifyAsync(new MemoryStream(bytes), manifest, signature);
        var truncated = await verifier.VerifyAsync(new MemoryStream(bytes[..^1]), manifest, signature);
        var oversized = await verifier.VerifyAsync(new MemoryStream([.. bytes, (byte)0]), manifest, signature);
        var changed = bytes.ToArray(); changed[0] ^= 1;
        var mismatch = await verifier.VerifyAsync(new MemoryStream(changed), manifest, signature);
        signature[0] ^= 1;
        var invalidSignature = await verifier.VerifyAsync(new MemoryStream(bytes), manifest, signature);

        Assert.Equal(PremiumTemplatePackageVerificationStatus.Valid, valid);
        Assert.Equal(PremiumTemplatePackageVerificationStatus.Truncated, truncated);
        Assert.Equal(PremiumTemplatePackageVerificationStatus.Oversized, oversized);
        Assert.Equal(PremiumTemplatePackageVerificationStatus.HashMismatch, mismatch);
        Assert.Equal(PremiumTemplatePackageVerificationStatus.InvalidSignature, invalidSignature);
    }

    private static Context Create(PremiumTemplateCatalogStatus catalogStatus = PremiumTemplateCatalogStatus.Succeeded,
        bool revoked = false, bool tamperSignature = false,
        TrustedServiceStatus grantStatus = TrustedServiceStatus.Succeeded,
        TrustedServiceStatus consumeStatus = TrustedServiceStatus.Succeeded,
        TrustedServiceStatus storageStatus = TrustedServiceStatus.Succeeded,
        Uri? downloadUri = null, TimeSpan? accessLifetime = null)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(PremiumTemplateVerificationKey.TryCreate(signer.ExportSubjectPublicKeyInfoPem(), out var key));
        var manifest = Manifest();
        var signingBytes = manifest.GetSigningBytes();
        var signature = signer.SignData(signingBytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        CryptographicOperations.ZeroMemory(signingBytes);
        if (tamperSignature) signature[0] ^= 1;
        var record = new PremiumTemplateCatalogRecord(manifest, "packages/pointer/1/package",
            ImmutableArray.Create(signature), revoked);
        CryptographicOperations.ZeroMemory(signature);
        var catalog = new StubCatalog(catalogStatus, record);
        var grants = new StubGrants(grantStatus, consumeStatus);
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        var storage = new StubStorage(storageStatus, downloadUri ?? new("https://storage.invalid/package?sig=short"),
            accessLifetime ?? TimeSpan.FromMinutes(1), time);
        return new(key!, storage, time, new(catalog, grants, storage, key!, time));
    }

    private static PremiumTemplateAccessRequest Request() =>
        new(new("pointer"), new("1"), new("audition"), new("pointer_mod"));
    private static TemplateIdentity Identity() => new(new("pointer"), new("1"),
        new(new string('A', 64)), new("build-1"));
    private static PremiumTemplatePackageManifest Manifest() => new(Identity(),
        new("audition"), new("pointer_mod"), 4096, PremiumTemplatePackageManifest.PackageMediaType);
    private static PremiumTemplatePackageManifest Manifest(byte[] content) => new(
        new(new("pointer"), new("1"), new(Convert.ToHexString(SHA256.HashData(content))), new("build-1")),
        new("audition"), new("pointer_mod"), content.Length, PremiumTemplatePackageManifest.PackageMediaType);

    private sealed record Context(PremiumTemplateVerificationKey Key, StubStorage Storage,
        FixedTimeProvider Time, PremiumTemplateDistributionService Service) : IDisposable
    { public void Dispose() => Key.Dispose(); }

    private sealed class StubCatalog(PremiumTemplateCatalogStatus status, PremiumTemplateCatalogRecord record)
        : IPremiumTemplateCatalogService
    {
        public Task<PremiumTemplateCatalogResult> ResolveAsync(TemplateId templateId, TemplateVersion version,
            CancellationToken cancellationToken = default) => Task.FromResult(new PremiumTemplateCatalogResult(
                status, "PREMIUM_TEMPLATE_CATALOG_RESULT", status == PremiumTemplateCatalogStatus.Succeeded ? record : null));
        public Task<PremiumTemplateCatalogListResult> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PremiumTemplateCatalogListResult(status, "PREMIUM_TEMPLATE_CATALOG_RESULT",
                status == PremiumTemplateCatalogStatus.Succeeded ? [record.Manifest] : []));
    }

    private sealed class StubGrants(TrustedServiceStatus issue, TrustedServiceStatus consume)
        : IEntitlementGrantService
    {
        public Task<EntitlementGrantResult> IssueAsync(AuthenticatedGatewayUser user,
            EntitlementGrantDescriptor descriptor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EntitlementGrantResult(issue, issue == TrustedServiceStatus.Succeeded
                ? "ENTITLEMENT_GRANT_ISSUED" : "ENTITLEMENT_DENIED",
                issue == TrustedServiceStatus.Succeeded ? "header.payload.signature" : null));
        public Task<EntitlementGrantResult> ValidateAndConsumeAsync(string grant,
            AuthenticatedGatewayUser expectedUser, EntitlementGrantDescriptor expectedDescriptor,
            CancellationToken cancellationToken = default) => Task.FromResult(new EntitlementGrantResult(consume,
                consume == TrustedServiceStatus.Succeeded ? "ENTITLEMENT_GRANT_ACCEPTED"
                    : "ENTITLEMENT_GRANT_REPLAYED", null));
    }

    private sealed class StubStorage(TrustedServiceStatus status, Uri uri, TimeSpan lifetime, TimeProvider time)
        : IPremiumTemplatePrivateStorage
    {
        public int CallCount { get; private set; }
        public string? LastReference { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public Task<PremiumTemplateStorageAccessResult> CreateDownloadAccessAsync(string storageReference,
            AuthenticatedGatewayUser user, DateTimeOffset maximumExpiry,
            CancellationToken cancellationToken = default)
        {
            CallCount++; LastReference = storageReference; LastUser = user;
            return Task.FromResult(new PremiumTemplateStorageAccessResult(status, "PREMIUM_TEMPLATE_STORAGE_RESULT",
                status == TrustedServiceStatus.Succeeded ? uri : null,
                status == TrustedServiceStatus.Succeeded ? time.GetUtcNow().Add(lifetime) : null,
                status == TrustedServiceStatus.Succeeded ? PremiumTemplateStorageEncryption.ServerManaged
                    : PremiumTemplateStorageEncryption.Unavailable));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
}
