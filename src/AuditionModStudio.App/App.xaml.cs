using AuditionModStudio.App.Bootstrap;
using AuditionModStudio.Infrastructure.Processes;
using AuditionModStudio.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AuditionModStudio.App;

/// <summary>
/// Owns the Windows UI lifecycle and delegates application composition to the bootstrapper.
/// </summary>
public partial class App : Application
{
    private ApplicationBootstrapper? _bootstrapper;
    private bool _closeAfterShutdown;
    private ILogger<App>? _logger;
    private int _shutdownState;
    private Window? _window;

    internal Window? ActiveWindow => _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _bootstrapper = new ApplicationBootstrapper();
            await _bootstrapper.StartAsync();
            _logger = _bootstrapper.Services.GetRequiredService<ILogger<App>>();
            var mainWindow = _bootstrapper.Services.GetRequiredService<MainWindow>();
            WindowsProcessLaunchHardening.ApplyProcessDllPolicy();
            mainWindow.AppWindow.Closing += OnMainWindowClosing;
            _window = mainWindow;
            _window.Activate();
            _logger.LogInformation("Main window activated");
        }
        catch (Exception exception)
        {
            WriteEarlyStartupDiagnostic(exception);
            LogCritical(exception, "Application launch failed");
            WindowsErrorDialog.ShowFatalStartupError();
            await ShutdownAsync();
            Exit();
        }
    }

    private static void WriteEarlyStartupDiagnostic(Exception exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuditionModStudio",
                "Logs");
            Directory.CreateDirectory(root);
            var redactor = new SensitiveDataRedactor();
            var diagnostic = redactor.Redact(
                $"Type={exception.GetType().FullName}; HResult=0x{exception.HResult:X8}; " +
                $"InnerType={exception.InnerException?.GetType().FullName ?? "NONE"}; " +
                $"InnerMessage={exception.InnerException?.Message ?? "NONE"}{Environment.NewLine}" +
                exception);
            File.AppendAllText(
                Path.Combine(root, "early-startup.log"),
                $"[{DateTimeOffset.UtcNow:O}] {diagnostic}{Environment.NewLine}");
        }
        catch (Exception diagnosticException)
        {
            System.Diagnostics.Debug.WriteLine(diagnosticException);
        }
    }

    private async void OnMainWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeAfterShutdown)
        {
            return;
        }

        args.Cancel = true;

        try
        {
            await ShutdownAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
        finally
        {
            _closeAfterShutdown = true;
            _window?.Close();
        }
    }

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownState, 1) != 0)
        {
            return;
        }

        if (_bootstrapper is not null)
        {
            await _bootstrapper.DisposeAsync();
        }

        UnhandledException -= OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnXamlUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        LogCritical(args.Exception, "Unhandled XAML exception");
        WindowsErrorDialog.ShowUnexpectedError();
        _bootstrapper?.FlushLogsForProcessTermination();

        // Do not set args.Handled. Continuing after an unknown XAML exception can leave
        // the UI runtime in an inconsistent state.
    }

    private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
    {
        var exception = args.ExceptionObject as Exception
            ?? new InvalidOperationException("A non-Exception object reached the AppDomain handler.");

        LogCritical(
            exception,
            "Unhandled AppDomain exception; terminating: {IsTerminating}",
            args.IsTerminating);
        _bootstrapper?.FlushLogsForProcessTermination();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        _logger?.LogError(args.Exception, "Unobserved task exception");
        args.SetObserved();
    }

    private void LogCritical(Exception exception, string message, params object?[] arguments)
    {
        if (_logger is not null)
        {
            _logger.LogCritical(exception, message, arguments);
            return;
        }

        try
        {
            _bootstrapper?
                .Services
                .GetService<ILogger<App>>()?
                .LogCritical(exception, message, arguments);
        }
        catch (ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }
}
