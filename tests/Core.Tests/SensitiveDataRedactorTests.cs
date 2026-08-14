using AuditionModStudio.Core.Diagnostics;

namespace Core.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwxyz")]
    [InlineData("password: super-private-password", "super-private-password")]
    [InlineData("{\"refresh_token\":\"refresh-private-value\"}", "refresh-private-value")]
    [InlineData("provider_secret: provider-private-value", "provider-private-value")]
    [InlineData("payment_webhook_secret=payment-private-value", "payment-private-value")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3OCJ9.c2lnbmF0dXJlMTIzNDU2",
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3OCJ9.c2lnbmF0dXJlMTIzNDU2")]
    [InlineData("https://storage.example.invalid/file?X-Amz-Signature=private&X-Amz-Credential=private",
        "storage.example.invalid")]
    [InlineData("whsec_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234", "ABCDEFGHIJKLMNOPQRSTUVWXYZ1234")]
    public void Redactor_removes_required_sensitive_values(string input, string forbidden)
    {
        var output = new SensitiveDataRedactor().Redact(input);

        Assert.DoesNotContain(forbidden, output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("AccessToken")]
    [InlineData("refresh_token")]
    [InlineData("ProviderApiKey")]
    [InlineData("PaymentWebhookSecret")]
    [InlineData("SignedUrl")]
    public void Sensitive_structured_property_names_are_recognized(string name)
    {
        Assert.True(new SensitiveDataRedactor().IsSensitiveName(name));
    }

    [Theory]
    [InlineData("SessionId")]
    [InlineData("DeviceId")]
    [InlineData("DiagnosticCode")]
    [InlineData("RouteTemplate")]
    public void Safe_operational_property_names_are_not_overclassified(string name)
    {
        Assert.False(new SensitiveDataRedactor().IsSensitiveName(name));
    }
}
