using System.Collections.Immutable;

namespace AuditionModStudio.Core.Payments;

public enum PaymentProductType { Subscription, Credits }
public enum PaymentOrderStatus
{
    WaitingPayment, Paid, Fulfilling, Fulfilled, Expired, Cancelled, ReviewRequired, Refunded, Unknown,
}

public sealed record PaymentProduct(
    string ProductId,
    PaymentProductType Type,
    string DisplayName,
    long PriceVnd,
    int? DurationDays,
    long? CreditAmount,
    int SortOrder);

public sealed record PaymentOrder(
    Guid OrderId,
    string OrderCode,
    PaymentOrderStatus Status,
    PaymentProductType ProductType,
    string ProductName,
    long PriceVnd,
    int? DurationDays,
    long? CreditAmount,
    string Currency,
    string PaymentContent,
    DateTimeOffset ExpiresAt,
    string? BankCode,
    string? AccountNumber,
    string? AccountHolder,
    Uri? QrUri,
    DateTimeOffset? FulfilledAt,
    string? ReviewReason);

public sealed record PaymentCatalogResult(bool Succeeded, string DiagnosticCode,
    ImmutableArray<PaymentProduct> Products);
public sealed record PaymentOrderResult(bool Succeeded, string DiagnosticCode, PaymentOrder? Order);

public interface IPaymentService
{
    Task<PaymentCatalogResult> GetCatalogAsync(CancellationToken cancellationToken = default);
    Task<PaymentOrderResult> CreateOrderAsync(string productId, string idempotencyKey,
        CancellationToken cancellationToken = default);
    Task<PaymentOrderResult> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<PaymentOrderResult> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
}

public sealed class UnavailablePaymentService : IPaymentService
{
    private static readonly PaymentCatalogResult CatalogFailure =
        new(false, "PAYMENT_SERVICE_UNAVAILABLE", []);
    private static readonly PaymentOrderResult OrderFailure =
        new(false, "PAYMENT_SERVICE_UNAVAILABLE", null);

    public Task<PaymentCatalogResult> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CatalogFailure);
    public Task<PaymentOrderResult> CreateOrderAsync(string productId, string idempotencyKey,
        CancellationToken cancellationToken = default) => Task.FromResult(OrderFailure);
    public Task<PaymentOrderResult> GetOrderAsync(Guid orderId,
        CancellationToken cancellationToken = default) => Task.FromResult(OrderFailure);
    public Task<PaymentOrderResult> CancelOrderAsync(Guid orderId,
        CancellationToken cancellationToken = default) => Task.FromResult(OrderFailure);
}
