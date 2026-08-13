using AuditionModStudio.Core.Diagnostics;

namespace AuditionModStudio.Gateway.Security;

public sealed class GatewayPrivacyLoggerProvider(ISensitiveDataRedactor redactor) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new GatewayPrivacyLogger(categoryName, redactor);
    public void Dispose() { }

    private sealed class GatewayPrivacyLogger(
        string categoryName,
        ISensitiveDataRedactor redactor) : ILogger
    {
        private static readonly object ConsoleGate = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sanitized = GatewayPrivacyLogSanitizer.Sanitize(
                state, exception, formatter, redactor);
            var category = categoryName.Length <= 160 ? categoryName : categoryName[..160];
            lock (ConsoleGate)
            {
                Console.Error.WriteLine("[{0:O} {1}] {2} [{3}] {4}{5}",
                    DateTimeOffset.UtcNow, logLevel, category, eventId.Id,
                    sanitized.Message, sanitized.ExceptionText);
            }
        }
    }
}

internal static class GatewayPrivacyLogSanitizer
{
    internal static (string Message, string ExceptionText) Sanitize<TState>(
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter,
        ISensitiveDataRedactor redactor)
    {
        var message = formatter(state, exception);
        if (state is IEnumerable<KeyValuePair<string, object?>> properties)
        {
            foreach (var property in properties)
            {
                if (!redactor.IsSensitiveName(property.Key) || property.Value is null) continue;
                var sensitive = property.Value.ToString();
                if (!string.IsNullOrEmpty(sensitive))
                    message = message.Replace(sensitive, "[REDACTED]", StringComparison.Ordinal);
            }
        }
        message = redactor.Redact(message);
        var exceptionText = exception is null ? string.Empty
            : Environment.NewLine + redactor.Redact(exception.ToString());
        return (message, exceptionText);
    }
}
