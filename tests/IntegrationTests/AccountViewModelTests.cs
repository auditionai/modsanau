using System.Collections.Immutable;
using AuditionModStudio.App.Account;
using AuditionModStudio.App.Shell;
using AuditionModStudio.Core.Accounts;
using AuditionModStudio.Core.Payments;

namespace IntegrationTests;

public sealed class AccountViewModelTests
{
    [Fact]
    public async Task Success_projects_server_snapshot_into_read_only_profile_wallet_usage_and_history()
    {
        var service = new StubService(Success(Transaction()));
        using var viewModel = new AccountViewModel(service);

        await viewModel.ActivateAsync();

        Assert.True(viewModel.HasSnapshot);
        Assert.False(viewModel.HasError);
        Assert.True(viewModel.ShowHistory);
        Assert.False(viewModel.ShowEmptyState);
        Assert.Equal("person@example.com", viewModel.Email);
        Assert.Equal("80", viewModel.AvailableCredits);
        Assert.Equal("20", viewModel.ReservedCredits);
        Assert.Equal("100", viewModel.CreditsGranted);
        Assert.Equal("20", viewModel.CreditsUsed);
        Assert.Equal("Capture", Assert.Single(viewModel.Transactions).Kind);
    }

    [Fact]
    public async Task Empty_error_and_cancelled_states_never_keep_client_balance_as_authority()
    {
        var service = new StubService(Success());
        using var viewModel = new AccountViewModel(service);
        await viewModel.ActivateAsync();
        Assert.True(viewModel.ShowEmptyState);

        service.Result = new(AccountOverviewStatus.Unavailable, "ACCOUNT_SERVICE_UNAVAILABLE", null);
        await viewModel.ActivateAsync();
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.HasSnapshot);
        Assert.Equal("—", viewModel.AvailableCredits);
        Assert.Empty(viewModel.Transactions);

        service.Result = new(AccountOverviewStatus.Cancelled, "ACCOUNT_CANCELLED", null);
        await viewModel.ActivateAsync();
        Assert.False(viewModel.HasError);
        Assert.Contains("Đã hủy", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shell_badges_follow_only_the_validated_account_snapshot()
    {
        using var account = new AccountViewModel(new StubService(Success(Transaction())));
        var shell = new AppShellViewModel(account);

        await account.ActivateAsync();

        Assert.Equal("Person", shell.AccountStatus);
        Assert.Equal("80 Credits", shell.CreditsStatus);
        Assert.Equal("Trực tuyến", shell.ConnectionStatus);
    }

    [Fact]
    public async Task Superseded_refresh_cannot_overwrite_the_newest_server_snapshot()
    {
        var service = new SequencedService();
        using var viewModel = new AccountViewModel(service);

        var first = viewModel.ActivateAsync();
        await service.FirstStarted.Task;
        var second = viewModel.ActivateAsync();
        service.Second.SetResult(Success(Transaction()));
        await second;
        service.First.SetResult(new(AccountOverviewStatus.Cancelled, "ACCOUNT_CANCELLED", null));
        await first;

        Assert.True(viewModel.HasSnapshot);
        Assert.Equal("80", viewModel.AvailableCredits);
        Assert.Contains("Đã tải", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Purchase_flow_uses_server_catalog_and_authoritative_order_instructions()
    {
        var payment = new StubPaymentService();
        using var viewModel = new AccountViewModel(new StubService(Success()), payments: payment);

        var prepared = await viewModel.PreparePurchaseAsync(PaymentProductType.Credits);
        await viewModel.CreatePaymentOrderAsync();

        Assert.True(prepared);
        Assert.Equal("credits_500", payment.CreatedProductId);
        Assert.StartsWith("desktop.", payment.IdempotencyKey, StringComparison.Ordinal);
        Assert.True(viewModel.HasPaymentOrder);
        Assert.Equal("50.000 đ", viewModel.PaymentAmount);
        Assert.Equal("VCB", viewModel.PaymentBank);
        Assert.Equal("AMS0123456789ABCDEF", viewModel.PaymentContent);
        Assert.Equal("https://vietqr.app/img?fixture=107", viewModel.PaymentQrUrl);
        Assert.Contains("Đang chờ thanh toán", viewModel.PaymentMessage, StringComparison.Ordinal);
    }

    private static AccountOverviewResult Success(params AccountCreditTransaction[] transactions) => new(
        AccountOverviewStatus.Succeeded, "ACCOUNT_READ", new(
            new(Guid.Parse("d344ca41-25a5-4f24-8b8e-1654de660997"), "person@example.com", "Person"),
            new(80, 20), new(100, 20, transactions.Length), transactions.ToImmutableArray(),
            new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero)));

    private static AccountCreditTransaction Transaction() => new(
        Guid.Parse("f56b54be-06c0-49e0-a1be-139e8017192f"), CreditTransactionKind.Capture,
        20, 0, -20, 80, 20, new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));

    private sealed class StubService(AccountOverviewResult result) : IAccountOverviewService
    {
        public AccountOverviewResult Result { get; set; } = result;
        public Task<AccountOverviewResult> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    private sealed class SequencedService : IAccountOverviewService
    {
        private int _callCount;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AccountOverviewResult> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AccountOverviewResult> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AccountOverviewResult> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                FirstStarted.SetResult();
                return First.Task;
            }
            return Second.Task;
        }
    }

    private sealed class StubPaymentService : IPaymentService
    {
        private static readonly PaymentProduct Product = new(
            "credits_500", PaymentProductType.Credits, "500 Credits", 50000, null, 500, 10);
        private static readonly PaymentOrder Order = new(
            Guid.Parse("10700000-0000-0000-0000-000000000001"), "AMS0123456789ABCDEF",
            PaymentOrderStatus.WaitingPayment, PaymentProductType.Credits, "500 Credits", 50000,
            null, 500, "VND", "AMS0123456789ABCDEF", DateTimeOffset.UtcNow.AddMinutes(15),
            "VCB", "1234567890", "AUDITION AI", new("https://vietqr.app/img?fixture=107"), null, null);

        public string? CreatedProductId { get; private set; }
        public string? IdempotencyKey { get; private set; }

        public Task<PaymentCatalogResult> GetCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentCatalogResult(true, "PAYMENT_CATALOG_READY", [Product]));

        public Task<PaymentOrderResult> CreateOrderAsync(string productId, string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            CreatedProductId = productId;
            IdempotencyKey = idempotencyKey;
            return Task.FromResult(new PaymentOrderResult(true, "PAYMENT_ORDER_READY", Order));
        }

        public Task<PaymentOrderResult> GetOrderAsync(Guid orderId,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new PaymentOrderResult(true, "PAYMENT_ORDER_READY", Order));

        public Task<PaymentOrderResult> CancelOrderAsync(Guid orderId,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new PaymentOrderResult(true, "PAYMENT_ORDER_READY", Order with
                { Status = PaymentOrderStatus.Cancelled }));
    }
}
