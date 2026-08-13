using AuditionModStudio.App.Shell;
using AuditionModStudio.App.AiStudio;
using AuditionModStudio.App.Editor;
using AuditionModStudio.App.Home;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.App.Account;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App;

public sealed partial class MainPage : Page
{
    private readonly HomePage _homePage;
    private readonly ProjectWorkspacePage _workspacePage;
    private readonly ImageEditorPage _imageEditorPage;
    private readonly AiStudioPage _aiStudioPage;
    private readonly AccountPage _accountPage;

    public MainPage(
        AppShellViewModel viewModel,
        HomePage homePage,
        ProjectWorkspacePage workspacePage,
        AiStudioPage aiStudioPage,
        ImageEditorPage imageEditorPage,
        AccountPage accountPage)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _homePage = homePage ?? throw new ArgumentNullException(nameof(homePage));
        _workspacePage = workspacePage ?? throw new ArgumentNullException(nameof(workspacePage));
        _aiStudioPage = aiStudioPage ?? throw new ArgumentNullException(nameof(aiStudioPage));
        _imageEditorPage = imageEditorPage ?? throw new ArgumentNullException(nameof(imageEditorPage));
        _accountPage = accountPage ?? throw new ArgumentNullException(nameof(accountPage));
        InitializeComponent();
        PopulateNavigationItems();
        HomeContent.Content = _homePage;
        WorkspaceContent.Content = _workspacePage;
        AiStudioContent.Content = _aiStudioPage;
        ImageEditorContent.Content = _imageEditorPage;
        AccountContent.Content = _accountPage;
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
            if (route != AppRoute.AiStudio)
            {
                _aiStudioPage.Deactivate();
            }
            if (route != AppRoute.Account)
            {
                _accountPage.Deactivate();
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
            else if (route == AppRoute.AiStudio)
            {
                _aiStudioPage.FocusPrimaryHeading();
                await _aiStudioPage.ActivateAsync();
            }
            else if (route == AppRoute.Account)
            {
                _accountPage.FocusPrimaryHeading();
                await _accountPage.ActivateAsync();
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
        var isAiStudio = ViewModel.CurrentRoute == AppRoute.AiStudio;
        var isAccount = ViewModel.CurrentRoute == AppRoute.Account;
        HomeContent.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceContent.Visibility = isWorkspace ? Visibility.Visible : Visibility.Collapsed;
        ImageEditorContent.Visibility = isImageEditor ? Visibility.Visible : Visibility.Collapsed;
        AiStudioContent.Visibility = isAiStudio ? Visibility.Visible : Visibility.Collapsed;
        AccountContent.Visibility = isAccount ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderContent.Visibility = isHome || isWorkspace || isImageEditor || isAiStudio || isAccount
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
