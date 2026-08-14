using AuditionModStudio.App.Account;
using AuditionModStudio.App.AiStudio;
using AuditionModStudio.App.Editor;
using AuditionModStudio.App.Home;
using AuditionModStudio.App.Settings;
using AuditionModStudio.App.Shell;
using AuditionModStudio.App.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace AuditionModStudio.App;

public sealed partial class MainPage : Page
{
    private const int MaximumActivityEntries = 100;
    private readonly ObservableCollection<ActivityLogEntry> _allActivity = [];
    private readonly ObservableCollection<ActivityLogEntry> _visibleActivity = [];
    private readonly ObservableCollection<ShellCommand> _allCommands = [];
    private readonly HomePage _homePage;
    private readonly ProjectWorkspacePage _workspacePage;
    private readonly ImageEditorPage _imageEditorPage;
    private readonly AiStudioPage _aiStudioPage;
    private readonly AccountPage _accountPage;
    private readonly SettingsPage _settingsPage;
    private readonly IUserActivityService _activityService;

    public MainPage(AppShellViewModel viewModel, HomePage homePage, ProjectWorkspacePage workspacePage, AiStudioPage aiStudioPage, ImageEditorPage imageEditorPage, AccountPage accountPage, SettingsPage settingsPage, IUserActivityService activityService)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _homePage = homePage ?? throw new ArgumentNullException(nameof(homePage));
        _workspacePage = workspacePage ?? throw new ArgumentNullException(nameof(workspacePage));
        _aiStudioPage = aiStudioPage ?? throw new ArgumentNullException(nameof(aiStudioPage));
        _imageEditorPage = imageEditorPage ?? throw new ArgumentNullException(nameof(imageEditorPage));
        _accountPage = accountPage ?? throw new ArgumentNullException(nameof(accountPage));
        _settingsPage = settingsPage ?? throw new ArgumentNullException(nameof(settingsPage));
        _activityService = activityService ?? throw new ArgumentNullException(nameof(activityService));
        InitializeComponent();
        PopulateNavigationItems();
        HomeContent.Content = _homePage;
        WorkspaceContent.Content = _workspacePage;
        AiStudioContent.Content = _aiStudioPage;
        ImageEditorContent.Content = _imageEditorPage;
        AccountContent.Content = _accountPage;
        SettingsContent.Content = _settingsPage;
        ActivityList.ItemsSource = _visibleActivity;
        CommandList.ItemsSource = _allCommands;
        foreach (var item in ViewModel.NavigationItems)
            _allCommands.Add(new ShellCommand(item.Label, item.Description, item.Glyph, item.Route, false));
        if (_settingsPage.UpdateServiceAvailable)
            _allCommands.Add(new ShellCommand("Kiểm tra cập nhật", "Kiểm tra kênh stable trong nền", "\uE895", null, true));
        _homePage.ViewModel.PropertyChanged += OnHomePropertyChanged;
        _workspacePage.ViewModel.PropertyChanged += OnWorkspacePropertyChanged;
        _workspacePage.BuildExportViewModel.PropertyChanged += OnBuildExportPropertyChanged;
        _imageEditorPage.ViewModel.PropertyChanged += OnEditorPropertyChanged;
        _aiStudioPage.ViewModel.PropertyChanged += OnAiStudioPropertyChanged;
        _activityService.Published += OnExternalActivityPublished;
        _settingsPage.UpdateAvailable += OnUpdateAvailable;
        AddActivity("SUCCESS", "Ứng dụng đã sẵn sàng.");
        UpdateRouteContent();
        _ = _settingsPage.CheckForUpdatesAsync(false);
    }

    public AppShellViewModel ViewModel { get; }

    private void PopulateNavigationItems()
    {
        foreach (var item in ViewModel.NavigationItems)
        {
            var navigationItem = new NavigationViewItem
            {
                Content = item.Label,
                Icon = new FontIcon { Glyph = item.Glyph, FontSize = 18 },
                Style = (Style)Application.Current.Resources["AmsNavigationItemStyle"],
                Tag = item.Route,
            };
            AutomationProperties.SetName(navigationItem, item.Label);
            if (item.Route is AppRoute.Account or AppRoute.Settings)
                ShellNavigation.FooterMenuItems.Add(navigationItem);
            else
                ShellNavigation.MenuItems.Add(navigationItem);
        }
        ShellNavigation.SelectedItem = ShellNavigation.MenuItems[0];
    }

    private async void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not AppRoute route || !ViewModel.Navigate(route)) return;
        if (route != AppRoute.ImageEditor) _imageEditorPage.Deactivate();
        if (route != AppRoute.AiStudio) _aiStudioPage.Deactivate();
        if (route != AppRoute.Account) _accountPage.Deactivate();
        UpdateRouteContent();
        AddActivity("INFO", $"Đã mở {ViewModel.CurrentTitle}.");
        if (route == AppRoute.Home) _homePage.FocusPrimaryHeading();
        else if (route == AppRoute.Projects) { _workspacePage.FocusPrimaryHeading(); await _workspacePage.ActivateAsync(); }
        else if (route == AppRoute.ImageEditor) { _imageEditorPage.FocusPrimaryHeading(); await _imageEditorPage.ActivateAsync(); }
        else if (route == AppRoute.AiStudio) { _aiStudioPage.FocusPrimaryHeading(); await _aiStudioPage.ActivateAsync(); }
        else if (route == AppRoute.Account) { _accountPage.FocusPrimaryHeading(); await _accountPage.ActivateAsync(); }
        else if (route == AppRoute.Settings) _settingsPage.FocusPrimaryHeading();
        else ContentHeading.Focus(FocusState.Programmatic);
    }

    private void OnCommandPaletteInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenCommandPalette();
    }

    private void OnCommandPaletteClicked(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void OpenCommandPalette()
    {
        CommandSearch.Text = string.Empty;
        ApplyCommandFilter(string.Empty);
        CommandPalette.Visibility = Visibility.Visible;
        CommandSearch.Focus(FocusState.Programmatic);
    }

    private void OnCloseCommandPaletteClicked(object sender, RoutedEventArgs e) =>
        CommandPalette.Visibility = Visibility.Collapsed;

    private void OnCommandSearchChanged(object sender, TextChangedEventArgs e) =>
        ApplyCommandFilter(CommandSearch.Text);

    private void ApplyCommandFilter(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        CommandList.ItemsSource = string.IsNullOrEmpty(normalized)
            ? _allCommands
            : _allCommands.Where(command =>
                command.Label.Contains(normalized, StringComparison.CurrentCultureIgnoreCase)
                || command.Description.Contains(normalized, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private void OnCommandItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ShellCommand command) return;
        CommandPalette.Visibility = Visibility.Collapsed;
        if (command.IsUpdateCheck)
        {
            SelectRoute(AppRoute.Settings);
            _ = _settingsPage.CheckForUpdatesAsync(true);
        }
        else if (command.Route is AppRoute route)
        {
            SelectRoute(route);
        }
    }

    private void SelectRoute(AppRoute route)
    {
        foreach (var item in ShellNavigation.MenuItems.Concat(ShellNavigation.FooterMenuItems).OfType<NavigationViewItem>())
        {
            if (item.Tag is AppRoute itemRoute && itemRoute == route)
            {
                ShellNavigation.SelectedItem = item;
                return;
            }
        }
    }

    private void OnToggleActivityClicked(object sender, RoutedEventArgs e)
    {
        ActivityDrawer.Visibility = ActivityDrawer.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (ActivityDrawer.Visibility == Visibility.Visible && AutoScrollToggle.IsOn && _visibleActivity.Count > 0)
            ActivityList.ScrollIntoView(_visibleActivity[^1]);
    }

    private void OnActivitySearchChanged(object sender, TextChangedEventArgs e) =>
        ApplyActivityFilter(ActivitySearch.Text);

    private void AddActivity(string severity, string message)
    {
        var entry = ActivityLogEntry.Create(severity, message);
        _allActivity.Add(entry);
        while (_allActivity.Count > MaximumActivityEntries) _allActivity.RemoveAt(0);
        ApplyActivityFilter(ActivitySearch?.Text);
        ActivityCountText.Text = _allActivity.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ActivityDrawer?.Visibility == Visibility.Visible && AutoScrollToggle?.IsOn == true && _visibleActivity.Count > 0)
            ActivityList.ScrollIntoView(_visibleActivity[^1]);
    }

    private void OnExternalActivityPublished(object? sender, UserActivityEvent activity)
    {
        if (DispatcherQueue.HasThreadAccess) AddActivity(activity.Severity, activity.Message);
        else DispatcherQueue.TryEnqueue(() => AddActivity(activity.Severity, activity.Message));
    }

    private void OnUpdateAvailable(object? sender, Version version)
    {
        NotificationBar.Title = "Có bản cập nhật mới";
        NotificationBar.Message = $"Phiên bản {version} đã sẵn sàng trong Cài đặt → Cập nhật.";
        NotificationBar.Severity = InfoBarSeverity.Informational;
        NotificationBar.IsOpen = true;
    }

    private void ApplyActivityFilter(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        _visibleActivity.Clear();
        foreach (var entry in _allActivity.Where(entry => string.IsNullOrEmpty(normalized)
                     || entry.Message.Contains(normalized, StringComparison.CurrentCultureIgnoreCase)
                     || entry.Severity.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
            _visibleActivity.Add(entry);
    }

    private void OnHomePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(HomeViewModel.StatusMessage)
            && _homePage.ViewModel.StatusMessage.StartsWith("Đã tạo dự án", StringComparison.Ordinal))
            CompleteActivity(_homePage.ViewModel.StatusMessage, "Dự án mới đã sẵn sàng.");
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ProjectWorkspaceViewModel.StatusMessage)
            && _workspacePage.ViewModel.StatusMessage.StartsWith("Đã tải thông tin", StringComparison.Ordinal))
            AddActivity("SUCCESS", _workspacePage.ViewModel.StatusMessage);
    }

    private void OnBuildExportPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(BuildExportViewModel.StatusMessage)) return;
        var message = _workspacePage.BuildExportViewModel.StatusMessage;
        if (message == "Đã Build và xuất tệp Mod thành công.")
            CompleteActivity(message, "Build & Xuất tệp đã hoàn tất.");
        else if (message.StartsWith("Đã hủy Build", StringComparison.Ordinal)) AddActivity("WARNING", message);
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ImageEditorViewModel.ApplyStatus)) return;
        var message = _imageEditorPage.ViewModel.ApplyStatus;
        if (message.StartsWith("Đã áp dụng Texture", StringComparison.Ordinal))
            CompleteActivity(message, "Texture đã được áp dụng an toàn.");
    }

    private void OnAiStudioPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AiStudioViewModel.StatusMessage)) return;
        var message = _aiStudioPage.ViewModel.StatusMessage;
        if (message.StartsWith("Đã áp dụng kết quả AI", StringComparison.Ordinal))
            CompleteActivity(message, "Kết quả AI đã được áp dụng.");
    }

    private void CompleteActivity(string activityMessage, string notificationMessage)
    {
        AddActivity("SUCCESS", activityMessage);
        NotificationBar.Title = "Hoàn tất";
        NotificationBar.Message = notificationMessage;
        NotificationBar.Severity = InfoBarSeverity.Success;
        NotificationBar.IsOpen = true;
    }

    private void UpdateRouteContent()
    {
        var route = ViewModel.CurrentRoute;
        HomeContent.Visibility = route == AppRoute.Home ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceContent.Visibility = route == AppRoute.Projects ? Visibility.Visible : Visibility.Collapsed;
        ImageEditorContent.Visibility = route == AppRoute.ImageEditor ? Visibility.Visible : Visibility.Collapsed;
        AiStudioContent.Visibility = route == AppRoute.AiStudio ? Visibility.Visible : Visibility.Collapsed;
        AccountContent.Visibility = route == AppRoute.Account ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility = route == AppRoute.Settings ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderContent.Visibility = Visibility.Collapsed;
    }

    private sealed record ShellCommand(string Label, string Description, string Glyph, AppRoute? Route, bool IsUpdateCheck);

    private sealed record ActivityLogEntry(string Time, string Severity, string Message, string Glyph, Brush Brush)
    {
        public static ActivityLogEntry Create(string severity, string message)
        {
            var (glyph, resource) = severity switch
            {
                "SUCCESS" => ("\uE73E", "AmsSuccessBrush"),
                "WARNING" => ("\uE7BA", "AmsWarningBrush"),
                "ERROR" => ("\uEA39", "AmsErrorBrush"),
                _ => ("\uE946", "AmsInfoBrush")
            };
            var brush = (Brush)Application.Current.Resources[resource];
            return new ActivityLogEntry(DateTime.Now.ToString("HH:mm"), severity, message, glyph, brush);
        }
    }
}
