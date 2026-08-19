using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Shell;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;

namespace AuditionModStudio.App.Workspace;

public sealed class ProjectWorkspaceViewModel : INotifyPropertyChanged, IWorkspaceTextureSelection
{
    private readonly IApplicationProjectSession _projectSession;
    private readonly ISmartModScanService _scanService;
    private readonly ITextureStateMachine _textureStateMachine;
    private readonly ITextureLazyLoadingService _lazyLoadingService;
    private readonly IBackgroundTaskManager _taskManager;
    private ImmutableArray<WorkspaceTextureItem> _allTextures = [];
    private ImmutableArray<WorkspaceTextureItem> _filteredTextures = [];
    private ImmutableDictionary<string, SmartTextureAsset> _sourceTextures =
        ImmutableDictionary<string, SmartTextureAsset>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    private ImmutableArray<WorkspaceFolderItem> _folders = [];
    private WorkspaceTextureItem? _selectedTexture;
    private string _searchQuery = string.Empty;
    private WorkspaceMappingFilter _mappingFilter;
    private TextureStatusFilter _statusFilter;
    private TextureSizeFilter _sizeFilter;
    private TextureAlphaFilter _alphaFilter;
    private string? _categoryFilter;
    private string? _folderFilter;
    private string _statusMessage = "Chưa mở dự án. Hãy tạo hoặc mở một dự án trước.";
    private Guid? _loadedProjectId;
    private BackgroundTaskId _activeTaskId;
    private BackgroundTaskId _activeImageTaskId;
    private bool _isLoading;
    private double _progressPercentage;

