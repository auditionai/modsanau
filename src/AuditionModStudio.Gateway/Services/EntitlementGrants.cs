using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Gateway.Services;

public enum PremiumEntitlementScope { PremiumTemplate, PremiumAi }

public sealed record EntitlementGrantDescriptor(
    PremiumEntitlementScope Scope,
    string Audience,
    TemplateIdentity? Template,
    GameId? GameId,
    ModId? ModId)
{
    public const string TemplateAudience = "audition-mod-studio:template-download";
    public const string PremiumAiAudience = "audition-mod-studio:premium-ai";

    public bool IsValid => Enum.IsDefined(Scope) && Audience == ExpectedAudience(Scope)
        && (Scope == PremiumEntitlementScope.PremiumTemplate
            ? Template is not null && Template.IsValid && GameId is { IsValid: true } && ModId is { IsValid: true }
            : Template is null && GameId is null && ModId is null);

    public static string ExpectedAudience(PremiumEntitlementScope scope) => scope switch
    {
        PremiumEntitlementScope.PremiumTemplate => TemplateAudience,
        PremiumEntitlementScope.PremiumAi => PremiumAiAudience,
        _ => string.Empty,
    };
}

public sealed record EntitlementRecordResult(TrustedServiceStatus Status, string DiagnosticCode, bool Granted);

public interface IEntitlementRecordService
{
    Task<EntitlementRecordResult> CheckAsync(AuthenticatedGatewayUser user,
        EntitlementGrantDescriptor descriptor, CancellationToken cancellationToken = default);
}

public enum EntitlementNonceConsumeStatus { Consumed, Replay, Unavailable }

public interface IEntitlementNonceStore
{
    Task<EntitlementNonceConsumeStatus> TryConsumeAsync(Guid userId, Guid nonce,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
}

public sealed record EntitlementGrantResult(TrustedServiceStatus Status, string DiagnosticCode, string? Grant);

public interface IEntitlementGrantService
{
    Task<EntitlementGrantResult> IssueAsync(AuthenticatedGatewayUser user,
        EntitlementGrantDescriptor descriptor, CancellationToken cancellationToken = default);
    Task<EntitlementGrantResult> ValidateAndConsumeAsync(string grant, AuthenticatedGatewayUser expectedUser,
        EntitlementGrantDescriptor expectedDescriptor,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableEntitlementRecordService : IEntitlementRecordService
{
    public Task<EntitlementRecordResult> CheckAsync(AuthenticatedGatewayUser user,
        EntitlementGrantDescriptor descriptor, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EntitlementRecordResult(TrustedServiceStatus.Unavailable,
            "ENTITLEMENT_RECORD_UNAVAILABLE", false));
}

public sealed class UnavailableEntitlementNonceStore : IEntitlementNonceStore
{
    public Task<EntitlementNonceConsumeStatus> TryConsumeAsync(Guid userId, Guid nonce,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        Task.FromResult(EntitlementNonceConsumeStatus.Unavailable);
}

public sealed class UnavailableEntitlementGrantService : IEntitlementGrantService
{
    public Task<EntitlementGrantResult> IssueAsync(AuthenticatedGatewayUser user,
        EntitlementGrantDescriptor descriptor, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable(cancellationToken));
    public Task<EntitlementGrantResult> ValidateAndConsumeAsync(string grant, AuthenticatedGatewayUser expectedUser,
        EntitlementGrantDescriptor expectedDescriptor,
        CancellationToken cancellationToken = default) => Task.FromResult(Unavailable(cancellationToken));
    private static EntitlementGrantResult Unavailable(CancellationToken token) => new(
        TrustedServiceStatus.Unavailable,
        token.IsCancellationRequested ? "ENTITLEMENT_GRANT_CANCELLED" : "ENTITLEMENT_GRANT_UNAVAILABLE", null);
}

public sealed class EntitlementSigningKey : IDisposable
{
    private readonly ECDsa _key;
    private int _disposed;
    private EntitlementSigningKey(ECDsa key) => _key = key;

    public static bool TryCreate(string privateKeyPem, out EntitlementSigningKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(privateKeyPem) || privateKeyPem.Length > 16_384) return false;
        try
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyPem);
            var parameters = ecdsa.ExportParameters(true);
            if (ecdsa.KeySize != 256 || parameters.D is not { Length: 32 }) { ecdsa.Dispose(); return false; }
            key = new(ecdsa); return true;
        }
        catch (ArgumentException) { return false; }
        catch (CryptographicException) { return false; }
    }

