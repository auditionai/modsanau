using System.Collections.Immutable;
using System.Buffers;
using System.Security.Cryptography;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Gateway.Services;

public enum PremiumTemplateCatalogStatus { Succeeded, Missing, Unavailable }

public sealed record PremiumTemplateCatalogRecord(
    PremiumTemplatePackageManifest Manifest,
    string StorageReference,
    ImmutableArray<byte> PackageSignature,
    bool Revoked)
{
    public bool IsValid => Manifest is { IsValid: true } && IsOpaqueStorageReference(StorageReference)
        && PackageSignature is { IsDefault: false, Length: 64 };

    private static bool IsOpaqueStorageReference(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256 && !value.StartsWith('/') && !value.EndsWith('/')
        && value.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".."
            && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
}

public sealed record PremiumTemplateCatalogResult(
    PremiumTemplateCatalogStatus Status,
    string DiagnosticCode,
    PremiumTemplateCatalogRecord? Record);

public sealed record PremiumTemplateCatalogListResult(
    PremiumTemplateCatalogStatus Status,
    string DiagnosticCode,
    IReadOnlyList<PremiumTemplatePackageManifest> Manifests);

public interface IPremiumTemplateCatalogService
{
    Task<PremiumTemplateCatalogResult> ResolveAsync(TemplateId templateId, TemplateVersion version,
        CancellationToken cancellationToken = default);

    Task<PremiumTemplateCatalogListResult> ListAsync(
        CancellationToken cancellationToken = default);
}

public sealed record PremiumTemplateStorageAccessResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    Uri? DownloadUri,
    DateTimeOffset? ExpiresAt,
    PremiumTemplateStorageEncryption EncryptionAtRest);

public enum PremiumTemplateStorageEncryption { Unavailable, ServerManaged }

public interface IPremiumTemplatePrivateStorage
{
    Task<PremiumTemplateStorageAccessResult> CreateDownloadAccessAsync(
        string storageReference,
        AuthenticatedGatewayUser user,
        DateTimeOffset maximumExpiry,
        CancellationToken cancellationToken = default);
}

public sealed record PremiumTemplateAccessRequest(
    TemplateId TemplateId,
    TemplateVersion Version,
    GameId GameId,
    ModId ModId);

public sealed record PremiumTemplateAccessResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    PremiumTemplatePackageManifest? Manifest,
    string? PackageSignature,
    Uri? DownloadUri,
    DateTimeOffset? ExpiresAt);

public interface IPremiumTemplateDistributionService
{
    Task<PremiumTemplateAccessResult> AuthorizeAsync(AuthenticatedGatewayUser user,
        PremiumTemplateAccessRequest request, CancellationToken cancellationToken = default);
}

public sealed class PremiumTemplateVerificationKey : IDisposable
{
    private readonly ECDsa _key;
    private int _disposed;
    private PremiumTemplateVerificationKey(ECDsa key) => _key = key;

