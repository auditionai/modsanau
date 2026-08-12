using AuditionModStudio.Core.Auth;

namespace Core.Tests;

public sealed class AuthenticationContractTests
{
    [Fact]
    public void Email_is_normalized_and_rejects_invalid_or_oversized_values()
    {
        Assert.Equal("person@example.com", new AuthEmail("  person@example.com  ").Value);
        Assert.Throws<ArgumentException>(() => new AuthEmail("not-an-email"));
        Assert.Throws<ArgumentException>(() => new AuthEmail(new string('a', AuthEmail.MaximumLength + 1)));
    }

    [Fact]
    public void Password_and_session_secrets_are_redacted_from_string_representation()
    {
        var password = new AuthPassword("do-not-log-this");
        var secrets = new AuthSessionSecrets("access-secret", "refresh-secret", DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal("[REDACTED]", password.ToString());
        Assert.Equal("[REDACTED]", secrets.ToString());
        Assert.DoesNotContain(password.Value, password.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.AccessToken, secrets.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new AuthPassword("unsafe\0password"));
    }

    [Fact]
    public void Contract_exposes_the_plan_58_operation_set()
    {
        var operations = typeof(IAuthenticationService).GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([
            "GetProfileAsync",
            "RefreshSessionAsync",
            "ResetPasswordAsync",
            "SignInAsync",
            "SignOutAsync",
            "SignUpAsync",
        ], operations);
    }
}
