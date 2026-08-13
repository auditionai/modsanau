using System.Net;
using AuditionModStudio.Core.Archives;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.Immutable;
using System.Security.Cryptography;
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
        using var client = SecureClient(factory);

        var responses = await Task.WhenAll(
            client.GetAsync("/v1/credits"),
            client.GetAsync("/v1/account"),
            client.PostAsJsonAsync("/v1/templates/entitlement", ValidTemplate()),
            client.PostAsJsonAsync("/v1/entitlements/grants", new EntitlementGrantRequest(
                PremiumEntitlementScope.PremiumAi, null, null, null)),
            client.GetAsync("/v1/premium-templates/catalog"),
            client.PostAsJsonAsync("/v1/premium-templates/access", new PremiumTemplateAccessApiRequest(
                "pointer", "v1", "audition", "pointer_mod")),
            client.PostAsJsonAsync("/v1/ai/generate", new AiGatewayRequest("prompt", 1, 1, null, null)),
            PricingQuoteAsync(client),
            JobEnqueueAsync(client),
            client.GetAsync("/v1/ai/content/output_1"));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(0, factory.Ai.CallCount);
        Assert.Equal(0, factory.Credits.CallCount);
        Assert.Equal(0, factory.Account.CallCount);
        Assert.Equal(0, factory.Entitlement.CallCount);
        Assert.Equal(0, factory.Grants.CallCount);
        Assert.Equal(0, factory.Distribution.CallCount);
        Assert.Equal(0, factory.Pricing.CallCount);
        Assert.Equal(0, factory.Jobs.EnqueueCallCount);
    }

    [Fact]
    public async Task Content_upload_uses_verified_owner_and_returns_opaque_private_identity()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        using var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.ContentType = new("image/png");

        var response = await client.PostAsync("/v1/ai/content/source", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserId, factory.Content.LastOwner!.UserId);
        Assert.Equal(AiContentKind.SourceImage, factory.Content.LastKind);
        Assert.Contains("content_1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Content_download_enforces_owner_and_expected_output_kind()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        factory.Content.OutputOwner = Guid.NewGuid();

        var crossOwner = await client.GetAsync("/v1/ai/content/output_1");
        factory.Content.OutputOwner = UserId;
        var owned = await client.GetAsync("/v1/ai/content/output_1");

        Assert.Equal(HttpStatusCode.NotFound, crossOwner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owned.StatusCode);
        Assert.Equal("image/png", owned.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 9, 8, 7 }, await owned.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Job_enqueue_uses_verified_owner_and_has_no_client_charge_authority()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);

        var response = await JobEnqueueAsync(client);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserId, factory.Jobs.LastUser!.UserId);
        Assert.Equal(TrustedAiOperation.Generate, factory.Jobs.LastOperation);
        Assert.Equal("request-1", factory.Jobs.LastIdempotencyKey!.Value.Value);
        Assert.DoesNotContain("providerRequestId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"operation\":\"Generate\",\"inputMetadata\":{\"inputReference\":\"inputs/1\",\"promptLength\":1,\"inputBytes\":0},\"idempotencyKey\":\"one\",\"cost\":1}")]
    [InlineData("{\"operation\":\"Generate\",\"inputMetadata\":{\"inputReference\":\"../secret\",\"promptLength\":1,\"inputBytes\":0},\"idempotencyKey\":\"one\"}")]
    [InlineData("{\"operation\":0,\"inputMetadata\":{\"inputReference\":\"inputs/1\",\"promptLength\":1,\"inputBytes\":0},\"idempotencyKey\":\"one\"}")]
    [InlineData("{\"operation\":\"Outpaint\",\"inputMetadata\":{\"inputReference\":\"content_1\",\"promptLength\":6,\"targetWidth\":512,\"targetHeight\":512,\"inputBytes\":10,\"publicOptionId\":\"standard\",\"prompt\":\"expand\",\"sourceWidth\":1024,\"sourceHeight\":1024},\"idempotencyKey\":\"one\"}")]
    public async Task Job_enqueue_rejects_client_cost_unsafe_metadata_and_numeric_operation(string payload)
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/v1/ai/jobs", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Jobs.EnqueueCallCount);
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
    public async Task Account_snapshot_uses_verified_identity_and_exposes_only_read_only_bounded_history()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);

        var response = await client.GetAsync("/v1/account?userId=" + Guid.NewGuid());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal(UserId, factory.Account.LastUser!.UserId);
        Assert.Contains("person@example.com", body, StringComparison.Ordinal);
        Assert.Contains("\"availableCredits\":10", body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"capture\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("authorityReference", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inconsistent_account_snapshot_is_rejected_without_fabricating_balance()
    {
        await using var factory = new GatewayFactory();
        factory.Account.Result = factory.Account.Result with
        {
            Snapshot = factory.Account.Result.Snapshot! with { AvailableCredits = 999 },
        };
        using var client = AuthenticatedClient(factory);

        var response = await client.GetAsync("/v1/account");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("999", body, StringComparison.Ordinal);
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
    public async Task Grant_issuance_uses_verified_user_and_server_selected_scope_audience()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);

        var response = await client.PostAsync("/v1/entitlements/grants", new StringContent(
            "{\"scope\":\"PremiumTemplate\",\"template\":{\"templateId\":\"pointer\",\"version\":\"v1\","
            + $"\"sha256\":\"{new string('A', 64)}\",\"compatibleGameBuild\":\"build-1\"}},"
            + "\"gameId\":\"audition\",\"modId\":\"pointer_mod\"}",
            System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserId, factory.Grants.LastUser!.UserId);
        Assert.Equal(EntitlementGrantDescriptor.TemplateAudience, factory.Grants.LastDescriptor!.Audience);
        Assert.Equal("pointer_mod", factory.Grants.LastDescriptor.ModId!.Value.Value);
    }

    [Fact]
    public async Task Premium_template_access_uses_verified_user_and_exact_typed_request()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/v1/premium-templates/access",
            new PremiumTemplateAccessApiRequest("pointer", "v1", "audition", "pointer_mod"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserId, factory.Distribution.LastUser!.UserId);
        Assert.Equal("pointer", factory.Distribution.LastRequest!.TemplateId.Value);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageReference", json, StringComparison.OrdinalIgnoreCase);
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
        using var client = SecureClient(factory);

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
            PricingQuoteAsync(client),
            JobEnqueueAsync(client));

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

    [Fact]
    public async Task Cleartext_is_rejected_without_redirecting_bearer_or_body()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("HTTPS_REQUIRED", body, StringComparison.Ordinal);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Unsupported_json_media_type_and_oversized_body_fail_before_trusted_service()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);
        using var wrongType = new StringContent("{}", System.Text.Encoding.UTF8, "text/plain");
        using var oversized = new ByteArrayContent(new byte[21 * 1_024]);
        oversized.Headers.ContentType = new("application/json");

        var wrongTypeResponse = await client.PostAsync("/v1/ai/jobs", wrongType);
        var oversizedResponse = await client.PostAsync("/v1/ai/jobs", oversized);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongTypeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);
        Assert.Equal(0, factory.Jobs.EnqueueCallCount);
    }

    [Fact]
    public async Task Ip_and_charged_operation_limits_reject_without_queue_or_duplicate_execution()
    {
        await using var ipFactory = new GatewayFactory { PreAuthenticationPermitLimit = 2 };
        using var anonymousClient = SecureClient(ipFactory);
        var ipResponses = new[]
        {
            await anonymousClient.GetAsync("/health"),
            await anonymousClient.GetAsync("/health"),
            await anonymousClient.GetAsync("/health"),
        };

        await using var chargeFactory = new GatewayFactory { ChargedOperationPermitLimit = 1 };
        using var authenticatedClient = AuthenticatedClient(chargeFactory);
        var firstCharge = await JobEnqueueAsync(authenticatedClient);
        var limitedCharge = await JobEnqueueAsync(authenticatedClient);

        Assert.Equal(HttpStatusCode.OK, ipResponses[0].StatusCode);
        Assert.Equal(HttpStatusCode.OK, ipResponses[1].StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, ipResponses[2].StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstCharge.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedCharge.StatusCode);
        Assert.Equal(1, chargeFactory.Jobs.EnqueueCallCount);
        Assert.True(limitedCharge.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task Security_audit_uses_route_template_and_subject_fingerprint_without_secrets()
    {
        await using var factory = new GatewayFactory();
        using var client = AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("audit-secret-prompt", 1, 1, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(factory.Logs.Messages, message =>
            message.Contains("Gateway security audit POST /v1/ai/generate 200", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Messages, message =>
            message.Contains("access-token", StringComparison.Ordinal)
            || message.Contains("audit-secret-prompt", StringComparison.Ordinal)
            || message.Contains(UserId.ToString("D"), StringComparison.Ordinal));
    }

    private static HttpClient AuthenticatedClient(GatewayFactory factory)
    {
        var client = SecureClient(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "access-token");
        return client;
    }

    private static HttpClient SecureClient(GatewayFactory factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static Task<HttpResponseMessage> PricingQuoteAsync(HttpClient client)
    {
        var content = new StringContent("{\"operation\":\"Generate\"}",
            System.Text.Encoding.UTF8, "application/json");
        return client.PostAsync("/v1/ai/pricing/quote", content);
    }

    private static Task<HttpResponseMessage> JobEnqueueAsync(HttpClient client)
    {
        var content = new StringContent(
            "{\"operation\":\"Generate\",\"inputMetadata\":{\"inputReference\":\"inputs/request-1\",\"promptLength\":6,\"targetWidth\":512,\"targetHeight\":512,\"inputBytes\":0},\"idempotencyKey\":\"request-1\"}",
            System.Text.Encoding.UTF8, "application/json");
        return client.PostAsync("/v1/ai/jobs", content);
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
        public bool RequireHttps { get; init; } = true;
        public int PreAuthenticationPermitLimit { get; init; } = 120;
        public int AuthenticatedPermitLimit { get; init; } = 60;
        public int ChargedOperationPermitLimit { get; init; } = 10;
        public StubAiGateway Ai { get; } = new();
        public StubCreditService Credits { get; } = new();
        public StubAccountService Account { get; } = new();
        public StubEntitlementService Entitlement { get; } = new();
        public StubGrantService Grants { get; } = new();
        public StubPremiumTemplateDistribution Distribution { get; } = new();
        public StubPricingService Pricing { get; } = new();
        public StubJobService Jobs { get; } = new();
        public StubContentStore Content { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:AbuseProtection:RequireHttps", RequireHttps.ToString());
            builder.UseSetting("Gateway:AbuseProtection:PreAuthenticationPermitLimit",
                PreAuthenticationPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Gateway:AbuseProtection:AuthenticatedPermitLimit",
                AuthenticatedPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Gateway:AbuseProtection:ChargedOperationPermitLimit",
                ChargedOperationPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                    services.RemoveAll<ITrustedAccountQueryService>();
                    services.RemoveAll<ITrustedTemplateEntitlementService>();
                    services.RemoveAll<IEntitlementGrantService>();
                    services.RemoveAll<IPremiumTemplateDistributionService>();
                    services.RemoveAll<IAiPricingService>();
                    services.RemoveAll<IAiJobService>();
                    services.RemoveAll<IAiContentStore>();
                    services.RemoveAll<IAiMediaValidator>();
                    services.AddSingleton<ITrustedAiGateway>(Ai);
                    services.AddSingleton<ITrustedCreditQueryService>(Credits);
                    services.AddSingleton<ITrustedAccountQueryService>(Account);
                    services.AddSingleton<ITrustedTemplateEntitlementService>(Entitlement);
                    services.AddSingleton<IEntitlementGrantService>(Grants);
                    services.AddSingleton<IPremiumTemplateDistributionService>(Distribution);
                    services.AddSingleton<IAiPricingService>(Pricing);
                    services.AddSingleton<IAiJobService>(Jobs);
                    services.AddSingleton<IAiContentStore>(Content);
                    services.AddSingleton<IAiMediaValidator, StubMediaValidator>();
                }
            });
        }

    }

    private sealed class StubMediaValidator : IAiMediaValidator
    {
        public Task<AiValidatedMedia?> ValidateAsync(string mediaType, ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) => Task.FromResult<AiValidatedMedia?>(
            new(mediaType, 1, 1, ImmutableArray.CreateRange(bytes.ToArray())));
    }

    private sealed class StubContentStore : IAiContentStore
    {
        private static readonly ImmutableArray<byte> Output = ImmutableArray.Create<byte>(9, 8, 7);
        public Guid OutputOwner { get; set; } = UserId;
        public AuthenticatedGatewayUser? LastOwner { get; private set; }
        public AiContentKind? LastKind { get; private set; }
        public Task<AiContentStoreResult> PutAsync(AuthenticatedGatewayUser owner, AiContentKind kind,
            AiValidatedMedia media, CancellationToken cancellationToken = default)
        {
            LastOwner = owner;
            LastKind = kind;
            var metadata = Metadata(new("content_1"), owner.UserId, kind, media.MediaType,
                media.Width, media.Height, media.Bytes);
            return Task.FromResult(new AiContentStoreResult(true, "AI_CONTENT_STORED", metadata));
        }
        public Task<AiStoredContent?> GetAsync(AuthenticatedGatewayUser owner, AiContentId contentId,
            AiContentKind expectedKind, CancellationToken cancellationToken = default)
        {
            if (owner.UserId != OutputOwner || contentId.Value != "output_1"
                || expectedKind != AiContentKind.ProviderOutput) return Task.FromResult<AiStoredContent?>(null);
            return Task.FromResult<AiStoredContent?>(new(Metadata(contentId, owner.UserId,
                AiContentKind.ProviderOutput, "image/png", 1, 1, Output), Output));
        }
        public Task<bool> DeleteAsync(AuthenticatedGatewayUser owner, AiContentId contentId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
        private static AiContentMetadata Metadata(AiContentId id, Guid owner, AiContentKind kind,
            string mediaType, int width, int height, ImmutableArray<byte> bytes) => new(id, owner, kind,
            mediaType, width, height, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes.AsSpan())),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class StubJobService : IAiJobService
    {
        private static readonly AiJobSnapshot Job = new(Guid.Parse("7f1ded48-8a4a-44e9-914d-8bfbf260d69d"),
            TrustedAiOperation.Generate, AiJobStatus.Queued,
            new("inputs/request-1", 6, 512, 512, 0), 7, null, "2026.08.12", null,
            "provider-internal", null, 0, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        public int EnqueueCallCount { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public TrustedAiOperation? LastOperation { get; private set; }
        public AiJobIdempotencyKey? LastIdempotencyKey { get; private set; }

        public Task<AiJobOperationResult> EnqueueAsync(
            AuthenticatedGatewayUser user, TrustedAiOperation operation, AiJobInputMetadata metadata,
            AiJobIdempotencyKey idempotencyKey, AiPricingVersion? expectedPricingVersion = null,
            CancellationToken cancellationToken = default)
        {
            EnqueueCallCount++;
            LastUser = user;
            LastOperation = operation;
            LastIdempotencyKey = idempotencyKey;
            return Task.FromResult(new AiJobOperationResult(AiJobOperationStatus.Succeeded,
                "AI_JOB_QUEUED", Job));
        }

        public Task<AiJobOperationResult> GetAsync(
            AuthenticatedGatewayUser user, Guid jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiJobOperationResult(AiJobOperationStatus.Succeeded, "AI_JOB_READ", Job));

        public Task<AiJobListResult> ListAsync(
            AuthenticatedGatewayUser user, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiJobListResult(AiJobOperationStatus.Succeeded, "AI_JOB_LISTED", [Job]));

        public Task<AiJobOperationResult> CancelAsync(
            AuthenticatedGatewayUser user, Guid jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiJobOperationResult(AiJobOperationStatus.Succeeded,
                "AI_JOB_CANCELLED", Job with { Status = AiJobStatus.Cancelled }));
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
                ? AccessTokenValidationResult.Valid(userId, "person@example.com", "Person")
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

    private sealed class StubAccountService : ITrustedAccountQueryService
    {
        private static readonly TrustedAccountTransaction Item = new(
            Guid.Parse("f56b54be-06c0-49e0-a1be-139e8017192f"),
            "capture", 2, 0, -2, 10, 0, DateTimeOffset.UnixEpoch);
        public TrustedAccountResult Result { get; set; } = new(TrustedServiceStatus.Succeeded, "ACCOUNT_READ",
            new(10, 0, 12, 2, 1, [Item], DateTimeOffset.UnixEpoch));
        public int CallCount { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public Task<TrustedAccountResult> GetAsync(AuthenticatedGatewayUser user,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUser = user;
            return Task.FromResult(Result);
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

    private sealed class StubGrantService : IEntitlementGrantService
    {
        public int CallCount { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public EntitlementGrantDescriptor? LastDescriptor { get; private set; }
        public Task<EntitlementGrantResult> IssueAsync(AuthenticatedGatewayUser user,
            EntitlementGrantDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            CallCount++; LastUser = user; LastDescriptor = descriptor;
            return Task.FromResult(new EntitlementGrantResult(TrustedServiceStatus.Succeeded,
                "ENTITLEMENT_GRANT_ISSUED", "header.payload.signature"));
        }
        public Task<EntitlementGrantResult> ValidateAndConsumeAsync(string grant,
            AuthenticatedGatewayUser expectedUser, EntitlementGrantDescriptor expectedDescriptor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubPremiumTemplateDistribution : IPremiumTemplateDistributionService
    {
        public int CallCount { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public PremiumTemplateAccessRequest? LastRequest { get; private set; }
        public Task<PremiumTemplateAccessResult> AuthorizeAsync(AuthenticatedGatewayUser user,
            PremiumTemplateAccessRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++; LastUser = user; LastRequest = request;
            var manifest = new PremiumTemplatePackageManifest(
                new(new("pointer"), new("v1"), new(new string('A', 64)), new("build-1")),
                new("audition"), new("pointer_mod"), 4096,
                PremiumTemplatePackageManifest.PackageMediaType);
            return Task.FromResult(new PremiumTemplateAccessResult(TrustedServiceStatus.Succeeded,
                "PREMIUM_TEMPLATE_ACCESS_AUTHORIZED", manifest, Convert.ToBase64String(new byte[64]),
                new("https://storage.invalid/package?sig=short"),
                new DateTimeOffset(2026, 8, 12, 12, 1, 0, TimeSpan.Zero)));
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
