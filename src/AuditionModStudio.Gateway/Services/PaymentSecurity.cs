using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace AuditionModStudio.Gateway.Services;

public enum PaymentProviderEventStatus { Verified, Pending, Ignored, Invalid, Retryable }
public enum PaymentApplyStatus { Applied, Replay, Rejected, Unavailable }
public enum PaymentProcessingStatus
{
    Applied,
    Replay,
    Pending,
    Ignored,
    Invalid,
    Conflict,
    Retryable,
    Failed,
}

public sealed record VerifiedPaymentEvent(
    string Provider,
    string EventId,
    string PaymentId,
    Guid UserId,
    string ProductId,
    long AmountMinor,
    string Currency,
    long Credits,
    string PayloadSha256,
    string GrantRequestSha256);

public sealed record PaymentProviderEventResult(
    PaymentProviderEventStatus Status,
    string DiagnosticCode,
    VerifiedPaymentEvent? Payment = null);

public sealed record PaymentApplyResult(PaymentApplyStatus Status, string DiagnosticCode);
public sealed record PaymentProcessingResult(PaymentProcessingStatus Status, string DiagnosticCode);

public interface IPaymentProvider
{
    string ProviderId { get; }
    PaymentProviderEventResult VerifyWebhook(ReadOnlyMemory<byte> rawBody, string? signatureHeader);
}

public interface IPaymentProviderResolver
{
    bool TryResolve(string providerId, out IPaymentProvider provider);
}

public interface IPaymentApplicationService
{
    Task<PaymentProcessingResult> ProcessWebhookAsync(
        string providerId,
        ReadOnlyMemory<byte> rawBody,
        string? signatureHeader,
        CancellationToken cancellationToken = default);
}

public interface IPaymentFulfillmentService
{
    Task<PaymentApplyResult> ApplyAsync(
        VerifiedPaymentEvent payment,
        CancellationToken cancellationToken = default);
}

public sealed record PaymentProduct(
    string ProductId,
    long AmountMinor,
    string Currency,
    long Credits)
{
    public bool IsValid => IsIdentifier(ProductId, 64)
                           && AmountMinor is > 0 and <= 1_000_000_000_000
                           && Currency.Length == 3 && Currency.All(char.IsAsciiLetterLower)
                           && Credits is > 0 and <= 1_000_000_000;

    internal static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
}

public sealed class PaymentProductCatalog
{
    private readonly IReadOnlyDictionary<string, PaymentProduct> _products;

    private PaymentProductCatalog(IReadOnlyDictionary<string, PaymentProduct> products) => _products = products;

    public bool TryGet(string productId, out PaymentProduct product) =>
        _products.TryGetValue(productId, out product!);

    public static PaymentProductCatalog FromConfiguration(IConfiguration configuration)
    {
        var products = new Dictionary<string, PaymentProduct>(StringComparer.Ordinal);
        foreach (var section in configuration.GetSection("Gateway:Payments:Products").GetChildren())
        {
            var product = new PaymentProduct(
                section["ProductId"] ?? string.Empty,
                ParseLong(section["AmountMinor"]),
                (section["Currency"] ?? string.Empty).Trim().ToLowerInvariant(),
                ParseLong(section["Credits"]));
            if (!product.IsValid || !products.TryAdd(product.ProductId, product))
            {
                return new PaymentProductCatalog(new Dictionary<string, PaymentProduct>());
            }
        }
        return new PaymentProductCatalog(products);
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}

public sealed class PaymentProviderResolver : IPaymentProviderResolver
{
    private readonly IReadOnlyDictionary<string, IPaymentProvider> _providers;

    public PaymentProviderResolver(IEnumerable<IPaymentProvider> providers)
    {
        var resolved = new Dictionary<string, IPaymentProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (!IsValidProviderId(provider.ProviderId)
                || !resolved.TryAdd(provider.ProviderId, provider))
            {
                resolved.Clear();
                break;
            }
        }
        _providers = resolved;
    }

    public bool TryResolve(string providerId, out IPaymentProvider provider) =>
        _providers.TryGetValue(providerId, out provider!);

    internal static bool IsValidProviderId(string? providerId) =>
        !string.IsNullOrWhiteSpace(providerId) && providerId.Length <= 32
        && providerId.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character));
}

