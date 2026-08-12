using System.Net;
using System.Text;
using AuditionModStudio.Gateway;
using AuditionModStudio.Gateway.Authentication;

namespace Gateway.Tests;

public sealed class SupabaseAccessTokenValidatorTests
{
    private static readonly Guid UserId = Guid.Parse("d0347dba-1ec7-46fd-ac98-117670484414");

    [Fact]
    public async Task Validator_sends_token_only_to_exact_supabase_auth_user_endpoint()
    {
        RequestSnapshot? captured = null;
        var client = new StubClient((request, _) =>
        {
            captured = new(request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Headers.GetValues("apikey").Single());
            return Task.FromResult(Json(HttpStatusCode.OK,
                $$"""{"id":"{{UserId}}","email":"person@example.com"}"""));
        });
        var validator = Create(client);

        var result = await validator.ValidateAsync("header.payload.signature");

        Assert.Equal(AccessTokenValidationStatus.Valid, result.Status);
        Assert.Equal(UserId, result.UserId);
        Assert.Equal("https://project.example/auth/v1/user", captured!.Uri.ToString());
        Assert.Equal("Bearer header.payload.signature", captured.Authorization);
        Assert.Equal("publishable-key", captured.ApiKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AccessTokenValidationStatus.Invalid)]
    [InlineData(HttpStatusCode.Forbidden, AccessTokenValidationStatus.Invalid)]
    [InlineData(HttpStatusCode.InternalServerError, AccessTokenValidationStatus.Unavailable)]
    public async Task Validator_fails_closed_for_auth_rejection_or_outage(
        HttpStatusCode statusCode,
        AccessTokenValidationStatus expected)
    {
        var validator = Create(new StubClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode))));

        var result = await validator.ValidateAsync("header.payload.signature");

        Assert.Equal(expected, result.Status);
        Assert.Equal(Guid.Empty, result.UserId);
    }

    [Fact]
    public async Task Invalid_configuration_or_malformed_token_never_runs_http()
    {
        var client = new StubClient((_, _) => throw new InvalidOperationException("HTTP must not run"));
        var invalidConfig = new SupabaseAccessTokenValidator(client, new TrustedGatewayOptions());
        var validConfig = Create(client);

        var configurationResult = await invalidConfig.ValidateAsync("header.payload.signature");
        var tokenResult = await validConfig.ValidateAsync("invalid bearer token");

        Assert.Equal(AccessTokenValidationStatus.Unavailable, configurationResult.Status);
        Assert.Equal(AccessTokenValidationStatus.Invalid, tokenResult.Status);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Oversized_or_malformed_success_response_is_not_trusted()
    {
        var oversized = Create(new StubClient((_, _) => Task.FromResult(Json(
            HttpStatusCode.OK, new string('x', 70 * 1_024)))));
        var malformed = Create(new StubClient((_, _) => Task.FromResult(Json(
            HttpStatusCode.OK, "{\"id\":\"not-a-guid\"}"))));

        var oversizedResult = await oversized.ValidateAsync("header.payload.signature");
        var malformedResult = await malformed.ValidateAsync("header.payload.signature");

        Assert.Equal(AccessTokenValidationStatus.Unavailable, oversizedResult.Status);
        Assert.Equal(AccessTokenValidationStatus.Invalid, malformedResult.Status);
    }

    [Fact]
    public void Gateway_options_never_render_server_credentials()
    {
        var options = Options();

        Assert.DoesNotContain("provider-secret", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("publishable-key", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("project.example", options.ToString(), StringComparison.Ordinal);
    }

    private static SupabaseAccessTokenValidator Create(ISupabaseAuthClient client) => new(client, Options());

    private static TrustedGatewayOptions Options() => new()
    {
        SupabaseProjectUri = new("https://project.example"),
        SupabasePublishableKey = "publishable-key",
        ProviderEndpoint = new("https://provider.example/v1"),
        ProviderApiKey = "provider-secret",
    };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record RequestSnapshot(Uri Uri, string? Authorization, string ApiKey);

    private sealed class StubClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : ISupabaseAuthClient
    {
        private int _callCount;
        public int CallCount => _callCount;

        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return responder(request, cancellationToken);
        }
    }
}
