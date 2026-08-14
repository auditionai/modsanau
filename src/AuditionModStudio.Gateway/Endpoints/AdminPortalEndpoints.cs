using System.Globalization;
using System.Security.Claims;
using AuditionModStudio.Gateway.Security;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Primitives;

namespace AuditionModStudio.Gateway.Endpoints;

public sealed record AdminDeviceStatusRequest(string? Reason, Guid CorrelationId);
public sealed record AdminSubscriptionExtendRequest(int ExtensionDays, string? Reason, Guid CorrelationId);
public sealed record AdminCreditsGrantRequest(Guid TargetUserId, long Amount, string AuthorityReference,
    string? Reason, Guid CorrelationId);
public sealed record AdminGiftCodeCreateRequest(string Kind, int? DurationDays, long? CreditAmount,
    int MaximumRedemptions, DateTimeOffset? ExpiresAt, string? Reason, Guid CorrelationId);
public sealed record AdminGiftCodeRevokeRequest(string? Reason, Guid CorrelationId);
public sealed record AdminBootstrapRequest(string? Email, string? DisplayName);

public static class AdminPortalEndpoints
{
    private const long MaximumBootstrapBytes = 4 * 1024;
    private const long MaximumMutationBytes = 8 * 1024;
    private const long MaximumGiftCodeBytes = 8 * 1024;