public sealed class PaymentApplicationService(
    IPaymentProviderResolver providers,
    IPaymentFulfillmentService fulfillment) : IPaymentApplicationService
{
    public async Task<PaymentProcessingResult> ProcessWebhookAsync(
        string providerId,
        ReadOnlyMemory<byte> rawBody,
        string? signatureHeader,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PaymentProviderResolver.IsValidProviderId(providerId))
            return new(PaymentProcessingStatus.Invalid, "PAYMENT_PROVIDER_INVALID");
        if (!providers.TryResolve(providerId, out var provider))
            return new(PaymentProcessingStatus.Retryable, "PAYMENT_PROVIDER_UNAVAILABLE");

        var providerResult = provider.VerifyWebhook(rawBody, signatureHeader);
        if (!Enum.IsDefined(providerResult.Status) || !IsSafeCode(providerResult.DiagnosticCode))
            return Failed();
        if (providerResult.Status != PaymentProviderEventStatus.Verified)
        {
            if (providerResult.Payment is not null) return Failed();
            return providerResult.Status switch
            {
                PaymentProviderEventStatus.Pending =>
                    new(PaymentProcessingStatus.Pending, providerResult.DiagnosticCode),
                PaymentProviderEventStatus.Ignored =>
                    new(PaymentProcessingStatus.Ignored, providerResult.DiagnosticCode),
                PaymentProviderEventStatus.Invalid =>
                    new(PaymentProcessingStatus.Invalid, providerResult.DiagnosticCode),
                PaymentProviderEventStatus.Retryable =>
                    new(PaymentProcessingStatus.Retryable, providerResult.DiagnosticCode),
                _ => Failed(),
            };
        }

        var payment = providerResult.Payment;
        if (payment is null || payment.Provider != provider.ProviderId || payment.Provider != providerId)
            return Failed();
        var applied = await fulfillment.ApplyAsync(payment, cancellationToken).ConfigureAwait(false);
        if (!Enum.IsDefined(applied.Status) || !IsSafeCode(applied.DiagnosticCode)) return Failed();
        return applied.Status switch
        {
            PaymentApplyStatus.Applied => new(PaymentProcessingStatus.Applied, applied.DiagnosticCode),
            PaymentApplyStatus.Replay => new(PaymentProcessingStatus.Replay, applied.DiagnosticCode),
            PaymentApplyStatus.Rejected => new(PaymentProcessingStatus.Conflict, applied.DiagnosticCode),
            PaymentApplyStatus.Unavailable => new(PaymentProcessingStatus.Retryable, applied.DiagnosticCode),
            _ => Failed(),
        };
    }

    private static bool IsSafeCode(string code) => !string.IsNullOrWhiteSpace(code)
        && code.Length <= 128
        && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static PaymentProcessingResult Failed() =>
        new(PaymentProcessingStatus.Failed, "PAYMENT_PROVIDER_RESPONSE_INVALID");
}

public sealed class StripeWebhookOptions
{
    public string EndpointSecret { get; init; } = string.Empty;
    public bool LiveMode { get; init; }
    public TimeSpan SignatureTolerance { get; init; } = TimeSpan.FromMinutes(5);

    public bool IsValid => EndpointSecret.StartsWith("whsec_", StringComparison.Ordinal)
                           && EndpointSecret.Length is >= 32 and <= 256
                           && EndpointSecret.All(character => char.IsAsciiLetterOrDigit(character) || character is '_')
                           && SignatureTolerance > TimeSpan.Zero
                           && SignatureTolerance <= TimeSpan.FromMinutes(5);

    public static StripeWebhookOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Gateway:Payments:Stripe");
        return new()
        {
            EndpointSecret = section["WebhookSecret"] ?? string.Empty,
            LiveMode = bool.TryParse(section["LiveMode"], out var liveMode) && liveMode,
        };
    }

    public override string ToString() => "StripeWebhookOptions { [REDACTED] }";
}

