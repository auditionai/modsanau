using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using AuditionModStudio.Core.Payments;

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
    private async void OnRenewClicked(object sender, RoutedEventArgs e) => await OpenPurchaseAsync(PaymentProductType.Subscription);
    private async void OnBuyCreditsClicked(object sender, RoutedEventArgs e) => await OpenPurchaseAsync(PaymentProductType.Credits);
    private async Task OpenPurchaseAsync(PaymentProductType type)
    {
        await ViewModel.PreparePurchaseAsync(type);
        await PurchaseDialog.ShowAsync();
    }
    private async void OnCreatePaymentClicked(object sender, RoutedEventArgs e) => await ViewModel.CreatePaymentOrderAsync();
    private async void OnCancelPaymentClicked(object sender, RoutedEventArgs e) => await ViewModel.CancelPaymentAsync();
    private void OnCopyPaymentContentClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.PaymentContent) || ViewModel.PaymentContent == "—") return;
        var package = new DataPackage(); package.SetText(ViewModel.PaymentContent); Clipboard.SetContent(package);
    }
}
