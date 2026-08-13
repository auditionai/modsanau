using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
}
