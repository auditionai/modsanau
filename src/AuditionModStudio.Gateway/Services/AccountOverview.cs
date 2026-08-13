using System.Collections.Immutable;
using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace AuditionModStudio.Gateway.Services;

public sealed record TrustedAccountTransaction(
    Guid TransactionId,
    string Kind,
    long Amount,
    long AvailableDelta,
    long ReservedDelta,
    long AvailableAfter,
    long ReservedAfter,
    DateTimeOffset CreatedAt);

public sealed record TrustedAccountSnapshot(
    long AvailableCredits,
    long ReservedCredits,
    long CreditsGranted,
    long CreditsUsed,
    long TransactionCount,
    ImmutableArray<TrustedAccountTransaction> Transactions,
    DateTimeOffset ObservedAt);

public sealed record TrustedAccountResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    TrustedAccountSnapshot? Snapshot);

public interface ITrustedAccountQueryService
{
    Task<TrustedAccountResult> GetAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableTrustedAccountQueryService : ITrustedAccountQueryService
{
    public Task<TrustedAccountResult> GetAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default) => Task.FromResult(new TrustedAccountResult(
            TrustedServiceStatus.Unavailable, "ACCOUNT_SERVICE_UNAVAILABLE", null));
}

public sealed class PostgresAccountQueryService(NpgsqlDataSource dataSource) : ITrustedAccountQueryService
{
    public const int MaximumHistoryEntries = 50;
    private static readonly HashSet<string> AllowedKinds =
        ["grant", "reserve", "capture", "release", "refund"];

    public async Task<TrustedAccountResult> GetAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty) return Rejected();
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,
                cancellationToken).ConfigureAwait(false);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var (available, reserved) = await ReadWalletAsync(connection, transaction, user.UserId,
                cancellationToken).ConfigureAwait(false);
            var (granted, used, count) = await ReadUsageAsync(connection, transaction, user.UserId,
                cancellationToken).ConfigureAwait(false);
            var history = await ReadHistoryAsync(connection, transaction, user.UserId, cancellationToken)
                .ConfigureAwait(false);
            var observedAt = await ReadObservedAtAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (available < 0 || reserved < 0 || granted < 0 || used < 0 || count < 0
                || history.Any(item => !AllowedKinds.Contains(item.Kind) || item.Amount <= 0
                    || item.AvailableAfter < 0 || item.ReservedAfter < 0))
                return Unavailable();
            return new(TrustedServiceStatus.Succeeded, "ACCOUNT_READ",
                new(available, reserved, granted, used, count, history, observedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is NpgsqlException or OverflowException or InvalidCastException)
        { return Unavailable(); }
    }

    private static async Task<(long Available, long Reserved)> ReadWalletAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT available_credits, reserved_credits
            FROM private.credit_wallets
            WHERE user_id = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, userId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
    }

    private static async Task<(long Granted, long Used, long Count)> ReadUsageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
                COALESCE(SUM(amount) FILTER (WHERE entry_type = 'grant'), 0)::bigint,
                COALESCE(SUM(CASE WHEN entry_type = 'capture' THEN amount
                                  WHEN entry_type = 'refund' THEN -amount ELSE 0 END), 0)::bigint,
                COUNT(*)::bigint
            FROM private.credit_ledger
            WHERE user_id = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, userId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)) : (0, 0, 0);
    }

    private static async Task<ImmutableArray<TrustedAccountTransaction>> ReadHistoryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT transaction_id, entry_type, amount, available_delta, reserved_delta,
                   available_after, reserved_after, created_at
            FROM private.credit_ledger
            WHERE user_id = $1
            ORDER BY created_at DESC, transaction_id DESC
            LIMIT $2
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, userId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, MaximumHistoryEntries);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var output = ImmutableArray.CreateBuilder<TrustedAccountTransaction>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            output.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetFieldValue<DateTimeOffset>(7)));
        return output.ToImmutable();
    }

    private static async Task<DateTimeOffset> ReadObservedAtAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT transaction_timestamp()", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? reader.GetFieldValue<DateTimeOffset>(0)
            : throw new InvalidCastException("Database snapshot time is missing.");
    }

    private static TrustedAccountResult Rejected() =>
        new(TrustedServiceStatus.Rejected, "ACCOUNT_USER_INVALID", null);
    private static TrustedAccountResult Unavailable() =>
        new(TrustedServiceStatus.Unavailable, "ACCOUNT_SERVICE_UNAVAILABLE", null);
}
