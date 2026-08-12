using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace AuditionModStudio.Gateway.Services;

public enum AiJobStatus
{
    Pending,
    Queued,
    Processing,
    Completed,
    Failed,
    Cancelled
}

public readonly record struct AiJobIdempotencyKey
{
    public AiJobIdempotencyKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 80 || !value.All(character => char.IsAsciiLetterOrDigit(character)
                                               || character is '-' or '_' or '.' or ':'))
        {
            throw new ArgumentException("AI job idempotency key is invalid.", nameof(value));
        }
        Value = value;
    }

    public string Value { get; }
}

public sealed record AiJobInputMetadata(
    string InputReference,
    int PromptLength,
    int? TargetWidth,
    int? TargetHeight,
    int InputBytes)
{
    public bool IsValid =>
        IsOpaqueReference(InputReference)
        && PromptLength is >= 0 and <= 4_000
        && InputBytes is >= 0 and <= 4 * 1_024 * 1_024
        && ((TargetWidth is null && TargetHeight is null)
            || (TargetWidth is > 0 and <= 16_384 && TargetHeight is > 0 and <= 16_384));

    private static bool IsOpaqueReference(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && value[0] != '/'
        && value.Split('/').All(segment => segment.Length > 0
            && segment is not "." and not ".."
            && segment.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'));
}

public sealed record AiJobSnapshot(
    Guid JobId,
    TrustedAiOperation Operation,
    AiJobStatus Status,
    AiJobInputMetadata InputMetadata,
    long ReservedCredits,
    long? FinalCredits,
    string PricingVersion,
    string? OutputReference,
    string? ProviderRequestId,
    string? ErrorCode,
    int AttemptCount,
    bool CancelRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum AiJobOperationStatus
{
    Succeeded,
    PriceChanged,
    Rejected,
    NotFound,
    Unavailable
}

public sealed record AiJobOperationResult(
    AiJobOperationStatus Status,
    string DiagnosticCode,
    AiJobSnapshot? Job,
    AiPricingQuote? CurrentQuote = null);

public sealed record AiJobListResult(
    AiJobOperationStatus Status,
    string DiagnosticCode,
    IReadOnlyList<AiJobSnapshot> Jobs);

public interface IAiJobService
{
    Task<AiJobOperationResult> EnqueueAsync(
        AuthenticatedGatewayUser user,
        TrustedAiOperation operation,
        AiJobInputMetadata metadata,
        AiJobIdempotencyKey idempotencyKey,
        AiPricingVersion? expectedPricingVersion = null,
        CancellationToken cancellationToken = default);

    Task<AiJobOperationResult> GetAsync(
        AuthenticatedGatewayUser user,
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<AiJobListResult> ListAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default);

    Task<AiJobOperationResult> CancelAsync(
        AuthenticatedGatewayUser user,
        Guid jobId,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableAiJobService : IAiJobService
{
    private static readonly AiJobOperationResult Unavailable = new(
        AiJobOperationStatus.Unavailable, "AI_JOB_SERVICE_UNAVAILABLE", null);

    public Task<AiJobOperationResult> EnqueueAsync(
        AuthenticatedGatewayUser user, TrustedAiOperation operation, AiJobInputMetadata metadata,
        AiJobIdempotencyKey idempotencyKey, AiPricingVersion? expectedPricingVersion = null,
        CancellationToken cancellationToken = default) => Task.FromResult(Unavailable);

    public Task<AiJobOperationResult> GetAsync(
        AuthenticatedGatewayUser user, Guid jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable);

    public Task<AiJobListResult> ListAsync(
        AuthenticatedGatewayUser user, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiJobListResult(AiJobOperationStatus.Unavailable,
            "AI_JOB_SERVICE_UNAVAILABLE", []));

    public Task<AiJobOperationResult> CancelAsync(
        AuthenticatedGatewayUser user, Guid jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable);
}

public sealed class PostgresAiJobService(
    NpgsqlDataSource dataSource,
    IAiPricingService pricingService) : IAiJobService
{
    private const string Columns = "job_id, operation, status, input_metadata, reserved_credits, " +
        "final_credits, pricing_version, output_reference, provider_request_id, error_code, " +
        "attempt_count, cancel_requested, created_at, updated_at";
    private static readonly HashSet<string> RejectionCodes = new(StringComparer.Ordinal)
    {
        "AI_JOB_REQUEST_INVALID",
        "AI_JOB_IDEMPOTENCY_CONFLICT",
        "AI_JOB_RESERVATION_FAILED",
        "CREDIT_INSUFFICIENT",
        "CREDIT_IDEMPOTENCY_CONFLICT",
    };

    public async Task<AiJobOperationResult> EnqueueAsync(
        AuthenticatedGatewayUser user,
        TrustedAiOperation operation,
        AiJobInputMetadata metadata,
        AiJobIdempotencyKey idempotencyKey,
        AiPricingVersion? expectedPricingVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty || !Enum.IsDefined(operation) || !metadata.IsValid)
        {
            return Rejected("AI_JOB_REQUEST_INVALID");
        }

        var pricing = await pricingService.QuoteAsync(user, operation, expectedPricingVersion, cancellationToken)
            .ConfigureAwait(false);
        if (pricing.Status == AiPricingStatus.PriceChanged)
        {
            return new(AiJobOperationStatus.PriceChanged, pricing.DiagnosticCode, null, pricing.Quote);
        }
        if (pricing.Status != AiPricingStatus.Succeeded || pricing.Quote is null)
        {
            return pricing.Status == AiPricingStatus.Rejected
                ? Rejected(pricing.DiagnosticCode)
                : Unavailable();
        }
        if (pricing.Quote.Operation != operation
            || pricing.Quote.CreditCost.Value is <= 0 or > AiCreditPrice.MaximumCredits
            || string.IsNullOrEmpty(pricing.Quote.PricingVersion.Value)
            || pricing.Quote.EffectiveAt.Offset != TimeSpan.Zero)
        {
            return Unavailable();
        }

        var metadataJson = JsonSerializer.Serialize(metadata);
        var requestHash = RequestHash(user.UserId, operation, metadataJson,
            pricing.Quote.PricingVersion.Value, pricing.Quote.CreditCost.Value);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"SELECT {Columns} FROM private.ai_job_enqueue($1, $2, $3, $4, $5, $6, $7)",
                connection, transaction);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, operation.ToString());
            command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, metadataJson);
            command.Parameters.AddWithValue(NpgsqlDbType.Bigint, pricing.Quote.CreditCost.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, pricing.Quote.PricingVersion.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, idempotencyKey.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Char, requestHash);
            var job = await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
            if (job is null) return Unavailable();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(AiJobOperationStatus.Succeeded, "AI_JOB_QUEUED", job);
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
        catch (InvalidDataException)
        {
            return Unavailable();
        }
    }

    public async Task<AiJobOperationResult> GetAsync(
        AuthenticatedGatewayUser user, Guid jobId, CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty || jobId == Guid.Empty) return Rejected("AI_JOB_REQUEST_INVALID");
        try
        {
            await using var command = dataSource.CreateCommand(
                $"SELECT {Columns} FROM private.ai_jobs WHERE user_id = $1 AND job_id = $2");
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, jobId);
            var job = await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
            return job is null
                ? new(AiJobOperationStatus.NotFound, "AI_JOB_NOT_FOUND", null)
                : new(AiJobOperationStatus.Succeeded, "AI_JOB_READ", job);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NpgsqlException) { return Unavailable(); }
        catch (InvalidDataException) { return Unavailable(); }
    }

    public async Task<AiJobListResult> ListAsync(
        AuthenticatedGatewayUser user, CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty)
        {
            return new(AiJobOperationStatus.Rejected, "AI_JOB_REQUEST_INVALID", []);
        }
        try
        {
            await using var command = dataSource.CreateCommand(
                $"SELECT {Columns} FROM private.ai_jobs WHERE user_id = $1 ORDER BY created_at DESC LIMIT 100");
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var jobs = new List<AiJobSnapshot>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) jobs.Add(ReadJob(reader));
            return new(AiJobOperationStatus.Succeeded, "AI_JOB_LISTED", jobs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NpgsqlException)
        {
            return new(AiJobOperationStatus.Unavailable, "AI_JOB_SERVICE_UNAVAILABLE", []);
        }
        catch (InvalidDataException)
        {
            return new(AiJobOperationStatus.Unavailable, "AI_JOB_SERVICE_UNAVAILABLE", []);
        }
    }

    public async Task<AiJobOperationResult> CancelAsync(
        AuthenticatedGatewayUser user, Guid jobId, CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty || jobId == Guid.Empty) return Rejected("AI_JOB_REQUEST_INVALID");
        try
        {
            await using var command = dataSource.CreateCommand(
                $"SELECT {Columns} FROM private.ai_job_cancel($1, $2)");
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, jobId);
            var job = await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
            return job is null ? Unavailable() : new(AiJobOperationStatus.Succeeded, "AI_JOB_CANCELLED", job);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PostgresException exception) when (exception.SqlState == "P0001"
                                                   && exception.ConstraintName == "AI_JOB_NOT_FOUND")
        {
            return new(AiJobOperationStatus.NotFound, "AI_JOB_NOT_FOUND", null);
        }
        catch (NpgsqlException) { return Unavailable(); }
        catch (InvalidDataException) { return Unavailable(); }
    }

    private static async Task<AiJobSnapshot?> ReadSingleAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    private static AiJobSnapshot ReadJob(NpgsqlDataReader reader)
    {
        if (!Enum.TryParse<TrustedAiOperation>(reader.GetString(1), out var operation)
            || !Enum.TryParse<AiJobStatus>(reader.GetString(2), out var status))
        {
            throw new InvalidDataException("AI job contains an unknown state.");
        }
        var metadata = JsonSerializer.Deserialize<AiJobInputMetadata>(reader.GetString(3));
        if (metadata is null || !metadata.IsValid) throw new InvalidDataException("AI job metadata is invalid.");
        return new(reader.GetGuid(0), operation, status, metadata, reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt32(10), reader.GetBoolean(11),
            reader.GetFieldValue<DateTimeOffset>(12), reader.GetFieldValue<DateTimeOffset>(13));
    }

    private static string RequestHash(Guid userId, TrustedAiOperation operation, string metadata,
        string pricingVersion, long credits)
    {
        var canonical = string.Join('|', userId.ToString("D"), operation.ToString(), metadata,
            pricingVersion, credits.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static AiJobOperationResult Rejected(string code) =>
        new(AiJobOperationStatus.Rejected, code, null);
    private static AiJobOperationResult Unavailable() =>
        new(AiJobOperationStatus.Unavailable, "AI_JOB_SERVICE_UNAVAILABLE", null);
}
