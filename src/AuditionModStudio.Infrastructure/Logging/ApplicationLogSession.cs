using AuditionModStudio.Core.Paths;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AuditionModStudio.Infrastructure.Logging;

/// <summary>
/// Owns the file logger for one application process and flushes it when disposed.
/// </summary>
public sealed class ApplicationLogSession : IDisposable
{
    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] " +
        "{Message:lj} {Properties:j}{NewLine}{Exception}";

    private readonly Serilog.Core.Logger _logger;
    private int _disposed;

    public ApplicationLogSession(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.LogsDirectory);

        LogFilePattern = Path.Combine(paths.LogsDirectory, "audition-mod-studio-.log");
        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                LogFilePattern,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: OutputTemplate)
            .CreateLogger();
    }

    public string LogFilePattern { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(_logger, dispose: false);
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _logger.Dispose();
    }
}
