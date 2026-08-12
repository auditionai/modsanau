using AuditionModStudio.Gateway;
using AuditionModStudio.Gateway.Services;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

public sealed class AiPricingTests
{
    private static readonly AuthenticatedGatewayUser User = new(Guid.Parse(
        "2d8ad9bd-b31c-4f0e-8733-a2aad7ed6e48"));

    [Fact]
    public async Task All_operations_have_deterministic_positive_integer_prices()
    {
        using var provider = BuildProvider(CompleteConfiguration());
        var service = provider.GetRequiredService<IAiPricingService>();

        foreach (var operation in Enum.GetValues<TrustedAiOperation>())
        {
            var first = await service.QuoteAsync(User, operation);
            var second = await service.QuoteAsync(User, operation);

            Assert.Equal(AiPricingStatus.Succeeded, first.Status);
            Assert.Equal(first, second);
            Assert.InRange(first.Quote!.CreditCost.Value, 1, AiCreditPrice.MaximumCredits);
            Assert.Equal("2026.08.12", first.Quote.PricingVersion.Value);
        }
    }

    [Fact]
    public async Task Stale_version_returns_current_quote()
    {
        using var provider = BuildProvider(CompleteConfiguration());
        var service = provider.GetRequiredService<IAiPricingService>();

        var result = await service.QuoteAsync(User, TrustedAiOperation.Generate,
            new AiPricingVersion("old-version"));

        Assert.Equal(AiPricingStatus.PriceChanged, result.Status);
        Assert.Equal("AI_PRICE_CHANGED", result.DiagnosticCode);
        Assert.Equal("2026.08.12", result.Quote!.PricingVersion.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000000001")]
    [InlineData("1.5")]
    public void Invalid_or_fractional_credit_configuration_fails_closed(string value)
    {
        var configuration = CompleteConfiguration();
        configuration["Gateway:AiPricing:Credits:Generate"] = value;

        using var provider = BuildProvider(configuration);

        Assert.IsType<UnavailableAiPricingService>(provider.GetRequiredService<IAiPricingService>());
    }

    [Fact]
    public async Task Invalid_user_operation_and_future_catalog_fail_closed()
    {
        using var provider = BuildProvider(CompleteConfiguration(
            DateTimeOffset.UtcNow.AddDays(1).ToString("O")));
        var service = provider.GetRequiredService<IAiPricingService>();

        var invalidUser = await service.QuoteAsync(new AuthenticatedGatewayUser(Guid.Empty),
            TrustedAiOperation.Generate);
        var invalidOperation = await service.QuoteAsync(User, (TrustedAiOperation)999);
        var future = await service.QuoteAsync(User, TrustedAiOperation.Generate);

        Assert.Equal(AiPricingStatus.Rejected, invalidUser.Status);
        Assert.Equal(AiPricingStatus.Rejected, invalidOperation.Status);
        Assert.Equal(AiPricingStatus.Unavailable, future.Status);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_quote_publication()
    {
        using var provider = BuildProvider(CompleteConfiguration());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IAiPricingService>().QuoteAsync(User,
                TrustedAiOperation.Generate, cancellationToken: cancellation.Token));
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        GatewayApplication.ConfigureServices(services, configuration);
        return services.BuildServiceProvider();
    }

    private static IConfigurationRoot CompleteConfiguration(string? effectiveAt = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Gateway:AiPricing:Version"] = "2026.08.12",
            ["Gateway:AiPricing:EffectiveAt"] = effectiveAt ?? "2026-01-01T00:00:00.0000000+00:00",
        };
        var price = 1;
        foreach (var operation in Enum.GetValues<TrustedAiOperation>())
        {
            values[$"Gateway:AiPricing:Credits:{operation}"] =
                (price++).ToString(CultureInfo.InvariantCulture);
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