    public ProjectWorkspaceViewModel(
        IApplicationProjectSession projectSession,
        ISmartModScanService scanService,
        ITextureStateMachine textureStateMachine,
        ITextureLazyLoadingService lazyLoadingService,
        IBackgroundTaskManager taskManager)
    {
        _projectSession = projectSession ?? throw new ArgumentNullException(nameof(projectSession));
        _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
        _textureStateMachine = textureStateMachine ?? throw new ArgumentNullException(nameof(textureStateMachine));
        _lazyLoadingService = lazyLoadingService ?? throw new ArgumentNullException(nameof(lazyLoadingService));
        _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<WorkspaceMappingFilter> MappingFilters { get; } =
        Enum.GetValues<WorkspaceMappingFilter>().ToImmutableArray();

    public ImmutableArray<TextureStatusFilter> StatusFilters { get; } =
        Enum.GetValues<TextureStatusFilter>().ToImmutableArray();

    public ImmutableArray<TextureSizeFilter> SizeFilters { get; } =
        Enum.GetValues<TextureSizeFilter>().ToImmutableArray();

    public ImmutableArray<TextureAlphaFilter> AlphaFilters { get; } =
        Enum.GetValues<TextureAlphaFilter>().ToImmutableArray();

    public ImmutableArray<WorkspaceCategoryFilterOption> CategoryFilters { get; private set; } =
        [new(null, "Tất cả danh mục")];

    public ImmutableArray<WorkspaceFolderItem> Folders => _folders;

    public ImmutableArray<WorkspaceFolderFilterOption> FolderFilters { get; private set; } = [];

    public ImmutableArray<WorkspaceTextureItem> FilteredTextures => _filteredTextures;

    public string ProjectName => _projectSession.Project?.Name ?? "Không gian dự án";

    public string TextureCountDisplay => _projectSession.Project is null ? "—" : _allTextures.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string ModifiedCountDisplay => _projectSession.Project is null
        ? "—"
        : _allTextures.Count(texture => texture.StateLabel == "Đã chỉnh sửa").ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string WarningCountDisplay => _projectSession.Project is null
        ? "—"
        : _allTextures.Count(texture => !string.Equals(texture.Validation, "Hợp lệ", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(texture.Validation, "Đạt", StringComparison.OrdinalIgnoreCase)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var normalized = value ?? string.Empty;
            if (_searchQuery == normalized || normalized.Length > 256)
            {
                return;
            }

            _searchQuery = normalized;
            ApplyFilter();
            OnPropertyChanged();
        }
    }

    public WorkspaceMappingFilter MappingFilter
    {
        get => _mappingFilter;
        set
        {
            if (_mappingFilter == value || !Enum.IsDefined(value))
            {
                return;
            }

            _mappingFilter = value;
            ApplyFilter();
            OnPropertyChanged();
        }
    }

    public TextureStatusFilter StatusFilter
    {
        get => _statusFilter;
        set => SetFilter(ref _statusFilter, value);
    }

    public TextureSizeFilter SizeFilter
    {
        get => _sizeFilter;
        set => SetFilter(ref _sizeFilter, value);
    }

    public TextureAlphaFilter AlphaFilter
    {
        get => _alphaFilter;
        set => SetFilter(ref _alphaFilter, value);
    }

    public WorkspaceCategoryFilterOption SelectedCategoryFilter
    {
        get => CategoryFilters.First(option =>
            string.Equals(option.Category, _categoryFilter, StringComparison.Ordinal));
        set
        {
            if (value is null
                || !CategoryFilters.Contains(value)
                || string.Equals(_categoryFilter, value.Category, StringComparison.Ordinal))
            {
                return;
            }

            _categoryFilter = value.Category;
            ApplyFilter();
            OnPropertyChanged();
        }
    }

    public WorkspaceFolderFilterOption? SelectedFolderFilter
    {
        get => FolderFilters.FirstOrDefault(option => string.Equals(
            option.DirectoryRelativePath, _folderFilter, StringComparison.Ordinal));
        set
        {
            if (value is null
                || !FolderFilters.Contains(value)
                || string.Equals(_folderFilter, value.DirectoryRelativePath, StringComparison.Ordinal))
            {
                return;
            }

            _folderFilter = value.DirectoryRelativePath;
            ApplyFilter();
            OnPropertyChanged();
        }
    }

    public WorkspaceTextureItem? SelectedTexture
    {
        get => _selectedTexture;
        set
        {
            var selected = value is not null && _allTextures.Contains(value) ? value : null;
            if (Equals(_selectedTexture, selected))
            {
                return;
            }

            _selectedTexture = selected;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEditSelectedTexture));
            OnPropertyChanged(nameof(SelectedDisplayName));
            OnPropertyChanged(nameof(SelectedRelativePath));
            OnPropertyChanged(nameof(SelectedTargetSize));
            OnPropertyChanged(nameof(SelectedFormat));
            OnPropertyChanged(nameof(SelectedState));
            OnPropertyChanged(nameof(SelectedValidation));
            OnPropertyChanged(nameof(SelectedEditMode));
            OnPropertyChanged(nameof(PreviewMessage));
            OnPropertyChanged(nameof(SelectedAssetSummary));
        }
    }

    public bool CanEditSelectedTexture => _selectedTexture is not null;

    public bool IsLoading => _isLoading;

    public bool CanCancel => _isLoading && _activeTaskId.IsValid;

    public double ProgressPercentage => _progressPercentage;

    public string StatusMessage => _statusMessage;

    public string SelectedDisplayName => _selectedTexture?.DisplayName ?? "Chưa chọn Texture";

    public string SelectedRelativePath => _selectedTexture?.RelativePath ?? "—";

    public string SelectedTargetSize => _selectedTexture?.TargetSize ?? "—";

    public string SelectedFormat => _selectedTexture?.Format ?? "—";

    public string SelectedState => _selectedTexture?.StateLabel ?? "—";

    public string SelectedValidation => _selectedTexture?.Validation ?? "Chưa chọn";

    public string SelectedEditMode => _selectedTexture?.RecommendedEditMode ?? "Chưa xác định";

    public string PreviewMessage => _selectedTexture is null
            ? "Chọn một Texture trong cây thư mục để xem trước và kiểm tra thông tin."
            : "Ảnh xem trước chỉ được tải khi bạn mở một tác vụ chỉnh sửa phù hợp.";

    public string ProjectIdentitySummary => _projectSession.Project is { } project
            ? $"Dự án đang mở: {project.Name} · {project.GameId.Value}/{project.ModId.Value}"
            : "Chưa mở dự án. Hãy về Trang chủ để tạo dự án.";

    public string WorkflowSummary
    {
        get
        {
            var project = _projectSession.Project;
            if (project is null) return "Chưa có dự án";
            if (_isLoading) return "Đang quét";
            if (project.BuildState.Status == ProjectBuildStatus.Succeeded) return "Đã Build";
            if (!project.EditedTextures.IsEmpty) return "Đã áp dụng";
            return _loadedProjectId == project.ProjectId ? "Đã quét" : "Đã giải nén";
        }
    }

    public string SelectedAssetSummary => _selectedTexture is null
            ? "Texture đã chọn: chưa có"
            : $"Texture đã chọn: {_selectedTexture.DisplayName} · {_selectedTexture.StateLabel}";

    public string BuildReadinessSummary
    {
        get
        {
            if (_projectSession.Project is null) return "Build: cần mở một dự án trước";
            if (_isLoading) return "Build: đang chờ quét hoàn tất";
            if (_allTextures.Any(texture => texture.State == TextureState.Invalid))
                return "Build: bị chặn do trạng thái Texture không hợp lệ";
            return "Build: sẵn sàng kiểm tra";
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var project = _projectSession.Project;
        var workspace = _projectSession.Workspace;
        if (project is null || workspace is null)
        {
            Reset("Chưa mở dự án. Hãy tạo hoặc mở một dự án trước.");
            return;
        }

        if (_loadedProjectId == project.ProjectId)
        {
            return;
        }

        SmartModScanResult? scanResult = null;
        IProgress<SmartModScanProgress> uiProgress = new Progress<SmartModScanProgress>(UpdateProgress);
        SetLoading(true, "Đang quét Texture trong dự án.", 0);

        var enqueue = await _taskManager.EnqueueAsync(
            new BackgroundTaskRequest(
                BackgroundTaskKind.Scan,
                async (taskProgress, taskCancellationToken) =>
                {
                    var progress = new CallbackProgress<SmartModScanProgress>(value =>
                    {
                        taskProgress.Report(new BackgroundTaskProgress(
                            value.CompletedTextures,
                            Math.Max(value.TotalTextures, 1),
                            ToStageCode(value.Phase)));
                        uiProgress.Report(value);
                    });
                    scanResult = await _scanService.ScanAsync(
                        new SmartModScanRequest(project.GameId, project.ModId, workspace),
                        progress,
                        taskCancellationToken).ConfigureAwait(false);
                    return scanResult.Succeeded
                        ? BackgroundTaskExecutionResult.Success()
                        : BackgroundTaskExecutionResult.Failure(ToDiagnosticCode(scanResult.FailureReason));
                }),
            cancellationToken);

        if (!enqueue.Succeeded)
        {
            SetLoading(false, "Không thể đưa tác vụ quét Texture vào hàng đợi.", 0);
            return;
        }

        _activeTaskId = enqueue.TaskId;
        OnPropertyChanged(nameof(CanCancel));
        var snapshot = await _taskManager.WaitForCompletionAsync(enqueue.TaskId, cancellationToken);
        _activeTaskId = default;

        if (snapshot?.State == BackgroundTaskState.Succeeded
            && scanResult?.Succeeded == true
            && ReferenceEquals(_projectSession.Project, project)
            && ReferenceEquals(_projectSession.Workspace, workspace))
        {
            PublishScan(project, scanResult);
            return;
        }

        if (snapshot?.State == BackgroundTaskState.Cancelled || scanResult?.Cancelled == true)
        {
            SetLoading(false, "Đã hủy quét Texture.", _progressPercentage);
            return;
        }

        Reset("Không thể tải Texture của dự án. Hãy xem nhật ký ứng dụng rồi thử lại.");
    }

    public bool CancelLoading() =>
        _activeTaskId.IsValid && _taskManager.TryCancel(_activeTaskId);

    public async Task<InternalImage?> LoadThumbnailAsync(
        WorkspaceTextureItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var workspace = _projectSession.Workspace;
        if (workspace is null
            || !_allTextures.Contains(item)
            || !_sourceTextures.TryGetValue(item.RelativePath, out var source))
        {
            return null;
        }

        TextureThumbnailLoadResult? loadResult = null;
        var enqueue = await _taskManager.EnqueueAsync(
            new BackgroundTaskRequest(
                BackgroundTaskKind.Thumbnail,
                async (_, taskCancellationToken) =>
                {
                    loadResult = await _lazyLoadingService.LoadThumbnailAsync(
                        new TextureThumbnailLoadRequest(workspace, source.Asset, 192),
                        taskCancellationToken).ConfigureAwait(false);
                    return loadResult.Succeeded
                        ? BackgroundTaskExecutionResult.Success()
                        : BackgroundTaskExecutionResult.Failure("workspace.thumbnail_failed");
                }),
            cancellationToken);
        if (!enqueue.Succeeded)
        {
            return null;
        }

        var snapshot = await _taskManager.WaitForCompletionAsync(enqueue.TaskId, cancellationToken);
        return snapshot?.State == BackgroundTaskState.Succeeded && loadResult?.Succeeded == true
            ? loadResult.Image
            : null;
    }

    public async Task<InternalImage?> LoadSelectedImageAsync(CancellationToken cancellationToken = default)
    {
        var selected = _selectedTexture;
        var workspace = _projectSession.Workspace;
        if (selected is null
            || workspace is null
            || !_sourceTextures.TryGetValue(selected.RelativePath, out var source)
            || _activeImageTaskId.IsValid)
        {
            return null;
        }

        SelectedTextureLoadResult? loadResult = null;
        var enqueue = await _taskManager.EnqueueAsync(
            new BackgroundTaskRequest(
                BackgroundTaskKind.Convert,
                async (_, taskCancellationToken) =>
                {
                    loadResult = await _lazyLoadingService.LoadSelectedTextureAsync(
                        new SelectedTextureLoadRequest(workspace, source.Asset, source.Metadata),
                        taskCancellationToken).ConfigureAwait(false);
                    return loadResult.Succeeded
                        ? BackgroundTaskExecutionResult.Success()
                        : BackgroundTaskExecutionResult.Failure("workspace.selected_texture_failed");
                }),
            cancellationToken);
        if (!enqueue.Succeeded)
        {
            return null;
        }

        _activeImageTaskId = enqueue.TaskId;
        try
        {
            var snapshot = await _taskManager.WaitForCompletionAsync(enqueue.TaskId, cancellationToken);
            return snapshot?.State == BackgroundTaskState.Succeeded && loadResult?.Succeeded == true
                ? loadResult.Image
                : null;
        }
        finally
        {
            _activeImageTaskId = default;
        }
    }

    public bool CancelSelectedImageLoading() =>
        _activeImageTaskId.IsValid && _taskManager.TryCancel(_activeImageTaskId);

    public async Task RefreshAfterApplyAsync(
        ModRelativePath textureRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (!textureRelativePath.IsValid)
        {
            return;
        }

        _loadedProjectId = null;
        await LoadAsync(cancellationToken);
        var expectedPath = NormalizeTextureRelativePath(textureRelativePath.Value);
        SelectedTexture = _allTextures.FirstOrDefault(item => string.Equals(
            NormalizeTextureRelativePath(item.RelativePath),
            expectedPath,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeTextureRelativePath(string value) =>
        value.Replace('\\', '/').Trim().TrimStart('/');

    private void PublishScan(AuditionProject project, SmartModScanResult scanResult)
    {
        _loadedProjectId = project.ProjectId;
        _sourceTextures = scanResult.Groups
            .SelectMany(group => group.Textures)
            .ToImmutableDictionary(texture => texture.Asset.RelativePath, StringComparer.OrdinalIgnoreCase);
        _allTextures = scanResult.Groups
            .SelectMany(group => group.Textures.Select(texture => CreateItem(project, texture)))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToImmutableArray();
        CategoryFilters = [
            new(null, "Tất cả danh mục"),
            .. _allTextures
                .Select(item => item.Category)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(category => category, StringComparer.Ordinal)
                .Select(category => new WorkspaceCategoryFilterOption(category, category))
        ];
        _categoryFilter = null;
        FolderFilters = [
            new(null, "T\u1EA5t c\u1EA3 th\u01B0 m\u1EE5c"),
            .. _allTextures
                .Select(item => item.DirectoryRelativePath)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(folder => folder, StringComparer.Ordinal)
                .Select(folder => new WorkspaceFolderFilterOption(
                    folder, string.IsNullOrEmpty(folder) ? "T\u1EC7p g\u1ED1c" : folder))
        ];
        _folderFilter = null;
        _selectedTexture = null;
        ApplyFilter();
        SetLoading(false, $"Đã tải thông tin của {_allTextures.Length} Texture.", 100);
        OnPropertyChanged(nameof(TextureCountDisplay));
        OnPropertyChanged(nameof(ModifiedCountDisplay));
        OnPropertyChanged(nameof(WarningCountDisplay));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(CategoryFilters));
        OnPropertyChanged(nameof(SelectedCategoryFilter));
        OnPropertyChanged(nameof(FolderFilters));
        OnPropertyChanged(nameof(SelectedFolderFilter));
        OnPropertyChanged(nameof(SelectedTexture));
        OnPropertyChanged(nameof(CanEditSelectedTexture));
        RaiseSelectedProperties();
    }

    private WorkspaceTextureItem CreateItem(AuditionProject project, SmartTextureAsset texture)
    {
        var relativePath = new ModRelativePath(texture.Asset.RelativePath);
        var state = _textureStateMachine.Evaluate(
            project,
            relativePath,
            new TextureRuntimeObservation(true, true, false));
        var displayName = string.IsNullOrWhiteSpace(texture.ManifestResolution.DisplayName)
            ? texture.Asset.FileName
            : texture.ManifestResolution.DisplayName;
        return new WorkspaceTextureItem(
            texture.Asset.RelativePath,
            texture.Asset.DirectoryRelativePath,
            texture.Asset.FileName,
            displayName,
            texture.Metadata.Width,
            texture.Metadata.Height,
            $"{texture.Metadata.Width} × {texture.Metadata.Height}",
            texture.Metadata.Format.ToString(),
            texture.ManifestResolution.Slot?.Category.Value ?? "Chưa phân loại",
            texture.Metadata.HasAlphaChannel,
            state.Succeeded ? state.State : TextureState.Invalid,
            state.Succeeded ? "Hợp lệ" : "Chưa có trạng thái",
            !texture.ManifestResolution.UsedFallback,
            texture.ManifestResolution.Slot?.Editable == true,
            texture.ManifestResolution.Slot?.RecommendedEditMode.Value ?? "Chưa xác định");
    }

    private void ApplyFilter()
    {
        var query = _searchQuery.Trim();
        var filtered = _allTextures.Where(item =>
            (_mappingFilter == WorkspaceMappingFilter.All
             || _mappingFilter == WorkspaceMappingFilter.ManifestMapped && item.IsManifestMapped
             || _mappingFilter == WorkspaceMappingFilter.Unmapped && !item.IsManifestMapped)
            && MatchesStatus(item)
            && MatchesSize(item)
            && MatchesAlpha(item)
            && (_categoryFilter is null
                || string.Equals(item.Category, _categoryFilter, StringComparison.Ordinal))
            && (_folderFilter is null
                || string.Equals(item.DirectoryRelativePath, _folderFilter, StringComparison.Ordinal))
            && (query.Length == 0
                || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)));

        _filteredTextures = filtered
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToImmutableArray();

        _folders = _filteredTextures
            .GroupBy(item => item.DirectoryRelativePath, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new WorkspaceFolderItem(
                group.Key,
                group.OrderBy(item => item.FileName, StringComparer.Ordinal).ToImmutableArray()))
            .ToImmutableArray();

        if (_selectedTexture is not null && !_folders.Any(folder => folder.Textures.Contains(_selectedTexture)))
        {
            SelectedTexture = null;
        }

        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(FilteredTextures));
    }

    private bool MatchesStatus(WorkspaceTextureItem item) => _statusFilter switch
    {
        TextureStatusFilter.All => true,
        TextureStatusFilter.Modified => item.State == TextureState.Modified,
        TextureStatusFilter.Original => item.State == TextureState.Original,
        TextureStatusFilter.Invalid => item.State == TextureState.Invalid,
        TextureStatusFilter.AI => item.State == TextureState.AiGenerated,
        _ => false
    };

    private bool MatchesSize(WorkspaceTextureItem item) => _sizeFilter switch
    {
        TextureSizeFilter.All => true,
        TextureSizeFilter.Small => item.MaximumDimension <= WorkspaceTextureItem.SmallMaximumDimension,
        TextureSizeFilter.Medium => item.MaximumDimension > WorkspaceTextureItem.SmallMaximumDimension
            && item.MaximumDimension <= WorkspaceTextureItem.MediumMaximumDimension,
        TextureSizeFilter.Large => item.MaximumDimension > WorkspaceTextureItem.MediumMaximumDimension,
        _ => false
    };

    private bool MatchesAlpha(WorkspaceTextureItem item) => _alphaFilter switch
    {
        TextureAlphaFilter.All => true,
        TextureAlphaFilter.HasAlpha => item.HasAlpha,
        TextureAlphaFilter.NoAlpha => !item.HasAlpha,
        _ => false
    };

    private void SetFilter<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        where T : struct, Enum
    {
        if (EqualityComparer<T>.Default.Equals(field, value) || !Enum.IsDefined(value))
        {
            return;
        }

        field = value;
        ApplyFilter();
        OnPropertyChanged(propertyName);
    }

    private void Reset(string message)
    {
        _loadedProjectId = null;
        _allTextures = [];
        _filteredTextures = [];
        _sourceTextures = ImmutableDictionary<string, SmartTextureAsset>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
        _folders = [];
        CategoryFilters = [new(null, "Tất cả danh mục")];
        _categoryFilter = null;
        _selectedTexture = null;
        SetLoading(false, message, 0);
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(FilteredTextures));
        OnPropertyChanged(nameof(CategoryFilters));
        OnPropertyChanged(nameof(SelectedCategoryFilter));
        OnPropertyChanged(nameof(SelectedTexture));
        OnPropertyChanged(nameof(CanEditSelectedTexture));
        RaiseSelectedProperties();
    }

    private void UpdateProgress(SmartModScanProgress progress)
    {
        _progressPercentage = progress.TotalTextures <= 0
            ? 0
            : Math.Clamp((double)progress.CompletedTextures / progress.TotalTextures * 100, 0, 100);
        _statusMessage = progress.Phase switch
        {
            SmartModScanPhase.ScanningAssets => "Đang quét nội dung dự án.",
            SmartModScanPhase.ReadingMetadata => "Đang đọc thông tin DDS.",
            SmartModScanPhase.ResolvingManifest => "Đang xác định nhãn Texture.",
            SmartModScanPhase.Completed => "Đang hoàn tất danh sách Texture.",
            _ => "Đang tải Texture của dự án."
        };
        OnPropertyChanged(nameof(ProgressPercentage));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void SetLoading(bool isLoading, string message, double progress)
    {
        _isLoading = isLoading;
        _statusMessage = message;
        _progressPercentage = progress;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ProgressPercentage));
        RaiseWorkflowProperties();
    }

    private void RaiseSelectedProperties()
    {
        OnPropertyChanged(nameof(SelectedDisplayName));
        OnPropertyChanged(nameof(SelectedRelativePath));
        OnPropertyChanged(nameof(SelectedTargetSize));
        OnPropertyChanged(nameof(SelectedFormat));
        OnPropertyChanged(nameof(SelectedState));
        OnPropertyChanged(nameof(SelectedValidation));
        OnPropertyChanged(nameof(SelectedEditMode));
        OnPropertyChanged(nameof(PreviewMessage));
        OnPropertyChanged(nameof(SelectedAssetSummary));
    }

    private void RaiseWorkflowProperties()
    {
        OnPropertyChanged(nameof(ProjectIdentitySummary));
        OnPropertyChanged(nameof(WorkflowSummary));
        OnPropertyChanged(nameof(SelectedAssetSummary));
        OnPropertyChanged(nameof(BuildReadinessSummary));
    }

    private static string ToStageCode(SmartModScanPhase phase) => phase switch
    {
        SmartModScanPhase.ScanningAssets => "scanning_assets",
        SmartModScanPhase.ReadingMetadata => "reading_metadata",
        SmartModScanPhase.ResolvingManifest => "resolving_manifest",
        SmartModScanPhase.Completed => "completed",
        _ => "scanning"
    };

    private static string ToDiagnosticCode(SmartModScanFailureReason reason) => reason switch
    {
        SmartModScanFailureReason.Cancelled => "workspace.scan_cancelled",
        SmartModScanFailureReason.UnknownGame => "workspace.unknown_game",
        SmartModScanFailureReason.UnknownMod => "workspace.unknown_mod",
        _ => "workspace.scan_failed"
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
