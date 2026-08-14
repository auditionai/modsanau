using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace AuditionModStudio.Gateway.Services;

public sealed record AdminPortalOptions
{
    public const string DefaultBootstrapEmail = "codycn2804@gmail.com";
    public Guid? BootstrapUserId { get; init; }
    public string BootstrapEmail { get; init; } = string.Empty;
    public int DefaultPageSize { get; init; } = 25;
    public int MaximumPageSize { get; init; } = 100;

    public bool HasBootstrapConfiguration =>
        BootstrapUserId is not null && BootstrapUserId != Guid.Empty
        || !string.IsNullOrWhiteSpace(BootstrapEmail);

    public bool IsValid =>
        (!string.IsNullOrWhiteSpace(BootstrapEmail) ? IsSafeEmail(BootstrapEmail) : true)
        && DefaultPageSize is > 0 and <= 100
        && MaximumPageSize is > 0 and <= 250
        && DefaultPageSize <= MaximumPageSize;

    public static AdminPortalOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Gateway:AdminPortal");
        return new()
        {
            BootstrapUserId = Guid.TryParse(section["BootstrapUserId"], out var bootstrapUserId)
                && bootstrapUserId != Guid.Empty ? bootstrapUserId : null,
            BootstrapEmail = section["BootstrapEmail"] ?? DefaultBootstrapEmail,
            DefaultPageSize = ParseInt(section["DefaultPageSize"], 25, 1, 100),
            MaximumPageSize = ParseInt(section["MaximumPageSize"], 100, 1, 250),
        };
    }

    public override string ToString() => "AdminPortalOptions { [REDACTED] }";

    private static bool IsSafeEmail(string value) =>
        value.Length <= 320 && value.Contains('@') && !value.Any(char.IsControl);

    private static int ParseInt(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed >= minimum && parsed <= maximum ? parsed : fallback;
}

public sealed record AdminAccessProfile(
    Guid AdminUserId,
    string Role,
    string MfaState,
    string? DisplayLabel,
    bool IsActive,
    string BootstrapSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSeenAt);

public sealed record AdminBootstrapResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    AdminAccessProfile? Admin,
    bool Bootstrapped,
    bool BootstrapConfigured);

public sealed record AdminSummary(
    long TotalDevices,
    long ActiveDevices,
    long BlockedDevices,
    long RevokedDevices,
    long ExpiredSubscriptions,
    long AvailableCredits,
    long ReservedCredits,
    long ActiveGiftCodes,
    long GiftCodeRedemptions,
    long AdminActions,
    long DeviceEvents,
    DateTimeOffset ObservedAt);

public sealed record AdminDeviceSummary(
    Guid DeviceProfileId,
    Guid AuthUserId,
    string PublicDeviceCode,
    string DeviceStatus,
    string SubscriptionStatus,
    DateTimeOffset? SubscriptionStartsAt,
    DateTimeOffset? SubscriptionExpiresAt,
    long AvailableCredits,
    long ReservedCredits,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);

public sealed record AdminDeviceEventSummary(
    Guid EventId,
    string EventType,
    string PublicDeviceCode,
    Guid CorrelationId,
    DateTimeOffset EventAt,
    string Metadata);

public sealed record AdminGiftCodeSummary(
    Guid GiftCodeId,
    string CodePrefix,
    string Kind,
    int? DurationDays,
    long? CreditAmount,
    int MaximumRedemptions,
    int RedemptionCount,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? DisabledAt,
    DateTimeOffset CreatedAt);

public sealed record AdminGiftCodeRedemptionSummary(
    Guid RedemptionId,
    Guid GiftCodeId,
    Guid DeviceProfileId,
    string PublicDeviceCode,
    DateTimeOffset RedeemedAt,
    int? DurationDays,
    long? CreditAmount);

public sealed record AdminAuditSummary(
    Guid EventId,
    Guid ActorAdminUserId,
    string EventType,
    string TargetKind,
    Guid? TargetId,
    Guid CorrelationId,
    string Details,
    DateTimeOffset EventAt);

public sealed record AdminDeviceDetail(
    AdminDeviceSummary Device,
    ImmutableArray<AdminDeviceEventSummary> RecentEvents,
    ImmutableArray<AdminGiftCodeRedemptionSummary> Redemptions);

public sealed record AdminDashboardSnapshot(
    AdminSummary Summary,
    ImmutableArray<AdminDeviceSummary> Devices,
    ImmutableArray<AdminGiftCodeSummary> GiftCodes,
    ImmutableArray<AdminAuditSummary> RecentAudit,
    ImmutableArray<AdminDeviceEventSummary> RecentDeviceActivity);

public sealed record AdminDevicePageResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    ImmutableArray<AdminDeviceSummary> Items,
    long TotalCount);

public sealed record AdminGiftCodePageResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    ImmutableArray<AdminGiftCodeSummary> Items,
    long TotalCount);

public sealed record AdminAuditPageResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    ImmutableArray<AdminAuditSummary> Items);

public sealed record AdminMutationResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    AdminDeviceDetail? Device,
    AdminGiftCodeSummary? GiftCode,
    string? OneTimeCode = null);

public interface IAdminPortalService
{
    Task<AdminBootstrapResult> BootstrapAsync(
        AuthenticatedGatewayUser user,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default);

    Task<AdminBootstrapResult> GetBootstrapStateAsync(
        AuthenticatedGatewayUser user,
        string? email,
        CancellationToken cancellationToken = default);

