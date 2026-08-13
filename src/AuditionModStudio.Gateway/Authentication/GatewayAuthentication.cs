using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AuditionModStudio.Gateway.Security;
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
    string? Email,
    string? DisplayName)
{
    public static AccessTokenValidationResult Valid(Guid userId, string? email, string? displayName = null) =>
        new(AccessTokenValidationStatus.Valid, userId, email, displayName);
    public static AccessTokenValidationResult Invalid() =>
        new(AccessTokenValidationStatus.Invalid, Guid.Empty, null, null);
    public static AccessTokenValidationResult Unavailable() =>
        new(AccessTokenValidationStatus.Unavailable, Guid.Empty, null, null);
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

public sealed class SupabaseAccessTokenValidator : ISupabaseAccessTokenValidator
{
    private const int MaximumTokenLength = 4_096;
    private const int MaximumClaimsBytes = 16 * 1_024;
    private const int MaximumResponseBytes = 64 * 1_024;
    private readonly ISupabaseAuthClient _httpClient;
    private readonly TrustedGatewayOptions _options;
    private readonly GatewayAbuseProtectionOptions _abuseProtection;
    private readonly TimeProvider _timeProvider;

    public SupabaseAccessTokenValidator(
        ISupabaseAuthClient httpClient,
        TrustedGatewayOptions options,
        GatewayAbuseProtectionOptions abuseProtection,
        TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _options = options;
        _abuseProtection = abuseProtection;
        _timeProvider = timeProvider;
    }

    public SupabaseAccessTokenValidator(ISupabaseAuthClient httpClient, TrustedGatewayOptions options)
        : this(httpClient, options, new GatewayAbuseProtectionOptions(), TimeProvider.System)
    {
    }

    public async Task<AccessTokenValidationResult> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (!_options.HasValidSupabaseConfiguration)
        {
            return AccessTokenValidationResult.Unavailable();
        }
        if (!TryReadBoundedClaims(accessToken, out var claims))
        {
            return AccessTokenValidationResult.Invalid();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(_options.SupabaseProjectUri!, "auth/v1/user"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("apikey", _options.SupabasePublishableKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
                   && userId == claims.Subject
                ? AccessTokenValidationResult.Valid(userId, NormalizeEmail(user.Email), NormalizeDisplayName(user.Metadata))
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

    private bool TryReadBoundedClaims(string token, out AccessTokenClaims claims)
    {
        claims = default;
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength
            || !token.All(character => char.IsAsciiLetterOrDigit(character)
                                      || character is '-' or '.' or '_' or '~' or '+' or '/' or '='))
        {
            return false;
        }

        var segments = token.Split('.');
        if (segments.Length != 3 || segments.Any(string.IsNullOrEmpty)
            || !TryDecodeBase64Url(segments[1], out var payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("sub", out var subjectProperty)
                || !Guid.TryParse(subjectProperty.GetString(), out var subject) || subject == Guid.Empty
                || !TryReadUnixTime(root, "iat", out var issuedAt)
                || !TryReadUnixTime(root, "exp", out var expiresAt))
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            if (expiresAt <= issuedAt
                || expiresAt - issuedAt > _abuseProtection.MaximumAccessTokenLifetime
                || issuedAt > now + _abuseProtection.AccessTokenClockSkew
                || expiresAt <= now - _abuseProtection.AccessTokenClockSkew
                || root.TryGetProperty("nbf", out var notBeforeProperty)
                && (!notBeforeProperty.TryGetInt64(out var notBeforeSeconds)
                    || DateTimeOffset.FromUnixTimeSeconds(notBeforeSeconds)
                    > now + _abuseProtection.AccessTokenClockSkew))
            {
                return false;
            }

            claims = new(subject, issuedAt, expiresAt);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException
                                                     or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryReadUnixTime(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        try
        {
            return root.TryGetProperty(name, out var property)
                   && property.TryGetInt64(out var seconds)
                   && (value = DateTimeOffset.FromUnixTimeSeconds(seconds)) != default;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryDecodeBase64Url(string value, out byte[] decoded)
    {
        decoded = [];
        if (value.Length is 0 or > MaximumClaimsBytes * 2) return false;
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => "invalid",
        };
        try
        {
            decoded = Convert.FromBase64String(normalized);
            return decoded.Length is > 0 and <= MaximumClaimsBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? NormalizeEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Length <= 320 && !email.Any(char.IsControl)
            ? email.Trim()
            : null;

    private static string? NormalizeDisplayName(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null) return null;
        foreach (var key in new[] { "display_name", "full_name", "name" })
        {
            if (!metadata.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var displayName = value.GetString()?.Trim();
            return !string.IsNullOrWhiteSpace(displayName) && displayName.Length <= 128
                && !displayName.Any(char.IsControl) ? displayName : null;
        }
        return null;
    }

    private sealed record SupabaseUser(string? Id, string? Email,
        [property: System.Text.Json.Serialization.JsonPropertyName("user_metadata")]
        Dictionary<string, JsonElement>? Metadata);
    private readonly record struct AccessTokenClaims(
        Guid Subject,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt);
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
        if (validation.DisplayName is not null)
        {
            claims.Add(new(ClaimTypes.Name, validation.DisplayName));
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
