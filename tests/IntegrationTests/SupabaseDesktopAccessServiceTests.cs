using System.Net;
using System.Text;
using AuditionModStudio.Cloud;
using AuditionModStudio.Core.Auth;

namespace IntegrationTests;

public sealed class SupabaseDesktopAccessServiceTests
{
    [Fact]
    public async Task First_access_registers_random_device_without_sending_user_id()
    {
        string? body = null;
        string? authorization = null;
        var sessionId = Guid.NewGuid();
        var handler = new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            authorization = request.Headers.Authorization?.ToString();
            var deviceId = System.Text.Json.JsonDocument.Parse(body).RootElement
                .GetProperty("payload").GetProperty("deviceId").GetGuid();
            return Json($$"""{"deviceId":"{{deviceId}}","sessionId":"{{sessionId}}","publicDeviceCode":"AMS-2345-6789-ABCD","deviceStatus":"active","lastSeenAt":"2026-08-15T00:00:00Z"}""");
        });
        var sessions = new SessionStore { Value = new("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1)) };
        var bindings = new BindingStore();
        var service = Create(handler, sessions, bindings);

        var result = await service.EnsureAccessAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("Bearer access-token", authorization);
        Assert.Contains("\"action\":\"register\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("userId", body, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(bindings.Value);
        Assert.Equal(sessionId, bindings.Value!.SessionId);
    }

    [Fact]
    public async Task Existing_binding_is_validated_and_preserved()
    {
        var deviceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        string? body = null;
        var handler = new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json($$"""{"deviceId":"{{deviceId}}","sessionId":"{{sessionId}}","publicDeviceCode":"AMS-2345-6789-ABCD","deviceStatus":"active","lastSeenAt":"2026-08-15T00:00:00Z"}""");
        });
        var sessions = new SessionStore { Value = new("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1)) };
        var bindings = new BindingStore { Value = new(deviceId, sessionId) };
        var service = Create(handler, sessions, bindings);

        var result = await service.EnsureAccessAsync();

        Assert.True(result.Succeeded);
        Assert.Contains("\"action\":\"validate\"", body, StringComparison.Ordinal);
        Assert.Contains(sessionId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_auth_session_fails_closed_without_http()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("HTTP must not run"));
        var service = Create(handler, new SessionStore(), new BindingStore());

        var result = await service.EnsureAccessAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("AUTH_SESSION_MISSING", result.DiagnosticCode);
        Assert.Equal(0, handler.Count);
    }

    private static SupabaseDesktopAccessService Create(Handler handler, SessionStore sessions, BindingStore bindings) =>
        new(new HttpClient(handler), sessions, bindings,
            new SupabaseAuthOptions(new Uri("https://project.example"), "publishable-key"));

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return send(request);
        }
    }

    private sealed class SessionStore : ISecureSessionStore
    {
        public AuthSessionSecrets? Value { get; set; }
        public Task<AuthSessionSecrets?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(AuthSessionSecrets session, CancellationToken cancellationToken = default) { Value = session; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }

    private sealed class BindingStore : IDeviceSessionBindingStore
    {
        public DeviceSessionBinding? Value { get; set; }
        public Task<DeviceSessionBinding?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(DeviceSessionBinding binding, CancellationToken cancellationToken = default) { Value = binding; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }
}
