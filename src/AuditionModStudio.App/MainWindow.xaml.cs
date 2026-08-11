using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AuditionModStudio.App;

/// <summary>
/// The application window hosts the injected application shell and owns only
/// window-specific wiring.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(MainPage mainPage, ILogger<MainWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(mainPage);
        ArgumentNullException.ThrowIfNull(logger);
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootContent.Content = mainPage;
        logger.LogDebug("Main window initialized");
    }
}
