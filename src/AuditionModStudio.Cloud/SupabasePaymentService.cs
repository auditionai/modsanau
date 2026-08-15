using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditionModStudio.Core.Auth;
using AuditionModStudio.Core.Payments;

namespace AuditionModStudio.Cloud;

public sealed class SupabasePaymentService(
    HttpClient httpClient,
    ISecureSessionStore sessionStore,
    IAuthenticationService authenticationService,
    Uri projectUri) : IPaymentService
{
    private const int MaximumResponseBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri endpoint = new(projectUri, "functions/v1/payments/");

    public async Task<PaymentCatalogResult> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(new Uri(endpoint, "catalog"),
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var payload = await ReadAsync<CatalogPayload>(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || payload?.Products is null)
                return new(false, "PAYMENT_CATALOG_UNAVAILABLE", []);
            var products = payload.Products.Select(MapProduct).Where(item => item is not null)
                .Cast<PaymentProduct>().OrderBy(item => item.SortOrder).ToImmutableArray();
            return products.Length == payload.Products.Length
                ? new(true, "PAYMENT_CATALOG_READY", products)
                : new(false, "PAYMENT_CATALOG_INVALID", []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(false, "PAYMENT_CANCELLED", []); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                                                     or InvalidOperationException)
        { return new(false, "PAYMENT_CATALOG_UNAVAILABLE", []); }
    }

    public Task<PaymentOrderResult> CreateOrderAsync(string productId, string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        SendOrderAsync(HttpMethod.Post, "orders", new { product_id = productId }, idempotencyKey, cancellationToken);

    public Task<PaymentOrderResult> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        SendOrderAsync(HttpMethod.Get, $"orders/{orderId:D}", null, null, cancellationToken);

    public Task<PaymentOrderResult> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        SendOrderAsync(HttpMethod.Delete, $"orders/{orderId:D}", null, null, cancellationToken);

    private async Task<PaymentOrderResult> SendOrderAsync(HttpMethod method, string route, object? body,
        string? idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
            if (session is null) return new(false, "PAYMENT_AUTH_REQUIRED", null);
            using var request = new HttpRequestMessage(method, new Uri(endpoint, route));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                request.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);
            if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var payload = await ReadAsync<OrderPayload>(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || payload is null)
                return new(false, response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "PAYMENT_RATE_LIMITED" : "PAYMENT_REQUEST_FAILED", null);
            var order = MapOrder(payload);
            return order is null ? new(false, "PAYMENT_RESPONSE_INVALID", null)
                : new(true, "PAYMENT_ORDER_READY", order);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(false, "PAYMENT_CANCELLED", null); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                                                     or InvalidOperationException)
        { return new(false, "PAYMENT_SERVICE_UNAVAILABLE", null); }
    }

    private async Task<AuthSessionSecrets?> GetSessionAsync(CancellationToken cancellationToken)
    {
        var session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is not null && session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return session;
        var refreshed = await authenticationService.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
        return refreshed.Session is null ? null : await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) return default;
        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static PaymentProduct? MapProduct(ProductPayload value) =>
        TryType(value.Type, out var type) && !string.IsNullOrWhiteSpace(value.ProductId)
        && !string.IsNullOrWhiteSpace(value.DisplayName) && value.PriceVnd > 0
            ? new(value.ProductId, type, value.DisplayName, value.PriceVnd,
                value.DurationDays, value.CreditAmount, value.SortOrder) : null;

    private static PaymentOrder? MapOrder(OrderPayload value)
    {
        if (value.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(value.OrderCode)
            || !TryType(value.ProductType, out var type) || value.PriceVnd <= 0
            || value.ExpiresAt == default || !TryStatus(value.Status, out var status)) return null;
        Uri? qrUri = null;
        if (!string.IsNullOrWhiteSpace(value.QrUrl)
            && (!Uri.TryCreate(value.QrUrl, UriKind.Absolute, out qrUri)
                || qrUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(qrUri.Host, "vietqr.app", StringComparison.OrdinalIgnoreCase))) return null;
        return new(value.OrderId, value.OrderCode, status, type, value.ProductName ?? value.OrderCode,
            value.PriceVnd, value.DurationDays, value.CreditAmount, value.Currency ?? "VND",
            value.PaymentContent ?? value.OrderCode, value.ExpiresAt, value.BankCode,
            value.AccountNumber, value.AccountHolder, qrUri, value.FulfilledAt, value.ReviewReason);
    }

    private static bool TryType(string? value, out PaymentProductType type) =>
        Enum.TryParse(value, true, out type);

    private static bool TryStatus(string? value, out PaymentOrderStatus status)
    {
        status = value?.ToLowerInvariant() switch
        {
            "waiting_payment" => PaymentOrderStatus.WaitingPayment,
            "paid" => PaymentOrderStatus.Paid,
            "fulfilling" => PaymentOrderStatus.Fulfilling,
            "fulfilled" => PaymentOrderStatus.Fulfilled,
            "expired" => PaymentOrderStatus.Expired,
            "cancelled" => PaymentOrderStatus.Cancelled,
            "review_required" => PaymentOrderStatus.ReviewRequired,
            "refunded" => PaymentOrderStatus.Refunded,
            _ => PaymentOrderStatus.Unknown,
        };
        return status != PaymentOrderStatus.Unknown;
    }

    private sealed record CatalogPayload(ProductPayload[] Products);
    private sealed record ProductPayload(string ProductId, string Type, string DisplayName,
        long PriceVnd, int? DurationDays, long? CreditAmount, int SortOrder);
    private sealed record OrderPayload(Guid OrderId, string OrderCode, string Status, string ProductType,
        string? ProductName, long PriceVnd, int? DurationDays, long? CreditAmount, string? Currency,
        string? PaymentContent, DateTimeOffset ExpiresAt, string? BankCode, string? AccountNumber,
        string? AccountHolder, string? QrUrl, DateTimeOffset? FulfilledAt, string? ReviewReason);
}
