using AuditionModStudio.App.Shell;
using AuditionModStudio.App.Home;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App;

public sealed partial class MainPage : Page
{
    private readonly HomePage _homePage;

    public MainPage(AppShellViewModel viewModel, HomePage homePage)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _homePage = homePage ?? throw new ArgumentNullException(nameof(homePage));
        InitializeComponent();
        PopulateNavigationItems();
        HomeContent.Content = _homePage;
        UpdateRouteContent();
    }

    public AppShellViewModel ViewModel { get; }

    private void PopulateNavigationItems()
    {
        foreach (var item in ViewModel.NavigationItems)
        {
            var navigationItem = new NavigationViewItem
            {
                Content = item.Label,
                Icon = new FontIcon { Glyph = item.Glyph },
                Tag = item.Route
            };

            AutomationProperties.SetName(navigationItem, item.Label);
            ShellNavigation.MenuItems.Add(navigationItem);
        }

        ShellNavigation.SelectedItem = ShellNavigation.MenuItems[0];
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not AppRoute route)
        {
            return;
        }

        if (ViewModel.Navigate(route))
        {
            UpdateRouteContent();
            if (route == AppRoute.Home)
            {
                _homePage.FocusPrimaryHeading();
            }
            else
            {
                ContentHeading.Focus(FocusState.Programmatic);
            }
        }
    }

    private void UpdateRouteContent()
    {
        var isHome = ViewModel.CurrentRoute == AppRoute.Home;
        HomeContent.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderContent.Visibility = isHome ? Visibility.Collapsed : Visibility.Visible;
    }
}
