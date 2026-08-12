using System.ComponentModel;
using AuditionModStudio.App.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App.AiStudio;

public sealed partial class AiStudioPage : Page
{
    public AiStudioPage(AiStudioViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public AiStudioViewModel ViewModel { get; }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await ViewModel.ActivateAsync(cancellationToken);
    }

    public void Deactivate() => ViewModel.Deactivate();

    public void FocusPrimaryHeading() => StudioHeading.Focus(FocusState.Programmatic);

    private async void OnSubmitClicked(object sender, RoutedEventArgs e) => await ViewModel.SubmitAsync();
    private void OnCancelClicked(object sender, RoutedEventArgs e) => ViewModel.CancelCurrent();
    private async void OnRefreshHistoryClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshHistoryAsync();

    private async void OnCancelJobClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid jobId }) await ViewModel.CancelJobAsync(jobId);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.PreviewImage))
        {
            PreviewImage.Source = ViewModel.PreviewImage is null
                ? null
                : InternalImageBitmapAdapter.CreateBitmap(ViewModel.PreviewImage);
        }
    }
}
