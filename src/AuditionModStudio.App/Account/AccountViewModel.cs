using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AuditionModStudio.Core.Accounts;
using AuditionModStudio.Core.Payments;
using AuditionModStudio.Core.Subscriptions;

namespace AuditionModStudio.App.Account;

public sealed record AccountTransactionItem(
    Guid TransactionId,
    string Kind,
    string Amount,
    string BalanceChange,
    string BalanceAfter,
    string CreatedAt);

public sealed record PaymentProductItem(PaymentProduct Product)
{
    public string Label => Product.Type == PaymentProductType.Subscription
        ? $"{Product.DisplayName} · {Product.DurationDays:N0} ngày · {Product.PriceVnd:N0} đ"
        : $"{Product.DisplayName} · {Product.CreditAmount:N0} Credits · {Product.PriceVnd:N0} đ";
}

public sealed class AccountViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAccountOverviewService service;
    private readonly IDeviceEntitlementService deviceEntitlements;
    private readonly IPaymentService payments;
    private CancellationTokenSource? _activationCancellation;
    private CancellationTokenSource? _paymentCancellation;
    private ImmutableArray<AccountTransactionItem> _transactions = [];
    private string _statusMessage = "Mở Tài khoản để tải hồ sơ và lịch sử Credits.";
    private string _displayName = "Chưa cung cấp";
    private string _email = "Chưa có dữ liệu";
    private string _userId = "Chưa có dữ liệu";
    private string _availableCredits = "—";
    private string _reservedCredits = "—";
    private string _creditsGranted = "—";
    private string _creditsUsed = "—";
    private string _transactionCount = "—";
    private string _observedAt = "Chưa tải";
    private bool _isLoading;
    private bool _hasSnapshot;
    private bool _hasError;
    private string _deviceCode = "—", _subscriptionStatus = "Chưa tải", _subscriptionExpiry = "—", _remainingDays = "—";
    private string _benefits = "Chưa có dữ liệu quyền sử dụng.", _giftCode = string.Empty;
    private bool _canRedeem;
    private ImmutableArray<PaymentProductItem> _paymentProducts = [];
    private PaymentProductItem? _selectedPaymentProduct;
    private PaymentOrder? _paymentOrder;
    private string? _paymentQrUrl;
    private string _paymentMessage = "Chọn gói để xem tổng thanh toán.";
    private bool _isPaymentBusy;
    private int _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AccountViewModel(IAccountOverviewService service, IDeviceEntitlementService? deviceEntitlements = null,
        IPaymentService? payments = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.deviceEntitlements = deviceEntitlements ?? new UnavailableDeviceEntitlementService();
        this.payments = payments ?? new UnavailablePaymentService();
    }

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public string Email { get => _email; private set => Set(ref _email, value); }
    public string UserId { get => _userId; private set => Set(ref _userId, value); }
    public string AvailableCredits { get => _availableCredits; private set => Set(ref _availableCredits, value); }
    public string ReservedCredits { get => _reservedCredits; private set => Set(ref _reservedCredits, value); }
    public string CreditsGranted { get => _creditsGranted; private set => Set(ref _creditsGranted, value); }
    public string CreditsUsed { get => _creditsUsed; private set => Set(ref _creditsUsed, value); }
    public string TransactionCount { get => _transactionCount; private set => Set(ref _transactionCount, value); }
    public string ObservedAt { get => _observedAt; private set => Set(ref _observedAt, value); }
    public ImmutableArray<AccountTransactionItem> Transactions
    { get => _transactions; private set { if (Set(ref _transactions, value)) NotifyStates(); } }
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) NotifyStates(); } }
    public bool HasSnapshot { get => _hasSnapshot; private set { if (Set(ref _hasSnapshot, value)) NotifyStates(); } }
    public bool HasError { get => _hasError; private set { if (Set(ref _hasError, value)) NotifyStates(); } }
    public bool CanRefresh => !IsLoading;
    public string DeviceCode { get => _deviceCode; private set => Set(ref _deviceCode, value); }
    public string SubscriptionStatus { get => _subscriptionStatus; private set => Set(ref _subscriptionStatus, value); }
    public string SubscriptionExpiry { get => _subscriptionExpiry; private set => Set(ref _subscriptionExpiry, value); }
    public string RemainingDays { get => _remainingDays; private set => Set(ref _remainingDays, value); }
    public string Benefits { get => _benefits; private set => Set(ref _benefits, value); }
    public string GiftCode { get => _giftCode; set { if (Set(ref _giftCode, value ?? string.Empty)) CanRedeem = !IsLoading && !string.IsNullOrWhiteSpace(_giftCode); } }
    public bool CanRedeem { get => _canRedeem; private set => Set(ref _canRedeem, value); }
    public bool ShowProfile => HasSnapshot && !IsLoading;
    public bool ShowHistory => ShowProfile && Transactions.Length > 0;
    public bool ShowEmptyState => ShowProfile && Transactions.Length == 0;
    public ImmutableArray<PaymentProductItem> PaymentProducts
    { get => _paymentProducts; private set { if (Set(ref _paymentProducts, value)) NotifyPaymentStates(); } }
    public PaymentProductItem? SelectedPaymentProduct
    { get => _selectedPaymentProduct; set { if (Set(ref _selectedPaymentProduct, value)) NotifyPaymentStates(); } }
    public string PaymentMessage { get => _paymentMessage; private set => Set(ref _paymentMessage, value); }
    public bool IsPaymentBusy
    { get => _isPaymentBusy; private set { if (Set(ref _isPaymentBusy, value)) NotifyPaymentStates(); } }
    public bool CanCreatePayment => !IsPaymentBusy && SelectedPaymentProduct is not null;
    public bool HasPaymentOrder => _paymentOrder is not null;
    public bool ShowPaymentProductSelection => !HasPaymentOrder;
    public string PaymentProductName => _paymentOrder?.ProductName ?? "—";
    public string PaymentAmount => _paymentOrder is null ? "—" : $"{_paymentOrder.PriceVnd:N0} đ";
    public string PaymentBank => _paymentOrder?.BankCode ?? "—";
    public string PaymentAccount => _paymentOrder?.AccountNumber ?? "—";
    public string PaymentAccountHolder => _paymentOrder?.AccountHolder ?? "—";
    public string PaymentContent => _paymentOrder?.PaymentContent ?? "—";
    public string PaymentExpiresAt => _paymentOrder?.ExpiresAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
    public string? PaymentQrUrl => _paymentQrUrl;
    public bool CanCancelPayment => !IsPaymentBusy && _paymentOrder?.Status == PaymentOrderStatus.WaitingPayment;

    public async Task<bool> PreparePurchaseAsync(PaymentProductType type, CancellationToken cancellationToken = default)
    {
        _paymentCancellation?.Cancel();
        _paymentCancellation?.Dispose();
        _paymentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ClearPaymentOrder();
        IsPaymentBusy = true;
        PaymentMessage = "Đang tải danh sách gói…";
        try
        {
            var result = await payments.GetCatalogAsync(_paymentCancellation.Token);
            PaymentProducts = result.Succeeded
                ? result.Products.Where(product => product.Type == type).Select(product => new PaymentProductItem(product))
                    .ToImmutableArray()
                : [];
            SelectedPaymentProduct = PaymentProducts.FirstOrDefault();
            PaymentMessage = PaymentProducts.IsEmpty
                ? "Chưa có gói đang phát hành. Giá và sản phẩm do server quản lý."
                : "Kiểm tra gói và tổng thanh toán trước khi tạo đơn.";
            return !PaymentProducts.IsEmpty;
        }
        finally { IsPaymentBusy = false; }
    }

    public async Task CreatePaymentOrderAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCreatePayment || SelectedPaymentProduct is null) return;
        IsPaymentBusy = true;
        PaymentMessage = "Đang tạo đơn thanh toán…";
        try
        {
            var idempotencyKey = $"desktop.{Guid.NewGuid():N}";
            var result = await payments.CreateOrderAsync(SelectedPaymentProduct.Product.ProductId,
                idempotencyKey, cancellationToken);
            if (!result.Succeeded || result.Order is null)
            {
                PaymentMessage = "Không thể tạo đơn thanh toán lúc này. Vui lòng thử lại.";
                return;
            }
            ApplyPaymentOrder(result.Order);
            PaymentMessage = "Đang chờ thanh toán. Vui lòng giữ nguyên nội dung chuyển khoản.";
            _paymentCancellation?.Cancel();
            _paymentCancellation?.Dispose();
            _paymentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = MonitorPaymentAsync(result.Order.OrderId, _paymentCancellation.Token);
        }
        finally { IsPaymentBusy = false; }
    }

    public async Task CancelPaymentAsync(CancellationToken cancellationToken = default)
    {
        if (_paymentOrder is null || !CanCancelPayment) return;
        IsPaymentBusy = true;
        try
        {
            var result = await payments.CancelOrderAsync(_paymentOrder.OrderId, cancellationToken);
            if (result.Succeeded && result.Order is not null) ApplyPaymentOrder(result.Order);
            PaymentMessage = result.Succeeded ? "Đơn thanh toán đã được hủy." : "Không thể hủy đơn lúc này.";
            _paymentCancellation?.Cancel();
        }
        finally { IsPaymentBusy = false; }
    }

    private async Task MonitorPaymentAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var delays = new[] { 4, 5, 7, 10, 15, 20, 30 };
        var stopAt = (_paymentOrder?.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15)).AddMinutes(2);
        for (var attempt = 0;
             !cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < stopAt;
             attempt++)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(delays[Math.Min(attempt, delays.Length - 1)]), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            var result = await payments.GetOrderAsync(orderId, cancellationToken);
            if (!result.Succeeded || result.Order is null)
            {
                PaymentMessage = "Không thể cập nhật trạng thái thanh toán. Ứng dụng sẽ thử lại.";
                continue;
            }
            ApplyPaymentOrder(result.Order);
            switch (result.Order.Status)
            {
                case PaymentOrderStatus.Fulfilled:
                    PaymentMessage = result.Order.ProductType == PaymentProductType.Subscription
                        ? $"Thanh toán thành công. Đã gia hạn thêm {result.Order.DurationDays:N0} ngày."
                        : $"Thanh toán thành công. Đã cộng {result.Order.CreditAmount:N0} Credits.";
                    await RefreshAfterPaymentAsync(cancellationToken);
                    return;
                case PaymentOrderStatus.Expired:
                    PaymentMessage = "Đơn thanh toán đã hết hạn. Hãy tạo đơn mới.";
                    return;
                case PaymentOrderStatus.Cancelled:
                    PaymentMessage = "Đơn thanh toán đã được hủy.";
                    return;
                case PaymentOrderStatus.ReviewRequired:
                    PaymentMessage = "Thanh toán cần kiểm tra. Không chuyển thêm tiền cho đơn này.";
                    return;
                default:
                    PaymentMessage = "Đang chờ thanh toán. Trạng thái sẽ được cập nhật tự động.";
                    break;
            }
        }
        if (!cancellationToken.IsCancellationRequested)
            PaymentMessage = "Đã dừng cập nhật tự động. Hãy làm mới để kiểm tra trạng thái mới nhất.";
    }

    private async Task RefreshAfterPaymentAsync(CancellationToken cancellationToken)
    {
        var account = await service.RefreshAsync(cancellationToken);
        if (account.Succeeded && account.Snapshot is not null) Apply(account.Snapshot);
        await RefreshDeviceAsync(cancellationToken);
        HasSnapshot = account.Succeeded || HasSnapshot;
    }

    private void ApplyPaymentOrder(PaymentOrder order)
    {
        _paymentOrder = order;
        _paymentQrUrl = order.QrUri?.AbsoluteUri;
        NotifyPaymentStates();
    }

    private void ClearPaymentOrder()
    {
        _paymentOrder = null;
        _paymentQrUrl = null;
        NotifyPaymentStates();
    }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _activationCancellation.Token;
        ClearAuthoritativeData();
        IsLoading = true;
        HasError = false;
        StatusMessage = "Đang tải dữ liệu tài khoản…";
        try
        {
            var result = await service.RefreshAsync(token);
            if (_activationCancellation is null || _activationCancellation.Token != token) return;
            if (token.IsCancellationRequested || result.Status == AccountOverviewStatus.Cancelled)
            {
                StatusMessage = "Đã hủy làm mới tài khoản.";
                return;
            }
            if (!result.Succeeded || result.Snapshot is null)
            {
                HasError = true;
                StatusMessage = result.Status == AccountOverviewStatus.AuthenticationRequired
                ? "Đăng nhập để xem tài khoản và lịch sử Credits."
                    : result.Status == AccountOverviewStatus.InvalidResponse
                    ? "Dữ liệu tài khoản không hợp lệ nên số dư chưa được cập nhật."
                    : "Không thể kết nối dịch vụ tài khoản lúc này. Bạn vẫn có thể chỉnh sửa và xuất dự án bình thường.";
                return;
            }
            Apply(result.Snapshot);
            await RefreshDeviceAsync(token);
            HasSnapshot = true;
            StatusMessage = result.Snapshot.Transactions.IsEmpty
            ? "Đã tải tài khoản. Chưa có giao dịch Credits."
            : $"Đã tải {result.Snapshot.Transactions.Length:N0} giao dịch gần đây.";
        }
        finally
        {
            if (_activationCancellation is not null && _activationCancellation.Token == token) IsLoading = false;
        }
    }

    public async Task RedeemGiftCodeAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading || string.IsNullOrWhiteSpace(GiftCode)) return;
        IsLoading = true; CanRedeem = false;
        try
        {
            var result = await deviceEntitlements.RedeemGiftCodeAsync(GiftCode, cancellationToken);
            if (result.Succeeded && result.Snapshot is not null)
            { ApplyDevice(result.Snapshot); GiftCode = string.Empty; StatusMessage = "Mã quà tặng đã được kích hoạt."; }
            else StatusMessage = result.Status == DeviceEntitlementResultStatus.Rejected
                ? "Mã quà tặng không hợp lệ, đã hết hạn hoặc đã được sử dụng trên thiết bị này."
                : "Chưa thể kích hoạt mã lúc này. Vui lòng thử lại sau.";
        }
        finally { IsLoading = false; CanRedeem = !string.IsNullOrWhiteSpace(GiftCode); }
    }

    private async Task RefreshDeviceAsync(CancellationToken cancellationToken)
    {
        var result = await deviceEntitlements.RefreshAsync(cancellationToken);
        if (result.Succeeded && result.Snapshot is not null) ApplyDevice(result.Snapshot);
    }

    private void ApplyDevice(DeviceEntitlementSnapshot snapshot)
    {
        DeviceCode = snapshot.DeviceCode;
        SubscriptionStatus = snapshot.SubscriptionStatus switch
        { Core.Subscriptions.SubscriptionStatus.Active => "Đang hoạt động", Core.Subscriptions.SubscriptionStatus.Expired => "Đã hết hạn", Core.Subscriptions.SubscriptionStatus.Suspended => "Tạm dừng", Core.Subscriptions.SubscriptionStatus.Revoked => "Đã thu hồi", _ => "Chưa kích hoạt" };
        SubscriptionExpiry = snapshot.SubscriptionExpiresAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
        RemainingDays = snapshot.SubscriptionExpiresAt is { } expiry ? $"Còn {Math.Max(0, (int)Math.Ceiling((expiry - snapshot.ObservedAt).TotalDays)):N0} ngày" : "—";
        Benefits = $"AI: {Yes(snapshot.Capabilities.CanUseAi)} · Build: {Yes(snapshot.Capabilities.CanBuild)} · Export: {Yes(snapshot.Capabilities.CanExport)} · Mẫu Premium: {Yes(snapshot.Capabilities.CanUsePremiumTemplates)}";
        AvailableCredits = snapshot.AvailableCredits.ToString("N0", CultureInfo.CurrentCulture);
    }
    private static string Yes(bool value) => value ? "Có" : "Không";

    public void Deactivate()
    {
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationCancellation = null;
        _paymentCancellation?.Cancel();
        _paymentCancellation?.Dispose();
        _paymentCancellation = null;
        IsLoading = false;
    }

    private void Apply(AccountOverviewSnapshot snapshot)
    {
        DisplayName = snapshot.Profile.DisplayName ?? "Chưa cung cấp";
        Email = snapshot.Profile.Email ?? "Chưa có dữ liệu";
        UserId = snapshot.Profile.UserId.ToString("D");
        AvailableCredits = snapshot.Wallet.AvailableCredits.ToString("N0", CultureInfo.CurrentCulture);
        ReservedCredits = snapshot.Wallet.ReservedCredits.ToString("N0", CultureInfo.CurrentCulture);
        CreditsGranted = snapshot.Usage.CreditsGranted.ToString("N0", CultureInfo.CurrentCulture);
        CreditsUsed = snapshot.Usage.CreditsUsed.ToString("N0", CultureInfo.CurrentCulture);
        TransactionCount = snapshot.Usage.TransactionCount.ToString("N0", CultureInfo.CurrentCulture);
        ObservedAt = snapshot.ObservedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        Transactions = snapshot.Transactions.Select(ToPresentation).ToImmutableArray();
    }

    private static AccountTransactionItem ToPresentation(AccountCreditTransaction transaction)
    {
        var sign = transaction.Kind is CreditTransactionKind.Grant or CreditTransactionKind.Refund ? "+" : "−";
        return new(transaction.TransactionId, transaction.Kind.ToString(),
            $"{sign}{transaction.Amount:N0}",
                $"Khả dụng {transaction.AvailableDelta:+#;-#;0}; tạm giữ {transaction.ReservedDelta:+#;-#;0}",
                $"Khả dụng {transaction.AvailableAfter:N0}; tạm giữ {transaction.ReservedAfter:N0}",
            transaction.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    }

    private void ClearAuthoritativeData()
    {
        HasSnapshot = false;
        Transactions = [];
        DisplayName = "Chưa cung cấp";
        Email = UserId = "Chưa có dữ liệu";
        AvailableCredits = ReservedCredits = CreditsGranted = CreditsUsed = TransactionCount = "—";
        ObservedAt = "Chưa tải";
    }

    private void NotifyStates()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(ShowProfile));
        OnPropertyChanged(nameof(ShowHistory));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void NotifyPaymentStates()
    {
        OnPropertyChanged(nameof(CanCreatePayment));
        OnPropertyChanged(nameof(HasPaymentOrder));
        OnPropertyChanged(nameof(ShowPaymentProductSelection));
        OnPropertyChanged(nameof(PaymentProductName));
        OnPropertyChanged(nameof(PaymentAmount));
        OnPropertyChanged(nameof(PaymentBank));
        OnPropertyChanged(nameof(PaymentAccount));
        OnPropertyChanged(nameof(PaymentAccountHolder));
        OnPropertyChanged(nameof(PaymentContent));
        OnPropertyChanged(nameof(PaymentExpiresAt));
        OnPropertyChanged(nameof(PaymentQrUrl));
        OnPropertyChanged(nameof(CanCancelPayment));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Deactivate();
    }
}
