using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditionModStudio.Core.Auth;
using AuditionModStudio.Core.Subscriptions;

namespace AuditionModStudio.Cloud;

public sealed record GatewayDeviceEntitlementOptions(Uri GatewayUri, string SigningPublicKeyPem)
{
    public bool IsValid => GatewayUri is { IsAbsoluteUri: true, Scheme: "https", UserInfo: "" }
        && !string.IsNullOrWhiteSpace(SigningPublicKeyPem) && SigningPublicKeyPem.Length <= 16_384;
    public override string ToString() => "GatewayDeviceEntitlementOptions { [REDACTED] }";
}

public sealed class GatewayDeviceEntitlementService(
    HttpClient httpClient,
    ISecureSessionStore sessionStore,
    IDeviceSessionBindingStore deviceSessionStore,
    IDeviceEntitlementGrantStore grantStore,
    IAuthenticationService authentication,
    ICapabilityAuthorizationService capabilities,
    GatewayDeviceEntitlementOptions options) : IDeviceEntitlementService
{
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly byte[] ExpectedHeader = Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"AMS-ENT\",\"v\":1}");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } };

    public Task<DeviceEntitlementResult> RegisterAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "v1/device/register", new { }, cancellationToken);
    public Task<DeviceEntitlementResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "v1/device/register", new { }, cancellationToken);
    public Task<DeviceEntitlementResult> RedeemGiftCodeAsync(string giftCode,
        CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, "v1/gift-codes/redeem",
            new { giftCode = giftCode?.Trim(), correlationId = Guid.NewGuid() }, cancellationToken);

    private async Task<DeviceEntitlementResult> SendAsync(HttpMethod method, string route, object? body,
        CancellationToken cancellationToken)
    {
        if (!options.IsValid) return Failure(DeviceEntitlementResultStatus.Unavailable, "DEVICE_CONFIGURATION_INVALID");
        try
        {
            var secrets = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (secrets is null)
            {
                var signedIn = await authentication.SignInAnonymouslyAsync(cancellationToken).ConfigureAwait(false);
                if (!signedIn.Succeeded) return Failure(DeviceEntitlementResultStatus.AuthenticationRequired,
                    signedIn.DiagnosticCode);
                secrets = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (secrets.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var refreshed = await authentication.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
                if (!refreshed.Succeeded) return Failure(DeviceEntitlementResultStatus.AuthenticationRequired,
                    refreshed.DiagnosticCode);
                secrets = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            if (secrets is null) return Failure(DeviceEntitlementResultStatus.AuthenticationRequired, "AUTH_SESSION_MISSING");

            using var request = new HttpRequestMessage(method, new Uri(options.GatewayUri, route));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.AccessToken);
            var binding = await deviceSessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (binding is { IsValid: true, SessionId: not null })
            {
                request.Headers.TryAddWithoutValidation(DeviceSessionProtocol.DeviceIdHeader, binding.DeviceId.ToString("D"));
                request.Headers.TryAddWithoutValidation(DeviceSessionProtocol.SessionIdHeader, binding.SessionId.Value.ToString("D"));
            }
            if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return Failure(DeviceEntitlementResultStatus.AuthenticationRequired, "DEVICE_AUTHENTICATION_REQUIRED");
                if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                    return Failure(DeviceEntitlementResultStatus.Rejected, "DEVICE_REQUEST_REJECTED");
                if (!response.IsSuccessStatusCode)
                    return await CachedAsync(cancellationToken).ConfigureAwait(false)
                        ?? Failure(DeviceEntitlementResultStatus.Unavailable, "DEVICE_ENTITLEMENT_UNAVAILABLE");
                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                    return Invalid();
                await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
                var snapshot = await response.Content.ReadFromJsonAsync<DeviceEntitlementSnapshot>(JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (snapshot is null || !ValidateGrant(snapshot)) return Invalid();
                await grantStore.SaveAsync(snapshot.SignedGrant, cancellationToken).ConfigureAwait(false);
                capabilities.Apply(snapshot);
                return new(DeviceEntitlementResultStatus.Succeeded, "DEVICE_ENTITLEMENT_READY", snapshot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failure(DeviceEntitlementResultStatus.Cancelled, "DEVICE_ENTITLEMENT_CANCELLED"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException or CryptographicException or InvalidOperationException)
        { return await CachedAsync(cancellationToken).ConfigureAwait(false)
            ?? Failure(DeviceEntitlementResultStatus.Unavailable, "DEVICE_ENTITLEMENT_UNAVAILABLE"); }
    }

    private async Task<DeviceEntitlementResult?> CachedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var grant = await grantStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(grant)) return null;
            var parts = grant.Split('.');
            if (parts.Length != 3 || !Decode(parts[1], out var payloadBytes)) return null;
            try
            {
                var payload = JsonSerializer.Deserialize<CachedGrantPayload>(payloadBytes, JsonOptions);
                if (payload is null || payload.ExpiresAt <= DateTimeOffset.UtcNow
                    || !Enum.TryParse<DeviceCommercialStatus>(payload.DeviceStatus, true, out var deviceStatus)
                    || !Enum.TryParse<SubscriptionStatus>(payload.SubscriptionStatus, true, out var subscriptionStatus))
                    return null;
                var snapshot = new DeviceEntitlementSnapshot(payload.DeviceCode, deviceStatus, subscriptionStatus,
                    payload.SubscriptionExpiresAt, payload.AvailableCredits, payload.Capabilities,
                    payload.IssuedAt, payload.ExpiresAt, grant);
                if (!ValidateGrant(snapshot)) return null;
                capabilities.Apply(snapshot);
                return new(DeviceEntitlementResultStatus.Succeeded, "DEVICE_ENTITLEMENT_OFFLINE_GRANT", snapshot);
            }
            finally { CryptographicOperations.ZeroMemory(payloadBytes); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or CryptographicException) { return null; }
    }

    private bool ValidateGrant(DeviceEntitlementSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.DeviceCode) || snapshot.SignedGrant.Length > 8192
            || snapshot.GrantExpiresAt <= snapshot.ObservedAt) return false;
        var parts = snapshot.SignedGrant.Split('.');
        if (parts.Length != 3 || !Decode(parts[0], out var header) || !header.SequenceEqual(ExpectedHeader)
            || !Decode(parts[1], out var payload) || !Decode(parts[2], out var signature)) return false;
        using var key = ECDsa.Create();
        key.ImportFromPem(options.SigningPublicKeyPem);
        var input = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        try
        {
            if (!key.VerifyData(input, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return false;
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.GetProperty("deviceCode").GetString() == snapshot.DeviceCode
                && root.GetProperty("expiresAt").GetDateTimeOffset() == snapshot.GrantExpiresAt
                && root.GetProperty("availableCredits").GetInt64() == snapshot.AvailableCredits;
        }
        finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature); }
    }
    private static bool Decode(string value, out byte[] bytes)
    {
        bytes=[]; try { var padded=value.Replace('-','+').Replace('_','/'); padded += (padded.Length % 4) switch
            {2=>"==",3=>"=",0=>"",_=>throw new FormatException()}; bytes=Convert.FromBase64String(padded); return true; }
        catch (FormatException) { return false; }
    }
    private static DeviceEntitlementResult Invalid() =>
        Failure(DeviceEntitlementResultStatus.InvalidResponse, "DEVICE_ENTITLEMENT_RESPONSE_INVALID");
    private static DeviceEntitlementResult Failure(DeviceEntitlementResultStatus status, string code) => new(status,code,null);
    private sealed record CachedGrantPayload(string DeviceCode, string DeviceStatus, string SubscriptionStatus,
        DateTimeOffset? SubscriptionExpiresAt, DeviceCapabilities Capabilities, long AvailableCredits,
        DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
}
