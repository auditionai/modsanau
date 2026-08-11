using AuditionModStudio.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App;

public sealed partial class MainPage : Page
{
    public MainPage(AppShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        PopulateNavigationItems();
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
            ContentHeading.Focus(FocusState.Programmatic);
        }
    }
}
