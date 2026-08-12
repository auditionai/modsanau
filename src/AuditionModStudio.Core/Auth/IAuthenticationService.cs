namespace AuditionModStudio.Core.Auth;

public readonly record struct AuthEmail
{
    public const int MaximumLength = 320;

    public AuthEmail(string value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > MaximumLength
            || !System.Net.Mail.MailAddress.TryCreate(normalized, out var parsed)
            || !string.Equals(parsed.Address, normalized, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A valid bounded email address is required.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= MaximumLength;
    public override string ToString() => Value ?? string.Empty;
}

public sealed class AuthPassword
{
    public const int MaximumLength = 1_024;

    public AuthPassword(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength || value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A non-empty bounded password is required.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
    public override string ToString() => "[REDACTED]";
}

public sealed record AuthProfile(Guid UserId, AuthEmail Email, string? DisplayName);
public sealed record AuthSessionSnapshot(AuthProfile Profile, DateTimeOffset ExpiresAt);
public sealed class AuthSessionSecrets
{
    public AuthSessionSecrets(string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresAt = expiresAt;
    }

    public string AccessToken { get; }
    public string RefreshToken { get; }
    public DateTimeOffset ExpiresAt { get; }
    public override string ToString() => "[REDACTED]";
}

public enum AuthenticationFailureReason
{
    None,
    InvalidRequest,
    Unavailable,
    Rejected,
    SessionMissing,
    SecureStorageFailed,
    ProtocolError,
    Cancelled
}

public sealed record AuthenticationResult(
    bool Succeeded,
    bool Cancelled,
    AuthenticationFailureReason FailureReason,
    string DiagnosticCode,
    AuthSessionSnapshot? Session,
    AuthProfile? Profile)
{
    public static AuthenticationResult Success(AuthSessionSnapshot? session, AuthProfile? profile) =>
        new(true, false, AuthenticationFailureReason.None, "AUTH_COMPLETED", session, profile);
    public static AuthenticationResult Failure(AuthenticationFailureReason reason, string code) =>
        new(false, false, reason, code, null, null);
    public static AuthenticationResult CancelledResult() =>
        new(false, true, AuthenticationFailureReason.Cancelled, "AUTH_CANCELLED", null, null);
}

public interface ISecureSessionStore
{
    Task<AuthSessionSecrets?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AuthSessionSecrets session, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public interface IAuthenticationService
{
    Task<AuthenticationResult> SignUpAsync(AuthEmail email, AuthPassword password,
        CancellationToken cancellationToken = default);
    Task<AuthenticationResult> SignInAsync(AuthEmail email, AuthPassword password,
        CancellationToken cancellationToken = default);
    Task<AuthenticationResult> SignOutAsync(CancellationToken cancellationToken = default);
    Task<AuthenticationResult> RefreshSessionAsync(CancellationToken cancellationToken = default);
    Task<AuthenticationResult> ResetPasswordAsync(AuthEmail email,
        CancellationToken cancellationToken = default);
    Task<AuthenticationResult> GetProfileAsync(CancellationToken cancellationToken = default);
}
