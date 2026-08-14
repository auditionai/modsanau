using System.Collections.Immutable;

namespace AuditionModStudio.Core.Accounts;

public enum CreditTransactionKind
{
    Grant,
    Reserve,
    Capture,
    Release,
    Refund,
}

public sealed record AccountProfile(Guid UserId, string? Email, string? DisplayName);

public sealed record AccountWallet(long AvailableCredits, long ReservedCredits);

public sealed record AccountUsage(long CreditsGranted, long CreditsUsed, long TransactionCount);

public sealed record AccountCreditTransaction(
    Guid TransactionId,
    CreditTransactionKind Kind,
    long Amount,
    long AvailableDelta,
    long ReservedDelta,
    long AvailableAfter,
    long ReservedAfter,
    DateTimeOffset CreatedAt);

public sealed record AccountOverviewSnapshot(
    AccountProfile Profile,
    AccountWallet Wallet,
    AccountUsage Usage,
    ImmutableArray<AccountCreditTransaction> Transactions,
    DateTimeOffset ObservedAt);

public enum AccountOverviewStatus
{
    Succeeded,
    AuthenticationRequired,
    Unavailable,
    InvalidResponse,
    Cancelled,
}

public sealed record AccountOverviewResult(
    AccountOverviewStatus Status,
    string DiagnosticCode,
    AccountOverviewSnapshot? Snapshot)
{
    public bool Succeeded => Status == AccountOverviewStatus.Succeeded && Snapshot is not null;
}

public interface IAccountOverviewService
{
    Task<AccountOverviewResult> RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class UnavailableAccountOverviewService : IAccountOverviewService
{
    public Task<AccountOverviewResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AccountOverviewResult(
            cancellationToken.IsCancellationRequested ? AccountOverviewStatus.Cancelled : AccountOverviewStatus.Unavailable,
            cancellationToken.IsCancellationRequested ? "ACCOUNT_CANCELLED" : "ACCOUNT_SERVICE_UNAVAILABLE", null));
}
