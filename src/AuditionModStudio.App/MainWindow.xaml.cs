using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using AuditionModStudio.App.Authentication;
using AuditionModStudio.Core.Auth;
using Microsoft.UI.Dispatching;

namespace AuditionModStudio.App;

public sealed partial class MainWindow : Window
{
    private readonly IDesktopAccessService _desktopAccess;
    private readonly IAuthenticationService _authentication;
    private readonly LoginPage _loginPage;
    private readonly MainPage _mainPage;
    private readonly DispatcherQueueTimer _heartbeat;

    public MainWindow(MainPage mainPage, LoginPage loginPage, IDesktopAccessService desktopAccess,
        IAuthenticationService authentication, ILogger<MainWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(mainPage);
        ArgumentNullException.ThrowIfNull(loginPage);
        ArgumentNullException.ThrowIfNull(desktopAccess);
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(logger);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _mainPage = mainPage;
        _loginPage = loginPage;
        _desktopAccess = desktopAccess;
        _authentication = authentication;
        RootContent.Content = loginPage;
        _heartbeat = DispatcherQueue.CreateTimer();
        _heartbeat.Interval = TimeSpan.FromMinutes(1);
        _heartbeat.IsRepeating = true;
        _heartbeat.Tick += OnHeartbeat;
        loginPage.AccessGranted += (_, _) =>
        {
            RootContent.Content = _mainPage;
            SignOutButton.Visibility = Visibility.Visible;
            _heartbeat.Start();
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        logger.LogDebug("Main window initialized");
    }

    private async void OnHeartbeat(DispatcherQueueTimer sender, object args)
    {
        var result = await _desktopAccess.EnsureAccessAsync();
        if (result.Succeeded) return;
        sender.Stop();
        RootContent.Content = _loginPage;
        _loginPage.ShowGateFailure(result.DiagnosticCode);
        SignOutButton.Visibility = Visibility.Collapsed;
    }

    private async void OnSignOutClicked(object sender, RoutedEventArgs e)
    {
        SignOutButton.IsEnabled = false;
        try
        {
            _heartbeat.Stop();
            await _authentication.SignOutAsync();
            _loginPage.ResetForSignIn();
            RootContent.Content = _loginPage;
            SignOutButton.Visibility = Visibility.Collapsed;
        }
        finally { SignOutButton.IsEnabled = true; }
    }
}
