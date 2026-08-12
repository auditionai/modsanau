using System.Net;
using System.Text;
using AuditionModStudio.Cloud;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Auth;

namespace IntegrationTests;

public sealed class GatewayAiStudioServiceTests
{
    [Fact]
    public async Task Quote_uses_secure_bearer_session_and_parses_bounded_server_price()
    {
        var handler = new StubHandler(_ => Json("""
            {"creditCost":7,"pricingVersion":"v2"}
            """));
        var store = new StubSessionStore(Session("fake-access-token"));
        var service = CreateService(handler, store);

        var result = await service.GetQuoteAsync(AiStudioOperation.Generate);

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.Quote!.CreditCost);
        Assert.Equal("v2", result.Quote.PricingVersion);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("fake-access-token", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.DoesNotContain("fake-access-token", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_validates_owner_scoped_shape_and_derives_cancel_state()
    {
        var jobId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json($$"""
            {"jobs":[{"jobId":"{{jobId:D}}","operation":"Edit","status":"Processing","reservedCredits":9,"finalCredits":null,"createdAt":"2026-08-12T00:00:00+00:00"}]}
            """));
        var service = CreateService(handler, new StubSessionStore(Session("fake-access-token")));

        var result = await service.GetHistoryAsync();

        var job = Assert.Single(result.Jobs);
        Assert.True(result.Succeeded);
        Assert.Equal(jobId, job.JobId);
        Assert.Equal(AiStudioJobState.Processing, job.State);
        Assert.True(job.CanCancel);
    }

    [Fact]
    public async Task Expired_session_refreshes_through_auth_service_and_oversized_response_fails_closed()
    {
        var content = new ByteArrayContent(new byte[256 * 1_024 + 1]);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var store = new StubSessionStore(new("expired", "fake-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var auth = new StubAuthenticationService(store);
        var service = CreateService(handler, store, auth);

        var result = await service.GetQuoteAsync(AiStudioOperation.Generate);

        Assert.False(result.Succeeded);
        Assert.Equal("AI_STUDIO_RESPONSE_INVALID", result.DiagnosticCode);
        Assert.Equal(1, auth.RefreshCount);
        Assert.Equal("refreshed-access", handler.LastRequest!.Headers.Authorization!.Parameter);
    }

    private static GatewayAiStudioService CreateService(
        HttpMessageHandler handler,
        StubSessionStore store,
        IAuthenticationService? authentication = null) => new(
        new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) },
        store,
        authentication ?? new StubAuthenticationService(store),
        new(new Uri("https://gateway.example/")));

    private static AuthSessionSecrets Session(string accessToken) =>
        new(accessToken, "fake-refresh-token", DateTimeOffset.UtcNow.AddHours(1));

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response(request));
        }
    }

    private sealed class StubSessionStore(AuthSessionSecrets? session) : ISecureSessionStore
    {
        public AuthSessionSecrets? Session { get; set; } = session;
        public Task<AuthSessionSecrets?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Session);
        public Task SaveAsync(AuthSessionSecrets value, CancellationToken cancellationToken = default)
        { Session = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken cancellationToken = default)
        { Session = null; return Task.CompletedTask; }
    }

    private sealed class StubAuthenticationService(StubSessionStore store) : IAuthenticationService
    {
        public int RefreshCount { get; private set; }
        public Task<AuthenticationResult> RefreshSessionAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            store.Session = Session("refreshed-access");
            return Task.FromResult(AuthenticationResult.Success(null, null));
        }
        public Task<AuthenticationResult> SignUpAsync(AuthEmail email, AuthPassword password,
            CancellationToken cancellationToken = default) => Unsupported();
        public Task<AuthenticationResult> SignInAsync(AuthEmail email, AuthPassword password,
            CancellationToken cancellationToken = default) => Unsupported();
        public Task<AuthenticationResult> SignOutAsync(CancellationToken cancellationToken = default) => Unsupported();
        public Task<AuthenticationResult> ResetPasswordAsync(AuthEmail email,
            CancellationToken cancellationToken = default) => Unsupported();
        public Task<AuthenticationResult> GetProfileAsync(CancellationToken cancellationToken = default) => Unsupported();
        private static Task<AuthenticationResult> Unsupported() => Task.FromResult(
            AuthenticationResult.Failure(AuthenticationFailureReason.Unavailable, "TEST_UNAVAILABLE"));
    }
}
