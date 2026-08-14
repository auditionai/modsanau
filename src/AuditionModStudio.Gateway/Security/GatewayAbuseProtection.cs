using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.HttpOverrides;

namespace AuditionModStudio.Gateway.Security;

public static class GatewayAbuseProtectionDefaults
{
    public const string AdminLoginPolicy = "admin-login";
    public const string AuthenticatedPolicy = "authenticated-user";
    public const string ChargedOperationPolicy = "charged-operation";
}

public sealed class GatewayAbuseProtectionOptions
{
    public bool RequireHttps { get; init; } = true;
    public int PreAuthenticationPermitLimit { get; init; } = 120;
    public int AuthenticatedPermitLimit { get; init; } = 60;
    public int ChargedOperationPermitLimit { get; init; } = 10;
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaximumAccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan AccessTokenClockSkew { get; init; } = TimeSpan.FromMinutes(2);
    public IReadOnlyList<IPAddress> TrustedProxyAddresses { get; init; } = [];

    public static GatewayAbuseProtectionOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Gateway:AbuseProtection");
        return new()
        {
            RequireHttps = ParseBoolean(section["RequireHttps"], true),
            PreAuthenticationPermitLimit = ParseInteger(section["PreAuthenticationPermitLimit"], 120, 1, 10_000),
            AuthenticatedPermitLimit = ParseInteger(section["AuthenticatedPermitLimit"], 60, 1, 10_000),
            ChargedOperationPermitLimit = ParseInteger(section["ChargedOperationPermitLimit"], 10, 1, 1_000),
            RateLimitWindow = TimeSpan.FromSeconds(ParseInteger(section["RateLimitWindowSeconds"], 60, 1, 3_600)),
            MaximumAccessTokenLifetime = TimeSpan.FromSeconds(
                ParseInteger(section["MaximumAccessTokenLifetimeSeconds"], 3_600, 300, 3_600)),
            AccessTokenClockSkew = TimeSpan.FromSeconds(
                ParseInteger(section["AccessTokenClockSkewSeconds"], 120, 0, 300)),
            TrustedProxyAddresses = ParseTrustedProxies(section.GetSection("TrustedProxies")),
        };
    }

    public override string ToString() => "GatewayAbuseProtectionOptions { [REDACTED] }";

    private static bool ParseBoolean(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInteger(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed >= minimum && parsed <= maximum
            ? parsed
            : fallback;

    private static IReadOnlyList<IPAddress> ParseTrustedProxies(IConfigurationSection section)
    {
        var addresses = section.GetChildren()
            .Select(child => IPAddress.TryParse(child.Value, out var address) ? address : null)
            .Where(address => address is not null)
            .Cast<IPAddress>()
            .Distinct()
            .Take(8)
            .ToArray();
        return addresses;
    }
}

public sealed class GatewayIpRateLimiter : IAsyncDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;
    private readonly GatewayAbuseProtectionOptions _options;

    public GatewayIpRateLimiter(GatewayAbuseProtectionOptions options)
    {
        _options = options;
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => FixedWindow(options.PreAuthenticationPermitLimit, options.RateLimitWindow)));
    }

    public async Task<bool> AcquireAsync(HttpContext context)
    {
        using var lease = await _limiter.AcquireAsync(context, 1, context.RequestAborted).ConfigureAwait(false);
        if (lease.IsAcquired) return true;

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = Math.Ceiling(_options.RateLimitWindow.TotalSeconds)
            .ToString(CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new GatewayErrorResponse("RATE_LIMIT_EXCEEDED"),
            context.RequestAborted).ConfigureAwait(false);
        return false;
    }

    public ValueTask DisposeAsync() => _limiter.DisposeAsync();

    internal static FixedWindowRateLimiterOptions FixedWindow(int permitLimit, TimeSpan window) => new()
    {
        AutoReplenishment = true,
        PermitLimit = permitLimit,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        Window = window,
    };
}

public sealed class GatewaySecurityMiddleware(
    RequestDelegate next,
    GatewayAbuseProtectionOptions options,
    GatewayIpRateLimiter ipRateLimiter,
    ILogger<GatewaySecurityMiddleware> logger)
{
    private static readonly EventId AuditEventId = new(8200, "GatewaySecurityAudit");

    public async Task InvokeAsync(HttpContext context)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new GatewayErrorResponse("HTTPS_REQUIRED"),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (!HasSupportedContentType(context))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                await context.Response.WriteAsJsonAsync(new GatewayErrorResponse("CONTENT_TYPE_UNSUPPORTED"),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var sizeLimit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()
                ?.MaxRequestBodySize;
            if (sizeLimit is not null && context.Request.ContentLength > sizeLimit)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(new GatewayErrorResponse("REQUEST_BODY_TOO_LARGE"),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (!await ipRateLimiter.AcquireAsync(context).ConfigureAwait(false)) return;
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var subject = SubjectFingerprint(context.User);
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            logger.LogInformation(AuditEventId,
                "Gateway security audit {Method} {RouteTemplate} {StatusCode} {SubjectFingerprint} {ElapsedMilliseconds}",
                context.Request.Method, route, context.Response.StatusCode, subject, elapsedMilliseconds);
        }
    }

    private static bool HasSupportedContentType(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method)
            && !HttpMethods.IsPut(context.Request.Method)
            && !HttpMethods.IsPatch(context.Request.Method)) return true;
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/v1/ai/content/", StringComparison.Ordinal)
            || path.EndsWith("/cancel", StringComparison.Ordinal))
        {
            return true;
        }

        var separator = context.Request.ContentType?.IndexOf(';') ?? -1;
        var mediaType = separator >= 0
            ? context.Request.ContentType![..separator]
            : context.Request.ContentType;
        return string.Equals(mediaType?.Trim(), "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static string SubjectFingerprint(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId) || userId == Guid.Empty) return "anonymous";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(userId.ToString("D")));
        return Convert.ToHexString(digest.AsSpan(0, 8));
    }
}

public static class GatewayRateLimiting
{
    public static void AddGatewayRateLimiting(
        this IServiceCollection services,
        GatewayAbuseProtectionOptions options)
    {
        services.AddRateLimiter(rateLimiting =>
        {
            rateLimiting.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiting.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(options.RateLimitWindow.TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture);
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new GatewayErrorResponse("RATE_LIMIT_EXCEEDED"), cancellationToken).ConfigureAwait(false);
            };
            rateLimiting.AddPolicy(GatewayAbuseProtectionDefaults.AuthenticatedPolicy,
                context => UserPartition(context, options.AuthenticatedPermitLimit, options.RateLimitWindow));
            rateLimiting.AddPolicy(GatewayAbuseProtectionDefaults.AdminLoginPolicy,
                context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => GatewayIpRateLimiter.FixedWindow(5, TimeSpan.FromMinutes(1))));
            rateLimiting.AddPolicy(GatewayAbuseProtectionDefaults.ChargedOperationPolicy,
                context => UserPartition(context, options.ChargedOperationPermitLimit, options.RateLimitWindow));
        });
        if (options.TrustedProxyAddresses.Count == 0) return;
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.ForwardLimit = 1;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (var proxy in options.TrustedProxyAddresses)
            {
                forwarded.KnownProxies.Add(proxy);
            }
        });
    }

    private static RateLimitPartition<string> UserPartition(
        HttpContext context,
        int permitLimit,
        TimeSpan window)
    {
        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(subject,
            _ => GatewayIpRateLimiter.FixedWindow(permitLimit, window));
    }
}
