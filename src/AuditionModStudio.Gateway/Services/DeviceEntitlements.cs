using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using AuditionModStudio.Core.Subscriptions;

namespace AuditionModStudio.Gateway.Services;

public sealed class DeviceEntitlementOptions
{
    public bool Enabled { get; init; }
    public TimeSpan GrantLifetime { get; init; }
    public string SigningPrivateKeyPem { get; init; } = string.Empty;
    public bool IsOperational => Enabled && GrantLifetime is { TotalMinutes: >= 5 and <= 1440 }
        && !string.IsNullOrWhiteSpace(SigningPrivateKeyPem);
    public static DeviceEntitlementOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Gateway:DeviceEntitlements");
        return new()
        {
            Enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled,
            GrantLifetime = int.TryParse(section["GrantLifetimeMinutes"], out var minutes)
                && minutes is >= 5 and <= 1440 ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero,
            SigningPrivateKeyPem = section["SigningPrivateKeyPem"] ?? string.Empty,
        };
    }
    public override string ToString() => "DeviceEntitlementOptions { [REDACTED] }";
}

public sealed record TrustedDeviceEntitlementResult(
    TrustedServiceStatus Status, string DiagnosticCode, DeviceEntitlementSnapshot? Snapshot);

public interface ITrustedDeviceEntitlementService
{
    Task<TrustedDeviceEntitlementResult> RegisterAsync(AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default);
    Task<TrustedDeviceEntitlementResult> RefreshAsync(AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default);
    Task<TrustedDeviceEntitlementResult> RedeemAsync(AuthenticatedGatewayUser user, string giftCode,
        Guid correlationId, CancellationToken cancellationToken = default);
}

public sealed class UnavailableTrustedDeviceEntitlementService : ITrustedDeviceEntitlementService
{
    private static TrustedDeviceEntitlementResult Result() => new(TrustedServiceStatus.Unavailable,
        "DEVICE_ENTITLEMENT_UNAVAILABLE", null);
    public Task<TrustedDeviceEntitlementResult> RegisterAsync(AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default) => Task.FromResult(Result());
    public Task<TrustedDeviceEntitlementResult> RefreshAsync(AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default) => Task.FromResult(Result());
    public Task<TrustedDeviceEntitlementResult> RedeemAsync(AuthenticatedGatewayUser user, string giftCode,
        Guid correlationId, CancellationToken cancellationToken = default) => Task.FromResult(Result());
}

public sealed class PostgresDeviceEntitlementService(
    NpgsqlDataSource dataSource,
    EntitlementSigningKey signingKey,
    DeviceEntitlementOptions options) : ITrustedDeviceEntitlementService
{
    private static readonly byte[] Header = Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"AMS-ENT\",\"v\":1}");
    private static readonly HashSet<string> Rejections = new(StringComparer.Ordinal)
    { "DEVICE_PROFILE_REQUEST_INVALID", "DEVICE_INACTIVE", "GIFT_CODE_INVALID",
      "GIFT_CODE_UNAVAILABLE", "GIFT_CODE_ALREADY_REDEEMED" };

    public Task<TrustedDeviceEntitlementResult> RegisterAsync(AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default) => QueryAsync(user, null, Guid.Empty, cancellationToken);
    public Task<TrustedDeviceEntitlementResult> RefreshAsync(AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default) => QueryAsync(user, null, Guid.Empty, cancellationToken);
    public Task<TrustedDeviceEntitlementResult> RedeemAsync(AuthenticatedGatewayUser user, string giftCode,
        Guid correlationId, CancellationToken cancellationToken = default) =>
        QueryAsync(user, giftCode, correlationId, cancellationToken);

    private async Task<TrustedDeviceEntitlementResult> QueryAsync(AuthenticatedGatewayUser user,
        string? giftCode, Guid correlationId, CancellationToken cancellationToken)
    {
        if (!options.IsOperational || user.UserId == Guid.Empty || giftCode is not null
            && (correlationId == Guid.Empty || !ValidGiftCode(giftCode))) return Rejected("DEVICE_ENTITLEMENT_REQUEST_INVALID");
        try
        {
            await using var command = dataSource.CreateCommand(giftCode is null
                ? "SELECT * FROM private.device_profile_register($1)"
                : "SELECT * FROM private.gift_code_redeem($1,$2,$3)");
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            if (giftCode is not null)
            {
                command.Parameters.AddWithValue(NpgsqlDbType.Text, giftCode.Trim().ToUpperInvariant());
                command.Parameters.AddWithValue(NpgsqlDbType.Uuid, correlationId);
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return Unavailable();
            var observedAt = reader.GetFieldValue<DateTimeOffset>(10);
            var expiresAt = observedAt.Add(options.GrantLifetime);
            var capabilities = new DeviceCapabilities(reader.GetBoolean(5), reader.GetBoolean(6),
                reader.GetBoolean(7), reader.GetBoolean(8));
            var payload = new DeviceGrantPayload(user.UserId, reader.GetGuid(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                capabilities, reader.GetInt64(9), observedAt, expiresAt, Guid.NewGuid());
            var grant = Sign(payload);
            var snapshot = new DeviceEntitlementSnapshot(payload.DeviceCode,
                Enum.Parse<DeviceCommercialStatus>(payload.DeviceStatus, true),
                Enum.Parse<SubscriptionStatus>(payload.SubscriptionStatus, true), payload.SubscriptionExpiresAt,
                payload.AvailableCredits, capabilities, observedAt, expiresAt, grant);
            return new(TrustedServiceStatus.Succeeded,
                giftCode is null ? "DEVICE_ENTITLEMENT_REFRESHED" : "GIFT_CODE_REDEEMED", snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PostgresException exception) when (exception.SqlState == "P0001"
            && Rejections.Contains(exception.ConstraintName ?? string.Empty))
        { return Rejected(exception.ConstraintName!); }
        catch (NpgsqlException) { return Unavailable(); }
        catch (Exception exception) when (exception is CryptographicException or JsonException
            or InvalidOperationException or ArgumentException) { return Unavailable(); }
    }

    private string Sign(DeviceGrantPayload payload)
    {
        var h = Base64Url(Header);
        var p = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var input = Encoding.ASCII.GetBytes($"{h}.{p}");
        var signature = signingKey.Sign(input);
        try { return $"{h}.{p}.{Base64Url(signature)}"; }
        finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(signature); }
    }
    private static bool ValidGiftCode(string value) => value.Length is >= 8 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static string Base64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+','-').Replace('/','_');
    private static TrustedDeviceEntitlementResult Rejected(string code) =>
        new(TrustedServiceStatus.Rejected, code, null);
    private static TrustedDeviceEntitlementResult Unavailable() =>
        new(TrustedServiceStatus.Unavailable, "DEVICE_ENTITLEMENT_UNAVAILABLE", null);
    private sealed record DeviceGrantPayload(Guid UserId, Guid DeviceProfileId, string DeviceCode,
        string DeviceStatus, string SubscriptionStatus, DateTimeOffset? SubscriptionExpiresAt,
        DeviceCapabilities Capabilities, long AvailableCredits, DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt, Guid Nonce);
}
