using AuditionModStudio.App.Shell;
using AuditionModStudio.App.Editor;
using AuditionModStudio.App.Home;
using AuditionModStudio.App.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App;

public sealed partial class MainPage : Page
{
    private readonly HomePage _homePage;
    private readonly ProjectWorkspacePage _workspacePage;
    private readonly ImageEditorPage _imageEditorPage;

    public MainPage(
        AppShellViewModel viewModel,
        HomePage homePage,
        ProjectWorkspacePage workspacePage,
        ImageEditorPage imageEditorPage)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _homePage = homePage ?? throw new ArgumentNullException(nameof(homePage));
        _workspacePage = workspacePage ?? throw new ArgumentNullException(nameof(workspacePage));
        _imageEditorPage = imageEditorPage ?? throw new ArgumentNullException(nameof(imageEditorPage));
        InitializeComponent();
        PopulateNavigationItems();
        HomeContent.Content = _homePage;
        WorkspaceContent.Content = _workspacePage;
        ImageEditorContent.Content = _imageEditorPage;
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

    private async void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not AppRoute route)
        {
            return;
        }

        if (ViewModel.Navigate(route))
        {
            if (route != AppRoute.ImageEditor)
            {
                _imageEditorPage.Deactivate();
            }

            UpdateRouteContent();
            if (route == AppRoute.Home)
            {
                _homePage.FocusPrimaryHeading();
            }
            else if (route == AppRoute.Projects)
            {
                _workspacePage.FocusPrimaryHeading();
                await _workspacePage.ActivateAsync();
            }
            else if (route == AppRoute.ImageEditor)
            {
                _imageEditorPage.FocusPrimaryHeading();
                await _imageEditorPage.ActivateAsync();
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
        var isWorkspace = ViewModel.CurrentRoute == AppRoute.Projects;
        var isImageEditor = ViewModel.CurrentRoute == AppRoute.ImageEditor;
        HomeContent.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceContent.Visibility = isWorkspace ? Visibility.Visible : Visibility.Collapsed;
        ImageEditorContent.Visibility = isImageEditor ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderContent.Visibility = isHome || isWorkspace || isImageEditor
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
