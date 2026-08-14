using System.Collections.Immutable;
using AuditionModStudio.App.Account;
using AuditionModStudio.App.Shell;
using AuditionModStudio.Core.Accounts;

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
}
