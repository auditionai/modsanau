using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App.Home;

public sealed partial class HomePage : Page
{
    public HomePage(HomeViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    public bool FocusPrimaryHeading() => HomeHeading.Focus(FocusState.Programmatic);

    private async void OnCreateClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.CreateProjectAsync();

    private void OnCancelClicked(object sender, RoutedEventArgs e) =>
        ViewModel.CancelCreation();
}
