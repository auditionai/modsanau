using System.Collections.Immutable;
using System.Globalization;

namespace AuditionModStudio.Gateway.Services;

public readonly record struct AiPricingVersion
{
    public AiPricingVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 || !value.All(character => char.IsAsciiLetterOrDigit(character)
                                               || character is '-' or '_' or '.'))
        {
            throw new ArgumentException("Pricing version is invalid.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct AiCreditPrice
{
    public const long MaximumCredits = 1_000_000_000;

    public AiCreditPrice(long value)
    {
        if (value is <= 0 or > MaximumCredits) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public long Value { get; }
}

public sealed record AiPricingQuote(
    TrustedAiOperation Operation,
    AiCreditPrice CreditCost,
    AiPricingVersion PricingVersion,
    DateTimeOffset EffectiveAt);

public enum AiPricingStatus
{
    Succeeded,
    PriceChanged,
    Rejected,
    Unavailable
}

public sealed record AiPricingResult(
    AiPricingStatus Status,
    string DiagnosticCode,
    AiPricingQuote? Quote);

public interface IAiPricingService
{
    Task<AiPricingResult> QuoteAsync(
        AuthenticatedGatewayUser user,
        TrustedAiOperation operation,
        AiPricingVersion? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableAiPricingService : IAiPricingService
{
    public Task<AiPricingResult> QuoteAsync(
        AuthenticatedGatewayUser user,
        TrustedAiOperation operation,
        AiPricingVersion? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiPricingResult(AiPricingStatus.Unavailable,
            "AI_PRICING_UNAVAILABLE", null));
}

public sealed class ConfiguredAiPricingService(
    AiPricingCatalog catalog,
    TimeProvider timeProvider) : IAiPricingService
{
    public Task<AiPricingResult> QuoteAsync(
        AuthenticatedGatewayUser user,
        TrustedAiOperation operation,
        AiPricingVersion? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (user.UserId == Guid.Empty || !Enum.IsDefined(operation))
        {
            return Task.FromResult(new AiPricingResult(AiPricingStatus.Rejected,
                "AI_PRICING_REQUEST_INVALID", null));
        }

        if (timeProvider.GetUtcNow() < catalog.EffectiveAt
            || !catalog.Prices.TryGetValue(operation, out var price))
        {
            return Task.FromResult(new AiPricingResult(AiPricingStatus.Unavailable,
                "AI_PRICING_UNAVAILABLE", null));
        }

        var quote = new AiPricingQuote(operation, price, catalog.Version, catalog.EffectiveAt);
        return Task.FromResult(expectedVersion is not null && expectedVersion.Value != catalog.Version
            ? new AiPricingResult(AiPricingStatus.PriceChanged, "AI_PRICE_CHANGED", quote)
            : new AiPricingResult(AiPricingStatus.Succeeded, "AI_PRICE_QUOTED", quote));
    }
}

public sealed class AiPricingCatalog
{
    private AiPricingCatalog(
        AiPricingVersion version,
        DateTimeOffset effectiveAt,
        ImmutableDictionary<TrustedAiOperation, AiCreditPrice> prices)
    {
        Version = version;
        EffectiveAt = effectiveAt;
        Prices = prices;
    }

    public AiPricingVersion Version { get; }
    public DateTimeOffset EffectiveAt { get; }
    public ImmutableDictionary<TrustedAiOperation, AiCreditPrice> Prices { get; }

    public static bool TryFromConfiguration(IConfiguration configuration, out AiPricingCatalog? catalog)
    {
        catalog = null;
        var section = configuration.GetSection("Gateway:AiPricing");
        try
        {
            var version = new AiPricingVersion(section["Version"]!);
            if (!DateTimeOffset.TryParseExact(section["EffectiveAt"], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var effectiveAt))
            {
                return false;
            }

            var builder = ImmutableDictionary.CreateBuilder<TrustedAiOperation, AiCreditPrice>();
            foreach (var operation in Enum.GetValues<TrustedAiOperation>())
            {
                var value = section.GetSection("Credits")[operation.ToString()];
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var credits))
                {
                    return false;
                }
                builder.Add(operation, new AiCreditPrice(credits));
            }

            catalog = new AiPricingCatalog(version, effectiveAt.ToUniversalTime(), builder.ToImmutable());
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

}
