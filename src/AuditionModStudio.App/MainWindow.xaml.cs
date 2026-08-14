using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AuditionModStudio.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainPage mainPage, ILogger<MainWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(mainPage);
        ArgumentNullException.ThrowIfNull(logger);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RootContent.Content = mainPage;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        logger.LogDebug("Main window initialized");
    }
}