    internal byte[] Sign(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_key) return _key.SignData(data, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    internal bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_key) return _key.VerifyData(data, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public override string ToString() => "EntitlementSigningKey { [REDACTED] }";
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _key.Dispose(); }
}

public sealed class SignedEntitlementGrantService(
    IEntitlementRecordService records,
    IEntitlementNonceStore nonces,
    EntitlementSigningKey signingKey,
    TimeProvider timeProvider) : IEntitlementGrantService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaximumGrantLength = 8_192;
    private static readonly byte[] Header = Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"AMS-ENT\",\"v\":1}");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) },
    };

    public async Task<EntitlementGrantResult> IssueAsync(AuthenticatedGatewayUser user,
        EntitlementGrantDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty || descriptor is null || !descriptor.IsValid)
            return Rejected("ENTITLEMENT_GRANT_REQUEST_INVALID");
        if (cancellationToken.IsCancellationRequested)
            return Rejected("ENTITLEMENT_GRANT_CANCELLED");
        var entitlement = await records.CheckAsync(user, descriptor, cancellationToken).ConfigureAwait(false);
        if (entitlement.Status != TrustedServiceStatus.Succeeded)
            return new(entitlement.Status, SafeCode(entitlement.DiagnosticCode, "ENTITLEMENT_RECORD_UNAVAILABLE"), null);
        if (!entitlement.Granted) return Rejected("ENTITLEMENT_DENIED");

        var issuedAt = timeProvider.GetUtcNow();
        var payload = new GrantPayload(user.UserId, descriptor.Scope, descriptor.Audience,
            descriptor.Template?.TemplateId.Value, descriptor.Template?.Version.Value,
            descriptor.Template?.Sha256.Value, descriptor.Template?.CompatibleGameBuild.Value,
            descriptor.GameId?.Value, descriptor.ModId?.Value, issuedAt, issuedAt.Add(Lifetime), Guid.NewGuid());
        var encodedHeader = Base64Url(Header);
        var encodedPayload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, Options));
        var signingInput = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}");
        var signature = signingKey.Sign(signingInput);
        try
        {
            var grant = $"{encodedHeader}.{encodedPayload}.{Base64Url(signature)}";
            return grant.Length <= MaximumGrantLength
                ? new(TrustedServiceStatus.Succeeded, "ENTITLEMENT_GRANT_ISSUED", grant)
                : Rejected("ENTITLEMENT_GRANT_TOO_LARGE");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingInput);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public async Task<EntitlementGrantResult> ValidateAndConsumeAsync(string grant,
        AuthenticatedGatewayUser expectedUser, EntitlementGrantDescriptor expectedDescriptor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(grant) || grant.Length > MaximumGrantLength || expectedUser.UserId == Guid.Empty
            || expectedDescriptor is null || !expectedDescriptor.IsValid)
            return Rejected("ENTITLEMENT_GRANT_INVALID");
        if (cancellationToken.IsCancellationRequested) return Rejected("ENTITLEMENT_GRANT_CANCELLED");
        var parts = grant.Split('.');
        if (parts.Length != 3 || !TryDecode(parts[0], out var header) || !header.AsSpan().SequenceEqual(Header)
            || !TryDecode(parts[1], out var payloadBytes) || !TryDecode(parts[2], out var signature))
            return Rejected("ENTITLEMENT_GRANT_INVALID");
        try
        {
            var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            try { if (!signingKey.Verify(signingInput, signature)) return Rejected("ENTITLEMENT_GRANT_SIGNATURE_INVALID"); }
            finally { CryptographicOperations.ZeroMemory(signingInput); }
            GrantPayload? payload;
            try { payload = JsonSerializer.Deserialize<GrantPayload>(payloadBytes, Options); }
            catch (JsonException) { return Rejected("ENTITLEMENT_GRANT_INVALID"); }
            var now = timeProvider.GetUtcNow();
            if (payload is null || payload.UserId != expectedUser.UserId || !MatchesDescriptor(payload, expectedDescriptor)
                || payload.Nonce == Guid.Empty
                || payload.IssuedAt > now.AddSeconds(30) || payload.ExpiresAt <= now
                || payload.ExpiresAt - payload.IssuedAt != Lifetime || !ValidResource(payload))
                return Rejected("ENTITLEMENT_GRANT_CLAIMS_INVALID");
            var consumed = await nonces.TryConsumeAsync(payload.UserId, payload.Nonce,
                payload.ExpiresAt, cancellationToken).ConfigureAwait(false);
            return consumed switch
            {
                EntitlementNonceConsumeStatus.Consumed => new(TrustedServiceStatus.Succeeded,
                    "ENTITLEMENT_GRANT_ACCEPTED", null),
                EntitlementNonceConsumeStatus.Replay => Rejected("ENTITLEMENT_GRANT_REPLAYED"),
                _ => new(TrustedServiceStatus.Unavailable, "ENTITLEMENT_REPLAY_STORE_UNAVAILABLE", null),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(payloadBytes);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static bool ValidResource(GrantPayload payload) => payload.Scope switch
    {
        PremiumEntitlementScope.PremiumTemplate =>
            Stable(payload.TemplateId) && Stable(payload.TemplateVersion) && payload.TemplateSha256 is { Length: 64 }
            && payload.TemplateSha256.All(char.IsAsciiHexDigit) && Stable(payload.CompatibleGameBuild)
            && Stable(payload.GameId) && Stable(payload.ModId),
        PremiumEntitlementScope.PremiumAi => payload.TemplateId is null && payload.TemplateVersion is null
            && payload.TemplateSha256 is null && payload.CompatibleGameBuild is null
            && payload.GameId is null && payload.ModId is null,
        _ => false,
    };
    private static bool MatchesDescriptor(GrantPayload payload, EntitlementGrantDescriptor descriptor) =>
        payload.Scope == descriptor.Scope && payload.Audience == descriptor.Audience
        && payload.TemplateId == descriptor.Template?.TemplateId.Value
        && payload.TemplateVersion == descriptor.Template?.Version.Value
        && payload.TemplateSha256 == descriptor.Template?.Sha256.Value
        && payload.CompatibleGameBuild == descriptor.Template?.CompatibleGameBuild.Value
        && payload.GameId == descriptor.GameId?.Value && payload.ModId == descriptor.ModId?.Value;
    private static bool Stable(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static string SafeCode(string value, string fallback) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128 && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? value : fallback;
    private static EntitlementGrantResult Rejected(string code) => new(TrustedServiceStatus.Rejected, code, null);
    private static string Base64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Length > MaximumGrantLength) return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
            bytes = Convert.FromBase64String(padded); return true;
        }
        catch (FormatException) { return false; }
    }

    private sealed record GrantPayload(Guid UserId, PremiumEntitlementScope Scope, string Audience,
        string? TemplateId, string? TemplateVersion, string? TemplateSha256, string? CompatibleGameBuild,
        string? GameId, string? ModId,
        DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, Guid Nonce);
}
