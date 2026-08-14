using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AuditionModStudio.Gateway.Authentication;
using AuditionModStudio.Gateway.Security;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace AuditionModStudio.Gateway.Endpoints;

public sealed record AdminLoginRequest(string? Email, string? Password);

public static class AdminSessionEndpoints
{
    private const int MaximumLoginBytes = 8 * 1024;
    private const int MaximumAuthResponseBytes = 64 * 1024;

    public static void MapAdminSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/admin/session/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AdminLoginPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumLoginBytes));

        endpoints.MapGet("/v1/admin/session", async (HttpContext context, IAdminPortalService service,
                CancellationToken cancellationToken) =>
            {
                var state = await service.GetBootstrapStateAsync(User(context.User),
                    context.User.FindFirstValue(ClaimTypes.Email), cancellationToken).ConfigureAwait(false);
                if (state.Status != TrustedServiceStatus.Succeeded || state.Admin is not { IsActive: true } admin)
                    return Results.Json(new GatewayErrorResponse("ADMIN_SESSION_INVALID"), statusCode: 401);
                return Results.Ok(SessionPayload(context.User, admin));
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapPost("/v1/admin/session/logout", async (HttpContext context) =>
            {
                await context.SignOutAsync(GatewayAuthenticationDefaults.AdminCookieScheme).ConfigureAwait(false);
                return Results.Ok(new { code = "ADMIN_SIGNED_OUT" });
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        AdminLoginRequest? request,
        TrustedGatewayOptions options,
        ISupabaseAuthClient authClient,
        ISupabaseAccessTokenValidator validator,
        IAdminPortalService adminService,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request?.Email);
        var password = request?.Password ?? string.Empty;
        if (email is null || password.Length is < 8 or > 256 || !options.HasValidSupabaseConfiguration)
            return LoginRejected();

        try
        {
            using var authRequest = new HttpRequestMessage(HttpMethod.Post,
                new Uri(options.SupabaseProjectUri!, "auth/v1/token?grant_type=password"));
            authRequest.Headers.TryAddWithoutValidation("apikey", options.SupabasePublishableKey);
            authRequest.Content = JsonContent.Create(new { email, password });
            using var response = await authClient.SendAsync(authRequest, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden || !response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > MaximumAuthResponseBytes)
                return LoginRejected();

            await response.Content.LoadIntoBufferAsync(MaximumAuthResponseBytes, cancellationToken).ConfigureAwait(false);
            var token = await response.Content.ReadFromJsonAsync<SupabasePasswordToken>(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token?.AccessToken)) return LoginRejected();

            var validated = await validator.ValidateAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
            if (validated.Status != AccessTokenValidationStatus.Valid || validated.UserId == Guid.Empty
                || !string.Equals(validated.Email, email, StringComparison.OrdinalIgnoreCase))
                return LoginRejected();

            var user = new AuthenticatedGatewayUser(validated.UserId);
            var state = await adminService.GetBootstrapStateAsync(user, validated.Email, cancellationToken)
                .ConfigureAwait(false);
            if (state.Status == TrustedServiceStatus.Succeeded && state.Admin is null && state.BootstrapConfigured)
            {
                state = await adminService.BootstrapAsync(user, validated.Email,
                    validated.DisplayName ?? validated.Email, cancellationToken).ConfigureAwait(false);
            }
            if (state.Status != TrustedServiceStatus.Succeeded || state.Admin is not { IsActive: true } admin)
                return LoginRejected();

            var csrf = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, validated.UserId.ToString("D")),
                new("sub", validated.UserId.ToString("D")),
                new(ClaimTypes.Email, validated.Email ?? email),
                new(ClaimTypes.Role, admin.Role),
                new("aams:mfa", admin.MfaState),
                new("aams:csrf", csrf),
            };
            if (!string.IsNullOrWhiteSpace(admin.DisplayLabel)) claims.Add(new(ClaimTypes.Name, admin.DisplayLabel));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims,
                GatewayAuthenticationDefaults.AdminCookieScheme));
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            await context.SignInAsync(GatewayAuthenticationDefaults.AdminCookieScheme, principal,
                new AuthenticationProperties
                {
                    AllowRefresh = false,
                    IsPersistent = false,
                    IssuedUtc = DateTimeOffset.UtcNow,
                    ExpiresUtc = expiresAt,
                }).ConfigureAwait(false);
            return Results.Ok(SessionPayload(principal, admin, csrf, expiresAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                                     or System.Text.Json.JsonException)
        {
            return Results.Problem(statusCode: 503, title: "Admin authentication is unavailable.",
                extensions: new Dictionary<string, object?> { ["code"] = "ADMIN_AUTH_UNAVAILABLE" });
        }
    }

    private static object SessionPayload(ClaimsPrincipal principal, AdminAccessProfile admin,
        string? csrf = null, DateTimeOffset? expiresAt = null) => new
        {
            code = "ADMIN_SESSION_ACTIVE",
            csrfToken = csrf ?? principal.FindFirstValue("aams:csrf"),
            expiresAt,
            admin = new
            {
                admin.AdminUserId,
                email = principal.FindFirstValue(ClaimTypes.Email),
                admin.DisplayLabel,
                admin.Role,
                admin.MfaState,
                admin.IsActive,
                admin.LastSeenAt,
            },
        };

    private static IResult LoginRejected() =>
        Results.Json(new GatewayErrorResponse("ADMIN_LOGIN_REJECTED"), statusCode: 401);

    private static string? NormalizeEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 320 && value.Contains('@') && !value.Any(char.IsControl)
            ? value.Trim().ToLowerInvariant()
            : null;

    private static AuthenticatedGatewayUser User(ClaimsPrincipal principal) =>
        new(Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : Guid.Empty);

    private sealed record SupabasePasswordToken(
        [property: JsonPropertyName("access_token")] string? AccessToken);
}
