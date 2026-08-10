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

    private static void ThrowTestException()
    {
        throw new InvalidOperationException("Test exception");
    }
}
