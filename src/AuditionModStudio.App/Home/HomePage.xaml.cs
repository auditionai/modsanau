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

    private void OnHeroDotClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string index } && int.TryParse(index, out var selectedIndex))
            HeroSlider.SelectedIndex = selectedIndex;
    }

    private void OnHeroSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HeroDot0 is null) return;
        HeroDot0.Opacity = HeroSlider.SelectedIndex == 0 ? 1 : 0.45;
        HeroDot1.Opacity = HeroSlider.SelectedIndex == 1 ? 1 : 0.45;
        HeroDot2.Opacity = HeroSlider.SelectedIndex == 2 ? 1 : 0.45;
    }

    private void OnStartProjectClicked(object sender, RoutedEventArgs e) =>
        GamePicker.Focus(FocusState.Programmatic);
}
