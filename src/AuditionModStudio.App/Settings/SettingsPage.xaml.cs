using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AuditionModStudio.App.Editor;
using AuditionModStudio.App.Shell;
using AuditionModStudio.Core.Tasks;
using AuditionModStudio.Updater;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App.Settings;

public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private readonly IPortableUpdateCoordinator _updateCoordinator;
    private readonly IBackgroundTaskManager _taskManager;
    private readonly ImageEditorViewModel _editorViewModel;
    private readonly IUserActivityService _activity;
    private string _themeStatus = "Đang dùng giao diện Creative Daylight.";
    private string _updateStatus = "Chưa kiểm tra cập nhật.";
    private string _lastCheck = "Chưa có";
    private bool _isUpdateBusy;
    private double _updateProgress;
    private bool _updateProgressIndeterminate;
    private PortableUpdateManifest? _availableManifest;
    private PortableStagedUpdate? _stagedUpdate;
    private CancellationTokenSource? _downloadCancellation;

    public SettingsPage(
        IPortableUpdateCoordinator updateCoordinator,
        IBackgroundTaskManager taskManager,
        ImageEditorViewModel editorViewModel,
        IUserActivityService activity)
    {
        _updateCoordinator = updateCoordinator ?? throw new ArgumentNullException(nameof(updateCoordinator));
        _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
        _editorViewModel = editorViewModel ?? throw new ArgumentNullException(nameof(editorViewModel));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        InitializeComponent();
#if PLAN102_UI_EVIDENCE_DARK
        ThemeSelector.SelectedIndex = 0;
        Loaded += OnEvidenceDarkLoaded;
#else
        ThemeSelector.SelectedIndex = 0;
#endif
        LastCheck = _updateCoordinator.LastCheckAt is DateTimeOffset last
            ? last.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
            : "Chưa có";
        if (!_updateCoordinator.IsAvailable)
            UpdateStatus = "Kênh cập nhật production chưa được cấu hình. Trình chỉnh sửa cục bộ vẫn hoạt động bình thường.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<Version>? UpdateAvailable;

    public string ProductVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString()
        ?? "Không xác định";

    public Version CurrentVersion => typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
    public string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public bool UpdateServiceAvailable => _updateCoordinator.IsAvailable;
    public string UpdateChannel => "stable";
    public ObservableCollection<string> ReleaseNotes { get; } = [];
    public Visibility UpdateAvailableVisibility => _availableManifest is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ReadyVisibility => _stagedUpdate is null ? Visibility.Collapsed : Visibility.Visible;
    public bool CanCheckUpdate => !_isUpdateBusy;
    public bool CanDownloadUpdate => !_isUpdateBusy && _availableManifest is not null && _stagedUpdate is null;
    public bool CanCancelDownload => _downloadCancellation is not null;
    public bool CanRestartToUpdate => !_isUpdateBusy && _stagedUpdate is not null;

    public string ThemeStatus
    {
        get => _themeStatus;
        private set => SetField(ref _themeStatus, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetField(ref _updateStatus, value);
    }

    public string LastCheck
    {
        get => _lastCheck;
        private set => SetField(ref _lastCheck, value);
    }

    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        private set
        {
            if (!SetField(ref _isUpdateBusy, value)) return;
            RaiseUpdateActions();
        }
    }

    public double UpdateProgress
    {
        get => _updateProgress;
        private set => SetField(ref _updateProgress, value);
    }

    public bool UpdateProgressIndeterminate
    {
        get => _updateProgressIndeterminate;
        private set => SetField(ref _updateProgressIndeterminate, value);
    }

    public void FocusPrimaryHeading() => SettingsHeading.Focus(FocusState.Programmatic);

#if PLAN102_UI_EVIDENCE_DARK
    private void OnEvidenceDarkLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnEvidenceDarkLoaded;
        ThemeSelector.SelectedIndex = 1;
    }
