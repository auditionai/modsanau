using System.Security.Claims;
using AuditionModStudio.Gateway.Security;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace AuditionModStudio.Gateway.Endpoints;

public sealed record DeviceSessionRegistrationRequest(
    Guid DeviceId,
    string? DisplayName,
    string? ClientVersion,
    string? Platform);

public static class DeviceSessionEndpoints
{
    private const long MaximumRegistrationRequestBytes = 4 * 1_024;

    public static void MapDeviceSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/device-sessions/register", RegisterAsync)
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRegistrationRequestBytes))
            .WithMetadata(new SkipDeviceSessionAuthorizationMetadata());

        endpoints.MapGet("/v1/device-sessions", ListAsync)
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new SkipDeviceSessionAuthorizationMetadata());

        endpoints.MapDelete("/v1/device-sessions/{sessionId:guid}", RevokeAsync)
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new SkipDeviceSessionAuthorizationMetadata());
    }

    private static async Task<IResult> RegisterAsync(
        ClaimsPrincipal principal,
        DeviceSessionRegistrationRequest? request,
        DeviceSessionOptions options,
        IDeviceSessionService sessions,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled) return Unavailable("DEVICE_SESSIONS_DISABLED");
        if (!options.IsOperational) return Unavailable("DEVICE_SESSION_CONFIGURATION_INVALID");
        if (request is null || request.DeviceId == Guid.Empty
            || !TryNormalize(request.DisplayName, 64, false, out var displayName)
            || !TryNormalize(request.ClientVersion, 32, true, out var clientVersion)
            || !TryNormalize(request.Platform, 32, true, out var platform))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["Device session registration is invalid."],
            });
        }

        var result = await sessions.RegisterAsync(User(principal),
            new DeviceSessionDescriptor(request.DeviceId, displayName!, clientVersion!, platform!),
            options.MaximumActiveDevices, cancellationToken).ConfigureAwait(false);
        return MapOperation(result);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        DeviceSessionOptions options,
        IDeviceSessionService sessions,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled) return Unavailable("DEVICE_SESSIONS_DISABLED");
        if (!options.IsOperational) return Unavailable("DEVICE_SESSION_CONFIGURATION_INVALID");
        var result = await sessions.ListAsync(User(principal), cancellationToken).ConfigureAwait(false);
        if (!Enum.IsDefined(result.Status) || !IsSafeCode(result.DiagnosticCode)
            || result.Status == DeviceSessionOperationStatus.Succeeded
            && result.Sessions.Any(session => !IsValidSnapshot(session)))
            return Unavailable("DEVICE_SESSION_RESPONSE_INVALID");
        return result.Status switch
        {
            DeviceSessionOperationStatus.Succeeded => Results.Ok(new
            {
                result.DiagnosticCode,
                result.Sessions,
            }),
            DeviceSessionOperationStatus.Rejected => Results.Forbid(),
            _ => Unavailable(result.DiagnosticCode),
        };
    }

    private static async Task<IResult> RevokeAsync(
        ClaimsPrincipal principal,
        Guid sessionId,
        DeviceSessionOptions options,
        IDeviceSessionService sessions,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled) return Unavailable("DEVICE_SESSIONS_DISABLED");
        if (!options.IsOperational) return Unavailable("DEVICE_SESSION_CONFIGURATION_INVALID");
        var result = await sessions.RevokeAsync(User(principal), sessionId, cancellationToken)
            .ConfigureAwait(false);
        return MapOperation(result);
    }

    private static IResult MapOperation(DeviceSessionOperationResult result)
    {
        if (!Enum.IsDefined(result.Status) || !IsSafeCode(result.DiagnosticCode)
            || result.Status == DeviceSessionOperationStatus.Succeeded && !IsValidSnapshot(result.Session))
            return Unavailable("DEVICE_SESSION_RESPONSE_INVALID");
        return result.Status switch
        {
            DeviceSessionOperationStatus.Succeeded => Results.Ok(new
            {
                result.DiagnosticCode,
                result.IsNewDevice,
                result.Session,
            }),
            DeviceSessionOperationStatus.Rejected => Results.UnprocessableEntity(
                new GatewayErrorResponse(result.DiagnosticCode)),
            DeviceSessionOperationStatus.NotFound => Results.NotFound(
                new GatewayErrorResponse(result.DiagnosticCode)),
            _ => Unavailable(result.DiagnosticCode),
        };
    }

    private static bool IsValidSnapshot(DeviceSessionSnapshot? session) => session is not null
        && session.DeviceId != Guid.Empty && session.SessionId != Guid.Empty
        && session.CreatedAt != default && session.LastSeenAt >= session.CreatedAt
        && TryNormalize(session.DisplayName, 64, false, out _)
        && TryNormalize(session.ClientVersion, 32, true, out _)
        && TryNormalize(session.Platform, 32, true, out _);

    private static bool TryNormalize(string? value, int maximumLength, bool asciiOnly, out string? normalized)
    {
        normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.Length <= maximumLength
            && !normalized.Any(char.IsControl) && (!asciiOnly || normalized.All(char.IsAscii));
    }

    private static bool IsSafeCode(string code) => !string.IsNullOrWhiteSpace(code) && code.Length <= 64
        && code.All(character => character is >= 'A' and <= 'Z'
            || char.IsAsciiDigit(character) || character == '_');

    private static AuthenticatedGatewayUser User(ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));

    private static IResult Unavailable(string code) => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Device session service is unavailable.",
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