    public static bool TryCreate(string publicKeyPem, out PremiumTemplateVerificationKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(publicKeyPem) || publicKeyPem.Length > 16_384) return false;
        try
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            if (ecdsa.KeySize != 256) { ecdsa.Dispose(); return false; }
            key = new(ecdsa);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public bool Verify(PremiumTemplatePackageManifest manifest, ReadOnlySpan<byte> signature)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (manifest is not { IsValid: true } || signature.Length != 64) return false;
        var bytes = manifest.GetSigningBytes();
        try
        {
            lock (_key) return _key.VerifyData(bytes, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public override string ToString() => "PremiumTemplateVerificationKey { PublicKey = [REDACTED] }";
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _key.Dispose(); }
}

public enum PremiumTemplatePackageVerificationStatus
{
    Valid,
    InvalidManifest,
    InvalidSignature,
    Truncated,
    Oversized,
    HashMismatch,
    Cancelled,
}

public sealed class PremiumTemplatePackageIntegrityVerifier(PremiumTemplateVerificationKey verificationKey)
{
    public async Task<PremiumTemplatePackageVerificationStatus> VerifyAsync(Stream package,
        PremiumTemplatePackageManifest manifest, ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.CanRead || manifest is not { IsValid: true })
            return PremiumTemplatePackageVerificationStatus.InvalidManifest;
        if (!verificationKey.Verify(manifest, signature.Span))
            return PremiumTemplatePackageVerificationStatus.InvalidSignature;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await package.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > manifest.ContentLength)
                    return PremiumTemplatePackageVerificationStatus.Oversized;
                hash.AppendData(buffer, 0, read);
            }
            if (total < manifest.ContentLength) return PremiumTemplatePackageVerificationStatus.Truncated;
            var digest = hash.GetHashAndReset();
            try
            {
                var actual = Convert.ToHexString(digest);
                return string.Equals(actual, manifest.Identity.Sha256.Value, StringComparison.Ordinal)
                    ? PremiumTemplatePackageVerificationStatus.Valid
                    : PremiumTemplatePackageVerificationStatus.HashMismatch;
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PremiumTemplatePackageVerificationStatus.Cancelled;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public sealed class PremiumTemplateDistributionService(
    IPremiumTemplateCatalogService catalog,
    IEntitlementGrantService grants,
    IPremiumTemplatePrivateStorage storage,
    PremiumTemplateVerificationKey verificationKey,
    TimeProvider timeProvider) : IPremiumTemplateDistributionService
{
    public static readonly TimeSpan MaximumAccessLifetime = TimeSpan.FromMinutes(2);

    public async Task<PremiumTemplateAccessResult> AuthorizeAsync(
        AuthenticatedGatewayUser user,
        PremiumTemplateAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty || request is null || !request.TemplateId.IsValid || !request.Version.IsValid
            || !request.GameId.IsValid || !request.ModId.IsValid)
            return Rejected("PREMIUM_TEMPLATE_REQUEST_INVALID");
        if (cancellationToken.IsCancellationRequested) return Rejected("PREMIUM_TEMPLATE_CANCELLED");

        var resolved = await catalog.ResolveAsync(request.TemplateId, request.Version, cancellationToken)
            .ConfigureAwait(false);
        if (resolved.Status != PremiumTemplateCatalogStatus.Succeeded || resolved.Record is not { IsValid: true } record)
            return resolved.Status == PremiumTemplateCatalogStatus.Missing
                ? Rejected("PREMIUM_TEMPLATE_NOT_FOUND") : Unavailable("PREMIUM_TEMPLATE_CATALOG_UNAVAILABLE");
        if (record.Revoked) return Rejected("PREMIUM_TEMPLATE_REVOKED");
        if (record.Manifest.Identity.TemplateId != request.TemplateId
            || record.Manifest.Identity.Version != request.Version
            || record.Manifest.GameId != request.GameId || record.Manifest.ModId != request.ModId)
            return Rejected("PREMIUM_TEMPLATE_RESOURCE_MISMATCH");
        if (!verificationKey.Verify(record.Manifest, record.PackageSignature.AsSpan()))
            return Rejected("PREMIUM_TEMPLATE_SIGNATURE_INVALID");

        var descriptor = new EntitlementGrantDescriptor(PremiumEntitlementScope.PremiumTemplate,
            EntitlementGrantDescriptor.TemplateAudience, record.Manifest.Identity,
            record.Manifest.GameId, record.Manifest.ModId);
        var issued = await grants.IssueAsync(user, descriptor, cancellationToken).ConfigureAwait(false);
        if (issued.Status != TrustedServiceStatus.Succeeded || string.IsNullOrWhiteSpace(issued.Grant))
            return FromGrantFailure(issued);
        var consumed = await grants.ValidateAndConsumeAsync(issued.Grant, user, descriptor, cancellationToken)
            .ConfigureAwait(false);
        if (consumed.Status != TrustedServiceStatus.Succeeded) return FromGrantFailure(consumed);

        var now = timeProvider.GetUtcNow();
        var maximumExpiry = now.Add(MaximumAccessLifetime);
        var access = await storage.CreateDownloadAccessAsync(record.StorageReference, user,
            maximumExpiry, cancellationToken).ConfigureAwait(false);
        if (access.Status != TrustedServiceStatus.Succeeded || access.DownloadUri is null
            || access.ExpiresAt is null || access.ExpiresAt <= now || access.ExpiresAt > maximumExpiry
            || access.EncryptionAtRest != PremiumTemplateStorageEncryption.ServerManaged
            || !string.Equals(access.DownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(access.DownloadUri.UserInfo) || !string.IsNullOrEmpty(access.DownloadUri.Fragment))
            return Unavailable("PREMIUM_TEMPLATE_STORAGE_UNAVAILABLE");

        return new(TrustedServiceStatus.Succeeded, "PREMIUM_TEMPLATE_ACCESS_AUTHORIZED",
            record.Manifest, Convert.ToBase64String(record.PackageSignature.AsSpan()),
            access.DownloadUri, access.ExpiresAt);
    }

    private static PremiumTemplateAccessResult FromGrantFailure(EntitlementGrantResult result) =>
        new(result.Status == TrustedServiceStatus.Succeeded ? TrustedServiceStatus.Unavailable : result.Status,
            SafeCode(result.DiagnosticCode, "PREMIUM_TEMPLATE_ENTITLEMENT_UNAVAILABLE"), null, null, null, null);
    private static PremiumTemplateAccessResult Rejected(string code) =>
        new(TrustedServiceStatus.Rejected, code, null, null, null, null);
    private static PremiumTemplateAccessResult Unavailable(string code) =>
        new(TrustedServiceStatus.Unavailable, code, null, null, null, null);
    private static string SafeCode(string value, string fallback) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128 && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? value : fallback;
}

public sealed class UnavailablePremiumTemplateCatalogService : IPremiumTemplateCatalogService
{
    public Task<PremiumTemplateCatalogResult> ResolveAsync(TemplateId templateId, TemplateVersion version,
        CancellationToken cancellationToken = default) => Task.FromResult(new PremiumTemplateCatalogResult(
            PremiumTemplateCatalogStatus.Unavailable, "PREMIUM_TEMPLATE_CATALOG_UNAVAILABLE", null));
    public Task<PremiumTemplateCatalogListResult> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PremiumTemplateCatalogListResult(PremiumTemplateCatalogStatus.Unavailable,
            "PREMIUM_TEMPLATE_CATALOG_UNAVAILABLE", []));
}

public sealed class UnavailablePremiumTemplatePrivateStorage : IPremiumTemplatePrivateStorage
{
    public Task<PremiumTemplateStorageAccessResult> CreateDownloadAccessAsync(string storageReference,
        AuthenticatedGatewayUser user, DateTimeOffset maximumExpiry,
        CancellationToken cancellationToken = default) => Task.FromResult(new PremiumTemplateStorageAccessResult(
            TrustedServiceStatus.Unavailable, "PREMIUM_TEMPLATE_STORAGE_UNAVAILABLE", null, null,
            PremiumTemplateStorageEncryption.Unavailable));
}

public sealed class UnavailablePremiumTemplateDistributionService : IPremiumTemplateDistributionService
{
    public Task<PremiumTemplateAccessResult> AuthorizeAsync(AuthenticatedGatewayUser user,
        PremiumTemplateAccessRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PremiumTemplateAccessResult(TrustedServiceStatus.Unavailable,
            cancellationToken.IsCancellationRequested ? "PREMIUM_TEMPLATE_CANCELLED"
                : "PREMIUM_TEMPLATE_DISTRIBUTION_UNAVAILABLE", null, null, null, null));
}
