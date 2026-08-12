using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AuditionModStudio.Gateway.Authentication;

public static class GatewayAuthenticationDefaults
{
    public const string Scheme = "SupabaseAccessToken";
}

public enum AccessTokenValidationStatus
{
    Valid,
    Invalid,
    Unavailable
}

public sealed record AccessTokenValidationResult(
    AccessTokenValidationStatus Status,
    Guid UserId,
    string? Email)
{
    public static AccessTokenValidationResult Valid(Guid userId, string? email) =>
        new(AccessTokenValidationStatus.Valid, userId, email);
    public static AccessTokenValidationResult Invalid() =>
        new(AccessTokenValidationStatus.Invalid, Guid.Empty, null);
    public static AccessTokenValidationResult Unavailable() =>
        new(AccessTokenValidationStatus.Unavailable, Guid.Empty, null);
}

public interface ISupabaseAccessTokenValidator
{
    Task<AccessTokenValidationResult> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken = default);
}

public interface ISupabaseAuthClient
{
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken);
}

public sealed class SupabaseAuthHttpClient : ISupabaseAuthClient, IDisposable
{
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    public void Dispose() => _client.Dispose();
}

public sealed class SupabaseAccessTokenValidator(
    ISupabaseAuthClient httpClient,
    TrustedGatewayOptions options) : ISupabaseAccessTokenValidator
{
    private const int MaximumTokenLength = 4_096;
    private const int MaximumResponseBytes = 64 * 1_024;

    public async Task<AccessTokenValidationResult> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (!options.HasValidSupabaseConfiguration)
        {
            return AccessTokenValidationResult.Unavailable();
        }
        if (!IsSafeToken(accessToken))
        {
            return AccessTokenValidationResult.Invalid();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(options.SupabaseProjectUri!, "auth/v1/user"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("apikey", options.SupabasePublishableKey);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.BadRequest)
            {
                return AccessTokenValidationResult.Invalid();
            }
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return AccessTokenValidationResult.Unavailable();
            }

            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            var user = await response.Content.ReadFromJsonAsync<SupabaseUser>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return user is { Id: not null }
                   && Guid.TryParse(user.Id, out var userId)
                   && userId != Guid.Empty
                ? AccessTokenValidationResult.Valid(userId, NormalizeEmail(user.Email))
                : AccessTokenValidationResult.Invalid();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                                     or JsonException or NotSupportedException)
        {
            return AccessTokenValidationResult.Unavailable();
        }
    }

    private static bool IsSafeToken(string token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.Length <= MaximumTokenLength
        && token.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '-' or '.' or '_' or '~' or '+' or '/' or '=');

    private static string? NormalizeEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Length <= 320 && !email.Any(char.IsControl)
            ? email.Trim()
            : null;

    private sealed record SupabaseUser(string? Id, string? Email);
}

public sealed class GatewayAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ISupabaseAccessTokenValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string ValidationUnavailableItem = "Gateway.Authentication.ValidationUnavailable";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization;
        if (authorization.Count != 1)
        {
            return AuthenticateResult.NoResult();
        }

        var value = authorization.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("AUTH_SCHEME_INVALID");
        }

        var token = value[prefix.Length..];
        var validation = await validator.ValidateAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (validation.Status != AccessTokenValidationStatus.Valid || validation.UserId == Guid.Empty)
        {
            if (validation.Status == AccessTokenValidationStatus.Unavailable)
            {
                Context.Items[ValidationUnavailableItem] = true;
            }
            return AuthenticateResult.Fail(validation.Status == AccessTokenValidationStatus.Unavailable
                ? "AUTH_VALIDATION_UNAVAILABLE"
                : "AUTH_TOKEN_INVALID");
        }

        var claims = new List<Claim>
        {
            new("sub", validation.UserId.ToString("D")),
            new(ClaimTypes.NameIdentifier, validation.UserId.ToString("D")),
        };
        if (validation.Email is not null)
        {
            claims.Add(new(ClaimTypes.Email, validation.Email));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(ValidationUnavailableItem))
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsJsonAsync(new GatewayErrorResponse("AUTH_VALIDATION_UNAVAILABLE"),
                Context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await base.HandleChallengeAsync(properties).ConfigureAwait(false);
    }
}
