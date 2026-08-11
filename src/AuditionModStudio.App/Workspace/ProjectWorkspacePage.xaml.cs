using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using AuditionModStudio.Core.Images;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AuditionModStudio.App.Workspace;

public sealed partial class ProjectWorkspacePage : Page
{
    private readonly ILogger<ProjectWorkspacePage> _logger;

    public ProjectWorkspacePage(
        ProjectWorkspaceViewModel viewModel,
        ILogger<ProjectWorkspacePage> logger)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildFolderTree();
    }

    public ProjectWorkspaceViewModel ViewModel { get; }

    public async Task ActivateAsync(CancellationToken cancellationToken = default) =>
        await ViewModel.LoadAsync(cancellationToken);

    public bool FocusPrimaryHeading() => WorkspaceHeading.Focus(FocusState.Programmatic);

    private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        ViewModel.SelectedTexture = sender.SelectedNode?.Content as WorkspaceTextureItem;
    }

    private void OnCancelLoadingClicked(object sender, RoutedEventArgs e) =>
        ViewModel.CancelLoading();

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

            imageControl.Source = CreateBitmap(thumbnail);
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

    private static WriteableBitmap CreateBitmap(InternalImage source)
    {
        var bitmap = new WriteableBitmap(source.Width, source.Height);
        var rgba = source.Pixels.AsSpan();
        var bgraPremultiplied = new byte[rgba.Length];
        for (var index = 0; index < rgba.Length; index += 4)
        {
            var alpha = rgba[index + 3];
            bgraPremultiplied[index] = Premultiply(rgba[index + 2], alpha);
            bgraPremultiplied[index + 1] = Premultiply(rgba[index + 1], alpha);
            bgraPremultiplied[index + 2] = Premultiply(rgba[index], alpha);
            bgraPremultiplied[index + 3] = alpha;
        }

        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(bgraPremultiplied, 0, bgraPremultiplied.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private static byte Premultiply(byte channel, byte alpha) =>
        (byte)((channel * alpha + 127) / 255);

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
