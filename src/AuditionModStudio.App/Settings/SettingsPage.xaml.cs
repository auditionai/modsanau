using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App.Settings;

public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private string _themeStatus = "Đang dùng giao diện Creative Daylight.";

    public SettingsPage()
    {
        InitializeComponent();
        ThemeSelector.SelectedIndex = 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProductVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString()
        ?? "Không xác định";

    public string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public string ThemeStatus
    {
        get => _themeStatus;
        private set
        {
            if (_themeStatus == value) return;
            _themeStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeStatus)));
        }
    }

    public void FocusPrimaryHeading() => SettingsHeading.Focus(FocusState.Programmatic);

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
}
