using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AuditionModStudio.App.Shell;

public sealed class AppShellViewModel : INotifyPropertyChanged
{
    private static readonly ImmutableArray<ShellNavigationItem> RouteCatalog =
    [
        new(AppRoute.Home, "Home", "\uE80F", "Home", "Choose a game and mod to begin a project."),
        new(AppRoute.Projects, "Projects", "\uE8B7", "Projects", "Open and manage Audition mod projects."),
        new(AppRoute.AiStudio, "AI Studio", "\uE945", "AI Studio", "Create trusted AI previews and review server job history."),
        new(AppRoute.ImageEditor, "Image Editor", "\uE91B", "Image Editor", "Crop and resize a selected project texture against its exact DDS target."),
        new(AppRoute.ModLibrary, "Mod Library", "\uE8F1", "Mod Library", "Browse supported mod definitions and templates."),
        new(AppRoute.Batch, "Batch", "\uE8FD", "Batch", "Batch workflows will be available in a later plan."),
        new(AppRoute.Cloud, "Cloud", "\uE753", "Cloud", "Cloud services are not connected in this build."),
        new(AppRoute.Settings, "Settings", "\uE713", "Settings", "Application settings will be available in a later plan.")
    ];

    private ShellNavigationItem _currentItem = RouteCatalog[0];

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<ShellNavigationItem> NavigationItems => RouteCatalog;

    public AppRoute CurrentRoute => _currentItem.Route;

    public string CurrentTitle => _currentItem.Title;

    public string CurrentDescription => _currentItem.Description;

    public string AccountStatus => "Signed out";

    public string CreditsStatus => "Credits unavailable";

    public string NotificationStatus => "No notifications";

    public string ConnectionStatus => "Offline";

    public bool Navigate(AppRoute route)
    {
        var nextItem = RouteCatalog.FirstOrDefault(item => item.Route == route);
        if (nextItem is null || nextItem == _currentItem)
        {
            return false;
        }

        _currentItem = nextItem;
        OnPropertyChanged(nameof(CurrentRoute));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentDescription));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
