using System.Net;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Cloud;
using AuditionModStudio.Core.Auth;

namespace IntegrationTests;

public sealed class SupabaseAuthServiceTests
{
    private static readonly Guid UserId = Guid.Parse("1c57ba02-f23b-4523-a56f-a133c6992c42");

    [Fact]
    public async Task Sign_in_uses_supabase_auth_route_and_persists_rotatable_session()
    {
        RequestSnapshot? captured = null;
        var handler = new StubHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(Json(HttpStatusCode.OK, SessionJson("access-1", "refresh-1")));
        });
        var store = new MemorySessionStore();
        using var service = Create(handler, store);

        var result = await service.SignInAsync(new("person@example.com"), new("password-value"));

        Assert.True(result.Succeeded);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://project.example/auth/v1/token?grant_type=password", captured.Uri.ToString());
        Assert.Equal("publishable-key", captured.ApiKey);
        Assert.Null(captured.Authorization);
        Assert.Contains("\"password\":\"password-value\"", captured.Body, StringComparison.Ordinal);
        Assert.Equal("access-1", store.Session!.AccessToken);
        Assert.Equal("refresh-1", store.Session.RefreshToken);
    }

    [Fact]
    public async Task Sign_up_accepts_email_confirmation_response_without_claiming_a_session()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                id = UserId,
                email = "person@example.com",
                user_metadata = new { display_name = "Person" },
            }))));
        var store = new MemorySessionStore();
        using var service = Create(handler, store);

        var result = await service.SignUpAsync(new("person@example.com"), new("password-value"));

        Assert.True(result.Succeeded);
        Assert.Null(result.Session);
        Assert.Equal("Person", result.Profile!.DisplayName);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Concurrent_refreshes_are_serialized_and_each_uses_the_latest_rotated_token()
    {
        var active = 0;
        var maximumActive = 0;
        var bodies = new List<string>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            var nowActive = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, nowActive);
            lock (bodies)
            {
                bodies.Add(request.Body);
            }
            await Task.Delay(25, cancellationToken);
            Interlocked.Decrement(ref active);
            var suffix = request.Body.Contains("refresh-1", StringComparison.Ordinal) ? "2" : "3";
            return Json(HttpStatusCode.OK, SessionJson($"access-{suffix}", $"refresh-{suffix}"));
        });
        var store = new MemorySessionStore
        {
            Session = new("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)),
        };
        using var service = Create(handler, store);

        var results = await Task.WhenAll(service.RefreshSessionAsync(), service.RefreshSessionAsync());

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, maximumActive);
        Assert.Contains(bodies, body => body.Contains("refresh-1", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("refresh-2", StringComparison.Ordinal));
        Assert.Equal("refresh-3", store.Session!.RefreshToken);
    }

    [Fact]
    public async Task Sign_out_deletes_local_session_only_after_remote_success()
    {
        var store = StoreWithSession();
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("Bearer access-1", request.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var service = Create(handler, store);

        var result = await service.SignOutAsync();

        Assert.True(result.Succeeded);
        Assert.Null(store.Session);
        Assert.Equal(1, store.DeleteCount);
    }

    [Fact]
    public async Task Rejected_sign_out_preserves_local_session_for_retry()
    {
        var store = StoreWithSession();
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var service = Create(handler, store);

        var result = await service.SignOutAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(AuthenticationFailureReason.Rejected, result.FailureReason);
        Assert.NotNull(store.Session);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public async Task Profile_uses_bearer_session_and_maps_metadata_without_mutating_storage()
    {
        var store = StoreWithSession();
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://project.example/auth/v1/user", request.Uri.ToString());
            Assert.Equal("Bearer access-1", request.Authorization);
            return Task.FromResult(Json(HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    id = UserId,
                    email = "person@example.com",
                    user_metadata = new { full_name = "Profile Name" },
                })));
        });
        using var service = Create(handler, store);

        var result = await service.GetProfileAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(UserId, result.Profile!.UserId);
        Assert.Equal("Profile Name", result.Profile.DisplayName);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Password_reset_uses_recover_route_without_creating_a_session()
    {
        RequestSnapshot? captured = null;
        var store = new MemorySessionStore();
        var handler = new StubHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var service = Create(handler, store);

        var result = await service.ResetPasswordAsync(new("person@example.com"));

        Assert.True(result.Succeeded);
        Assert.Equal("https://project.example/auth/v1/recover", captured!.Uri.ToString());
        Assert.Contains("person@example.com", captured.Body, StringComparison.Ordinal);
        Assert.Null(store.Session);
    }

    [Fact]
    public async Task Invalid_configuration_and_cancellation_fail_closed_as_typed_results()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("HTTP must not run"));
        using var invalid = new SupabaseAuthService(
            new HttpClient(handler), new MemorySessionStore(), new(new("http://project.example"), "key"));
        var unavailable = new UnavailableAuthenticationService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var invalidResult = await invalid.SignInAsync(new("person@example.com"), new("password-value"));
        var cancelledResult = await unavailable.GetProfileAsync(cancellation.Token);

        Assert.Equal(AuthenticationFailureReason.InvalidRequest, invalidResult.FailureReason);
        Assert.Equal("AUTH_CONFIGURATION_INVALID", invalidResult.DiagnosticCode);
        Assert.True(cancelledResult.Cancelled);
        Assert.Equal(AuthenticationFailureReason.Cancelled, cancelledResult.FailureReason);
    }

    [Fact]
    public async Task Secure_store_failure_is_reported_without_sending_a_request()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("HTTP must not run"));
        var store = new MemorySessionStore { LoadException = new IOException("credential failure") };
        using var service = Create(handler, store);

        var result = await service.GetProfileAsync();

        Assert.Equal(AuthenticationFailureReason.SecureStorageFailed, result.FailureReason);
        Assert.Equal("AUTH_SECURE_STORAGE_LOAD_FAILED", result.DiagnosticCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Pre_cancelled_refresh_does_not_enter_http_or_throw_from_the_session_gate()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("HTTP must not run"));
        using var service = Create(handler, StoreWithSession());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.RefreshSessionAsync(cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal("AUTH_CANCELLED", result.DiagnosticCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void Configuration_string_representation_does_not_disclose_client_configuration()
    {
        var options = new SupabaseAuthOptions(new("https://project.example"), "publishable-key");

        Assert.DoesNotContain("project.example", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("publishable-key", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_success_response_is_rejected_as_a_protocol_error()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(
            HttpStatusCode.OK, new string('x', 70 * 1_024))));
        var store = new MemorySessionStore();
        using var service = Create(handler, store);

        var result = await service.SignInAsync(new("person@example.com"), new("password-value"));

        Assert.Equal(AuthenticationFailureReason.ProtocolError, result.FailureReason);
        Assert.Equal("AUTH_SESSION_RESPONSE_INVALID", result.DiagnosticCode);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Malformed_stored_bearer_token_is_rejected_before_http()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("HTTP must not run"));
        var store = new MemorySessionStore
        {
            Session = new("invalid bearer token", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)),
        };
        using var service = Create(handler, store);

        var result = await service.GetProfileAsync();

        Assert.Equal(AuthenticationFailureReason.SecureStorageFailed, result.FailureReason);
        Assert.Equal("AUTH_SECURE_STORAGE_CONTENT_INVALID", result.DiagnosticCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Sign_out_waits_for_in_flight_refresh_and_removes_the_rotated_session()
    {
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.Uri.Query.Contains("refresh_token", StringComparison.Ordinal))
            {
                refreshEntered.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return Json(HttpStatusCode.OK, SessionJson("access-2", "refresh-2"));
            }

            Assert.Equal("Bearer access-2", request.Authorization);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var store = StoreWithSession();
        using var service = Create(handler, store);

        var refresh = service.RefreshSessionAsync();
        await refreshEntered.Task;
        var signOut = service.SignOutAsync();
        releaseRefresh.SetResult();
        var results = await Task.WhenAll(refresh, signOut);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Null(store.Session);
        Assert.Equal(1, store.DeleteCount);
    }

    private static SupabaseAuthService Create(StubHandler handler, MemorySessionStore store) =>
        new(new HttpClient(handler), store, new(new("https://project.example"), "publishable-key"));

    private static MemorySessionStore StoreWithSession() => new()
    {
        Session = new("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)),
    };

    private static string SessionJson(string accessToken, string refreshToken) =>
        JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            refresh_token = refreshToken,
            expires_in = 3600,
            user = new
            {
                id = UserId,
                email = "person@example.com",
                user_metadata = new { },
            },
        });

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        string? ApiKey,
        string? Authorization,
        string Body);

    private sealed class StubHandler(
        Func<RequestSnapshot, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var snapshot = new RequestSnapshot(
                request.Method,
                request.RequestUri!,
                request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null,
                request.Headers.Authorization?.ToString(),
                body);
            return await response(snapshot, cancellationToken);
        }
    }

    private sealed class MemorySessionStore : ISecureSessionStore
    {
        private readonly object _sync = new();
        public AuthSessionSecrets? Session { get; set; }
        public Exception? LoadException { get; init; }
        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<AuthSessionSecrets?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadException is not null)
            {
                throw LoadException;
            }
            lock (_sync)
            {
                return Task.FromResult(Session);
            }
        }

        public Task SaveAsync(AuthSessionSecrets session, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Session = session;
                SaveCount++;
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Session = null;
                DeleteCount++;
            }
            return Task.CompletedTask;
        }
    }
}
