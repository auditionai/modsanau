using AuditionModStudio.Core.Auth;

namespace AuditionModStudio.Cloud;

public sealed class UnavailableAuthenticationService : IAuthenticationService
{
    public Task<AuthenticationResult> SignUpAsync(AuthEmail email, AuthPassword password,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);
    public Task<AuthenticationResult> SignInAsync(AuthEmail email, AuthPassword password,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);
    public Task<AuthenticationResult> SignOutAsync(CancellationToken cancellationToken = default) =>
        UnavailableAsync(cancellationToken);
    public Task<AuthenticationResult> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
        UnavailableAsync(cancellationToken);
    public Task<AuthenticationResult> ResetPasswordAsync(AuthEmail email,
        CancellationToken cancellationToken = default) => UnavailableAsync(cancellationToken);
    public Task<AuthenticationResult> GetProfileAsync(CancellationToken cancellationToken = default) =>
        UnavailableAsync(cancellationToken);

    private static Task<AuthenticationResult> UnavailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(cancellationToken.IsCancellationRequested
            ? AuthenticationResult.CancelledResult()
            : AuthenticationResult.Failure(AuthenticationFailureReason.Unavailable,
                "AUTH_CONFIGURATION_UNAVAILABLE"));
}
