using System.Net;
using System.Text;
using AuditionModStudio.Cloud;
using AuditionModStudio.Core.Accounts;
using AuditionModStudio.Core.Auth;

namespace IntegrationTests;

public sealed class GatewayAccountOverviewServiceTests
{
    [Fact]
    public async Task Valid_bounded_response_is_accepted_with_bearer_and_without_client_user_input()
    {
        var handler = new StubHandler(ValidJson());
        var sessions = new StubSessions();
        using var service = new GatewayAccountOverviewService(new HttpClient(handler), sessions,
            new StubAuthentication(), new(new("https://gateway.example/")));

        var result = await service.RefreshAsync();

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal("token", handler.Request!.Headers.Authorization!.Parameter);
        Assert.Equal(HttpMethod.Get, handler.Request.Method);
        Assert.Equal("/v1/account", handler.Request.RequestUri!.AbsolutePath);
        Assert.Null(handler.Request.Content);
        Assert.Equal(80, result.Snapshot!.Wallet.AvailableCredits);
        Assert.Equal(CreditTransactionKind.Capture, Assert.Single(result.Snapshot.Transactions).Kind);
    }

    [Theory]
    [InlineData("unknown", "true")]
    [InlineData("wallet", "{\"availableCredits\":-1,\"reservedCredits\":0}")]
    [InlineData("transactions", "[]")]
    public async Task Unknown_negative_or_inconsistent_server_data_is_rejected(string property, string value)
    {
        var json = property == "unknown"
            ? ValidJson().Replace("{\"profile\"", "{\"unknown\":true,\"profile\"", StringComparison.Ordinal)
            : property == "wallet"
                ? ValidJson().Replace("{\"availableCredits\":80,\"reservedCredits\":20}", value, StringComparison.Ordinal)
                : ValidJson().Replace("[" + TransactionJson() + "]", value, StringComparison.Ordinal);
        using var service = new GatewayAccountOverviewService(new HttpClient(new StubHandler(json)),
            new StubSessions(), new StubAuthentication(), new(new("https://gateway.example/")));

        var result = await service.RefreshAsync();

        Assert.Equal(AccountOverviewStatus.InvalidResponse, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task Missing_session_is_auth_required_and_does_not_send_network_request()
    {
        var handler = new StubHandler(ValidJson());
        using var service = new GatewayAccountOverviewService(new HttpClient(handler), new StubSessions(false),
            new StubAuthentication(), new(new("https://gateway.example/")));

        var result = await service.RefreshAsync();

        Assert.Equal(AccountOverviewStatus.AuthenticationRequired, result.Status);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Cancelled_refresh_is_structured_and_oversized_response_is_never_accepted()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var cancelledService = new GatewayAccountOverviewService(
            new HttpClient(new StubHandler(ValidJson())), new StubSessions(), new StubAuthentication(),
            new(new("https://gateway.example/")));
        var cancelled = await cancelledService.RefreshAsync(cancellation.Token);

        var oversized = new string('x', GatewayAccountOptions.MaximumResponseBytes + 1);
        using var oversizedService = new GatewayAccountOverviewService(
            new HttpClient(new StubHandler(oversized)), new StubSessions(), new StubAuthentication(),
            new(new("https://gateway.example/")));
        var rejected = await oversizedService.RefreshAsync();

        Assert.Equal(AccountOverviewStatus.Cancelled, cancelled.Status);
        Assert.Equal(AccountOverviewStatus.InvalidResponse, rejected.Status);
        Assert.Null(rejected.Snapshot);
    }

    private static string ValidJson() => $$"""
        {"profile":{"userId":"d344ca41-25a5-4f24-8b8e-1654de660997","email":"person@example.com","displayName":null},"wallet":{"availableCredits":80,"reservedCredits":20},"usage":{"creditsGranted":100,"creditsUsed":20,"transactionCount":1},"transactions":[{{TransactionJson()}}],"observedAt":"2026-08-13T00:00:00+00:00"}
        """;

    private static string TransactionJson() =>
        "{\"transactionId\":\"f56b54be-06c0-49e0-a1be-139e8017192f\",\"kind\":\"capture\",\"amount\":20,\"availableDelta\":0,\"reservedDelta\":-20,\"availableAfter\":80,\"reservedAfter\":20,\"createdAt\":\"2026-08-12T00:00:00+00:00\"}";

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class StubSessions(bool available = true) : ISecureSessionStore
    {
        private readonly AuthSessionSecrets? _session = available
            ? new("token", "refresh", DateTimeOffset.UtcNow.AddHours(1)) : null;
        public Task<AuthSessionSecrets?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_session);
        public Task SaveAsync(AuthSessionSecrets session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubAuthentication : IAuthenticationService
    {
        private static readonly AuthenticationResult Missing = AuthenticationResult.Failure(
            AuthenticationFailureReason.SessionMissing, "AUTH_SESSION_MISSING");
        public Task<AuthenticationResult> SignUpAsync(AuthEmail email, AuthPassword password, CancellationToken cancellationToken = default) => Task.FromResult(Missing);
        public Task<AuthenticationResult> SignInAsync(AuthEmail email, AuthPassword password, CancellationToken cancellationToken = default) => Task.FromResult(Missing);
        public Task<AuthenticationResult> SignOutAsync(CancellationToken cancellationToken = default) => Task.FromResult(Missing);
        public Task<AuthenticationResult> RefreshSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Missing);
        public Task<AuthenticationResult> ResetPasswordAsync(AuthEmail email, CancellationToken cancellationToken = default) => Task.FromResult(Missing);
        public Task<AuthenticationResult> GetProfileAsync(CancellationToken cancellationToken = default) => Task.FromResult(Missing);
    }
}
