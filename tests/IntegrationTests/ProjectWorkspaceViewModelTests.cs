using System.Collections.Immutable;
using AuditionModStudio.App.Shell;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;

namespace IntegrationTests;

public sealed class ProjectWorkspaceViewModelTests
{
    [Fact]
    public async Task No_active_project_is_an_explicit_empty_state_without_scan()
    {
        var scan = new TestScanService(CreateScanResult());
        var viewModel = CreateViewModel(new TestProjectSession(), scan);

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Folders);
        Assert.Contains("No active project", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, scan.CallCount);
    }

    [Fact]
    public async Task Scan_publishes_folder_tree_and_exact_selected_metadata()
    {
        var session = new TestProjectSession(CreateProject(), new TestWorkspace());
        var viewModel = CreateViewModel(session, new TestScanService(CreateScanResult()));

        await viewModel.LoadAsync();
        var folder = Assert.Single(viewModel.Folders);
        var texture = Assert.Single(folder.Textures);
        viewModel.SelectedTexture = texture;

        Assert.Equal("interface", folder.DirectoryRelativePath);
        Assert.Equal("Login texture", viewModel.SelectedDisplayName);
        Assert.Equal("6000 × 1801", viewModel.SelectedTargetSize);
        Assert.Equal("BC3", viewModel.SelectedFormat);
        Assert.Equal("Original", viewModel.SelectedState);
        Assert.Equal("Valid", viewModel.SelectedValidation);
    }

    [Fact]
    public async Task Search_and_mapping_filter_are_applied_to_immutable_scan_snapshot()
    {
        var session = new TestProjectSession(CreateProject(), new TestWorkspace());
        var result = CreateScanResult(includeUnmapped: true);
        var viewModel = CreateViewModel(session, new TestScanService(result));
        await viewModel.LoadAsync();

        viewModel.MappingFilter = WorkspaceMappingFilter.Unmapped;

        Assert.Equal("effects", Assert.Single(viewModel.Folders).DirectoryRelativePath);
        viewModel.MappingFilter = WorkspaceMappingFilter.All;
        viewModel.SearchQuery = "login";
        Assert.Equal("tn_login.dds", Assert.Single(Assert.Single(viewModel.Folders).Textures).FileName);
    }

    [Fact]
    public async Task Grid_facets_compose_over_status_category_size_and_alpha_metadata()
    {
        var session = new TestProjectSession(CreateProject(), new TestWorkspace());
        var viewModel = CreateViewModel(
            session,
            new TestScanService(CreateScanResult(includeUnmapped: true)),
            new TestTextureStateMachine(path => path.EndsWith("raw.dds", StringComparison.Ordinal)
                ? TextureState.Modified
                : TextureState.Original));
        await viewModel.LoadAsync();

        viewModel.StatusFilter = TextureStatusFilter.Modified;
        Assert.Equal("raw.dds", Assert.Single(viewModel.FilteredTextures).FileName);

        viewModel.StatusFilter = TextureStatusFilter.All;
        viewModel.SelectedCategoryFilter = viewModel.CategoryFilters.Single(option => option.Category == "interface");
        Assert.Equal("tn_login.dds", Assert.Single(viewModel.FilteredTextures).FileName);

        viewModel.SelectedCategoryFilter = viewModel.CategoryFilters[0];
        viewModel.SizeFilter = TextureSizeFilter.Small;
        Assert.Equal("raw.dds", Assert.Single(viewModel.FilteredTextures).FileName);

        viewModel.SizeFilter = TextureSizeFilter.All;
        viewModel.AlphaFilter = TextureAlphaFilter.NoAlpha;
        Assert.Equal("raw.dds", Assert.Single(viewModel.FilteredTextures).FileName);
    }

    [Theory]
    [InlineData(TextureState.Modified, TextureStatusFilter.Modified)]
    [InlineData(TextureState.Original, TextureStatusFilter.Original)]
    [InlineData(TextureState.Invalid, TextureStatusFilter.Invalid)]
    [InlineData(TextureState.AiGenerated, TextureStatusFilter.AI)]
    public async Task Status_facets_match_exact_texture_state(
        TextureState state,
        TextureStatusFilter filter)
    {
        var session = new TestProjectSession(CreateProject(), new TestWorkspace());
        var viewModel = CreateViewModel(
            session,
            new TestScanService(CreateScanResult()),
            new TestTextureStateMachine(_ => state));
        await viewModel.LoadAsync();

        viewModel.StatusFilter = filter;

        Assert.Single(viewModel.FilteredTextures);
    }

    [Fact]
    public async Task Scan_is_metadata_only_and_explicit_thumbnail_uses_thumbnail_task()
    {
        var lazyLoading = new TestLazyLoadingService();
        var taskManager = new ImmediateBackgroundTaskManager();
        var session = new TestProjectSession(CreateProject(), new TestWorkspace());
        var viewModel = CreateViewModel(
            session,
            new TestScanService(CreateScanResult()),
            lazyLoadingService: lazyLoading,
            taskManager: taskManager);

        await viewModel.LoadAsync();
        Assert.Equal(0, lazyLoading.CallCount);

        var result = await viewModel.LoadThumbnailAsync(Assert.Single(viewModel.FilteredTextures));

        Assert.Null(result);
        Assert.Equal(1, lazyLoading.CallCount);
        Assert.Equal(192, lazyLoading.MaximumDimension);
        Assert.Contains(BackgroundTaskKind.Thumbnail, taskManager.Kinds);
    }

    [Fact]
    public async Task Explicit_selected_image_load_uses_convert_task_and_lazy_service()
    {
        var image = new InternalImage(
            1,
            1,
            4,
            [1, 2, 3, 255],
            new ImageSourceMetadata(
                ImageSourceFormat.Png,
                1,
                1,
                ImageSourceOrientation.Normal,
                true,
                false));
        var lazyLoading = new TestLazyLoadingService(image);
        var taskManager = new ImmediateBackgroundTaskManager();
        var viewModel = CreateViewModel(
            new TestProjectSession(CreateProject(), new TestWorkspace()),
            new TestScanService(CreateScanResult()),
            lazyLoadingService: lazyLoading,
            taskManager: taskManager);
        await viewModel.LoadAsync();
        viewModel.SelectedTexture = Assert.Single(viewModel.FilteredTextures);

        var loaded = await viewModel.LoadSelectedImageAsync();

        Assert.Same(image, loaded);
        Assert.Equal(1, lazyLoading.SelectedCallCount);
        Assert.Contains(BackgroundTaskKind.Convert, taskManager.Kinds);
    }

    private static ProjectWorkspaceViewModel CreateViewModel(
        IApplicationProjectSession session,
        ISmartModScanService scan,
        ITextureStateMachine? stateMachine = null,
        ITextureLazyLoadingService? lazyLoadingService = null,
        ImmediateBackgroundTaskManager? taskManager = null) => new(
            session,
            scan,
            stateMachine ?? new TestTextureStateMachine(),
            lazyLoadingService ?? new TestLazyLoadingService(),
            taskManager ?? new ImmediateBackgroundTaskManager());

    private static AuditionProject CreateProject()
    {
        var timestamp = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var result = AuditionProject.Create(
            AuditionProject.CurrentSchemaVersion,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Workspace Project",
            new GameId("audition"),
            new ModId("login_mod"),
            new TemplateIdentity(
                new TemplateId("archive-015"),
                new TemplateVersion("1"),
                new TemplateSha256(new string('A', 64)),
                new CompatibleGameBuild("audition-vn-2026")),
            new ProjectWorkspaceReference(
                "0123456789abcdef0123456789abcdef",
                new ModRelativePath("Working/015.ab"),
                new ModRelativePath("Extracted/015")),
            [],
            [],
            [],
            new ProjectEditStateSnapshot(0, 0, null, []),
            new ProjectBuildStateSnapshot(ProjectBuildStatus.NotBuilt, null, null, null),
            timestamp,
            timestamp);
        Assert.True(result.Succeeded);
        return result.Project!;
    }

    private static SmartModScanResult CreateScanResult(bool includeUnmapped = false)
    {
        var mappedAsset = Texture("interface/tn_login.dds", "interface", "tn_login.dds", 'A');
        var slot = new TextureSlot(
            new TextureSlotId("login"),
            new ModRelativePath(mappedAsset.RelativePath),
            "Login texture",
            new TextureCategory("interface"),
            "Login screen texture.",
            ["login"],
            true,
            true,
            new TextureEditMode("crop"));
        var mapped = new SmartTextureAsset(
            mappedAsset,
            Metadata(),
            new TextureManifestResolution(new ModRelativePath(mappedAsset.RelativePath), "Login texture", false, slot),
            false,
            false);

        var groups = ImmutableArray.CreateBuilder<SmartTextureGroup>();
        groups.Add(new SmartTextureGroup("interface", [mapped]));
        var assets = new List<ArchiveAsset> { mappedAsset };
        if (includeUnmapped)
        {
            var rawAsset = Texture("effects/raw.dds", "effects", "raw.dds", 'B');
            var raw = new SmartTextureAsset(
                rawAsset,
                Metadata() with { Width = 256, Height = 256, HasAlphaChannel = false },
                new TextureManifestResolution(new ModRelativePath(rawAsset.RelativePath), "raw.dds", true, null),
                true,
                true);
            groups.Add(new SmartTextureGroup("effects", [raw]));
            assets.Add(rawAsset);
        }

        return SmartModScanResult.Success(
            new ArchiveAssetCatalog(assets, groups.Count, assets.Sum(asset => asset.FileSize)),
            groups.ToImmutable(),
            []);
    }

    private static TextureAsset Texture(string path, string directory, string fileName, char hash) => new(
        path,
        fileName,
        ".dds",
        directory,
        128,
        new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
        new string(hash, 64));

    private static DdsMetadata Metadata() => new(
        6000,
        1801,
        null,
        1,
        1,
        DdsFormat.BC3,
        DdsFormatSupport.Known,
        "DXT5",
        null,
        DdsHeaderType.Legacy,
        true,
        true,
        DdsAlphaMode.Interpolated,
        DdsColorSpace.Linear,
        DdsResourceDimension.Texture2D,
        false,
        1,
        128,
        128);

    private sealed class TestProjectSession(
        AuditionProject? project = null,
        IProjectArchiveWorkspace? workspace = null) : IApplicationProjectSession
    {
        public AuditionProject? Project { get; } = project;
        public IProjectArchiveWorkspace? Workspace { get; } = workspace;

        public ValueTask ActivateAsync(AuditionProject nextProject, IProjectArchiveWorkspace nextWorkspace) =>
            throw new NotSupportedException();

        public ValueTask<bool> TryUpdateProjectAsync(
            AuditionProject expectedProject,
            IProjectArchiveWorkspace expectedWorkspace,
            AuditionProject updatedProject) => ValueTask.FromResult(false);
    }

    private sealed class TestWorkspace : IProjectArchiveWorkspace
    {
        public ProjectArchiveWorkspaceDescriptor Descriptor => throw new NotSupportedException();
        public ArchiveWorkspace ArchiveWorkspace => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestScanService(SmartModScanResult result) : ISmartModScanService
    {
        public int CallCount { get; private set; }

        public Task<SmartModScanResult> ScanAsync(
            SmartModScanRequest request,
            IProgress<SmartModScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            progress?.Report(new SmartModScanProgress(SmartModScanPhase.Completed, result.TextureCount, result.TextureCount, null));
            return Task.FromResult(result);
        }
    }

    private sealed class TestTextureStateMachine(
        Func<string, TextureState>? resolve = null) : ITextureStateMachine
    {
        public TextureStateResult Evaluate(
            AuditionProject project,
            ModRelativePath texturePath,
            TextureRuntimeObservation observation,
            TextureState? previousState = null) =>
            TextureStateResult.Success(resolve?.Invoke(texturePath.Value) ?? TextureState.Original, previousState);
    }

    private sealed class TestLazyLoadingService(InternalImage? selectedImage = null) : ITextureLazyLoadingService
    {
        public int CallCount { get; private set; }
        public int MaximumDimension { get; private set; }
        public int SelectedCallCount { get; private set; }

        public Task<TextureThumbnailLoadResult> LoadThumbnailAsync(
            TextureThumbnailLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            MaximumDimension = request.MaximumDimension;
            return Task.FromResult(TextureThumbnailLoadResult.Failure(
                TextureLazyLoadFailureReason.ThumbnailFailed,
                "test.thumbnail_unavailable"));
        }

        public Task<SelectedTextureLoadResult> LoadSelectedTextureAsync(
            SelectedTextureLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            SelectedCallCount++;
            return Task.FromResult(selectedImage is null
                ? SelectedTextureLoadResult.Failure(
                    TextureLazyLoadFailureReason.PreviewFailed,
                    "test.preview_unavailable")
                : SelectedTextureLoadResult.Success(selectedImage));
        }
    }

    private sealed class ImmediateBackgroundTaskManager : IBackgroundTaskManager
    {
        private BackgroundTaskSnapshot? _snapshot;

        public List<BackgroundTaskKind> Kinds { get; } = [];

        public event EventHandler<BackgroundTaskNotification>? Notification
        {
            add { }
            remove { }
        }

        public async ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(
            BackgroundTaskRequest request,
            CancellationToken cancellationToken = default)
        {
            var id = new BackgroundTaskId(Guid.NewGuid());
            Kinds.Add(request.Kind);
            var result = await request.Operation(new Progress<BackgroundTaskProgress>(), cancellationToken);
            _snapshot = new BackgroundTaskSnapshot(
                id,
                request.Kind,
                result.Succeeded ? BackgroundTaskState.Succeeded : BackgroundTaskState.Failed,
                null,
                result.DiagnosticCode,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            return BackgroundTaskEnqueueResult.Success(id);
        }

        public bool TryCancel(BackgroundTaskId taskId) => false;
        public bool TryGetSnapshot(BackgroundTaskId taskId, out BackgroundTaskSnapshot snapshot)
        {
            snapshot = _snapshot!;
            return _snapshot is not null && _snapshot.TaskId == taskId;
        }

        public ImmutableArray<BackgroundTaskSnapshot> GetSnapshots() => _snapshot is null ? [] : [_snapshot];

        public Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(
            BackgroundTaskId taskId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot is { } snapshot && snapshot.TaskId == taskId ? snapshot : null);
    }
}