    public static void MapAdminPortalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/admin/bootstrap/state", async (ClaimsPrincipal principal,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            MapBootstrapState(await service.GetBootstrapStateAsync(User(principal),
                principal.FindFirstValue(ClaimTypes.Email), cancellationToken)
                .ConfigureAwait(false)))
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapPost("/v1/admin/bootstrap", async (ClaimsPrincipal principal,
                AdminBootstrapRequest? request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                if (request is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = ["Admin bootstrap request is invalid."],
                    });

                return MapBootstrapState(await service.BootstrapAsync(User(principal), request.Email,
                    request.DisplayName, cancellationToken).ConfigureAwait(false));
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumBootstrapBytes));

        endpoints.MapGet("/v1/admin/dashboard", async (ClaimsPrincipal principal,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            MapDashboard(await service.GetDashboardAsync(User(principal), 12, 12, 12, 12, cancellationToken)
                .ConfigureAwait(false)))
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapGet("/v1/admin/devices", async (ClaimsPrincipal principal,
                HttpRequest request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                var query = request.Query["query"].ToString();
                var status = request.Query["status"].ToString();
                var limit = ParseInt(request.Query["limit"], 25, 1, 100);
                var offset = ParseInt(request.Query["offset"], 0, 0, 1_000_000);
                return MapDevices(await service.SearchDevicesAsync(User(principal), query, status, limit, offset,
                    cancellationToken).ConfigureAwait(false));
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapGet("/v1/admin/devices/{deviceProfileId:guid}", async (
                ClaimsPrincipal principal,
                Guid deviceProfileId,
                HttpRequest request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                var activityLimit = ParseInt(request.Query["activityLimit"], 8, 1, 50);
                var redemptionLimit = ParseInt(request.Query["redemptionLimit"], 8, 1, 50);
                var snapshot = await service.GetDeviceAsync(User(principal), deviceProfileId, activityLimit,
                    redemptionLimit, cancellationToken).ConfigureAwait(false);
                return snapshot is null ? Results.NotFound(new GatewayErrorResponse("ADMIN_DEVICE_NOT_FOUND"))
                    : Results.Ok(snapshot);
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapPost("/v1/admin/devices/{deviceProfileId:guid}/block", MutateDeviceAsync)
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));

        endpoints.MapPost("/v1/admin/devices/{deviceProfileId:guid}/unblock", MutateDeviceAsync)
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));

        endpoints.MapPost("/v1/admin/devices/{deviceProfileId:guid}/revoke", MutateDeviceAsync)
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));

        endpoints.MapPost("/v1/admin/devices/{deviceProfileId:guid}/subscription/extend", async (
                ClaimsPrincipal principal,
                Guid deviceProfileId,
                AdminSubscriptionExtendRequest? request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                if (request is null || request.CorrelationId == Guid.Empty)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = ["Subscription extension request is invalid."],
                    });

                var result = await service.ExtendSubscriptionAsync(User(principal), deviceProfileId,
                    request.ExtensionDays, request.Reason ?? string.Empty, request.CorrelationId, cancellationToken)
                    .ConfigureAwait(false);
                return MapMutation(result);
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));

        endpoints.MapPost("/v1/admin/credits/grant", async (ClaimsPrincipal principal,
                AdminCreditsGrantRequest? request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                if (request is null || request.CorrelationId == Guid.Empty)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = ["Credits grant request is invalid."],
                    });

                var result = await service.GrantCreditsAsync(User(principal), request.TargetUserId, request.Amount,
                    request.AuthorityReference, request.Reason ?? string.Empty, request.CorrelationId,
                    cancellationToken).ConfigureAwait(false);
                return MapMutation(result);
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));

        endpoints.MapGet("/v1/admin/gift-codes", async (ClaimsPrincipal principal,
                HttpRequest request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                var limit = ParseInt(request.Query["limit"], 25, 1, 100);
                var offset = ParseInt(request.Query["offset"], 0, 0, 1_000_000);
                return MapGiftCodes(await service.ListGiftCodesAsync(User(principal), limit, offset, cancellationToken)
                    .ConfigureAwait(false));
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapPost("/v1/admin/gift-codes", async (ClaimsPrincipal principal,
                AdminGiftCodeCreateRequest? request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                if (request is null || request.CorrelationId == Guid.Empty)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = ["Gift code request is invalid."],
                    });

                var result = await service.CreateGiftCodeAsync(User(principal), request.Kind, request.DurationDays,
                    request.CreditAmount, request.MaximumRedemptions, request.ExpiresAt, request.Reason ?? string.Empty,
                    request.CorrelationId, cancellationToken).ConfigureAwait(false);
                return MapMutation(result);
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumGiftCodeBytes));

        endpoints.MapPost("/v1/admin/gift-codes/{giftCodeId:guid}/revoke", async (
                ClaimsPrincipal principal,
                Guid giftCodeId,
                AdminGiftCodeRevokeRequest? request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                if (request is null || request.CorrelationId == Guid.Empty)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = ["Gift code revoke request is invalid."],
                    });

                var result = await service.RevokeGiftCodeAsync(User(principal), giftCodeId, request.Reason ?? string.Empty,
                    request.CorrelationId, cancellationToken).ConfigureAwait(false);
                return MapMutation(result);
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));

        endpoints.MapGet("/v1/admin/gift-codes/{giftCodeId:guid}/redemptions", async (
                ClaimsPrincipal principal,
                Guid giftCodeId,
                HttpRequest request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                var limit = ParseInt(request.Query["limit"], 12, 1, 100);
                var items = await service.ListGiftCodeRedemptionsAsync(User(principal), giftCodeId, limit, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(new { Items = items });
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapGet("/v1/admin/audit", async (ClaimsPrincipal principal,
                HttpRequest request,
                IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                var limit = ParseInt(request.Query["limit"], 25, 1, 100);
                var offset = ParseInt(request.Query["offset"], 0, 0, 1_000_000);
                return MapAudit(await service.ListAuditAsync(User(principal), limit, offset, cancellationToken)
                    .ConfigureAwait(false));
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);
    }

    private static Task<IResult> MutateDeviceAsync(
        ClaimsPrincipal principal,
        Guid deviceProfileId,
        HttpRequest request,
        IAdminPortalService service,
        CancellationToken cancellationToken)
    {
        var payload = request.Path.Value?.EndsWith("/block", StringComparison.Ordinal) == true ? "blocked"
            : request.Path.Value?.EndsWith("/unblock", StringComparison.Ordinal) == true ? "active"
            : "revoked";
        return MapMutationAsync(service.SetDeviceStatusAsync(User(principal), deviceProfileId, payload,
            request.Headers["X-Admin-Reason"].ToString(), GetCorrelationId(request), cancellationToken));
    }

    private static async Task<IResult> MapMutationAsync(Task<AdminMutationResult> operation)
    {
        var result = await operation.ConfigureAwait(false);
        return MapMutation(result);
    }

    private static IResult MapMutation(AdminMutationResult result) =>
        result.Status switch
        {
            TrustedServiceStatus.Succeeded => Results.Ok(new
            {
                result.DiagnosticCode,
                result.Device,
                result.GiftCode,
            }),
            TrustedServiceStatus.Rejected => Results.Json(new GatewayErrorResponse(result.DiagnosticCode),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Admin service is unavailable.",
                extensions: new Dictionary<string, object?> { ["code"] = result.DiagnosticCode }),
        };

    private static IResult MapDevices(AdminDevicePageResult result) =>
        result.Status switch
        {
            TrustedServiceStatus.Succeeded => Results.Ok(new
            {
                result.DiagnosticCode,
                result.TotalCount,
                result.Items,
            }),
            TrustedServiceStatus.Rejected => Results.Json(new GatewayErrorResponse(result.DiagnosticCode),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Admin device query is unavailable.",
                extensions: new Dictionary<string, object?> { ["code"] = result.DiagnosticCode }),
        };

    private static IResult MapGiftCodes(AdminGiftCodePageResult result) =>
        result.Status switch
        {
            TrustedServiceStatus.Succeeded => Results.Ok(new
            {
                result.DiagnosticCode,
                result.TotalCount,
                result.Items,
            }),
            TrustedServiceStatus.Rejected => Results.Json(new GatewayErrorResponse(result.DiagnosticCode),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Admin gift code query is unavailable.",
                extensions: new Dictionary<string, object?> { ["code"] = result.DiagnosticCode }),
        };

    private static IResult MapDashboard(AdminDashboardSnapshot result) =>
        Results.Ok(new
        {
            result.Summary,
            result.Devices,
            result.GiftCodes,
            result.RecentAudit,
            result.RecentDeviceActivity,
        });

    private static IResult MapAudit(AdminAuditPageResult result) =>
        result.Status switch
        {
            TrustedServiceStatus.Succeeded => Results.Ok(new
            {
                result.DiagnosticCode,
                result.Items,
            }),
            TrustedServiceStatus.Rejected => Results.Json(new GatewayErrorResponse(result.DiagnosticCode),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Admin audit query is unavailable.",
                extensions: new Dictionary<string, object?> { ["code"] = result.DiagnosticCode }),
        };

    private static IResult MapBootstrapState(AdminBootstrapResult result) =>
        result.Status switch
        {
            TrustedServiceStatus.Succeeded => Results.Ok(new
            {
                result.DiagnosticCode,
                result.BootstrapConfigured,
                result.Bootstrapped,
                Admin = result.Admin,
                CanBootstrap = result.BootstrapConfigured && result.Admin is null,
            }),
            TrustedServiceStatus.Rejected => Results.Json(new GatewayErrorResponse(result.DiagnosticCode),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Admin bootstrap state is unavailable.",
                extensions: new Dictionary<string, object?> { ["code"] = result.DiagnosticCode }),
        };

    private static int ParseInt(StringValues value, int fallback, int minimum, int maximum) =>
        int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed >= minimum && parsed <= maximum ? parsed : fallback;

    private static Guid GetCorrelationId(HttpRequest request) =>
        Guid.TryParse(request.Headers["X-Admin-Correlation-Id"].ToString(), out var correlationId)
            ? correlationId : Guid.NewGuid();

    private static AuthenticatedGatewayUser User(ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
}
