using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace AuditionModStudio.Gateway.Services;

public enum CreditLedgerOperationStatus
{
    Succeeded,
    Rejected,
    Unavailable
}

public readonly record struct CreditIdempotencyKey
{
    public CreditIdempotencyKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || !value.All(character => char.IsAsciiLetterOrDigit(character)
                                               || character is '-' or '_' or '.' or ':'))
        {
            throw new ArgumentException("Idempotency key is invalid.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct PositiveCreditAmount
{
    public PositiveCreditAmount(long value)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public long Value { get; }
}

public sealed record CreditLedgerOperationResult(
    CreditLedgerOperationStatus Status,
    string DiagnosticCode,
    TrustedCreditSnapshot? Snapshot,
    Guid? ReservationId,
    Guid? TransactionId);

public interface ICreditLedgerService
{
    Task<TrustedCreditResult> GetAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default);

    Task<CreditLedgerOperationResult> GrantAsync(
        AuthenticatedGatewayUser user,
        PositiveCreditAmount amount,
        string authorityReference,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<CreditLedgerOperationResult> ReserveAsync(
        AuthenticatedGatewayUser user,
        PositiveCreditAmount amount,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<CreditLedgerOperationResult> CaptureAsync(
        AuthenticatedGatewayUser user,
        Guid reservationId,
        PositiveCreditAmount serverResolvedCost,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<CreditLedgerOperationResult> ReleaseAsync(
        AuthenticatedGatewayUser user,
        Guid reservationId,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<CreditLedgerOperationResult> RefundAsync(
        AuthenticatedGatewayUser user,
        Guid capturedTransactionId,
        PositiveCreditAmount serverAuthorizedAmount,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableCreditLedgerService : ICreditLedgerService, ITrustedCreditQueryService
{
    private static readonly CreditLedgerOperationResult Unavailable = new(
        CreditLedgerOperationStatus.Unavailable, "CREDIT_SERVICE_UNAVAILABLE", null, null, null);

    public Task<TrustedCreditResult> GetAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TrustedCreditResult(TrustedServiceStatus.Unavailable,
            "CREDIT_SERVICE_UNAVAILABLE", null));

    public Task<CreditLedgerOperationResult> GrantAsync(
        AuthenticatedGatewayUser user, PositiveCreditAmount amount, string authorityReference,
        CreditIdempotencyKey idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable);

    public Task<CreditLedgerOperationResult> ReserveAsync(
        AuthenticatedGatewayUser user, PositiveCreditAmount amount,
        CreditIdempotencyKey idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable);

    public Task<CreditLedgerOperationResult> CaptureAsync(
        AuthenticatedGatewayUser user, Guid reservationId, PositiveCreditAmount serverResolvedCost,
        CreditIdempotencyKey idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable);

    public Task<CreditLedgerOperationResult> ReleaseAsync(
        AuthenticatedGatewayUser user, Guid reservationId,
        CreditIdempotencyKey idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable);

    public Task<CreditLedgerOperationResult> RefundAsync(
        AuthenticatedGatewayUser user, Guid capturedTransactionId, PositiveCreditAmount serverAuthorizedAmount,
        CreditIdempotencyKey idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable);
}

public sealed class PostgresCreditLedgerService(NpgsqlDataSource dataSource)
    : ICreditLedgerService, ITrustedCreditQueryService
{
    private static readonly HashSet<string> RejectionCodes = new(StringComparer.Ordinal)
    {
        "CREDIT_AUTHORITY_REFERENCE_INVALID",
        "CREDIT_REQUEST_INVALID",
        "CREDIT_IDEMPOTENCY_CONFLICT",
        "CREDIT_INSUFFICIENT",
        "CREDIT_RESERVATION_INVALID",
        "CREDIT_RESERVATION_NOT_OPEN",
        "CREDIT_CAPTURE_COST_INVALID",
        "CREDIT_TRANSACTION_INVALID",
        "CREDIT_REFUND_EXCEEDS_CAPTURE",
        "CREDIT_OVERFLOW",
    };

    public async Task<TrustedCreditResult> GetAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty) return RejectedQuery();

        try
        {
            await using var command = dataSource.CreateCommand(
                "SELECT available_credits, reserved_credits " +
                "FROM private.credit_wallets WHERE user_id = $1");
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return SuccessQuery(new TrustedCreditSnapshot(0, 0));
            }

            var available = reader.GetInt64(0);
            var reserved = reader.GetInt64(1);
            return available >= 0 && reserved >= 0
                ? SuccessQuery(new TrustedCreditSnapshot(available, reserved))
                : UnavailableQuery();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            return UnavailableQuery();
        }
    }

    public Task<CreditLedgerOperationResult> GrantAsync(
        AuthenticatedGatewayUser user,
        PositiveCreditAmount amount,
        string authorityReference,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorityReferenceValid(authorityReference)) return Task.FromResult(Rejected("CREDIT_AUTHORITY_REFERENCE_INVALID"));
        return ExecuteMutationAsync("private.credit_grant", user, "grant", idempotencyKey,
            [amount.Value.ToString(CultureInfo.InvariantCulture), authorityReference],
            [new(NpgsqlDbType.Bigint, amount.Value), new(NpgsqlDbType.Text, authorityReference)], cancellationToken);
    }

    public Task<CreditLedgerOperationResult> ReserveAsync(
        AuthenticatedGatewayUser user,
        PositiveCreditAmount amount,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync("private.credit_reserve", user, "reserve", idempotencyKey,
            [amount.Value.ToString(CultureInfo.InvariantCulture)],
            [new(NpgsqlDbType.Bigint, amount.Value)], cancellationToken);

    public Task<CreditLedgerOperationResult> CaptureAsync(
        AuthenticatedGatewayUser user,
        Guid reservationId,
        PositiveCreditAmount serverResolvedCost,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (reservationId == Guid.Empty) return Task.FromResult(Rejected("CREDIT_RESERVATION_INVALID"));
        return ExecuteMutationAsync("private.credit_capture", user, "capture", idempotencyKey,
            [reservationId.ToString("D"), serverResolvedCost.Value.ToString(CultureInfo.InvariantCulture)],
            [new(NpgsqlDbType.Uuid, reservationId), new(NpgsqlDbType.Bigint, serverResolvedCost.Value)], cancellationToken);
    }

    public Task<CreditLedgerOperationResult> ReleaseAsync(
        AuthenticatedGatewayUser user,
        Guid reservationId,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (reservationId == Guid.Empty) return Task.FromResult(Rejected("CREDIT_RESERVATION_INVALID"));
        return ExecuteMutationAsync("private.credit_release", user, "release", idempotencyKey,
            [reservationId.ToString("D")], [new(NpgsqlDbType.Uuid, reservationId)], cancellationToken);
    }

    public Task<CreditLedgerOperationResult> RefundAsync(
        AuthenticatedGatewayUser user,
        Guid capturedTransactionId,
        PositiveCreditAmount serverAuthorizedAmount,
        CreditIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (capturedTransactionId == Guid.Empty) return Task.FromResult(Rejected("CREDIT_TRANSACTION_INVALID"));
        return ExecuteMutationAsync("private.credit_refund", user, "refund", idempotencyKey,
            [capturedTransactionId.ToString("D"), serverAuthorizedAmount.Value.ToString(CultureInfo.InvariantCulture)],
            [new(NpgsqlDbType.Uuid, capturedTransactionId),
                new(NpgsqlDbType.Bigint, serverAuthorizedAmount.Value)], cancellationToken);
    }

    private async Task<CreditLedgerOperationResult> ExecuteMutationAsync(
        string functionName,
        AuthenticatedGatewayUser user,
        string operation,
        CreditIdempotencyKey idempotencyKey,
        IReadOnlyList<string> canonicalParts,
        IReadOnlyList<ParameterValue> operationParameters,
        CancellationToken cancellationToken)
    {
        if (user.UserId == Guid.Empty) return Rejected("CREDIT_USER_INVALID");
        var requestHash = RequestHash(operation, user.UserId, canonicalParts);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken).ConfigureAwait(false);
            var placeholders = string.Join(", ", Enumerable.Range(1, operationParameters.Count + 3)
                .Select(index => $"${index}"));
            await using var command = new NpgsqlCommand(
                $"SELECT status_code, available_credits, reserved_credits, reservation_id, transaction_id " +
                $"FROM {functionName}({placeholders})", connection, transaction);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            foreach (var parameter in operationParameters)
            {
                command.Parameters.AddWithValue(parameter.Type, parameter.Value);
            }
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, idempotencyKey.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, requestHash);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return Unavailable();
            var result = ReadResult(reader);
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == "P0001"
                                                   && RejectionCodes.Contains(exception.ConstraintName ?? string.Empty))
        {
            return Rejected(exception.ConstraintName!);
        }
        catch (NpgsqlException)
        {
            return Unavailable();
        }
    }

    private static CreditLedgerOperationResult ReadResult(NpgsqlDataReader reader)
    {
        var code = reader.GetString(0);
        var available = reader.GetInt64(1);
        var reserved = reader.GetInt64(2);
        if (code is not ("CREDIT_APPLIED" or "CREDIT_IDEMPOTENT_REPLAY") || available < 0 || reserved < 0)
        {
            return Unavailable();
        }

        return new(CreditLedgerOperationStatus.Succeeded, code,
            new TrustedCreditSnapshot(available, reserved),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4));
    }

    private static string RequestHash(string operation, Guid userId, IReadOnlyList<string> parts)
    {
        var canonical = string.Join('|', new[] { operation, userId.ToString("D") }.Concat(parts));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsAuthorityReferenceValid(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '-' or '_' or '.' or ':');

    private static TrustedCreditResult SuccessQuery(TrustedCreditSnapshot snapshot) =>
        new(TrustedServiceStatus.Succeeded, "CREDIT_READ", snapshot);
    private static TrustedCreditResult RejectedQuery() =>
        new(TrustedServiceStatus.Rejected, "CREDIT_USER_INVALID", null);
    private static TrustedCreditResult UnavailableQuery() =>
        new(TrustedServiceStatus.Unavailable, "CREDIT_SERVICE_UNAVAILABLE", null);
    private static CreditLedgerOperationResult Rejected(string code) =>
        new(CreditLedgerOperationStatus.Rejected, code, null, null, null);
    private static CreditLedgerOperationResult Unavailable() =>
        new(CreditLedgerOperationStatus.Unavailable, "CREDIT_SERVICE_UNAVAILABLE", null, null, null);

    private sealed record ParameterValue(NpgsqlDbType Type, object Value);
}
