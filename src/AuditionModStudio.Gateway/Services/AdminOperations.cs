using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace AuditionModStudio.Gateway.Services;

public sealed record AdminDailyMetric(DateOnly Day, long Users, long Transactions, long RevenueMinor);
public sealed record AdminAnalyticsSnapshot(long TotalUsers, long NewUsers30Days, long ActiveDevices,
    long BlockedDevices, long RevokedDevices, long ActiveSubscriptions, long ExpiringSubscriptions7Days,
    long TotalTransactions, long RevenueMinor, long CreditsSold, long ActiveGiftCodes, long GiftCodeRedemptions,
    string Currency, DateTimeOffset ObservedAt, ImmutableArray<AdminDailyMetric> Daily);
public sealed record AdminManagedUserSummary(Guid UserId, string Email, string? DisplayName, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt, string? PublicDeviceCode, string DeviceStatus,
    long AvailableCredits, long ReservedCredits, string SubscriptionStatus, DateTimeOffset? SubscriptionExpiresAt);
public sealed record AdminManagedUserPage(ImmutableArray<AdminManagedUserSummary> Items, long TotalCount);
public sealed record AdminPaymentSummary(Guid PaymentEventId, string Provider, string ProviderEventId,
    string ProviderPaymentId, Guid UserId, string Email, string ProductId, long AmountMinor, string Currency,
    long Credits, Guid GrantTransactionId, DateTimeOffset VerifiedAt);
