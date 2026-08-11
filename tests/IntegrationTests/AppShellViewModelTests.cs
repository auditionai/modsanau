using AuditionModStudio.App.Shell;

namespace IntegrationTests;

public sealed class AppShellViewModelTests
{
    [Fact]
    public void Route_catalog_is_exact_and_home_is_selected_by_default()
    {
        var viewModel = new AppShellViewModel();

        Assert.Equal(
            [
                AppRoute.Home,
                AppRoute.Projects,
                AppRoute.AiStudio,
                AppRoute.ImageEditor,
                AppRoute.ModLibrary,
                AppRoute.Batch,
                AppRoute.Cloud,
                AppRoute.Settings
            ],
            viewModel.NavigationItems.Select(item => item.Route));
        Assert.Equal(AppRoute.Home, viewModel.CurrentRoute);
        Assert.Equal("Home", viewModel.CurrentTitle);
    }

    [Fact]
    public void Navigate_publishes_route_title_and_description_changes_once()
    {
        var viewModel = new AppShellViewModel();
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        var changed = viewModel.Navigate(AppRoute.ImageEditor);
        var duplicate = viewModel.Navigate(AppRoute.ImageEditor);

        Assert.True(changed);
        Assert.False(duplicate);
        Assert.Equal(AppRoute.ImageEditor, viewModel.CurrentRoute);
        Assert.Equal("Image Editor", viewModel.CurrentTitle);
        Assert.Equal(
            [nameof(viewModel.CurrentRoute), nameof(viewModel.CurrentTitle), nameof(viewModel.CurrentDescription)],
            changedProperties);
    }
}
