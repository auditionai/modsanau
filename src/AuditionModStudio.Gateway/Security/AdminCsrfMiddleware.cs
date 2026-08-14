using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Gateway.Authentication;

namespace AuditionModStudio.Gateway.Security;

public sealed class AdminCsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresCsrf(context) && !HasValidToken(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new GatewayErrorResponse("ADMIN_CSRF_REJECTED"),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }
        await next(context).ConfigureAwait(false);
    }

    private static bool RequiresCsrf(HttpContext context) =>
        context.User.Identity?.AuthenticationType == GatewayAuthenticationDefaults.AdminCookieScheme
        && context.Request.Path.StartsWithSegments("/v1/admin")
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method)
        && !context.Request.Path.Equals("/v1/admin/session/login");

    private static bool HasValidToken(HttpContext context)
    {
        var expected = context.User.FindFirst("aams:csrf")?.Value;
        var supplied = context.Request.Headers["X-CSRF-Token"];
        if (string.IsNullOrEmpty(expected) || supplied.Count != 1) return false;
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(supplied.ToString());
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
