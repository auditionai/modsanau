using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App.Workspace;

public sealed partial class ProjectWorkspacePage : Page
{
    public ProjectWorkspacePage(ProjectWorkspaceViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
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
