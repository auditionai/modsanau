using System.Security.Claims;
using AuditionModStudio.Gateway.Security;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace AuditionModStudio.Gateway.Endpoints;

public sealed record GiftCodeRedemptionRequest(string GiftCode, Guid CorrelationId);

public static class DeviceEntitlementEndpoints
{
    public static void MapDeviceEntitlementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/device/register", (ClaimsPrincipal principal,
            ITrustedDeviceEntitlementService service, CancellationToken token) =>
            MapAsync(service.RegisterAsync(User(principal), token)))
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new SkipDeviceSessionAuthorizationMetadata());
        endpoints.MapGet("/v1/device/entitlements", (ClaimsPrincipal principal,
            ITrustedDeviceEntitlementService service, CancellationToken token) =>
            MapAsync(service.RefreshAsync(User(principal), token)))
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);
        endpoints.MapPost("/v1/gift-codes/redeem", (ClaimsPrincipal principal,
            GiftCodeRedemptionRequest request, ITrustedDeviceEntitlementService service, CancellationToken token) =>
            MapAsync(service.RedeemAsync(User(principal), request.GiftCode, request.CorrelationId, token)))
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.ChargedOperationPolicy)
            .WithMetadata(new SkipDeviceSessionAuthorizationMetadata());
    }

    private static async Task<IResult> MapAsync(Task<TrustedDeviceEntitlementResult> operation)
    {
        var result = await operation.ConfigureAwait(false);
        return result.Status switch
        {
            TrustedServiceStatus.Succeeded when result.Snapshot is not null => Results.Ok(result.Snapshot),
            TrustedServiceStatus.Rejected => Results.UnprocessableEntity(new GatewayErrorResponse(result.DiagnosticCode)),
            _ => Results.Problem(statusCode: 503, title: "Device entitlement service is unavailable.",
                extensions: new Dictionary<string, object?> { ["code"] = result.DiagnosticCode }),
        };
    }
    private static AuthenticatedGatewayUser User(ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
}
