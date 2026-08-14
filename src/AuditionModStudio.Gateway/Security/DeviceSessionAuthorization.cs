using System.Security.Claims;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.Authorization;

namespace AuditionModStudio.Gateway.Security;

public sealed class SkipDeviceSessionAuthorizationMetadata;

public sealed class DeviceSessionAuthorizationMiddleware(
    RequestDelegate next,
    DeviceSessionOptions options,
    IDeviceSessionService sessions)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Enabled || context.GetEndpoint() is null
            || context.GetEndpoint()!.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || context.GetEndpoint()!.Metadata.GetMetadata<SkipDeviceSessionAuthorizationMetadata>() is not null
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!options.IsOperational)
        {
            await RejectAsync(context, StatusCodes.Status503ServiceUnavailable,
                "DEVICE_SESSION_CONFIGURATION_INVALID").ConfigureAwait(false);
            return;
        }

        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParseExact(subject, "D", out var userId) || userId == Guid.Empty
            || !TryReadId(context, DeviceSessionHeaders.DeviceId, out var deviceId)
            || !TryReadId(context, DeviceSessionHeaders.SessionId, out var sessionId))
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden,
                "DEVICE_SESSION_REQUIRED").ConfigureAwait(false);
            return;
        }

        var result = await sessions.ValidateAsync(new AuthenticatedGatewayUser(userId), deviceId, sessionId,
            options.LastSeenWriteInterval, context.RequestAborted).ConfigureAwait(false);
        if (result.Status == DeviceSessionOperationStatus.Succeeded
            && result.DiagnosticCode == "DEVICE_SESSION_ACTIVE")
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (result.Status == DeviceSessionOperationStatus.Unavailable)
        {
            await RejectAsync(context, StatusCodes.Status503ServiceUnavailable,
                "DEVICE_SESSION_VALIDATION_UNAVAILABLE").ConfigureAwait(false);
            return;
        }

        await RejectAsync(context, StatusCodes.Status403Forbidden,
            "DEVICE_SESSION_INACTIVE").ConfigureAwait(false);
    }

    private static bool TryReadId(HttpContext context, string headerName, out Guid id)
    {
        id = Guid.Empty;
        var values = context.Request.Headers[headerName];
        return values.Count == 1 && Guid.TryParseExact(values[0], "D", out id) && id != Guid.Empty;
    }

    private static Task RejectAsync(HttpContext context, int statusCode, string code)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new GatewayErrorResponse(code), context.RequestAborted);
    }
}