public sealed class StripePaymentProvider(
    StripeWebhookOptions options,
    PaymentProductCatalog products,
    TimeProvider timeProvider) : IPaymentProvider
{
    private const int MaximumSignatureHeaderLength = 2_048;
    private const int MaximumJsonDepth = 16;

    public string ProviderId => "stripe";

    public PaymentProviderEventResult VerifyWebhook(ReadOnlyMemory<byte> rawBody, string? signatureHeader)
    {
        if (!options.IsValid) return Retryable();
        if (rawBody.IsEmpty || string.IsNullOrWhiteSpace(signatureHeader)
            || signatureHeader.Length > MaximumSignatureHeaderLength
            || !TryParseSignature(signatureHeader, out var timestamp, out var signatures))
        {
            return Invalid("PAYMENT_WEBHOOK_SIGNATURE_INVALID");
        }

        DateTimeOffset signedAt;
        try { signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
        catch (ArgumentOutOfRangeException) { return Invalid("PAYMENT_WEBHOOK_SIGNATURE_INVALID"); }
        if ((timeProvider.GetUtcNow() - signedAt).Duration() > options.SignatureTolerance)
            return Invalid("PAYMENT_WEBHOOK_TIMESTAMP_INVALID");

        var timestampBytes = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture));
        var signedPayload = new byte[timestampBytes.Length + 1 + rawBody.Length];
        timestampBytes.CopyTo(signedPayload, 0);
        signedPayload[timestampBytes.Length] = (byte)'.';
        rawBody.Span.CopyTo(signedPayload.AsSpan(timestampBytes.Length + 1));
        var key = Encoding.UTF8.GetBytes(options.EndpointSecret);
        byte[] expected;
        try { expected = HMACSHA256.HashData(key, signedPayload); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(signedPayload);
        }
        var signatureMatches = signatures.Any(signature => signature.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(signature, expected));
        if (!signatureMatches) return Invalid("PAYMENT_WEBHOOK_SIGNATURE_INVALID");

        try
        {
            using var document = JsonDocument.Parse(rawBody, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
            return ParseVerifiedEvent(document.RootElement, rawBody);
        }
        catch (JsonException)
        {
            return Invalid("PAYMENT_WEBHOOK_PAYLOAD_INVALID");
        }
    }

    private PaymentProviderEventResult ParseVerifiedEvent(JsonElement root, ReadOnlyMemory<byte> rawBody)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryString(root, "id", 128, out var eventId)
            || !TryString(root, "type", 80, out var eventType)
            || !root.TryGetProperty("livemode", out var liveModeElement)
            || liveModeElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || liveModeElement.GetBoolean() != options.LiveMode)
            return Invalid("PAYMENT_WEBHOOK_PAYLOAD_INVALID");

        if (eventType is "charge.refunded" or "refund.created" or "refund.updated")
            return new(PaymentProviderEventStatus.Ignored, "PAYMENT_REFUND_EVENT_IGNORED");
        if (eventType is "checkout.session.async_payment_failed" or "checkout.session.expired")
            return new(PaymentProviderEventStatus.Ignored, "PAYMENT_EVENT_FAILED");
        if (eventType is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded"))
            return new(PaymentProviderEventStatus.Ignored, "PAYMENT_EVENT_IGNORED");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("object", out var session) || session.ValueKind != JsonValueKind.Object
            || !TryString(session, "id", 120, out var paymentId)
            || !TryString(session, "payment_status", 32, out var paymentStatus)
            || !TryString(session, "mode", 32, out var mode)
            || !PaymentProduct.IsIdentifier(eventId, 128)
            || !PaymentProduct.IsIdentifier(paymentId, 120))
            return Invalid("PAYMENT_WEBHOOK_PAYLOAD_INVALID");
        if (mode != "payment")
            return new(PaymentProviderEventStatus.Ignored, "PAYMENT_EVENT_NOT_FULFILLABLE");
        if (paymentStatus != "paid")
            return new(PaymentProviderEventStatus.Pending, "PAYMENT_EVENT_PENDING");
        if (!TryString(session, "client_reference_id", 64, out var userText)
            || !Guid.TryParse(userText, out var userId) || userId == Guid.Empty
            || !session.TryGetProperty("amount_total", out var amountElement)
            || !amountElement.TryGetInt64(out var amountMinor) || amountMinor <= 0
            || !TryString(session, "currency", 3, out var currency)
            || !session.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object
            || !TryString(metadata, "credit_product_id", 64, out var productId)
            || !PaymentProduct.IsIdentifier(productId, 64)
            || !products.TryGet(productId, out var product)
            || product.AmountMinor != amountMinor || product.Currency != currency)
            return Invalid("PAYMENT_WEBHOOK_PAYLOAD_INVALID");

        var payloadHash = Convert.ToHexString(SHA256.HashData(rawBody.Span));
        var grantCanonical = string.Join('|', "stripe", paymentId, userId.ToString("D"),
            product.ProductId, amountMinor.ToString(CultureInfo.InvariantCulture), currency,
            product.Credits.ToString(CultureInfo.InvariantCulture));
        var grantHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(grantCanonical)));
        return new(PaymentProviderEventStatus.Verified, "PAYMENT_WEBHOOK_VERIFIED",
            new("stripe", eventId, paymentId, userId, product.ProductId, amountMinor,
                currency, product.Credits, payloadHash, grantHash));
    }

    private static bool TryParseSignature(string value, out long timestamp, out IReadOnlyList<byte[]> signatures)
    {
        timestamp = 0;
        signatures = [];
        var parsed = new List<byte[]>();
        var timestampCount = 0;
        foreach (var component in value.Split(','))
        {
            var pair = component.Split('=', 2);
            if (pair.Length != 2) continue;
            if (pair[0] == "t")
            {
                timestampCount++;
                if (!long.TryParse(pair[1], NumberStyles.None,
                        CultureInfo.InvariantCulture, out timestamp)) return false;
            }
            if (pair[0] == "v1" && pair[1].Length == 64)
            {
                try { parsed.Add(Convert.FromHexString(pair[1])); }
                catch (FormatException) { return false; }
            }
        }
        signatures = parsed;
        return timestampCount == 1 && timestamp > 0 && parsed.Count is > 0 and <= 8;
    }

    private static bool TryString(JsonElement element, string name, int maximum, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && (value = property.GetString() ?? string.Empty).Length is > 0 && value.Length <= maximum
            && !value.Any(char.IsControl);
    }

    private static PaymentProviderEventResult Invalid(string code) =>
        new(PaymentProviderEventStatus.Invalid, code);
    private static PaymentProviderEventResult Retryable() =>
        new(PaymentProviderEventStatus.Retryable, "PAYMENT_PROVIDER_UNAVAILABLE");
}

