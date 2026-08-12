using System.Collections.Immutable;
using AuditionModStudio.Core.Archives;

namespace AuditionModStudio.Gateway.Services;

public sealed record AuthenticatedGatewayUser(Guid UserId);

public enum TrustedAiOperation
{
    Generate,
    Edit,
    Inpaint,
    Outpaint,
    RemoveObject,
    ReplaceObject,
    Upscale
}

public sealed record TrustedAiRequest(
    TrustedAiOperation Operation,
    string? Prompt,
    int? TargetWidth,
    int? TargetHeight,
    ImmutableArray<byte> Image,
    ImmutableArray<byte> Mask);

public enum TrustedServiceStatus
{
    Succeeded,
    Rejected,
    Unavailable
}

public sealed record TrustedAiResult(TrustedServiceStatus Status, string DiagnosticCode, string? OutputReference);

public interface ITrustedAiGateway
{
    Task<TrustedAiResult> ExecuteAsync(
        AuthenticatedGatewayUser user,
        TrustedAiRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TrustedCreditSnapshot(long AvailableCredits, long ReservedCredits);
public sealed record TrustedCreditResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    TrustedCreditSnapshot? Snapshot);

public interface ITrustedCreditQueryService
{
    Task<TrustedCreditResult> GetAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default);
}

public sealed record TrustedTemplateEntitlementResult(
    TrustedServiceStatus Status,
    string DiagnosticCode,
    bool Granted);

public interface ITrustedTemplateEntitlementService
{
    Task<TrustedTemplateEntitlementResult> CheckAsync(
        AuthenticatedGatewayUser user,
        TemplateIdentity identity,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableTrustedAiGateway : ITrustedAiGateway
{
    public Task<TrustedAiResult> ExecuteAsync(
        AuthenticatedGatewayUser user,
        TrustedAiRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TrustedAiResult(TrustedServiceStatus.Unavailable,
            "AI_PROVIDER_UNAVAILABLE", null));
}

public sealed class UnavailableTrustedTemplateEntitlementService : ITrustedTemplateEntitlementService
{
    public Task<TrustedTemplateEntitlementResult> CheckAsync(
        AuthenticatedGatewayUser user,
        TemplateIdentity identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TrustedTemplateEntitlementResult(TrustedServiceStatus.Unavailable,
            "ENTITLEMENT_SERVICE_UNAVAILABLE", false));
}
