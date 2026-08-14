using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditionModStudio.Core.Auth;

namespace AuditionModStudio.Cloud;

public sealed class SupabaseAuthOptions
{
    public SupabaseAuthOptions(Uri projectUri, string publishableKey)
    {
        ProjectUri = projectUri;
        PublishableKey = publishableKey;
    }

    public Uri ProjectUri { get; }
    public string PublishableKey { get; }
    public bool IsValid => ProjectUri is { IsAbsoluteUri: true, Scheme: "https" }
                           && string.IsNullOrEmpty(ProjectUri.UserInfo)
                           && ProjectUri.AbsolutePath == "/"
                           && string.IsNullOrEmpty(ProjectUri.Query)
                           && string.IsNullOrEmpty(ProjectUri.Fragment)
                           && !string.IsNullOrWhiteSpace(PublishableKey)
                           && PublishableKey.Length <= 2_048
                           && PublishableKey.All(IsSafeCredentialCharacter);
    public override string ToString() => "SupabaseAuthOptions { ProjectUri = [REDACTED], PublishableKey = [REDACTED] }";

    private static bool IsSafeCredentialCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~' or '+' or '/' or '=';
}

public sealed class SupabaseAuthService(
    HttpClient httpClient,
    ISecureSessionStore sessionStore,
    SupabaseAuthOptions options) : IAuthenticationService, IDisposable
{
    private const int MaximumResponseBytes = 64 * 1_024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private int _disposeState;

    public Task<AuthenticationResult> SignInAnonymouslyAsync(CancellationToken cancellationToken = default) =>
        ExecuteSessionMutationAsync(() => SendSessionAsync("signup", new { }, cancellationToken), cancellationToken);

    public Task<AuthenticationResult> SignUpAsync(
        AuthEmail email,
        AuthPassword password,
        CancellationToken cancellationToken = default) =>
        ExecuteSessionMutationAsync(
            () => SendCredentialsAsync("signup", email, password, false, cancellationToken), cancellationToken);

    public Task<AuthenticationResult> SignInAsync(
        AuthEmail email,
        AuthPassword password,
        CancellationToken cancellationToken = default) =>
        ExecuteSessionMutationAsync(
            () => SendCredentialsAsync("token?grant_type=password", email, password, true, cancellationToken),
            cancellationToken);

    public Task<AuthenticationResult> SignOutAsync(CancellationToken cancellationToken = default) =>
        ExecuteSessionMutationAsync(() => SignOutCoreAsync(cancellationToken), cancellationToken);

    private async Task<AuthenticationResult> SignOutCoreAsync(CancellationToken cancellationToken)
    {
        var secrets = await LoadSecretsAsync(cancellationToken).ConfigureAwait(false);
        if (secrets.Result is not null)
        {
            return secrets.Result;
        }

        if (secrets.Secrets is null)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.SessionMissing, "AUTH_SESSION_MISSING");
        }

        var response = await SendAsync(HttpMethod.Post, "logout?scope=local", null,
            secrets.Secrets.AccessToken, cancellationToken).ConfigureAwait(false);
        if (response.Result is not null)
        {
            return response.Result;
        }

        try
        {
            await sessionStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
            return AuthenticationResult.Success(null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuthenticationResult.CancelledResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                     or InvalidOperationException or NotSupportedException)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.SecureStorageFailed,
                "AUTH_SECURE_STORAGE_DELETE_FAILED");
        }
    }

    public Task<AuthenticationResult> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
        ExecuteSessionMutationAsync(() => RefreshSessionCoreAsync(cancellationToken), cancellationToken);

    private async Task<AuthenticationResult> RefreshSessionCoreAsync(CancellationToken cancellationToken)
    {
        var loaded = await LoadSecretsAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.Result is not null)
        {
            return loaded.Result;
        }

        if (loaded.Secrets is null)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.SessionMissing, "AUTH_SESSION_MISSING");
        }

        return await SendSessionAsync(
            "token?grant_type=refresh_token",
            new Dictionary<string, string> { ["refresh_token"] = loaded.Secrets.RefreshToken },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationResult> ExecuteSessionMutationAsync(
        Func<Task<AuthenticationResult>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        var entered = false;
        try
        {
            await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuthenticationResult.CancelledResult();
        }
        finally
        {
            if (entered) _sessionGate.Release();
        }
    }

    public async Task<AuthenticationResult> ResetPasswordAsync(
        AuthEmail email,
        CancellationToken cancellationToken = default)
    {
        if (!email.IsValid)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.InvalidRequest, "AUTH_EMAIL_INVALID");
        }

        var response = await SendAsync(HttpMethod.Post, "recover",
            new Dictionary<string, string> { ["email"] = email.Value }, null, cancellationToken).ConfigureAwait(false);
        return response.Result ?? AuthenticationResult.Success(null, null);
    }

    public async Task<AuthenticationResult> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var secrets = await LoadSecretsAsync(cancellationToken).ConfigureAwait(false);
        if (secrets.Result is not null)
        {
            return secrets.Result;
        }

        if (secrets.Secrets is null)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.SessionMissing, "AUTH_SESSION_MISSING");
        }

        var response = await SendAsync(HttpMethod.Get, "user", null,
            secrets.Secrets.AccessToken, cancellationToken).ConfigureAwait(false);
        if (response.Result is not null)
        {
            return response.Result;
        }

        AuthProfile? profile;
        try
        {
            profile = await ReadProfileAsync(response.Response!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuthenticationResult.CancelledResult();
        }
        return profile is null
            ? AuthenticationResult.Failure(AuthenticationFailureReason.ProtocolError, "AUTH_PROFILE_RESPONSE_INVALID")
            : AuthenticationResult.Success(new(profile, secrets.Secrets.ExpiresAt), profile);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _sessionGate.Dispose();
        }
    }

    private async Task<AuthenticationResult> SendCredentialsAsync(
        string route,
        AuthEmail email,
        AuthPassword password,
        bool requiresSession,
        CancellationToken cancellationToken)
    {
        if (!email.IsValid || password is null)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.InvalidRequest, "AUTH_CREDENTIALS_INVALID");
        }

        return requiresSession
            ? await SendSessionAsync(route,
                new Dictionary<string, string> { ["email"] = email.Value, ["password"] = password.Value },
                cancellationToken).ConfigureAwait(false)
            : await SendSignUpAsync(route, email, password, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationResult> SendSignUpAsync(
        string route,
        AuthEmail email,
        AuthPassword password,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, route,
            new Dictionary<string, string> { ["email"] = email.Value, ["password"] = password.Value },
            null, cancellationToken).ConfigureAwait(false);
        if (response.Result is not null)
        {
            return response.Result;
        }

        AuthPayload? payload;
        try
        {
            payload = await ReadPayloadAsync(response.Response!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuthenticationResult.CancelledResult();
        }
        if (payload?.AccessToken is not null && payload.RefreshToken is not null)
        {
            return await PersistAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        var profile = payload?.ToProfile();
        return profile is null
            ? AuthenticationResult.Failure(AuthenticationFailureReason.ProtocolError, "AUTH_SIGNUP_RESPONSE_INVALID")
            : AuthenticationResult.Success(null, profile);
    }

    private async Task<AuthenticationResult> SendSessionAsync(
        string route,
        object body,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, route, body, null, cancellationToken).ConfigureAwait(false);
        if (response.Result is not null)
        {
            return response.Result;
        }

        AuthPayload? payload;
        try
        {
            payload = await ReadPayloadAsync(response.Response!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuthenticationResult.CancelledResult();
        }
        return payload is null
            ? AuthenticationResult.Failure(AuthenticationFailureReason.ProtocolError, "AUTH_SESSION_RESPONSE_INVALID")
            : await PersistAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationResult> PersistAsync(AuthPayload payload, CancellationToken cancellationToken)
    {
        var profile = payload.ToProfile();
        var expiresIn = payload.ExpiresIn;
        if (profile is null || string.IsNullOrWhiteSpace(payload.AccessToken)
                            || string.IsNullOrWhiteSpace(payload.RefreshToken)
                            || !IsSafeToken(payload.AccessToken)
                            || !IsSafeToken(payload.RefreshToken)
                            || expiresIn is not > 0)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.ProtocolError, "AUTH_SESSION_RESPONSE_INVALID");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value);
        try
        {
            await sessionStore.SaveAsync(new(payload.AccessToken, payload.RefreshToken, expiresAt), cancellationToken)
                .ConfigureAwait(false);
            return AuthenticationResult.Success(new(profile, expiresAt), profile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuthenticationResult.CancelledResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                     or InvalidOperationException or NotSupportedException)
        {
            return AuthenticationResult.Failure(AuthenticationFailureReason.SecureStorageFailed,
                "AUTH_SECURE_STORAGE_SAVE_FAILED");
        }
    }

    private async Task<(AuthSessionSecrets? Secrets, AuthenticationResult? Result)> LoadSecretsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var secrets = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            return secrets is null || IsSafeToken(secrets.AccessToken) && IsSafeToken(secrets.RefreshToken)
                                   && secrets.ExpiresAt != default
                ? (secrets, null)
                : (null, AuthenticationResult.Failure(AuthenticationFailureReason.SecureStorageFailed,
                    "AUTH_SECURE_STORAGE_CONTENT_INVALID"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, AuthenticationResult.CancelledResult());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                     or InvalidOperationException or NotSupportedException)
        {
            return (null, AuthenticationResult.Failure(AuthenticationFailureReason.SecureStorageFailed,
                "AUTH_SECURE_STORAGE_LOAD_FAILED"));
        }
    }

    private async Task<(HttpResponseMessage? Response, AuthenticationResult? Result)> SendAsync(
        HttpMethod method,
        string route,
        object? body,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        if (!options.IsValid)
        {
            return (null, AuthenticationResult.Failure(AuthenticationFailureReason.InvalidRequest,
                "AUTH_CONFIGURATION_INVALID"));
        }

        try
        {
            using var request = new HttpRequestMessage(method, new Uri(options.ProjectUri, $"auth/v1/{route}"));
            request.Headers.TryAddWithoutValidation("apikey", options.PublishableKey);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.Authorization = new("Bearer", accessToken);
            }
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: JsonOptions);
            }

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (response, null);
            }

            var statusCode = response.StatusCode;
            response.Dispose();
            return (null, AuthenticationResult.Failure(
                statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity
                    ? AuthenticationFailureReason.Rejected
                    : AuthenticationFailureReason.Unavailable,
                statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity
                    ? "AUTH_REQUEST_REJECTED"
                    : "AUTH_SERVICE_UNAVAILABLE"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, AuthenticationResult.CancelledResult());
        }
        catch (HttpRequestException)
        {
            return (null, AuthenticationResult.Failure(AuthenticationFailureReason.Unavailable,
                "AUTH_SERVICE_UNAVAILABLE"));
        }
    }

    private static async Task<AuthPayload?> ReadPayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using (response)
        {
            try
            {
                return await ReadBoundedJsonAsync<AuthPayload>(response.Content, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }
    }

    private static async Task<AuthProfile?> ReadProfileAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using (response)
        {
            try
            {
                var user = await ReadBoundedJsonAsync<AuthUser>(response.Content, cancellationToken)
                    .ConfigureAwait(false);
                return user?.ToProfile();
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }
    }

    private static async Task<T?> ReadBoundedJsonAsync<T>(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            return default;
        }

        await content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        return await content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSafeToken(string token) =>
        !string.IsNullOrWhiteSpace(token) && token.Length <= 2_560 && token.All(IsSafeCredentialCharacter);

    private static bool IsSafeCredentialCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~' or '+' or '/' or '=';

    private sealed record AuthPayload(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("user")] AuthUser? User,
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("user_metadata")] Dictionary<string, JsonElement>? Metadata)
    {
        public AuthProfile? ToProfile() =>
            (User ?? (Id == Guid.Empty ? null : new AuthUser(Id, Email, Metadata)))?.ToProfile();
    }

    private sealed record AuthUser(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("user_metadata")] Dictionary<string, JsonElement>? Metadata,
        [property: JsonPropertyName("is_anonymous")] bool IsAnonymous = false)
    {
        private const int MaximumDisplayNameLength = 256;

        public AuthProfile? ToProfile()
        {
            try
            {
                AuthEmail? email = string.IsNullOrWhiteSpace(Email) ? null : new AuthEmail(Email);
                string? displayName = null;
                if (Metadata is not null)
                {
                    foreach (var key in new[] { "display_name", "full_name", "name" })
                    {
                        if (Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            displayName = value.GetString()?.Trim();
                            if (displayName?.Length > MaximumDisplayNameLength
                                || displayName?.Any(char.IsControl) == true)
                            {
                                return null;
                            }
                            break;
                        }
                    }
                }
                return Id == Guid.Empty || email is null && !IsAnonymous ? null : new(Id, email, displayName, IsAnonymous);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