public sealed class UnavailablePaymentFulfillmentService : IPaymentFulfillmentService
{
    public Task<PaymentApplyResult> ApplyAsync(VerifiedPaymentEvent payment,
        CancellationToken cancellationToken = default) => Task.FromResult(
        new PaymentApplyResult(PaymentApplyStatus.Unavailable, "PAYMENT_FULFILLMENT_UNAVAILABLE"));
}

public sealed class PostgresPaymentFulfillmentService(NpgsqlDataSource dataSource) : IPaymentFulfillmentService
{
    public async Task<PaymentApplyResult> ApplyAsync(
        VerifiedPaymentEvent payment,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand(
                "SELECT status_code FROM private.payment_apply_verified($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)");
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, payment.Provider);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, payment.EventId);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, payment.PaymentId);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, payment.UserId);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, payment.ProductId);
            command.Parameters.AddWithValue(NpgsqlDbType.Bigint, payment.AmountMinor);
            command.Parameters.AddWithValue(NpgsqlDbType.Char, payment.Currency);
            command.Parameters.AddWithValue(NpgsqlDbType.Bigint, payment.Credits);
            command.Parameters.AddWithValue(NpgsqlDbType.Char, payment.PayloadSha256);
            command.Parameters.AddWithValue(NpgsqlDbType.Char, payment.GrantRequestSha256);
            var status = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return status switch
            {
                "PAYMENT_APPLIED" => new(PaymentApplyStatus.Applied, status),
                "PAYMENT_IDEMPOTENT_REPLAY" => new(PaymentApplyStatus.Replay, status),
                _ => new(PaymentApplyStatus.Rejected, "PAYMENT_FULFILLMENT_INVALID"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PostgresException exception) when (exception.ConstraintName is
            "PAYMENT_EVENT_CONFLICT" or "PAYMENT_REQUEST_INVALID")
        {
            return new(PaymentApplyStatus.Rejected, exception.ConstraintName);
        }
        catch (NpgsqlException)
        {
            return new(PaymentApplyStatus.Unavailable, "PAYMENT_FULFILLMENT_UNAVAILABLE");
        }
    }
}
