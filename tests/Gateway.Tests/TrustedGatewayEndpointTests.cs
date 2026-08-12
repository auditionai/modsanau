using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuditionModStudio.Gateway.Authentication;
using AuditionModStudio.Gateway.Endpoints;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Gateway.Tests;

public sealed class TrustedGatewayEndpointTests
{
    private static readonly Guid UserId = Guid.Parse("2be3bead-4ba1-475d-b620-5b2dd21bcb5e");

    [Fact]
    public async Task Commercial_endpoints_require_a_bearer_token()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            client.GetAsync("/v1/credits"),
            client.PostAsJsonAsync("/v1/templates/entitlement", ValidTemplate()),
            client.PostAsJsonAsync("/v1/ai/generate", new AiGatewayRequest("prompt", 1, 1, null, null)),
            PricingQuoteAsync(client));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(0, factory.Ai.CallCount);
        Assert.Equal(0, factory.Credits.CallCount);
        Assert.Equal(0, factory.Entitlement.CallCount);
        Assert.Equal(0, factory.Pricing.CallCount);
    }

    [Fact]
    public async Task Pricing_quote_uses_verified_user_and_server_catalog_without_credit_mutation()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);

        var response = await PricingQuoteAsync(client);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"creditCost\":7", body, StringComparison.Ordinal);
        Assert.Contains("\"pricingVersion\":\"2026.08.12\"", body, StringComparison.Ordinal);
        Assert.Equal(UserId, factory.Pricing.LastUser!.UserId);
        Assert.Equal(TrustedAiOperation.Generate, factory.Pricing.LastOperation);
        Assert.Null(factory.Pricing.LastExpectedVersion);
        Assert.Equal(0, factory.Credits.CallCount);
    }

    [Fact]
    public async Task Stale_pricing_version_returns_conflict_with_current_server_quote()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        using var content = new StringContent(
            "{\"operation\":\"Generate\",\"expectedPricingVersion\":\"old\"}",
            System.Text.Encoding.UTF8, "application/json");
        factory.Pricing.Result = factory.Pricing.Result with
        {
            Status = AiPricingStatus.PriceChanged,
            DiagnosticCode = "AI_PRICE_CHANGED",
        };

        var response = await client.PostAsync("/v1/ai/pricing/quote", content);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("old", factory.Pricing.LastExpectedVersion!.Value.Value);
        Assert.Equal(0, factory.Credits.CallCount);
    }

    [Theory]
    [InlineData("{\"operation\":0}")]
    [InlineData("{\"operation\":\"Generate\",\"cost\":0}")]
    [InlineData("{\"operation\":\"Generate\",\"provider\":\"client\"}")]
    [InlineData("null")]
    public async Task Pricing_rejects_numeric_enum_client_authority_and_null_payloads(string payload)
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/v1/ai/pricing/quote", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Pricing.CallCount);
        Assert.Equal(0, factory.Credits.CallCount);
    }

    [Fact]
    public async Task Invalid_token_is_rejected_before_the_trusted_service()
    {
        await using var factory = new GatewayFactory { TokenStatus = AccessTokenValidationStatus.Invalid };
        using var client = AuthenticatedClient(factory);

        var response = await client.GetAsync("/v1/credits");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Credits.CallCount);
    }

    [Fact]
    public async Task Auth_validation_outage_returns_service_unavailable_without_running_service()
    {
        await using var factory = new GatewayFactory { TokenStatus = AccessTokenValidationStatus.Unavailable };
        using var client = AuthenticatedClient(factory);

        var response = await client.GetAsync("/v1/credits");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, factory.Credits.CallCount);
    }

    [Fact]
    public async Task Verified_subject_is_the_only_user_identity_passed_to_credit_service()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);

        var response = await client.GetAsync("/v1/credits");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserId, factory.Credits.LastUser!.UserId);
        Assert.Equal(1, factory.Credits.CallCount);
    }

    [Fact]
    public async Task Unknown_or_operation_incompatible_ai_schema_is_rejected_before_service()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        using var unknown = new StringContent(
            "{\"prompt\":\"hello\",\"targetWidth\":1,\"targetHeight\":1,\"clientCost\":0}",
            System.Text.Encoding.UTF8,
            "application/json");

        var unknownResponse = await client.PostAsync("/v1/ai/generate", unknown);
        var incompatibleResponse = await client.PostAsJsonAsync("/v1/ai/remove-object",
            new AiGatewayRequest("client prompt is forbidden for remove", null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, incompatibleResponse.StatusCode);
        Assert.Equal(0, factory.Ai.CallCount);
    }

    [Fact]
    public async Task Null_body_unsupported_image_signature_and_extraneous_fields_are_rejected()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        using var nullBody = new StringContent("null", System.Text.Encoding.UTF8, "application/json");
        var unsupported = Convert.ToBase64String([1, 2, 3, 4]);
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var nullResponse = await client.PostAsync("/v1/ai/generate", nullBody);
        var unsupportedResponse = await client.PostAsJsonAsync("/v1/ai/edit",
            new AiGatewayRequest("prompt", null, null, unsupported, null));
        var extraResponse = await client.PostAsJsonAsync("/v1/ai/edit",
            new AiGatewayRequest("prompt", 100, 100, png, null));

        Assert.Equal(HttpStatusCode.BadRequest, nullResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, extraResponse.StatusCode);
        Assert.Equal(0, factory.Ai.CallCount);
    }

    [Fact]
    public async Task All_seven_ai_routes_map_to_typed_operations_for_verified_user()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        var image = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var cases = new (string Route, AiGatewayRequest Request, TrustedAiOperation Operation)[]
        {
            ("generate", new("prompt", 1, 1, null, null), TrustedAiOperation.Generate),
            ("edit", new("prompt", null, null, image, null), TrustedAiOperation.Edit),
            ("inpaint", new("prompt", null, null, image, image), TrustedAiOperation.Inpaint),
            ("outpaint", new("prompt", 2, 3, image, null), TrustedAiOperation.Outpaint),
            ("remove-object", new(null, null, null, image, image), TrustedAiOperation.RemoveObject),
            ("replace-object", new("prompt", null, null, image, image), TrustedAiOperation.ReplaceObject),
            ("upscale", new(null, 2, 3, image, null), TrustedAiOperation.Upscale),
        };

        foreach (var item in cases)
        {
            var response = await client.PostAsJsonAsync($"/v1/ai/{item.Route}", item.Request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(item.Operation, factory.Ai.LastRequest!.Operation);
            Assert.Equal(UserId, factory.Ai.LastUser!.UserId);
        }

        Assert.Equal(7, factory.Ai.CallCount);
    }

    [Fact]
    public async Task Entitlement_uses_exact_validated_template_identity_and_verified_user()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/v1/templates/entitlement", ValidTemplate());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserId, factory.Entitlement.LastUser!.UserId);
        Assert.Equal("pointer", factory.Entitlement.LastIdentity!.TemplateId.Value);
        Assert.Equal("v1", factory.Entitlement.LastIdentity.Version.Value);
    }

    [Fact]
    public async Task Client_cannot_mutate_credit_or_send_client_authority_fields()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        var payload = "{\"balance\":999999,\"cost\":0,\"refund\":true,\"paymentSucceeded\":true}";
        var methods = new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete };
        var responses = await Task.WhenAll(methods.Select(async method =>
        {
            using var request = new HttpRequestMessage(method, "/v1/credits")
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
            return await client.SendAsync(request);
        }));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode));
        Assert.Equal(0, factory.Credits.CallCount);
    }

    [Fact]
    public async Task Health_is_the_only_anonymous_route()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Production_services_fail_closed_when_backend_capabilities_are_not_configured()
    {
        await using var factory = new GatewayFactory { ReplaceTrustedServices = false };
        using var client = AuthenticatedClient(factory);

        var responses = await Task.WhenAll(
            client.GetAsync("/v1/credits"),
            client.PostAsJsonAsync("/v1/templates/entitlement", ValidTemplate()),
            client.PostAsJsonAsync("/v1/ai/generate", new AiGatewayRequest("prompt", 1, 1, null, null)),
            PricingQuoteAsync(client));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode));
    }

    [Fact]
    public async Task Untrusted_service_output_is_not_reflected_to_the_client()
    {
        await using var factory = new GatewayFactory();
        factory.Ai.Result = new(TrustedServiceStatus.Succeeded,
            "provider leaked error: provider-secret", "https://signed.example/?secret=value");
        using var client = AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("GATEWAY_RESPONSE_INVALID", body, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("signed.example", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_exception_message_is_neither_returned_nor_logged()
    {
        await using var factory = new GatewayFactory();
        factory.Ai.Exception = new InvalidOperationException("provider-secret-response");
        using var client = AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("provider-secret-response", body, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Messages,
            message => message.Contains("provider-secret-response", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Messages,
            message => message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    }

    private static HttpClient AuthenticatedClient(GatewayFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "access-token");
        return client;
    }

    private static Task<HttpResponseMessage> PricingQuoteAsync(HttpClient client)
    {
        var content = new StringContent("{\"operation\":\"Generate\"}",
            System.Text.Encoding.UTF8, "application/json");
        return client.PostAsync("/v1/ai/pricing/quote", content);
    }

    private static TemplateEntitlementRequest ValidTemplate() => new(
        "pointer",
        "v1",
        new string('A', 64),
        "build-1");

    private sealed class GatewayFactory : WebApplicationFactory<Program>
    {
        public AccessTokenValidationStatus TokenStatus { get; init; } = AccessTokenValidationStatus.Valid;
        public bool ReplaceTrustedServices { get; init; } = true;
        public StubAiGateway Ai { get; } = new();
        public StubCreditService Credits { get; } = new();
        public StubEntitlementService Entitlement { get; } = new();
        public StubPricingService Pricing { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISupabaseAccessTokenValidator>();
                services.AddSingleton<ISupabaseAccessTokenValidator>(
                    new StubTokenValidator(TokenStatus, UserId));
                if (ReplaceTrustedServices)
                {
                    services.RemoveAll<ITrustedAiGateway>();
                    services.RemoveAll<ITrustedCreditQueryService>();
                    services.RemoveAll<ITrustedTemplateEntitlementService>();
                    services.RemoveAll<IAiPricingService>();
                    services.AddSingleton<ITrustedAiGateway>(Ai);
                    services.AddSingleton<ITrustedCreditQueryService>(Credits);
                    services.AddSingleton<ITrustedTemplateEntitlementService>(Entitlement);
                    services.AddSingleton<IAiPricingService>(Pricing);
                }
            });
        }
    }

    private sealed class StubPricingService : IAiPricingService
    {
        public AiPricingResult Result { get; set; } = new(AiPricingStatus.Succeeded,
            "AI_PRICE_QUOTED", new(TrustedAiOperation.Generate, new AiCreditPrice(7),
                new AiPricingVersion("2026.08.12"), DateTimeOffset.UnixEpoch));
        public int CallCount { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public TrustedAiOperation? LastOperation { get; private set; }
        public AiPricingVersion? LastExpectedVersion { get; private set; }

        public Task<AiPricingResult> QuoteAsync(
            AuthenticatedGatewayUser user,
            TrustedAiOperation operation,
            AiPricingVersion? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUser = user;
            LastOperation = operation;
            LastExpectedVersion = expectedVersion;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubTokenValidator(AccessTokenValidationStatus status, Guid userId)
        : ISupabaseAccessTokenValidator
    {
        public Task<AccessTokenValidationResult> ValidateAsync(
            string accessToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(status == AccessTokenValidationStatus.Valid
                ? AccessTokenValidationResult.Valid(userId, "person@example.com")
                : status == AccessTokenValidationStatus.Invalid
                    ? AccessTokenValidationResult.Invalid()
                    : AccessTokenValidationResult.Unavailable());
    }

    private sealed class StubAiGateway : ITrustedAiGateway
    {
        public TrustedAiResult Result { get; set; } = new(TrustedServiceStatus.Succeeded,
            "AI_COMPLETED", "outputs/result");
        public Exception? Exception { get; set; }
        public int CallCount { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public TrustedAiRequest? LastRequest { get; private set; }

        public Task<TrustedAiResult> ExecuteAsync(
            AuthenticatedGatewayUser user,
            TrustedAiRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUser = user;
            LastRequest = request;
            if (Exception is not null) throw Exception;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubCreditService : ITrustedCreditQueryService
    {
        public int CallCount { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }

        public Task<TrustedCreditResult> GetAsync(
            AuthenticatedGatewayUser user,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUser = user;
            return Task.FromResult(new TrustedCreditResult(TrustedServiceStatus.Succeeded,
                "CREDIT_READ", new(10, 2)));
        }
    }

    private sealed class StubEntitlementService : ITrustedTemplateEntitlementService
    {
        public int CallCount { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public AuditionModStudio.Core.Archives.TemplateIdentity? LastIdentity { get; private set; }

        public Task<TrustedTemplateEntitlementResult> CheckAsync(
            AuthenticatedGatewayUser user,
            AuditionModStudio.Core.Archives.TemplateIdentity identity,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUser = user;
            LastIdentity = identity;
            return Task.FromResult(new TrustedTemplateEntitlementResult(TrustedServiceStatus.Succeeded,
                "ENTITLEMENT_CHECKED", true));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
        public IReadOnlyCollection<string> Messages => _messages.ToArray();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
        public void Dispose() { }

        private sealed class CapturingLogger(
            System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var rendered = formatter(state, exception);
                messages.Enqueue(exception is null ? rendered : $"{rendered} {exception}");
            }
        }
    }
}
