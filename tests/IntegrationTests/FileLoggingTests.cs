using AuditionModStudio.Infrastructure.Logging;
using AuditionModStudio.Infrastructure.Paths;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests;

public sealed class FileLoggingTests
{
    [Fact]
    public void Logger_writes_level_timestamp_message_and_exception_stack_trace()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "AuditionModStudio.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var paths = new AppPaths(testRoot);
            using (var logSession = new ApplicationLogSession(paths))
            {
                var services = new ServiceCollection();
                logSession.ConfigureServices(services);

                using var provider = services.BuildServiceProvider();
                var logger = provider.GetRequiredService<ILogger<FileLoggingTests>>();

                try
                {
                    ThrowTestException();
                }
                catch (InvalidOperationException exception)
                {
                    logger.LogError(exception, "Expected logging test failure for {Component}", "FileLogger");
                }
            }

            var logFile = Assert.Single(Directory.GetFiles(paths.LogsDirectory, "*.log"));
            var logText = File.ReadAllText(logFile);

            Assert.Contains(" ERR]", logText, StringComparison.Ordinal);
            Assert.Contains("Expected logging test failure for FileLogger", logText, StringComparison.Ordinal);
            Assert.Contains(nameof(InvalidOperationException), logText, StringComparison.Ordinal);
            Assert.Contains(nameof(ThrowTestException), logText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Logger_redacts_named_secrets_tokens_signed_urls_and_exception_text_at_the_sink()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "AuditionModStudio.Tests", Guid.NewGuid().ToString("N"));
        var password = "opaque-password-material";
        var bearer = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3OCJ9.c2lnbmF0dXJlMTIzNDU2";
        var signedUrl = "https://storage.example.invalid/private/template.ab?X-Amz-Signature=signed-value&X-Amz-Credential=credential";
        var providerSecret = "opaque-provider-material";
        var paymentSecret = "opaque-payment-material";
        try
        {
            var paths = new AppPaths(testRoot);
            using (var logSession = new ApplicationLogSession(paths))
            {
                var services = new ServiceCollection();
                logSession.ConfigureServices(services);
                using var provider = services.BuildServiceProvider();
                var logger = provider.GetRequiredService<ILogger<FileLoggingTests>>();
                logger.LogError(new InvalidOperationException($"failure at {signedUrl}"),
                    "Privacy values {Password} {Authorization} {SignedUrl} {ProviderSecret} {PaymentWebhookSecret}",
                    password, $"Bearer {bearer}", signedUrl, providerSecret, paymentSecret);
            }

            var text = File.ReadAllText(Assert.Single(Directory.GetFiles(paths.LogsDirectory, "*.log")));
            Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
            Assert.Contains("[REDACTED_SIGNED_URL]", text, StringComparison.Ordinal);
            Assert.DoesNotContain(password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(bearer, text, StringComparison.Ordinal);
            Assert.DoesNotContain(signedUrl, text, StringComparison.Ordinal);
            Assert.DoesNotContain(providerSecret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(paymentSecret, text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void ThrowTestException()
    {
        throw new InvalidOperationException("Test exception");
    }
}