public sealed record AdminPaymentPage(ImmutableArray<AdminPaymentSummary> Items, long TotalCount);
public sealed record AdminPackageSummary(Guid PackageId, string ProductId, string DisplayName, string? Description,
    long AmountMinor, string Currency, long Credits, bool IsActive, int SortOrder, DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record AdminAccountSummary(Guid AdminUserId, string Email, string Role, string MfaState,
    string? DisplayLabel, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);
public sealed record AdminUserUpdate(string? DisplayName, string? ContactEmail, string Status,
    string? InternalNote, string Reason, Guid CorrelationId);
public sealed record AdminPackageUpdate(Guid? PackageId, string ProductId, string DisplayName, string? Description,
    long AmountMinor, string Currency, long Credits, bool IsActive, int SortOrder, string Reason, Guid CorrelationId);
public sealed record AdminAccountUpdate(string Role, bool IsActive, string MfaState, string? DisplayLabel,
    string Reason, Guid CorrelationId);

public interface IAdminOperationsService
{
    Task<AdminAnalyticsSnapshot?> GetAnalyticsAsync(AuthenticatedGatewayUser user, int days, CancellationToken ct);
    Task<AdminManagedUserPage?> SearchUsersAsync(AuthenticatedGatewayUser user, string? query, string? status,
        int limit, int offset, CancellationToken ct);
    Task<AdminManagedUserSummary?> GetUserAsync(AuthenticatedGatewayUser user, Guid userId, CancellationToken ct);
    Task<AdminManagedUserSummary?> UpdateUserAsync(AuthenticatedGatewayUser actor, Guid userId,
        AdminUserUpdate update, CancellationToken ct);
    Task<AdminPaymentPage?> SearchPaymentsAsync(AuthenticatedGatewayUser user, string? query, string? provider,
        int limit, int offset, CancellationToken ct);
    Task<ImmutableArray<AdminPackageSummary>?> ListPackagesAsync(AuthenticatedGatewayUser user, CancellationToken ct);
    Task<AdminPackageSummary?> SavePackageAsync(AuthenticatedGatewayUser actor, AdminPackageUpdate update,
        CancellationToken ct);
    Task<bool> ArchivePackageAsync(AuthenticatedGatewayUser actor, Guid packageId, string reason,
        Guid correlationId, CancellationToken ct);
    Task<ImmutableArray<AdminAccountSummary>?> ListAdminsAsync(AuthenticatedGatewayUser user, CancellationToken ct);
    Task<bool> UpdateAdminAsync(AuthenticatedGatewayUser actor, Guid adminUserId, AdminAccountUpdate update,
        CancellationToken ct);
}

public sealed class UnavailableAdminOperationsService : IAdminOperationsService
{
    public Task<AdminAnalyticsSnapshot?> GetAnalyticsAsync(AuthenticatedGatewayUser user, int days, CancellationToken ct) => Task.FromResult<AdminAnalyticsSnapshot?>(null);
    public Task<AdminManagedUserPage?> SearchUsersAsync(AuthenticatedGatewayUser user, string? query, string? status, int limit, int offset, CancellationToken ct) => Task.FromResult<AdminManagedUserPage?>(null);
    public Task<AdminManagedUserSummary?> GetUserAsync(AuthenticatedGatewayUser user, Guid userId, CancellationToken ct) => Task.FromResult<AdminManagedUserSummary?>(null);
    public Task<AdminManagedUserSummary?> UpdateUserAsync(AuthenticatedGatewayUser actor, Guid userId, AdminUserUpdate update, CancellationToken ct) => Task.FromResult<AdminManagedUserSummary?>(null);
    public Task<AdminPaymentPage?> SearchPaymentsAsync(AuthenticatedGatewayUser user, string? query, string? provider, int limit, int offset, CancellationToken ct) => Task.FromResult<AdminPaymentPage?>(null);
    public Task<ImmutableArray<AdminPackageSummary>?> ListPackagesAsync(AuthenticatedGatewayUser user, CancellationToken ct) => Task.FromResult<ImmutableArray<AdminPackageSummary>?>(null);
    public Task<AdminPackageSummary?> SavePackageAsync(AuthenticatedGatewayUser actor, AdminPackageUpdate update, CancellationToken ct) => Task.FromResult<AdminPackageSummary?>(null);
    public Task<bool> ArchivePackageAsync(AuthenticatedGatewayUser actor, Guid packageId, string reason, Guid correlationId, CancellationToken ct) => Task.FromResult(false);
    public Task<ImmutableArray<AdminAccountSummary>?> ListAdminsAsync(AuthenticatedGatewayUser user, CancellationToken ct) => Task.FromResult<ImmutableArray<AdminAccountSummary>?>(null);
    public Task<bool> UpdateAdminAsync(AuthenticatedGatewayUser actor, Guid adminUserId, AdminAccountUpdate update, CancellationToken ct) => Task.FromResult(false);
}

public sealed class PostgresAdminOperationsService(NpgsqlDataSource dataSource) : IAdminOperationsService
{
    private static readonly HashSet<string> UserStatuses = new(StringComparer.Ordinal) { "active", "suspended", "deactivated" };
    private static readonly HashSet<string> AdminRoles = new(StringComparer.Ordinal) { "owner", "operator", "auditor" };
    private static readonly HashSet<string> MfaStates = new(StringComparer.Ordinal) { "MFA_NOT_VERIFIED", "MFA_PENDING", "MFA_VERIFIED" };

    public async Task<AdminAnalyticsSnapshot?> GetAnalyticsAsync(AuthenticatedGatewayUser user, int days, CancellationToken ct)
    {
        await using var connection = await OpenAsync(user, false, false, ct).ConfigureAwait(false);
        if (connection is null) return null;
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM auth.users),
              (SELECT count(*) FROM auth.users WHERE created_at >= clock_timestamp() - interval '30 days'),
              (SELECT count(*) FROM private.device_profiles WHERE status='active'),
              (SELECT count(*) FROM private.device_profiles WHERE status='blocked'),
              (SELECT count(*) FROM private.device_profiles WHERE status='revoked'),
              (SELECT count(*) FROM private.subscriptions WHERE status='active' AND expires_at > clock_timestamp()),
              (SELECT count(*) FROM private.subscriptions WHERE status='active' AND expires_at > clock_timestamp()
                  AND expires_at <= clock_timestamp() + interval '7 days'),
              (SELECT count(*) FROM private.payment_events),
              (SELECT COALESCE(sum(amount_minor),0) FROM private.payment_events),
              (SELECT COALESCE(sum(credits),0) FROM private.payment_events),
              (SELECT count(*) FROM private.gift_codes WHERE disabled_at IS NULL
                  AND (expires_at IS NULL OR expires_at > clock_timestamp()) AND redemption_count < maximum_redemptions),
              (SELECT count(*) FROM private.gift_code_redemptions),
              COALESCE((SELECT currency::text FROM private.payment_events GROUP BY currency ORDER BY count(*) DESC LIMIT 1),'vnd'),
              clock_timestamp()
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var values = Enumerable.Range(0, 12).Select(reader.GetInt64).ToArray();
        var currency = reader.GetString(12);
        var observedAt = reader.GetFieldValue<DateTimeOffset>(13);
        await reader.DisposeAsync().ConfigureAwait(false);

        days = Math.Clamp(days, 7, 90);
        await using var trend = new NpgsqlCommand("""
            WITH dates AS (
                SELECT generate_series((current_date - ($1::integer - 1)), current_date, interval '1 day')::date AS day
            ), users_by_day AS (
                SELECT created_at::date AS day, count(*) AS total FROM auth.users
                WHERE created_at >= current_date - ($1::integer - 1) GROUP BY created_at::date
            ), payments_by_day AS (
                SELECT verified_at::date AS day, count(*) AS total, COALESCE(sum(amount_minor),0) AS revenue
                FROM private.payment_events WHERE verified_at >= current_date - ($1::integer - 1)
                GROUP BY verified_at::date
            )
            SELECT d.day, COALESCE(u.total,0), COALESCE(p.total,0), COALESCE(p.revenue,0)
            FROM dates d LEFT JOIN users_by_day u USING(day) LEFT JOIN payments_by_day p USING(day) ORDER BY d.day
            """, connection);
        trend.Parameters.AddWithValue(NpgsqlDbType.Integer, days);
        var daily = ImmutableArray.CreateBuilder<AdminDailyMetric>();
        await using var trendReader = await trend.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await trendReader.ReadAsync(ct).ConfigureAwait(false))
            daily.Add(new(trendReader.GetFieldValue<DateOnly>(0), trendReader.GetInt64(1), trendReader.GetInt64(2), trendReader.GetInt64(3)));
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11], currency, observedAt, daily.ToImmutable());
    }

    public async Task<AdminManagedUserPage?> SearchUsersAsync(AuthenticatedGatewayUser user, string? query, string? status, int limit, int offset, CancellationToken ct)
    {
        await using var connection = await OpenAsync(user, false, false, ct).ConfigureAwait(false);
        if (connection is null) return null;
        var normalized = NormalizeQuery(query);
        var normalizedStatus = UserStatuses.Contains(status ?? string.Empty) ? status : null;
        const string where = """
            WHERE ($1 IS NULL OR u.id::text ILIKE '%' || $1 || '%' OR COALESCE(u.email,'') ILIKE '%' || $1 || '%'
                OR COALESCE(d.public_device_code,'') ILIKE '%' || upper($1) || '%')
              AND ($2 IS NULL OR COALESCE(p.status::text,'active')=$2)
            """;
        await using var count = new NpgsqlCommand("SELECT count(*) FROM auth.users u LEFT JOIN private.managed_user_profiles p ON p.user_id=u.id LEFT JOIN private.device_profiles d ON d.auth_user_id=u.id " + where, connection);
        AddNullableText(count, normalized); AddNullableText(count, normalizedStatus);
        var total = (long)(await count.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        await using var command = new NpgsqlCommand("""
            SELECT u.id, COALESCE(u.email,''), COALESCE(p.display_name, u.raw_user_meta_data->>'display_name', u.raw_user_meta_data->>'full_name'),
                COALESCE(p.status::text,'active'), u.created_at, u.last_sign_in_at, d.public_device_code,
                COALESCE(d.status::text,'none'), COALESCE(w.available_credits,0), COALESCE(w.reserved_credits,0),
                CASE WHEN s.subscription_id IS NULL THEN 'none' WHEN s.status='active' AND s.expires_at<=clock_timestamp() THEN 'expired' ELSE s.status::text END,
                s.expires_at
            FROM auth.users u LEFT JOIN private.managed_user_profiles p ON p.user_id=u.id
            LEFT JOIN private.device_profiles d ON d.auth_user_id=u.id
            LEFT JOIN private.credit_wallets w ON w.user_id=u.id
            LEFT JOIN private.subscriptions s ON s.device_profile_id=d.device_profile_id
            """ + where + " ORDER BY u.created_at DESC, u.id DESC LIMIT $3 OFFSET $4", connection);
        AddNullableText(command, normalized); AddNullableText(command, normalizedStatus);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, Math.Clamp(limit, 1, 100));
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, Math.Max(0, offset));
        var items = ImmutableArray.CreateBuilder<AdminManagedUserSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(ReadUser(reader));
        return new(items.ToImmutable(), total);
    }

    public async Task<AdminManagedUserSummary?> GetUserAsync(AuthenticatedGatewayUser user, Guid userId, CancellationToken ct)
    {
        var page = await SearchUsersAsync(user, userId.ToString("D"), null, 2, 0, ct).ConfigureAwait(false);
        return page?.Items.FirstOrDefault(item => item.UserId == userId);
    }

    public async Task<AdminManagedUserSummary?> UpdateUserAsync(AuthenticatedGatewayUser actor, Guid userId, AdminUserUpdate update, CancellationToken ct)
    {
        if (userId == Guid.Empty || !UserStatuses.Contains(update.Status) || update.CorrelationId == Guid.Empty
            || !ValidText(update.DisplayName, 128) || !ValidEmail(update.ContactEmail) || !ValidText(update.InternalNote, 1000)
            || !ValidReason(update.Reason)) return null;
        await using var connection = await OpenAsync(actor, true, false, ct).ConfigureAwait(false);
        if (connection is null) return null;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO private.managed_user_profiles(user_id,display_name,contact_email,status,internal_note)
            SELECT $1,$2,$3,$4::private.managed_user_status,$5 WHERE EXISTS(SELECT 1 FROM auth.users WHERE id=$1)
            ON CONFLICT(user_id) DO UPDATE SET display_name=excluded.display_name, contact_email=excluded.contact_email,
                status=excluded.status, internal_note=excluded.internal_note, updated_at=clock_timestamp()
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, userId);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)Clean(update.DisplayName) ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)Clean(update.ContactEmail) ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, update.Status);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)Clean(update.InternalNote) ?? DBNull.Value);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0) { await transaction.RollbackAsync(ct); return null; }
        }
        if (update.Status is "suspended" or "deactivated")
        {
            var deviceStatus = update.Status == "deactivated" ? "revoked" : "blocked";
            var subscriptionStatus = update.Status == "deactivated" ? "revoked" : "suspended";
            await ExecuteAsync(connection, transaction, "UPDATE private.device_profiles SET status=$2::private.device_commercial_status, updated_at=clock_timestamp() WHERE auth_user_id=$1 AND status<>'revoked'", ct, userId, deviceStatus);
            await ExecuteAsync(connection, transaction, "UPDATE private.subscriptions s SET status=$2::private.subscription_status, updated_at=clock_timestamp() FROM private.device_profiles d WHERE s.device_profile_id=d.device_profile_id AND d.auth_user_id=$1 AND s.status<>'revoked'", ct, userId, subscriptionStatus);
        }
        await AuditAsync(connection, transaction, actor.UserId, "USER_UPDATED", "AUTH_USER", userId,
            update.CorrelationId, new { update.Status, reason = Clean(update.Reason) }, ct);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return await GetUserAsync(actor, userId, ct).ConfigureAwait(false);
    }

    public async Task<AdminPaymentPage?> SearchPaymentsAsync(AuthenticatedGatewayUser user, string? query, string? provider, int limit, int offset, CancellationToken ct)
    {
        await using var connection = await OpenAsync(user, false, false, ct).ConfigureAwait(false);
        if (connection is null) return null;
        var q = NormalizeQuery(query); var p = NormalizeQuery(provider);
        const string where = """
            WHERE ($1 IS NULL OR e.payment_event_id::text ILIKE '%'||$1||'%' OR e.provider_event_id ILIKE '%'||$1||'%'
                OR e.provider_payment_id ILIKE '%'||$1||'%' OR e.grant_transaction_id::text ILIKE '%'||$1||'%'
                OR e.user_id::text ILIKE '%'||$1||'%' OR COALESCE(u.email,'') ILIKE '%'||$1||'%')
              AND ($2 IS NULL OR e.provider=$2)
            """;
        await using var count = new NpgsqlCommand("SELECT count(*) FROM private.payment_events e LEFT JOIN auth.users u ON u.id=e.user_id " + where, connection);
        AddNullableText(count, q); AddNullableText(count, p);
        var total = (long)(await count.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        await using var command = new NpgsqlCommand("""
            SELECT e.payment_event_id,e.provider,e.provider_event_id,e.provider_payment_id,e.user_id,COALESCE(u.email,''),
                e.product_id,e.amount_minor,e.currency::text,e.credits,e.grant_transaction_id,e.verified_at
            FROM private.payment_events e LEFT JOIN auth.users u ON u.id=e.user_id
            """ + where + " ORDER BY e.verified_at DESC,e.payment_event_id DESC LIMIT $3 OFFSET $4", connection);
        AddNullableText(command, q); AddNullableText(command, p);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, Math.Clamp(limit, 1, 100));
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, Math.Max(0, offset));
        var items = ImmutableArray.CreateBuilder<AdminPaymentSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetGuid(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7), reader.GetString(8), reader.GetInt64(9), reader.GetGuid(10), reader.GetFieldValue<DateTimeOffset>(11)));
        return new(items.ToImmutable(), total);
    }

    public async Task<ImmutableArray<AdminPackageSummary>?> ListPackagesAsync(AuthenticatedGatewayUser user, CancellationToken ct)
    {
        await using var connection = await OpenAsync(user, false, false, ct).ConfigureAwait(false);
        if (connection is null) return null;
        await using var command = new NpgsqlCommand("SELECT package_id,product_id,display_name,description,amount_minor,currency::text,credits,is_active,sort_order,archived_at,created_at,updated_at FROM private.commercial_packages ORDER BY archived_at NULLS FIRST,sort_order,created_at", connection);
        var items = ImmutableArray.CreateBuilder<AdminPackageSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(ReadPackage(reader));
        return items.ToImmutable();
    }

    public async Task<AdminPackageSummary?> SavePackageAsync(AuthenticatedGatewayUser actor, AdminPackageUpdate update, CancellationToken ct)
    {
        if (!ValidProduct(update.ProductId) || !ValidRequired(update.DisplayName, 128) || !ValidText(update.Description, 500)
            || update.AmountMinor is < 1 or > 1000000000000 || update.Credits is < 1 or > 1000000000
            || update.Currency.Length != 3 || !update.Currency.All(char.IsAsciiLetter) || update.SortOrder is < -100000 or > 100000
            || update.CorrelationId == Guid.Empty || !ValidReason(update.Reason)) return null;
        await using var connection = await OpenAsync(actor, true, false, ct).ConfigureAwait(false);
        if (connection is null) return null;
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var packageId = update.PackageId is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        await using (var command = new NpgsqlCommand("""
            INSERT INTO private.commercial_packages(package_id,product_id,display_name,description,amount_minor,currency,credits,is_active,sort_order,archived_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,CASE WHEN $8 THEN NULL ELSE clock_timestamp() END)
            ON CONFLICT(package_id) DO UPDATE SET product_id=excluded.product_id,display_name=excluded.display_name,
                description=excluded.description,amount_minor=excluded.amount_minor,currency=excluded.currency,
                credits=excluded.credits,is_active=excluded.is_active,sort_order=excluded.sort_order,
                archived_at=CASE WHEN excluded.is_active THEN NULL ELSE COALESCE(private.commercial_packages.archived_at,clock_timestamp()) END,
                updated_at=clock_timestamp()
            RETURNING package_id,product_id,display_name,description,amount_minor,currency::text,credits,is_active,sort_order,archived_at,created_at,updated_at
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, packageId); command.Parameters.AddWithValue(update.ProductId.Trim());
            command.Parameters.AddWithValue(update.DisplayName.Trim()); command.Parameters.AddWithValue((object?)Clean(update.Description) ?? DBNull.Value);
            command.Parameters.AddWithValue(update.AmountMinor); command.Parameters.AddWithValue(update.Currency.ToLowerInvariant()); command.Parameters.AddWithValue(update.Credits);
            command.Parameters.AddWithValue(update.IsActive); command.Parameters.AddWithValue(update.SortOrder);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            var result = ReadPackage(reader); await reader.DisposeAsync().ConfigureAwait(false);
            await AuditAsync(connection, transaction, actor.UserId, "PACKAGE_SAVED", "COMMERCIAL_PACKAGE", packageId, update.CorrelationId, new { update.ProductId, reason = Clean(update.Reason) }, ct);
            await transaction.CommitAsync(ct).ConfigureAwait(false); return result;
        }
    }

    public async Task<bool> ArchivePackageAsync(AuthenticatedGatewayUser actor, Guid packageId, string reason, Guid correlationId, CancellationToken ct)
    {
        if (packageId == Guid.Empty || correlationId == Guid.Empty || !ValidReason(reason)) return false;
        await using var connection = await OpenAsync(actor, true, false, ct).ConfigureAwait(false); if (connection is null) return false;
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("UPDATE private.commercial_packages SET is_active=false,archived_at=COALESCE(archived_at,clock_timestamp()),updated_at=clock_timestamp() WHERE package_id=$1", connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, packageId); if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0) return false;
        await AuditAsync(connection, transaction, actor.UserId, "PACKAGE_ARCHIVED", "COMMERCIAL_PACKAGE", packageId, correlationId, new { reason = Clean(reason) }, ct);
        await transaction.CommitAsync(ct).ConfigureAwait(false); return true;
    }

    public async Task<ImmutableArray<AdminAccountSummary>?> ListAdminsAsync(AuthenticatedGatewayUser user, CancellationToken ct)
    {
        await using var connection = await OpenAsync(user, false, true, ct).ConfigureAwait(false); if (connection is null) return null;
        await using var command = new NpgsqlCommand("SELECT a.admin_user_id,COALESCE(u.email,''),a.role::text,a.mfa_state::text,a.display_label,a.is_active,a.created_at,a.last_seen_at FROM private.admin_users a LEFT JOIN auth.users u ON u.id=a.admin_user_id ORDER BY a.created_at", connection);
        var items = ImmutableArray.CreateBuilder<AdminAccountSummary>(); await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.GetFieldValue<DateTimeOffset>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        return items.ToImmutable();
    }

    public async Task<bool> UpdateAdminAsync(AuthenticatedGatewayUser actor, Guid adminUserId, AdminAccountUpdate update, CancellationToken ct)
    {
        if (adminUserId == Guid.Empty || !AdminRoles.Contains(update.Role) || !MfaStates.Contains(update.MfaState) || !ValidText(update.DisplayLabel, 128) || !ValidReason(update.Reason) || update.CorrelationId == Guid.Empty || actor.UserId == adminUserId && !update.IsActive) return false;
        await using var connection = await OpenAsync(actor, true, true, ct).ConfigureAwait(false); if (connection is null) return false;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        if (!update.IsActive || update.Role != "owner") { await using var owners = new NpgsqlCommand("SELECT count(*) FROM private.admin_users WHERE role='owner' AND is_active AND admin_user_id<>$1", connection, transaction); owners.Parameters.AddWithValue(adminUserId); if ((long)(await owners.ExecuteScalarAsync(ct) ?? 0L) == 0) return false; }
        await using var command = new NpgsqlCommand("UPDATE private.admin_users SET role=$2::private.admin_role,is_active=$3,mfa_state=$4::private.admin_mfa_state,display_label=$5,updated_at=clock_timestamp() WHERE admin_user_id=$1", connection, transaction);
        command.Parameters.AddWithValue(adminUserId); command.Parameters.AddWithValue(update.Role); command.Parameters.AddWithValue(update.IsActive); command.Parameters.AddWithValue(update.MfaState); command.Parameters.AddWithValue((object?)Clean(update.DisplayLabel) ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0) return false;
        await AuditAsync(connection, transaction, actor.UserId, "ADMIN_ACCOUNT_UPDATED", "ADMIN_USER", adminUserId, update.CorrelationId, new { update.Role, update.IsActive, update.MfaState, reason = Clean(update.Reason) }, ct);
        await transaction.CommitAsync(ct).ConfigureAwait(false); return true;
    }

    private async Task<NpgsqlConnection?> OpenAsync(AuthenticatedGatewayUser user, bool mutation, bool ownerOnly, CancellationToken ct)
    {
        if (user.UserId == Guid.Empty) return null; var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT role::text FROM private.admin_users WHERE admin_user_id=$1 AND is_active", connection); command.Parameters.AddWithValue(user.UserId);
        var role = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (role is null || ownerOnly && role != "owner" || mutation && role == "auditor") { await connection.DisposeAsync(); return null; }
        return connection;
    }

    private static AdminManagedUserSummary ReadUser(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetInt64(8), r.GetInt64(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11));
    private static AdminPackageSummary ReadPackage(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetInt64(6), r.GetBoolean(7), r.GetInt32(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), r.GetFieldValue<DateTimeOffset>(10), r.GetFieldValue<DateTimeOffset>(11));
    private static void AddNullableText(NpgsqlCommand c, string? value) => c.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)value ?? DBNull.Value);
    private static string? NormalizeQuery(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 128)];
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool ValidText(string? value, int max) => value is null || value.Length <= max && !value.Any(char.IsControl);
    private static bool ValidRequired(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static bool ValidEmail(string? value) => value is null || value.Length <= 320 && value.Contains('@') && !value.Any(char.IsControl);
    private static bool ValidReason(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 500 && !value.Any(char.IsControl);
    private static bool ValidProduct(string value) => ValidRequired(value, 64) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
    private static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object[] args) { await using var cmd = new NpgsqlCommand(sql, c, t); foreach (var arg in args) cmd.Parameters.AddWithValue(arg); await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
    private static async Task AuditAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid actor, string eventType, string targetKind, Guid target, Guid correlation, object details, CancellationToken ct) { await using var cmd = new NpgsqlCommand("SELECT event_id FROM private.admin_audit_record($1,$2,$3,$4,$5,$6)", c, t); cmd.Parameters.AddWithValue(actor); cmd.Parameters.AddWithValue(eventType); cmd.Parameters.AddWithValue(targetKind); cmd.Parameters.AddWithValue(target); cmd.Parameters.AddWithValue(correlation); cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(details) }); await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false); }
}