#endif

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (IsUpdateBusy || (!manual && !_updateCoordinator.IsAvailable)) return;
        IsUpdateBusy = true;
        UpdateProgressIndeterminate = true;
        UpdateStatus = "Đang kiểm tra cập nhật trong nền…";
        if (manual) _activity.Publish("INFO", "Đang kiểm tra cập nhật.");
        try
        {
            var result = await _updateCoordinator.CheckAsync(CurrentVersion, manual);
            LastCheck = result.AttemptedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            _availableManifest = result.Manifest;
            _stagedUpdate = null;
            ReleaseNotes.Clear();
            if (result.Manifest is not null)
                foreach (var note in result.Manifest.ReleaseNotes) ReleaseNotes.Add(note);
            UpdateStatus = result.Availability switch
            {
                PortableUpdateAvailability.Available =>
                    $"Có bản cập nhật mới {result.Manifest!.Version}.",
                PortableUpdateAvailability.Current => result.DiagnosticCode == "PORTABLE_UPDATE_JUST_INSTALLED"
                    ? "Cập nhật thành công. Bạn đang dùng phiên bản mới nhất."
                    : "Bạn đang dùng phiên bản mới nhất.",
                PortableUpdateAvailability.Unavailable =>
                    "Không thể kiểm tra cập nhật lúc này. Trình chỉnh sửa cục bộ vẫn hoạt động bình thường.",
                _ => "Không thể kiểm tra cập nhật. Hãy thử lại sau."
            };
            _activity.Publish(result.Availability == PortableUpdateAvailability.Failed ? "WARNING" : "INFO", UpdateStatus);
            if (result.Availability == PortableUpdateAvailability.Available && result.Manifest is not null)
                UpdateAvailable?.Invoke(this, result.Manifest.Version);
            OnPropertyChanged(nameof(UpdateAvailableVisibility));
            OnPropertyChanged(nameof(ReadyVisibility));
            RaiseUpdateActions();
        }
        finally
        {
            UpdateProgressIndeterminate = false;
            IsUpdateBusy = false;
        }
    }

    private async void OnCheckUpdateClicked(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(true);

    private async void OnDownloadUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (!CanDownloadUpdate || _availableManifest is null) return;
        if (HasCriticalOperation())
        {
            UpdateStatus = "Ứng dụng sẽ cập nhật sau khi tác vụ hiện tại hoàn tất.";
            _activity.Publish("WARNING", UpdateStatus);
            return;
        }
        IsUpdateBusy = true;
        _downloadCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancelDownload));
        UpdateStatus = "Đang tải bản cập nhật…";
        _activity.Publish("INFO", UpdateStatus);
        try
        {
            var progress = new Progress<PortableUpdateStageProgress>(value =>
            {
                UpdateProgressIndeterminate = value.TotalBytes is null or 0;
                UpdateProgress = value.TotalBytes is > 0 ? value.ProcessedBytes * 100d / value.TotalBytes.Value : 0;
                UpdateStatus = value.Stage switch
                {
                    PortableUpdateStage.Downloading => "Đang tải bản cập nhật…",
                    PortableUpdateStage.Verifying => "Đang xác minh…",
                    PortableUpdateStage.Extracting => "Đang chuẩn bị cập nhật…",
                    PortableUpdateStage.ValidatingInventory => "Đang kiểm tra gói ứng dụng…",
                    _ => "Bản cập nhật đã sẵn sàng."
                };
            });
            var result = await _updateCoordinator.DownloadAsync(_availableManifest, progress, _downloadCancellation.Token);
            if (result.Succeeded)
            {
                _stagedUpdate = result.Update;
                UpdateStatus = "Bản cập nhật đã sẵn sàng. Ứng dụng cần khởi động lại để hoàn tất.";
                _activity.Publish("SUCCESS", "Đã tải và xác minh bản cập nhật.");
            }
            else
            {
                UpdateStatus = result.DiagnosticCode == "PORTABLE_UPDATE_CANCELLED"
                    ? "Đã hủy tải bản cập nhật."
                    : "Không thể chuẩn bị bản cập nhật. Hãy thử lại.";
                _activity.Publish(result.DiagnosticCode == "PORTABLE_UPDATE_CANCELLED" ? "INFO" : "ERROR", UpdateStatus);
            }
            OnPropertyChanged(nameof(ReadyVisibility));
        }
        finally
        {
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            UpdateProgressIndeterminate = false;
            IsUpdateBusy = false;
            OnPropertyChanged(nameof(CanCancelDownload));
            RaiseUpdateActions();
        }
    }

    private void OnCancelDownloadClicked(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();

    private async void OnRestartUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (!CanRestartToUpdate || _stagedUpdate is null) return;
        if (HasCriticalOperation())
        {
            UpdateStatus = "Ứng dụng sẽ cập nhật sau khi tác vụ hiện tại hoàn tất.";
            return;
        }
        if (_editorViewModel.CanApply)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Hãy lưu thay đổi trước khi cập nhật",
                Content = "Texture đang chỉnh sửa có thay đổi chưa được Áp dụng. Hãy Áp dụng/Lưu rồi thử lại, hoặc hủy cập nhật.",
                CloseButtonText = "Đóng"
            };
            await dialog.ShowAsync();
            return;
        }
        if (!_updateCoordinator.TryLaunchUpdater(_stagedUpdate, Environment.ProcessId, AppContext.BaseDirectory,
                out var diagnosticCode))
        {
            UpdateStatus = diagnosticCode == "PORTABLE_UPDATE_INSTALL_DIRECTORY_NOT_WRITABLE"
                ? "Không thể cập nhật tự động vì thư mục ứng dụng không cho phép ghi."
                : "Không thể bắt đầu cập nhật. Hãy thử lại.";
            _activity.Publish("ERROR", UpdateStatus);
            return;
        }
        _activity.Publish("INFO", "Ứng dụng sẽ khởi động lại để hoàn tất cập nhật.");
        Application.Current.Exit();
    }

    private void OnDeferUpdateClicked(object sender, RoutedEventArgs e)
    {
        UpdateStatus = "Đã để bản cập nhật lại sau.";
        _activity.Publish("INFO", UpdateStatus);
    }

    private bool HasCriticalOperation() => _taskManager.GetSnapshots().Any(static task =>
        task.State is BackgroundTaskState.Queued or BackgroundTaskState.Running
        && task.Kind is BackgroundTaskKind.Extract or BackgroundTaskKind.Convert or BackgroundTaskKind.Build);

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ThemeSelector.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var root = XamlRoot?.Content as FrameworkElement;
        if (root is null) return;
        root.RequestedTheme = tag switch
        {
            "Dark" => ElementTheme.Dark,
            "Default" => ElementTheme.Default,
            _ => ElementTheme.Light
        };
        ThemeStatus = tag switch
        {
            "Dark" => "Đang dùng giao diện Studio Night.",
            "Default" => "Giao diện đang theo cài đặt Windows.",
            _ => "Đang dùng giao diện Creative Daylight."
        };
    }

    private void RaiseUpdateActions()
    {
        OnPropertyChanged(nameof(CanCheckUpdate));
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(CanRestartToUpdate));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
