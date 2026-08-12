using System.Text.Json.Serialization;
using AuditionModStudio.Gateway.Authentication;
using AuditionModStudio.Gateway.Endpoints;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace AuditionModStudio.Gateway;

public static class GatewayApplication
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var options = TrustedGatewayOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.AddSingleton<ISupabaseAuthClient, SupabaseAuthHttpClient>();
        services.AddSingleton<ISupabaseAccessTokenValidator, SupabaseAccessTokenValidator>();
        services.AddSingleton<ITrustedAiGateway, UnavailableTrustedAiGateway>();
        services.AddSingleton<ITrustedCreditQueryService, UnavailableTrustedCreditQueryService>();
        services.AddSingleton<ITrustedTemplateEntitlementService, UnavailableTrustedTemplateEntitlementService>();

        services.AddAuthentication(GatewayAuthenticationDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, GatewayAuthenticationHandler>(
                GatewayAuthenticationDefaults.Scheme, _ => { });
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                context.Abort();
            }
            catch (Exception exception)
            {
                app.Logger.LogError("Gateway request failed with {ExceptionType}", exception.GetType().Name);
                if (context.Response.HasStarted)
                {
                    context.Abort();
                    return;
                }

                context.Response.Clear();
                var malformedRequest = exception is BadHttpRequestException;
                context.Response.StatusCode = malformedRequest
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new GatewayErrorResponse(malformedRequest
                    ? "REQUEST_BODY_INVALID"
                    : "GATEWAY_UNEXPECTED_FAILURE"), context.RequestAborted).ConfigureAwait(false);
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
        app.MapTrustedGatewayEndpoints();
    }
}

public sealed class TrustedGatewayOptions
{
    public Uri? SupabaseProjectUri { get; init; }
    public string SupabasePublishableKey { get; init; } = string.Empty;
    public Uri? ProviderEndpoint { get; init; }
    public string ProviderApiKey { get; init; } = string.Empty;

    public bool HasValidSupabaseConfiguration =>
        SupabaseProjectUri is { IsAbsoluteUri: true, Scheme: "https", AbsolutePath: "/" }
        && string.IsNullOrEmpty(SupabaseProjectUri.UserInfo)
        && string.IsNullOrEmpty(SupabaseProjectUri.Query)
        && string.IsNullOrEmpty(SupabaseProjectUri.Fragment)
        && IsSafeCredential(SupabasePublishableKey, 2_048);

    public bool HasProviderConfiguration =>
        ProviderEndpoint is { IsAbsoluteUri: true, Scheme: "https" }
        && string.IsNullOrEmpty(ProviderEndpoint.UserInfo)
        && IsSafeCredential(ProviderApiKey, 4_096);

    public static TrustedGatewayOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Gateway");
        return new()
        {
            SupabaseProjectUri = ParseAbsoluteUri(section["SupabaseUrl"]),
            SupabasePublishableKey = section["SupabasePublishableKey"] ?? string.Empty,
            ProviderEndpoint = ParseAbsoluteUri(section["ProviderEndpoint"]),
            ProviderApiKey = section["ProviderApiKey"] ?? string.Empty,
        };
    }

    public override string ToString() => "TrustedGatewayOptions { [REDACTED] }";

    private static Uri? ParseAbsoluteUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static bool IsSafeCredential(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '-' or '.' or '_' or '~' or '+' or '/' or '=');
}

public sealed record GatewayErrorResponse(string Code);
