using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AuditionModStudio.Core.Accounts;

namespace AuditionModStudio.App.Account;

public sealed record AccountTransactionItem(
    Guid TransactionId,
    string Kind,
    string Amount,
    string BalanceChange,
    string BalanceAfter,
    string CreatedAt);

public sealed class AccountViewModel(IAccountOverviewService service) : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? _activationCancellation;
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
    private int _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

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
    public bool ShowProfile => HasSnapshot && !IsLoading;
    public bool ShowHistory => ShowProfile && Transactions.Length > 0;
    public bool ShowEmptyState => ShowProfile && Transactions.Length == 0;

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

    public void Deactivate()
    {
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationCancellation = null;
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
