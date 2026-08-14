using System.ComponentModel;
using AuditionModStudio.App.Imaging;
using AuditionModStudio.Core.Images;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AuditionModStudio.App.Workspace;

public sealed partial class ProjectWorkspacePage : Page
{
    private readonly ILogger<ProjectWorkspacePage> _logger;

    public ProjectWorkspacePage(
        ProjectWorkspaceViewModel viewModel,
        BuildExportViewModel buildExportViewModel,
        ILogger<ProjectWorkspacePage> logger)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        BuildExportViewModel = buildExportViewModel ?? throw new ArgumentNullException(nameof(buildExportViewModel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildFolderTree();
    }

    public ProjectWorkspaceViewModel ViewModel { get; }

    public BuildExportViewModel BuildExportViewModel { get; }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        BuildExportViewModel.RefreshProject();
        await ViewModel.LoadAsync(cancellationToken);
    }

    public bool FocusPrimaryHeading() => WorkspaceHeading.Focus(FocusState.Programmatic);

    private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        ViewModel.SelectedTexture = sender.SelectedNode?.Content as WorkspaceTextureItem;
    }

    private void OnCancelLoadingClicked(object sender, RoutedEventArgs e) =>
        ViewModel.CancelLoading();

    private async void OnChooseExportFolderClicked(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App { ActiveWindow: { } window })
        {
            return;
        }

        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                BuildExportViewModel.OutputDirectory = folder.Path;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning(
                "The export folder picker failed with exception type {ExceptionType}",
                exception.GetType().Name);
            BuildExportViewModel.ReportFolderSelectionUnavailable();
        }
    }

    private async void OnBuildExportClicked(object sender, RoutedEventArgs e) =>
        await BuildExportViewModel.StartAsync();

    private void OnCancelBuildExportClicked(object sender, RoutedEventArgs e) =>
        BuildExportViewModel.Cancel();

    private void OnTextureGridItemClicked(object sender, ItemClickEventArgs e) =>
        ViewModel.SelectedTexture = e.ClickedItem as WorkspaceTextureItem;

    private async void OnTextureContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not FrameworkElement root)
        {
            if (!args.InRecycleQueue)
            {
                args.RegisterUpdateCallback(OnTextureContainerContentChanging);
            }

            return;
        }

        var imageControl = root.FindName("ThumbnailImage") as Image;
        var placeholder = root.FindName("ThumbnailPlaceholder") as FontIcon;
        if (args.InRecycleQueue || args.Item is not WorkspaceTextureItem item)
        {
            if (imageControl is not null)
            {
                imageControl.Source = null;
            }

            if (placeholder is not null)
            {
                placeholder.Visibility = Visibility.Visible;
            }

            return;
        }

        args.Handled = true;

        try
        {
            var thumbnail = await ViewModel.LoadThumbnailAsync(item);
            if (thumbnail is null
                || !ReferenceEquals(sender.ItemFromContainer(args.ItemContainer), item)
                || imageControl is null)
            {
                return;
            }

            imageControl.Source = InternalImageBitmapAdapter.CreateBitmap(thumbnail);
            if (placeholder is not null)
            {
                placeholder.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("A recycled texture thumbnail request was cancelled");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "A texture thumbnail could not be presented; exception type: {ExceptionType}",
                exception.GetType().Name);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.Folders))
        {
            RebuildFolderTree();
        }
    }

    private void RebuildFolderTree()
    {
        FolderTree.RootNodes.Clear();
        foreach (var folder in ViewModel.Folders)
        {
            var folderNode = new TreeViewNode
            {
                Content = folder,
                IsExpanded = true
            };
            foreach (var texture in folder.Textures)
            {
                folderNode.Children.Add(new TreeViewNode { Content = texture });
            }

            FolderTree.RootNodes.Add(folderNode);
        }
    }
}
