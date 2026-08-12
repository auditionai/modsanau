using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Tests;

public sealed class PaymentSecurityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_786_435_200);
    private static readonly Guid UserId = Guid.Parse("e670aec2-b952-420e-93d3-cb14d750ce90");
    private const string Secret = "whsec_1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    [Fact]
    public void Exact_signed_raw_body_maps_only_server_catalog_product_to_credit()
    {
        var verifier = Verifier();
        var body = ValidPayload();

        var result = verifier.Verify(body, Signature(body, Now));

        Assert.Equal(PaymentWebhookStatus.Verified, result.Status);
        Assert.NotNull(result.Payment);
        Assert.Equal("stripe", result.Payment.Provider);
        Assert.Equal(UserId, result.Payment.UserId);
        Assert.Equal("credits.small", result.Payment.ProductId);
        Assert.Equal(499, result.Payment.AmountMinor);
        Assert.Equal("usd", result.Payment.Currency);
        Assert.Equal(50, result.Payment.Credits);
        Assert.Matches("^[0-9A-F]{64}$", result.Payment.PayloadSha256);
        Assert.Matches("^[0-9A-F]{64}$", result.Payment.GrantRequestSha256);
    }

    [Fact]
    public void Modified_body_wrong_secret_and_stale_timestamp_are_rejected()
    {
        var verifier = Verifier();
        var body = ValidPayload();
        var modified = body.Concat(" "u8.ToArray()).ToArray();

        Assert.Equal(PaymentWebhookStatus.Invalid,
            verifier.Verify(modified, Signature(body, Now)).Status);
        Assert.Equal(PaymentWebhookStatus.Invalid,
            verifier.Verify(body, Signature(body, Now, "whsec_wrong_1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ")).Status);
        Assert.Equal("PAYMENT_WEBHOOK_TIMESTAMP_INVALID",
            verifier.Verify(body, Signature(body, Now.AddMinutes(-6))).DiagnosticCode);
        Assert.Equal("PAYMENT_WEBHOOK_TIMESTAMP_INVALID",
            verifier.Verify(body, Signature(body, Now.AddMinutes(6))).DiagnosticCode);
    }

    [Theory]
    [InlineData("amount", 500, "usd", "credits.small", true)]
    [InlineData("currency", 499, "eur", "credits.small", true)]
    [InlineData("product", 499, "usd", "credits.unknown", true)]
    [InlineData("livemode", 499, "usd", "credits.small", false)]
    public void Provider_fields_cannot_choose_credit_or_cross_environment(
        string _, long amount, string currency, string product, bool liveMode)
    {
        var body = ValidPayload(amount, currency, product, liveMode: liveMode);

        var result = Verifier().Verify(body, Signature(body, Now));

        Assert.Equal(PaymentWebhookStatus.Invalid, result.Status);
        Assert.Null(result.Payment);
    }

    [Fact]
    public void Unrelated_signed_event_is_acknowledged_without_a_payment()
    {
        var body = ValidPayload(eventType: "customer.created");

        var result = Verifier().Verify(body, Signature(body, Now));

        Assert.Equal(PaymentWebhookStatus.Ignored, result.Status);
        Assert.Null(result.Payment);
    }

    [Fact]
    public void Signed_unpaid_checkout_is_acknowledged_without_granting_credit()
    {
        var body = ValidPayload(paymentStatus: "unpaid");

        var result = Verifier().Verify(body, Signature(body, Now));

        Assert.Equal(PaymentWebhookStatus.Ignored, result.Status);
        Assert.Equal("PAYMENT_EVENT_NOT_FULFILLABLE", result.DiagnosticCode);
        Assert.Null(result.Payment);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"id\":\"evt_plan84\"}")]
    [InlineData("{\"id\":\"evt_plan84\",\"type\":\"checkout.session.completed\",\"livemode\":true,\"data\":[]}")]
    public void Structurally_invalid_signed_json_is_rejected_without_throwing(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);

        var result = Verifier().Verify(body, Signature(body, Now));

        Assert.Equal(PaymentWebhookStatus.Invalid, result.Status);
        Assert.Null(result.Payment);
    }

    [Fact]
    public void Missing_configuration_fails_closed_and_options_never_print_secret()
    {
        var options = new StripeWebhookOptions { EndpointSecret = Secret, LiveMode = true };
        var unconfigured = new StripePaymentWebhookVerifier(new StripeWebhookOptions(), Catalog(),
            new FixedTimeProvider(Now));

        Assert.DoesNotContain(Secret, options.ToString(), StringComparison.Ordinal);
        Assert.Equal(PaymentWebhookStatus.Unavailable,
            unconfigured.Verify(ValidPayload(), "t=1,v1=" + new string('0', 64)).Status);
    }

    [Fact]
    public async Task Anonymous_webhook_is_signature_authenticated_and_applied_once()
    {
        var verifier = new StubVerifier(new(PaymentWebhookStatus.Verified, "PAYMENT_WEBHOOK_VERIFIED",
            VerifiedPayment()));
        var fulfillment = new StubFulfillment(new(PaymentApplyStatus.Applied, "PAYMENT_APPLIED"));
        await using var factory = new PaymentFactory(verifier, fulfillment);
        using var client = SecureClient(factory);
        using var request = Request("{}", "t=1,v1=" + new string('0', 64));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, verifier.CallCount);
        Assert.Equal(1, fulfillment.CallCount);
        Assert.Equal(VerifiedPayment(), fulfillment.LastPayment);
    }

    [Fact]
    public async Task Http_pipeline_preserves_exact_body_for_real_signature_verification()
    {
        var fulfillment = new StubFulfillment(new(PaymentApplyStatus.Applied, "PAYMENT_APPLIED"));
        await using var factory = new PaymentFactory(null, fulfillment);
        using var client = SecureClient(factory);
        var bytes = ValidPayload();
        using var request = Request(Encoding.UTF8.GetString(bytes), Signature(bytes, Now));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fulfillment.CallCount);
        Assert.Equal(UserId, fulfillment.LastPayment?.UserId);
        Assert.Equal(50, fulfillment.LastPayment?.Credits);
    }

    [Fact]
    public async Task Invalid_signature_never_reaches_fulfillment()
    {
        var verifier = new StubVerifier(new(PaymentWebhookStatus.Invalid,
            "PAYMENT_WEBHOOK_SIGNATURE_INVALID"));
        var fulfillment = new StubFulfillment(new(PaymentApplyStatus.Applied, "PAYMENT_APPLIED"));
        await using var factory = new PaymentFactory(verifier, fulfillment);
        using var client = SecureClient(factory);
        using var request = Request("{}", "invalid");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fulfillment.CallCount);
    }

    [Fact]
    public async Task Webhook_enforces_json_size_and_has_no_client_success_authority_route()
    {
        var verifier = new StubVerifier(new(PaymentWebhookStatus.Ignored, "PAYMENT_EVENT_IGNORED"));
        var fulfillment = new StubFulfillment(new(PaymentApplyStatus.Applied, "PAYMENT_APPLIED"));
        await using var factory = new PaymentFactory(verifier, fulfillment);
        using var client = SecureClient(factory);
        using var oversized = Request(new string('x', 128 * 1_024 + 1), "signature");
        using var wrongType = new HttpRequestMessage(HttpMethod.Post, "/v1/payments/webhooks/stripe")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };

        var oversizedResponse = await client.SendAsync(oversized);
        var wrongTypeResponse = await client.SendAsync(wrongType);
        var successScreenResponse = await client.GetAsync("/v1/payments/success");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongTypeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, successScreenResponse.StatusCode);
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal(0, fulfillment.CallCount);
    }

    [Fact]
    public void Migration_is_append_only_idempotent_and_server_only()
    {
        var sql = File.ReadAllText(MigrationPath());

        Assert.Contains("CREATE TABLE private.payment_events", sql, StringComparison.Ordinal);
        Assert.Contains("payment_events_provider_event_unique UNIQUE (provider, provider_event_id)", sql,
            StringComparison.Ordinal);
        Assert.Contains("payment_events_provider_payment_unique UNIQUE (provider, provider_payment_id)", sql,
            StringComparison.Ordinal);
        Assert.Contains("payment_events_append_only", sql, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", sql, StringComparison.Ordinal);
        Assert.Contains("PAYMENT_IDEMPOTENT_REPLAY", sql, StringComparison.Ordinal);
        Assert.Contains("PAYMENT_EVENT_CONFLICT", sql, StringComparison.Ordinal);
        Assert.Contains("FROM private.credit_grant", sql, StringComparison.Ordinal);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("FROM PUBLIC, anon, authenticated, service_role", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TO anon", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TO authenticated", sql, StringComparison.Ordinal);
        Assert.Equal(1, Count(sql, "GRANT EXECUTE ON FUNCTION private.payment_apply_verified"));
    }

    private static StripePaymentWebhookVerifier Verifier() => new(
        new StripeWebhookOptions { EndpointSecret = Secret, LiveMode = true },
        Catalog(), new FixedTimeProvider(Now));

    private static PaymentProductCatalog Catalog()
    {
        var values = new Dictionary<string, string?>
        {
            ["Gateway:Payments:Products:0:ProductId"] = "credits.small",
            ["Gateway:Payments:Products:0:AmountMinor"] = "499",
            ["Gateway:Payments:Products:0:Currency"] = "usd",
            ["Gateway:Payments:Products:0:Credits"] = "50",
        };
        return PaymentProductCatalog.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static byte[] ValidPayload(long amount = 499, string currency = "usd",
        string product = "credits.small", string eventType = "checkout.session.completed", bool liveMode = true,
        string paymentStatus = "paid") =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["id"] = "evt_plan84",
            ["type"] = eventType,
            ["livemode"] = liveMode,
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = new Dictionary<string, object?>
                {
                    ["id"] = "cs_live_plan84",
                    ["payment_status"] = paymentStatus,
                    ["mode"] = "payment",
                    ["client_reference_id"] = UserId.ToString("D"),
                    ["amount_total"] = amount,
                    ["currency"] = currency,
                    ["metadata"] = new Dictionary<string, object?> { ["credit_product_id"] = product },
                },
            },
        });

    private static string Signature(byte[] body, DateTimeOffset timestamp, string secret = Secret)
    {
        var seconds = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var payload = Encoding.UTF8.GetBytes(seconds + ".").Concat(body).ToArray();
        return $"t={seconds},v1={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant()}";
    }

    private static HttpRequestMessage Request(string body, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments/webhooks/stripe")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", signature);
        return request;
    }

    private static HttpClient SecureClient(WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static VerifiedPaymentEvent VerifiedPayment() => new("stripe", "evt_plan84", "cs_live_plan84",
        UserId, "credits.small", 499, "usd", 50, new string('A', 64), new string('B', 64));

    private static string MigrationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "supabase", "migrations",
            "202608120005_plan84_payment_security.sql");
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class StubVerifier(PaymentWebhookResult result) : IPaymentWebhookVerifier
    {
        public int CallCount { get; private set; }
        public PaymentWebhookResult Verify(ReadOnlySpan<byte> rawBody, string? signatureHeader)
        {
            CallCount++;
            return result;
        }
    }

    private sealed class StubFulfillment(PaymentApplyResult result) : IPaymentFulfillmentService
    {
        public int CallCount { get; private set; }
        public VerifiedPaymentEvent? LastPayment { get; private set; }
        public Task<PaymentApplyResult> ApplyAsync(VerifiedPaymentEvent payment,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPayment = payment;
            return Task.FromResult(result);
        }
    }

    private sealed class PaymentFactory(IPaymentWebhookVerifier? verifier, IPaymentFulfillmentService fulfillment)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:AbuseProtection:RequireHttps", "true");
            builder.UseSetting("Gateway:Payments:Stripe:WebhookSecret", Secret);
            builder.UseSetting("Gateway:Payments:Stripe:LiveMode", "true");
            builder.UseSetting("Gateway:Payments:Products:0:ProductId", "credits.small");
            builder.UseSetting("Gateway:Payments:Products:0:AmountMinor", "499");
            builder.UseSetting("Gateway:Payments:Products:0:Currency", "usd");
            builder.UseSetting("Gateway:Payments:Products:0:Credits", "50");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
                if (verifier is not null)
                {
                    services.RemoveAll<IPaymentWebhookVerifier>();
                    services.AddSingleton(verifier);
                }
                services.RemoveAll<IPaymentFulfillmentService>();
                services.AddSingleton(fulfillment);
            });
        }
    }
}
