using System.Globalization;
using System.Security.Claims;
using AuditionModStudio.Gateway.Security;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace AuditionModStudio.Gateway.Endpoints;

public sealed record AdminUserUpdateRequest(string? DisplayName, string? ContactEmail, string Status,
    string? InternalNote, string Reason, Guid CorrelationId);
public sealed record AdminPackageSaveRequest(Guid? PackageId, string ProductId, string DisplayName,
    string? Description, long AmountMinor, string Currency, long Credits, bool IsActive, int SortOrder,
    string Reason, Guid CorrelationId);
public sealed record AdminArchiveRequest(string Reason, Guid CorrelationId);
public sealed record AdminAccountUpdateRequest(string Role, bool IsActive, string MfaState,
    string? DisplayLabel, string Reason, Guid CorrelationId);

public static class AdminOperationsEndpoints
{
    private const long MaximumMutationBytes = 12 * 1024;

    public static void MapAdminOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/admin")
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        group.MapGet("/analytics", async (ClaimsPrincipal principal, HttpRequest request,
            IAdminOperationsService service, CancellationToken ct) =>
        {
            var result = await service.GetAnalyticsAsync(User(principal), ParseInt(request.Query["days"], 30, 7, 90), ct);
            return result is null ? Denied() : Results.Ok(result);
        });

        group.MapGet("/users", async (ClaimsPrincipal principal, HttpRequest request,
            IAdminOperationsService service, CancellationToken ct) =>
        {
            var result = await service.SearchUsersAsync(User(principal), request.Query["query"], request.Query["status"],
                ParseInt(request.Query["limit"], 25, 1, 100), ParseInt(request.Query["offset"], 0, 0, 1_000_000), ct);
            return result is null ? Denied() : Results.Ok(result);
        });

        group.MapGet("/users/{userId:guid}", async (ClaimsPrincipal principal, Guid userId,
            IAdminOperationsService service, CancellationToken ct) =>
        {
            var result = await service.GetUserAsync(User(principal), userId, ct);
            return result is null ? Results.NotFound(new GatewayErrorResponse("ADMIN_USER_NOT_FOUND")) : Results.Ok(result);
        });

        group.MapPatch("/users/{userId:guid}", async (ClaimsPrincipal principal, Guid userId,
            AdminUserUpdateRequest? request, IAdminOperationsService service, CancellationToken ct) =>
        {
            if (request is null) return Invalid();
            var result = await service.UpdateUserAsync(User(principal), userId,
                new(request.DisplayName, request.ContactEmail, request.Status, request.InternalNote,
                    request.Reason, request.CorrelationId), ct);
            return result is null ? Denied() : Results.Ok(result);
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));

        group.MapGet("/transactions", async (ClaimsPrincipal principal, HttpRequest request,
            IAdminOperationsService service, CancellationToken ct) =>
        {
            var result = await service.SearchPaymentsAsync(User(principal), request.Query["query"],
                request.Query["provider"], ParseInt(request.Query["limit"], 25, 1, 100),
                ParseInt(request.Query["offset"], 0, 0, 1_000_000), ct);
            return result is null ? Denied() : Results.Ok(result);
        });

        group.MapGet("/packages", async (ClaimsPrincipal principal, IAdminOperationsService service, CancellationToken ct) =>
        {
            var result = await service.ListPackagesAsync(User(principal), ct);
            return result is null ? Denied() : Results.Ok(new { items = result.Value });
        });

        group.MapPost("/packages", async (ClaimsPrincipal principal, AdminPackageSaveRequest? request,
            IAdminOperationsService service, CancellationToken ct) =>
        {
            if (request is null) return Invalid();
            var result = await service.SavePackageAsync(User(principal), new(request.PackageId, request.ProductId,
                request.DisplayName, request.Description, request.AmountMinor, request.Currency, request.Credits,
                request.IsActive, request.SortOrder, request.Reason, request.CorrelationId), ct);
            return result is null ? Denied() : Results.Ok(result);
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));

        group.MapPost("/packages/{packageId:guid}/archive", async (ClaimsPrincipal principal, Guid packageId,
            AdminArchiveRequest? request, IAdminOperationsService service, CancellationToken ct) =>
        {
            if (request is null) return Invalid();
            return await service.ArchivePackageAsync(User(principal), packageId, request.Reason,
                request.CorrelationId, ct) ? Results.Ok(new { code = "ADMIN_PACKAGE_ARCHIVED" }) : Denied();
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));

        group.MapGet("/admins", async (ClaimsPrincipal principal, IAdminOperationsService service, CancellationToken ct) =>
        {
            var result = await service.ListAdminsAsync(User(principal), ct);
            return result is null ? Denied() : Results.Ok(new { items = result.Value });
        });

        group.MapPatch("/admins/{adminUserId:guid}", async (ClaimsPrincipal principal, Guid adminUserId,
            AdminAccountUpdateRequest? request, IAdminOperationsService service, CancellationToken ct) =>
        {
            if (request is null) return Invalid();
            return await service.UpdateAdminAsync(User(principal), adminUserId,
                new(request.Role, request.IsActive, request.MfaState, request.DisplayLabel, request.Reason,
                    request.CorrelationId), ct)
                ? Results.Ok(new { code = "ADMIN_ACCOUNT_UPDATED" }) : Denied();
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumMutationBytes));
    }

    private static IResult Denied() => Results.Json(new GatewayErrorResponse("ADMIN_ACCESS_REQUIRED"), statusCode: 403);
    private static IResult Invalid() => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Admin request is invalid."] });
    private static int ParseInt(StringValues value, int fallback, int min, int max) =>
        int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= min && parsed <= max ? parsed : fallback;
    private static AuthenticatedGatewayUser User(ClaimsPrincipal principal) =>
        new(Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : Guid.Empty);
}
