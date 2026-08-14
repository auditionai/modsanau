using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AuditionModStudio.App.Account;

public sealed partial class AccountPage : Page
{
    public AccountPage(AccountViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public AccountViewModel ViewModel { get; }

    public Task ActivateAsync(CancellationToken cancellationToken = default) =>
        ViewModel.ActivateAsync(cancellationToken);

    public void Deactivate() => ViewModel.Deactivate();

    public void FocusPrimaryHeading() => AccountHeading.Focus(FocusState.Programmatic);

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await ViewModel.ActivateAsync();
    private void OnCopyDeviceCodeClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.DeviceCode) || ViewModel.DeviceCode == "—") return;
        var package = new DataPackage(); package.SetText(ViewModel.DeviceCode); Clipboard.SetContent(package);
    }
    private async void OnRedeemGiftCodeClicked(object sender, RoutedEventArgs e) => await ViewModel.RedeemGiftCodeAsync();
}