    Task<AdminDashboardSnapshot> GetDashboardAsync(
        AuthenticatedGatewayUser user,
        int deviceLimit,
        int giftCodeLimit,
        int auditLimit,
        int activityLimit,
        CancellationToken cancellationToken = default);

    Task<AdminDevicePageResult> SearchDevicesAsync(
        AuthenticatedGatewayUser user,
        string? query,
        string? status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task<AdminDeviceDetail?> GetDeviceAsync(
        AuthenticatedGatewayUser user,
        Guid deviceProfileId,
        int activityLimit,
        int redemptionLimit,
        CancellationToken cancellationToken = default);

    Task<AdminMutationResult> SetDeviceStatusAsync(
        AuthenticatedGatewayUser user,
        Guid deviceProfileId,
        string status,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<AdminMutationResult> ExtendSubscriptionAsync(
        AuthenticatedGatewayUser user,
        Guid deviceProfileId,
        int extensionDays,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<AdminMutationResult> GrantCreditsAsync(
        AuthenticatedGatewayUser user,
        Guid targetUserId,
        long amount,
        string authorityReference,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<AdminGiftCodePageResult> ListGiftCodesAsync(
        AuthenticatedGatewayUser user,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task<AdminMutationResult> CreateGiftCodeAsync(
        AuthenticatedGatewayUser user,
        string kind,
        int? durationDays,
        long? creditAmount,
        int maximumRedemptions,
        DateTimeOffset? expiresAt,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<AdminMutationResult> RevokeGiftCodeAsync(
        AuthenticatedGatewayUser user,
        Guid giftCodeId,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<AdminGiftCodeRedemptionSummary>> ListGiftCodeRedemptionsAsync(
        AuthenticatedGatewayUser user,
        Guid giftCodeId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<AdminAuditPageResult> ListAuditAsync(
        AuthenticatedGatewayUser user,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableAdminPortalService : IAdminPortalService
{
    private static readonly AdminBootstrapResult BootstrapUnavailable =
        new(TrustedServiceStatus.Unavailable, "ADMIN_PORTAL_UNAVAILABLE", null, false, false);
    private static readonly AdminDevicePageResult DeviceUnavailable =
        new(TrustedServiceStatus.Unavailable, "ADMIN_PORTAL_UNAVAILABLE", [], 0);
    private static readonly AdminGiftCodePageResult GiftCodeUnavailable =
        new(TrustedServiceStatus.Unavailable, "ADMIN_PORTAL_UNAVAILABLE", [], 0);
    private static readonly AdminAuditPageResult AuditUnavailable =
        new(TrustedServiceStatus.Unavailable, "ADMIN_PORTAL_UNAVAILABLE", []);
    private static readonly AdminMutationResult MutationUnavailable =
        new(TrustedServiceStatus.Unavailable, "ADMIN_PORTAL_UNAVAILABLE", null, null);

    public Task<AdminBootstrapResult> BootstrapAsync(
        AuthenticatedGatewayUser user,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default) => Task.FromResult(BootstrapUnavailable);

    public Task<AdminBootstrapResult> GetBootstrapStateAsync(
        AuthenticatedGatewayUser user,
        string? email,
        CancellationToken cancellationToken = default) => Task.FromResult(BootstrapUnavailable);

    public Task<AdminDashboardSnapshot> GetDashboardAsync(
        AuthenticatedGatewayUser user,
        int deviceLimit,
        int giftCodeLimit,
        int auditLimit,
        int activityLimit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdminDashboardSnapshot(
            new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow),
            [], [], [], []));

    public Task<AdminDevicePageResult> SearchDevicesAsync(
        AuthenticatedGatewayUser user,
        string? query,
        string? status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default) => Task.FromResult(DeviceUnavailable);

    public Task<AdminDeviceDetail?> GetDeviceAsync(
        AuthenticatedGatewayUser user,
        Guid deviceProfileId,
        int activityLimit,
        int redemptionLimit,
        CancellationToken cancellationToken = default) => Task.FromResult<AdminDeviceDetail?>(null);

    public Task<AdminMutationResult> SetDeviceStatusAsync(
        AuthenticatedGatewayUser user,
        Guid deviceProfileId,
        string status,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default) => Task.FromResult(MutationUnavailable);

    public Task<AdminMutationResult> ExtendSubscriptionAsync(
        AuthenticatedGatewayUser user,
        Guid deviceProfileId,
        int extensionDays,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default) => Task.FromResult(MutationUnavailable);

    public Task<AdminMutationResult> GrantCreditsAsync(
        AuthenticatedGatewayUser user,
        Guid targetUserId,
        long amount,
        string authorityReference,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default) => Task.FromResult(MutationUnavailable);

    public Task<AdminGiftCodePageResult> ListGiftCodesAsync(
        AuthenticatedGatewayUser user,
        int limit,
        int offset,
        CancellationToken cancellationToken = default) => Task.FromResult(GiftCodeUnavailable);

    public Task<AdminMutationResult> CreateGiftCodeAsync(
        AuthenticatedGatewayUser user,
        string kind,
        int? durationDays,
        long? creditAmount,
        int maximumRedemptions,
        DateTimeOffset? expiresAt,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default) => Task.FromResult(MutationUnavailable);

    public Task<AdminMutationResult> RevokeGiftCodeAsync(
        AuthenticatedGatewayUser user,
        Guid giftCodeId,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default) => Task.FromResult(MutationUnavailable);

    public Task<ImmutableArray<AdminGiftCodeRedemptionSummary>> ListGiftCodeRedemptionsAsync(
        AuthenticatedGatewayUser user,
        Guid giftCodeId,
        int limit,
        CancellationToken cancellationToken = default) => Task.FromResult(ImmutableArray<AdminGiftCodeRedemptionSummary>.Empty);

    public Task<AdminAuditPageResult> ListAuditAsync(
        AuthenticatedGatewayUser user,
        int limit,
        int offset,
        CancellationToken cancellationToken = default) => Task.FromResult(AuditUnavailable);
}

public sealed class PostgresAdminPortalService(
    NpgsqlDataSource dataSource,
    AdminPortalOptions options) : IAdminPortalService
{
    private static readonly HashSet<string> DeviceStatuses = new(StringComparer.Ordinal)
        { "active", "blocked", "revoked" };
    private static readonly HashSet<string> AdminEventTypes = new(StringComparer.Ordinal)
    {
        "ADMIN_BOOTSTRAPPED",
        "DEVICE_BLOCKED",
        "DEVICE_UNBLOCKED",
        "DEVICE_REVOKED",
        "SUBSCRIPTION_EXTENDED",
        "CREDITS_GRANTED",
        "GIFT_CODE_CREATED",
        "GIFT_CODE_REVOKED",
    };

    public async Task<AdminBootstrapResult> BootstrapAsync(
        AuthenticatedGatewayUser user,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        if (!options.HasBootstrapConfiguration || user.UserId == Guid.Empty)
            return new(TrustedServiceStatus.Rejected, "ADMIN_BOOTSTRAP_NOT_CONFIGURED", null, false, false);

        if (!MatchesBootstrapIdentity(user.UserId, email))
            return new(TrustedServiceStatus.Rejected, "ADMIN_BOOTSTRAP_FORBIDDEN", null, false, true);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var label = NormalizeDisplayName(displayName) ?? NormalizeDisplayName(email);
            await using var command = new NpgsqlCommand("""
                SELECT admin_user_id, role::text, mfa_state::text, display_label, is_active,
                    bootstrap_source, created_at, updated_at, last_seen_at, bootstrap_created
                FROM private.admin_bootstrap_first_user(
                    $1,
                    'owner'::private.admin_role,
                    'MFA_NOT_VERIFIED'::private.admin_mfa_state,
                    $2,
                    'existing_supabase_user')
                """, connection);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)label ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new(TrustedServiceStatus.Unavailable, "ADMIN_BOOTSTRAP_FAILED", null, false, true);
            }

            var profile = ReadProfile(reader);
            var created = reader.GetBoolean(9);
            return new(TrustedServiceStatus.Succeeded,
                created ? "ADMIN_BOOTSTRAPPED" : "ADMIN_BOOTSTRAP_REPLAY",
                profile,
                created,
                true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState is "23505" or "P0001")
        {
            return new(TrustedServiceStatus.Rejected, "ADMIN_BOOTSTRAP_REJECTED", null, false, true);
        }
        catch (NpgsqlException)
        {
            return new(TrustedServiceStatus.Unavailable, "ADMIN_PORTAL_UNAVAILABLE", null, false, true);
        }
    }

    public async Task<AdminBootstrapResult> GetBootstrapStateAsync(
        AuthenticatedGatewayUser user,
        string? email,
        CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty)
            return new(TrustedServiceStatus.Rejected, "ADMIN_BOOTSTRAP_FORBIDDEN", null, false,
                options.HasBootstrapConfiguration);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("""
                SELECT admin_user_id, role::text, mfa_state::text, display_label, is_active,
                    bootstrap_source, created_at, updated_at, last_seen_at
                FROM private.admin_users
                WHERE admin_user_id = $1
                """, connection);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new(TrustedServiceStatus.Succeeded, "ADMIN_ACCESS_GRANTED", ReadProfile(reader), false,
                    options.HasBootstrapConfiguration);
            }

            await reader.DisposeAsync().ConfigureAwait(false);
            await using var countCommand = new NpgsqlCommand("SELECT count(*) FROM private.admin_users", connection);
            var count = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            return new(TrustedServiceStatus.Succeeded,
                count == 0
                    ? "ADMIN_BOOTSTRAP_READY"
                    : "ADMIN_ACCESS_REQUIRED",
                null,
                false,
                options.HasBootstrapConfiguration
                    && count == 0
                    && MatchesBootstrapIdentity(user.UserId, email));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            return new(TrustedServiceStatus.Unavailable, "ADMIN_PORTAL_UNAVAILABLE", null, false,
                options.HasBootstrapConfiguration);
        }
    }

    public async Task<AdminDashboardSnapshot> GetDashboardAsync(
        AuthenticatedGatewayUser user,
        int deviceLimit,
        int giftCodeLimit,
        int auditLimit,
        int activityLimit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureAdminConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return EmptyDashboard();
        }

        var summary = await ReadSummaryAsync(connection, cancellationToken).ConfigureAwait(false);
        var devices = await ReadDevicesAsync(connection, null, null, deviceLimit, 0, cancellationToken)
            .ConfigureAwait(false);
        var giftCodes = await ReadGiftCodesAsync(connection, giftCodeLimit, 0, cancellationToken).ConfigureAwait(false);
        var audit = await ReadAuditAsync(connection, auditLimit, 0, cancellationToken).ConfigureAwait(false);
        var activities = await ReadDeviceActivityAsync(connection, activityLimit, null, cancellationToken)
            .ConfigureAwait(false);
        return new(summary, devices.Items, giftCodes.Items, audit.Items, activities);
    }

    public async Task<AdminDevicePageResult> SearchDevicesAsync(
        AuthenticatedGatewayUser user,
        string? query,
        string? status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureAdminConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null) return new(TrustedServiceStatus.Rejected, "ADMIN_ACCESS_REQUIRED", [], 0);
        return await ReadDevicesAsync(connection, query, status, limit, offset, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdminDeviceDetail?> GetDeviceAsync(
        AuthenticatedGatewayUser user,
        Guid deviceProfileId,
        int activityLimit,
        int redemptionLimit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureAdminConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null || deviceProfileId == Guid.Empty) return null;

        var device = await ReadDeviceDetailAsync(connection, deviceProfileId, activityLimit, redemptionLimit,
            cancellationToken).ConfigureAwait(false);
        return device;
    }

    public async Task<AdminMutationResult> SetDeviceStatusAsync(
        AuthenticatedGatewayUser user,
        Guid deviceProfileId,
        string status,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureMutationConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null || deviceProfileId == Guid.Empty || !DeviceStatuses.Contains(status))
            return MutationRejected("ADMIN_MUTATION_REJECTED");

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (await HasCorrelationAsync(connection, transaction, correlationId, cancellationToken).ConfigureAwait(false))
            {
                var replay = await ReadDeviceDetailAsync(connection, deviceProfileId, 8, 8, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(TrustedServiceStatus.Succeeded, "ADMIN_MUTATION_REPLAY", replay, null);
            }

            var eventType = status switch
            {
                "blocked" => "DEVICE_BLOCKED",
                "active" => "DEVICE_UNBLOCKED",
                _ => "DEVICE_REVOKED",
            };

            if (status == "blocked")
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE private.device_profiles
                    SET status = 'blocked', updated_at = clock_timestamp()
                    WHERE device_profile_id = $1 AND status <> 'revoked'
                    """, cancellationToken, deviceProfileId).ConfigureAwait(false);
            }
            else if (status == "active")
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE private.device_profiles
                    SET status = 'active', updated_at = clock_timestamp()
                    WHERE device_profile_id = $1 AND status = 'blocked'
                    """, cancellationToken, deviceProfileId).ConfigureAwait(false);
            }
            else
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE private.device_profiles
                    SET status = 'revoked', updated_at = clock_timestamp()
                    WHERE device_profile_id = $1 AND status <> 'revoked'
                    """, cancellationToken, deviceProfileId).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, """
                    UPDATE private.subscriptions
                    SET status = 'revoked', updated_at = clock_timestamp()
                    WHERE device_profile_id = $1 AND status <> 'revoked'
                    """, cancellationToken, deviceProfileId).ConfigureAwait(false);
            }

            await AuditAsync(connection, transaction, user.UserId, eventType,
                "DEVICE_PROFILE", deviceProfileId, correlationId,
                new Dictionary<string, object?>
                {
                    ["reason"] = NormalizeReason(reason),
                    ["status"] = status,
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadDeviceDetailAsync(connection, deviceProfileId, 8, 8, cancellationToken)
                .ConfigureAwait(false);
            return new(TrustedServiceStatus.Succeeded, "ADMIN_DEVICE_STATUS_UPDATED", snapshot, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
        catch (NpgsqlException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
    }

    public async Task<AdminMutationResult> ExtendSubscriptionAsync(
        AuthenticatedGatewayUser user,
        Guid deviceProfileId,
        int extensionDays,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureMutationConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null || deviceProfileId == Guid.Empty || extensionDays is < 1 or > 3650)
            return MutationRejected("ADMIN_MUTATION_REJECTED");

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (await HasCorrelationAsync(connection, transaction, correlationId, cancellationToken).ConfigureAwait(false))
            {
                var replay = await ReadDeviceDetailAsync(connection, deviceProfileId, 8, 8, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(TrustedServiceStatus.Succeeded, "ADMIN_MUTATION_REPLAY", replay, null);
            }

            await using var command = new NpgsqlCommand("""
                WITH profile AS (
                    SELECT device_profile_id
                    FROM private.device_profiles
                    WHERE device_profile_id = $1
                    FOR UPDATE
                ), current_subscription AS (
                    SELECT subscription_id, starts_at, expires_at, status
                    FROM private.subscriptions
                    WHERE device_profile_id = $1
                    FOR UPDATE
                ), upserted AS (
                    INSERT INTO private.subscriptions(device_profile_id, status, starts_at, expires_at)
                    SELECT $1,
                        'active',
                        clock_timestamp(),
                        clock_timestamp() + make_interval(days => $2)
                    WHERE NOT EXISTS (SELECT 1 FROM current_subscription)
                    ON CONFLICT (device_profile_id) DO UPDATE
                        SET status = CASE WHEN private.subscriptions.status = 'revoked'
                                THEN private.subscriptions.status ELSE 'active' END,
                            starts_at = LEAST(private.subscriptions.starts_at, clock_timestamp()),
                            expires_at = GREATEST(private.subscriptions.expires_at, clock_timestamp())
                                + make_interval(days => $2),
                            updated_at = clock_timestamp()
                    RETURNING device_profile_id
                )
                SELECT 1
                """, connection, transaction);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, deviceProfileId);
            command.Parameters.AddWithValue(NpgsqlDbType.Integer, extensionDays);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await AuditAsync(connection, transaction, user.UserId, "SUBSCRIPTION_EXTENDED",
                "DEVICE_PROFILE", deviceProfileId, correlationId,
                new Dictionary<string, object?>
                {
                    ["extensionDays"] = extensionDays,
                    ["reason"] = NormalizeReason(reason),
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadDeviceDetailAsync(connection, deviceProfileId, 8, 8, cancellationToken)
                .ConfigureAwait(false);
            return new(TrustedServiceStatus.Succeeded, "ADMIN_SUBSCRIPTION_EXTENDED", snapshot, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
        catch (NpgsqlException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
    }

    public async Task<AdminMutationResult> GrantCreditsAsync(
        AuthenticatedGatewayUser user,
        Guid targetUserId,
        long amount,
        string authorityReference,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureMutationConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null || targetUserId == Guid.Empty || amount <= 0
            || !IsValidAuthorityReference(authorityReference))
            return MutationRejected("ADMIN_MUTATION_REJECTED");

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (await HasCorrelationAsync(connection, transaction, correlationId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(TrustedServiceStatus.Succeeded, "ADMIN_MUTATION_REPLAY", null, null);
            }

            var requestHash = HashCanonical("admin-grant", user.UserId, targetUserId, amount, authorityReference,
                correlationId, reason);
            await using var command = new NpgsqlCommand("""
                SELECT status_code, available_credits, reserved_credits, reservation_id, transaction_id
                FROM private.credit_grant($1, $2, $3, $4, $5)
                """, connection, transaction);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, targetUserId);
            command.Parameters.AddWithValue(NpgsqlDbType.Bigint, amount);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, authorityReference);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, correlationId.ToString("D"));
            command.Parameters.AddWithValue(NpgsqlDbType.Text, requestHash);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return MutationUnavailable();
            }

            var statusCode = reader.GetString(0);
            var availableCredits = reader.GetInt64(1);
            var reservedCredits = reader.GetInt64(2);
            await reader.DisposeAsync().ConfigureAwait(false);
            await AuditAsync(connection, transaction, user.UserId, "CREDITS_GRANTED", "CREDIT_WALLET",
                targetUserId, correlationId,
                new Dictionary<string, object?>
                {
                    ["amount"] = amount,
                    ["authorityReference"] = authorityReference,
                    ["reason"] = NormalizeReason(reason),
                    ["availableCredits"] = availableCredits,
                    ["reservedCredits"] = reservedCredits,
                    ["statusCode"] = statusCode,
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(TrustedServiceStatus.Succeeded, "ADMIN_CREDITS_GRANTED", null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
        catch (NpgsqlException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
    }

    public async Task<AdminGiftCodePageResult> ListGiftCodesAsync(
        AuthenticatedGatewayUser user,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureAdminConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null) return new(TrustedServiceStatus.Rejected, "ADMIN_ACCESS_REQUIRED", [], 0);
        return await ReadGiftCodesAsync(connection, limit, offset, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdminMutationResult> CreateGiftCodeAsync(
        AuthenticatedGatewayUser user,
        string kind,
        int? durationDays,
        long? creditAmount,
        int maximumRedemptions,
        DateTimeOffset? expiresAt,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureMutationConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null || maximumRedemptions is < 1 or > 1_000_000)
            return MutationRejected("ADMIN_MUTATION_REJECTED");

        var normalizedKind = kind.Trim().ToLowerInvariant();
        var isDuration = normalizedKind == "duration";
        var isCredits = normalizedKind == "credits";
        if (!isDuration && !isCredits) return MutationRejected("ADMIN_MUTATION_REJECTED");
        if (isDuration && (durationDays is null or < 1 or > 3_650 || creditAmount is not null))
            return MutationRejected("ADMIN_MUTATION_REJECTED");
        if (isCredits && (creditAmount is null or <= 0 or > 1_000_000_000L || durationDays is not null))
            return MutationRejected("ADMIN_MUTATION_REJECTED");
        if (expiresAt is not null && expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            return MutationRejected("ADMIN_MUTATION_REJECTED");

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (await HasCorrelationAsync(connection, transaction, correlationId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(TrustedServiceStatus.Succeeded, "ADMIN_MUTATION_REPLAY", null, null);
            }

            var code = GenerateGiftCode();
            var codeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
            await using var command = new NpgsqlCommand("""
                INSERT INTO private.gift_codes(
                    code_prefix, code_sha256, kind, duration_days, credit_amount, maximum_redemptions,
                    redemption_count, expires_at, disabled_at)
                VALUES ($1, $2, $3, $4, $5, $6, 0, $7, NULL)
                RETURNING gift_code_id, code_prefix, kind::text, duration_days, credit_amount,
                    maximum_redemptions, redemption_count, expires_at, disabled_at, created_at
                """, connection, transaction);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, code[..Math.Min(12, code.Length)]);
            command.Parameters.AddWithValue(NpgsqlDbType.Bytea, codeBytes);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, isDuration ? "duration" : "credits");
            command.Parameters.AddWithValue(NpgsqlDbType.Integer, (object?)durationDays ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Bigint, (object?)creditAmount ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Integer, maximumRedemptions);
            command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)expiresAt ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return MutationUnavailable();
            }

            var summary = ReadGiftCode(reader);
            await reader.DisposeAsync().ConfigureAwait(false);
            await AuditAsync(connection, transaction, user.UserId, "GIFT_CODE_CREATED", "GIFT_CODE",
                summary.GiftCodeId, correlationId,
                new Dictionary<string, object?>
                {
                    ["reason"] = NormalizeReason(reason),
                    ["kind"] = summary.Kind,
                    ["durationDays"] = summary.DurationDays,
                    ["creditAmount"] = summary.CreditAmount,
                    ["maximumRedemptions"] = summary.MaximumRedemptions,
                    ["codePrefix"] = summary.CodePrefix,
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(TrustedServiceStatus.Succeeded, "ADMIN_GIFT_CODE_CREATED", null, summary, code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
        catch (NpgsqlException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
    }

    public async Task<AdminMutationResult> RevokeGiftCodeAsync(
        AuthenticatedGatewayUser user,
        Guid giftCodeId,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureMutationConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null || giftCodeId == Guid.Empty) return MutationRejected("ADMIN_MUTATION_REJECTED");

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (await HasCorrelationAsync(connection, transaction, correlationId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(TrustedServiceStatus.Succeeded, "ADMIN_MUTATION_REPLAY", null, null);
            }

            await ExecuteAsync(connection, transaction, """
                UPDATE private.gift_codes
                SET disabled_at = clock_timestamp()
                WHERE gift_code_id = $1 AND disabled_at IS NULL
                """, cancellationToken, giftCodeId).ConfigureAwait(false);
            await AuditAsync(connection, transaction, user.UserId, "GIFT_CODE_REVOKED", "GIFT_CODE",
                giftCodeId, correlationId,
                new Dictionary<string, object?>
                {
                    ["reason"] = NormalizeReason(reason),
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(TrustedServiceStatus.Succeeded, "ADMIN_GIFT_CODE_REVOKED", null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
        catch (NpgsqlException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MutationUnavailable();
        }
    }

    public async Task<ImmutableArray<AdminGiftCodeRedemptionSummary>> ListGiftCodeRedemptionsAsync(
        AuthenticatedGatewayUser user,
        Guid giftCodeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureAdminConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null || giftCodeId == Guid.Empty) return [];

        await using var command = new NpgsqlCommand("""
            SELECT r.redemption_id, r.gift_code_id, r.device_profile_id, dp.public_device_code,
                r.redeemed_at, r.duration_days, r.credit_amount
            FROM private.gift_code_redemptions r
            JOIN private.device_profiles dp ON dp.device_profile_id = r.device_profile_id
            WHERE r.gift_code_id = $1
            ORDER BY r.redeemed_at DESC, r.redemption_id DESC
            LIMIT $2
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, giftCodeId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, ClampLimit(limit, 50));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = ImmutableArray.CreateBuilder<AdminGiftCodeRedemptionSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }

        return items.ToImmutable();
    }

    public async Task<AdminAuditPageResult> ListAuditAsync(
        AuthenticatedGatewayUser user,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await EnsureAdminConnectionAsync(user, cancellationToken).ConfigureAwait(false);
        if (connection is null) return new(TrustedServiceStatus.Rejected, "ADMIN_ACCESS_REQUIRED", []);
        return await ReadAuditAsync(connection, limit, offset, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection?> EnsureAdminConnectionAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken)
    {
        if (user.UserId == Guid.Empty) return null;
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var authorized = await IsAdminAsync(connection, user.UserId, cancellationToken).ConfigureAwait(false);
        if (!authorized)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        await MarkSeenAsync(connection, user.UserId, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<NpgsqlConnection?> EnsureMutationConnectionAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken)
    {
        if (user.UserId == Guid.Empty) return null;
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT role::text
            FROM private.admin_users
            WHERE admin_user_id = $1 AND is_active
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
        var role = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (role is not ("owner" or "operator"))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        await MarkSeenAsync(connection, user.UserId, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<bool> IsAdminAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT is_active
            FROM private.admin_users
            WHERE admin_user_id = $1
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, userId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool active && active;
    }

    private static async Task MarkSeenAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE private.admin_users
            SET last_seen_at = clock_timestamp(), updated_at = clock_timestamp()
            WHERE admin_user_id = $1
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, userId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdminDevicePageResult> ReadDevicesAsync(
        NpgsqlConnection connection,
        string? query,
        string? status,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = NormalizeStatus(status);
        var where = new StringBuilder("WHERE true");
        var parameters = new List<NpgsqlParameter>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Append(" AND (dp.public_device_code ILIKE $1 OR dp.auth_user_id::text ILIKE $1)");
            parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"%{query.Trim()}%" });
        }
        if (normalizedStatus is not null)
        {
            var index = parameters.Count + 1;
            where.Append($" AND dp.status = ${index}");
            parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = normalizedStatus });
        }

        var limitIndex = parameters.Count + 1;
        var offsetIndex = parameters.Count + 2;
        var sql = $"""
            SELECT dp.device_profile_id, dp.auth_user_id, dp.public_device_code, dp.status::text,
                COALESCE(s.status::text, 'none'), s.starts_at, s.expires_at,
                COALESCE(cw.available_credits, 0), COALESCE(cw.reserved_credits, 0),
                dp.created_at, dp.last_seen_at
            FROM private.device_profiles dp
            LEFT JOIN private.subscriptions s ON s.device_profile_id = dp.device_profile_id
            LEFT JOIN private.credit_wallets cw ON cw.user_id = dp.auth_user_id
            {where}
            ORDER BY dp.updated_at DESC, dp.device_profile_id DESC
            LIMIT ${limitIndex} OFFSET ${offsetIndex}
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, ClampLimit(limit, options.DefaultPageSize));
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, Math.Max(0, offset));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = ImmutableArray.CreateBuilder<AdminDeviceSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadDeviceSummary(reader));
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        var total = await CountDevicesAsync(connection, query, normalizedStatus, cancellationToken).ConfigureAwait(false);
        return new(TrustedServiceStatus.Succeeded, "ADMIN_DEVICES_READ", items.ToImmutable(), total);
    }

    private async Task<long> CountDevicesAsync(
        NpgsqlConnection connection,
        string? query,
        string? status,
        CancellationToken cancellationToken)
    {
        var where = new StringBuilder("WHERE true");
        var parameters = new List<NpgsqlParameter>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Append(" AND (public_device_code ILIKE $1 OR auth_user_id::text ILIKE $1)");
            parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"%{query.Trim()}%" });
        }
        if (status is not null)
        {
            var index = parameters.Count + 1;
            where.Append($" AND status = ${index}");
            parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = status });
        }

        var sql = $"""
            SELECT count(*)
            FROM private.device_profiles
            {where}
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<AdminDeviceDetail?> ReadDeviceDetailAsync(
        NpgsqlConnection connection,
        Guid deviceProfileId,
        int activityLimit,
        int redemptionLimit,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT dp.device_profile_id, dp.auth_user_id, dp.public_device_code, dp.status::text,
                COALESCE(s.status::text, 'none'), s.starts_at, s.expires_at,
                COALESCE(cw.available_credits, 0), COALESCE(cw.reserved_credits, 0),
                dp.created_at, dp.last_seen_at
            FROM private.device_profiles dp
            LEFT JOIN private.subscriptions s ON s.device_profile_id = dp.device_profile_id
            LEFT JOIN private.credit_wallets cw ON cw.user_id = dp.auth_user_id
            WHERE dp.device_profile_id = $1
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, deviceProfileId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        var device = ReadDeviceSummary(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        var activity = await ReadDeviceActivityAsync(connection, activityLimit, deviceProfileId, cancellationToken)
            .ConfigureAwait(false);
        var redemptions = await ReadRedemptionsByDeviceAsync(connection, deviceProfileId, redemptionLimit,
            cancellationToken).ConfigureAwait(false);
        return new(device, activity, redemptions);
    }

    private async Task<ImmutableArray<AdminDeviceEventSummary>> ReadDeviceActivityAsync(
        NpgsqlConnection connection,
        int limit,
        Guid? deviceProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT e.event_id, e.event_type, dp.public_device_code, e.correlation_id, e.event_at, e.metadata::text
            FROM private.device_commercial_events e
            JOIN private.device_profiles dp ON dp.device_profile_id = e.device_profile_id
            """;
        if (deviceProfileId is not null)
        {
            sql += " WHERE e.device_profile_id = $1";
        }
        sql += " ORDER BY e.event_at DESC, e.event_id DESC LIMIT $2";

        await using var command = new NpgsqlCommand(sql, connection);
        if (deviceProfileId is not null) command.Parameters.AddWithValue(NpgsqlDbType.Uuid, deviceProfileId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, ClampLimit(limit, options.DefaultPageSize));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = ImmutableArray.CreateBuilder<AdminDeviceEventSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5)));
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        return items.ToImmutable();
    }

    private async Task<ImmutableArray<AdminGiftCodeRedemptionSummary>> ReadRedemptionsByDeviceAsync(
        NpgsqlConnection connection,
        Guid deviceProfileId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.redemption_id, r.gift_code_id, r.device_profile_id, dp.public_device_code,
                r.redeemed_at, r.duration_days, r.credit_amount
            FROM private.gift_code_redemptions r
            JOIN private.device_profiles dp ON dp.device_profile_id = r.device_profile_id
            WHERE r.device_profile_id = $1
            ORDER BY r.redeemed_at DESC, r.redemption_id DESC
            LIMIT $2
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, deviceProfileId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, ClampLimit(limit, options.DefaultPageSize));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = ImmutableArray.CreateBuilder<AdminGiftCodeRedemptionSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        return items.ToImmutable();
    }

    private async Task<AdminGiftCodePageResult> ReadGiftCodesAsync(
        NpgsqlConnection connection,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT gift_code_id, code_prefix, kind::text, duration_days, credit_amount,
                maximum_redemptions, redemption_count, expires_at, disabled_at, created_at
            FROM private.gift_codes
            ORDER BY created_at DESC, gift_code_id DESC
            LIMIT $1 OFFSET $2
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, ClampLimit(limit, options.DefaultPageSize));
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, Math.Max(0, offset));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = ImmutableArray.CreateBuilder<AdminGiftCodeSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadGiftCode(reader));
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        var countCommand = new NpgsqlCommand("SELECT count(*) FROM private.gift_codes", connection);
        var total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new(TrustedServiceStatus.Succeeded, "ADMIN_GIFT_CODES_READ", items.ToImmutable(), total);
    }

    private async Task<AdminAuditPageResult> ReadAuditAsync(
        NpgsqlConnection connection,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT event_id, actor_admin_user_id, event_type, target_kind, target_id, correlation_id,
                details::text, event_at
            FROM private.admin_audit_events
            ORDER BY event_at DESC, event_id DESC
            LIMIT $1 OFFSET $2
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, ClampLimit(limit, options.DefaultPageSize));
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, Math.Max(0, offset));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = ImmutableArray.CreateBuilder<AdminAuditSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetGuid(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        return new(TrustedServiceStatus.Succeeded, "ADMIN_AUDIT_READ", items.ToImmutable());
    }

    private async Task<AdminSummary> ReadSummaryAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM private.device_profiles) AS total_devices,
                (SELECT count(*) FROM private.device_profiles WHERE status = 'active') AS active_devices,
                (SELECT count(*) FROM private.device_profiles WHERE status = 'blocked') AS blocked_devices,
                (SELECT count(*) FROM private.device_profiles WHERE status = 'revoked') AS revoked_devices,
                (SELECT count(*)
                    FROM private.device_profiles dp
                    JOIN private.subscriptions s ON s.device_profile_id = dp.device_profile_id
                    WHERE s.expires_at <= clock_timestamp() AND s.status <> 'revoked') AS expired_subscriptions,
                (SELECT COALESCE(sum(available_credits), 0) FROM private.credit_wallets) AS available_credits,
                (SELECT COALESCE(sum(reserved_credits), 0) FROM private.credit_wallets) AS reserved_credits,
                (SELECT count(*) FROM private.gift_codes
                    WHERE disabled_at IS NULL AND (expires_at IS NULL OR expires_at > clock_timestamp())) AS active_gift_codes,
                (SELECT COALESCE(sum(redemption_count), 0) FROM private.gift_codes) AS gift_code_redemptions,
                (SELECT count(*) FROM private.admin_audit_events) AS admin_actions,
                (SELECT count(*) FROM private.device_commercial_events) AS device_events,
                clock_timestamp() AS observed_at
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow);
        }

        return new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetFieldValue<DateTimeOffset>(11));
    }

    private static async Task<bool> HasCorrelationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1
            FROM private.admin_audit_events
            WHERE correlation_id = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, correlationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task AuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorAdminUserId,
        string eventType,
        string targetKind,
        Guid? targetId,
        Guid correlationId,
        IReadOnlyDictionary<string, object?> details,
        CancellationToken cancellationToken)
    {
        if (!AdminEventTypes.Contains(eventType))
            throw new ArgumentOutOfRangeException(nameof(eventType));

        await using var command = new NpgsqlCommand("""
            SELECT event_id, event_at
            FROM private.admin_audit_record($1, $2, $3, $4, $5, $6)
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, actorAdminUserId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, eventType);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, targetKind);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)targetId ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, correlationId);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.SerializeToElement(details));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string HashCanonical(params object?[] parts)
    {
        var canonical = string.Join('|', parts.Select(part => part switch
        {
            null => string.Empty,
            DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D"),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => part.ToString() ?? string.Empty,
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string NormalizeReason(string? reason)
    {
        var value = reason?.Trim();
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value[..Math.Min(256, value.Length)];
    }

    private static AdminSummary EmptySummary() =>
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow);

    private static AdminDashboardSnapshot EmptyDashboard() =>
        new(EmptySummary(), [], [], [], []);

    private static int ClampLimit(int limit, int fallback) =>
        limit is > 0 and <= 250 ? limit : fallback;

    private static string? NormalizeStatus(string? status)
    {
        var value = status?.Trim().ToLowerInvariant();
        return value is "active" or "blocked" or "revoked" ? value : null;
    }

    private static string? NormalizeDisplayName(string? value)
    {
        var display = value?.Trim();
        return string.IsNullOrWhiteSpace(display) || display.Length > 128 || display.Any(char.IsControl)
            ? null
            : display;
    }

    private bool MatchesBootstrapIdentity(Guid userId, string? email)
    {
        if (options.BootstrapUserId is not null && options.BootstrapUserId == userId)
            return true;

        return !string.IsNullOrWhiteSpace(options.BootstrapEmail)
            && !string.IsNullOrWhiteSpace(email)
            && string.Equals(options.BootstrapEmail, email.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidAuthorityReference(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '-' or '_' or '.' or ':');

    private static string GenerateGiftCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        var builder = new StringBuilder("GFT-", 23);
        for (var index = 0; index < buffer.Length; index++)
        {
            if (index is > 0 && index % 4 == 0) builder.Append('-');
            builder.Append(alphabet[buffer[index] % alphabet.Length]);
        }

        return builder.ToString();
    }

    private static AdminAccessProfile ReadProfile(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));

    private static AdminDeviceSummary ReadDeviceSummary(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));

    private static AdminGiftCodeSummary ReadGiftCode(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9));

    private static AdminMutationResult MutationRejected(string diagnosticCode) =>
        new(TrustedServiceStatus.Rejected, diagnosticCode, null, null);

    private static AdminMutationResult MutationUnavailable() =>
        new(TrustedServiceStatus.Unavailable, "ADMIN_PORTAL_UNAVAILABLE", null, null);

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        for (var index = 0; index < parameters.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index]);
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AdminDashboardSnapshot> ReadDashboardSnapshotAsync(
        NpgsqlConnection connection,
        int deviceLimit,
        int giftCodeLimit,
        int auditLimit,
        int activityLimit,
        CancellationToken cancellationToken)
    {
        var summary = EmptySummary();
        var devices = ImmutableArray<AdminDeviceSummary>.Empty;
        var giftCodes = ImmutableArray<AdminGiftCodeSummary>.Empty;
        var audit = ImmutableArray<AdminAuditSummary>.Empty;
        var activities = ImmutableArray<AdminDeviceEventSummary>.Empty;
        return new(summary, devices, giftCodes, audit, activities);
    }
}
