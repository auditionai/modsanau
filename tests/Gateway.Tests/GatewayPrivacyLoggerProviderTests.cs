using AuditionModStudio.Core.Diagnostics;
using AuditionModStudio.Gateway.Security;

namespace Gateway.Tests;

public sealed class GatewayPrivacyLoggerProviderTests
{
    [Fact]
    public void Sanitizer_redacts_structured_and_unstructured_secrets_in_message_and_exception()
    {
        const string password = "arbitrary-structured-value";
        const string bearer = "Bearer abcdefghijklmnopqrstuvwxyz";
        const string paymentSecret = "payment_webhook_secret=payment-private-value";
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Password", password),
            new("SafeCode", "SAFE_DIAGNOSTIC"),
        };

        var result = GatewayPrivacyLogSanitizer.Sanitize(
            state,
            new InvalidOperationException(paymentSecret),
            (_, _) => $"Password {password}; Authorization {bearer}; SAFE_DIAGNOSTIC",
            new SensitiveDataRedactor());

        Assert.DoesNotContain(password, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("payment-private-value", result.ExceptionText, StringComparison.Ordinal);
        Assert.Contains("SAFE_DIAGNOSTIC", result.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.ExceptionText, StringComparison.Ordinal);
    }
}
