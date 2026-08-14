using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Shell;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;

namespace AuditionModStudio.App.Editor;

public sealed class ImageEditorViewModel : INotifyPropertyChanged
{
    private readonly IWorkspaceTextureSelection _selection;
    private readonly IImageTransformService _transformService;
    private readonly IImageResizeService _resizeService;
    private readonly ITextureApplyService? _applyService;
    private readonly IApplicationProjectSession? _projectSession;
    private readonly IBackgroundTaskManager? _taskManager;
    private InternalImage? _sourceImage;
    private InternalImage? _afterImage;
    private InteractiveImageTransformState? _transform;
    private EditorResizeModeOption _selectedMode = ImageEditorModes.Supported[0];
    private ImageCompareModeOption _selectedCompareMode = ImageCompareModes.Supported[0];
    private ImageCompareToggleState _compareToggleState = ImageCompareToggleState.After;
    private string _loadedRelativePath = string.Empty;
    private string _statusMessage = "Hãy chọn Texture trong dự án trước khi mở Trình chỉnh sửa ảnh.";
    private string _afterPreviewStatus = "Bản xem trước chỉ khả dụng sau khi tải Texture.";
    private bool _isLoading;
    private bool _isAfterPreviewLoading;
    private bool _showCheckerboard = true;
    private bool _isApplying;
    private string _applyStatus = "Áp dụng sẽ kiểm tra và chỉ thay Texture đang chọn một cách an toàn.";
    private double _applyProgress;
    private double _compareDivider = 0.5;
    private double _compareZoom = 1;
    private ViewportVector _comparePan;
    private int _transformRevision;
    private int _compareRevision;
    private long _activationVersion;
    private long _afterPreviewVersion;
    private CancellationTokenSource? _afterPreviewCancellation;
    private Task _afterPreviewTask = Task.CompletedTask;
    private BackgroundTaskId _activeApplyTaskId;

    public ImageEditorViewModel(
        IWorkspaceTextureSelection selection,
        IImageTransformService transformService,
        IImageResizeService resizeService,
        ITextureApplyService? applyService = null,
        IApplicationProjectSession? projectSession = null,
        IBackgroundTaskManager? taskManager = null)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _transformService = transformService ?? throw new ArgumentNullException(nameof(transformService));
        _resizeService = resizeService ?? throw new ArgumentNullException(nameof(resizeService));
        _applyService = applyService;
        _projectSession = projectSession;
        _taskManager = taskManager;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<EditorResizeModeOption> Modes => ImageEditorModes.Supported;

    public EditorResizeModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is null || !ImageEditorModes.Supported.Contains(value) || value == _selectedMode)
            {
                return;
            }

            _selectedMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeDescription));
            AdvanceTransformRevision();
            StartAfterPreviewRefresh();
        }
    }

    public InternalImage? BeforeImage => _sourceImage;

    public InternalImage? SourceImage => _sourceImage;

    public InternalImage? AfterImage => _afterImage;

    public IReadOnlyList<ImageCompareModeOption> CompareModes => ImageCompareModes.Supported;

    public ImageCompareModeOption SelectedCompareMode
    {
        get => _selectedCompareMode;
        set
        {
            if (value is null || !ImageCompareModes.Supported.Contains(value) || value == _selectedCompareMode)
            {
                return;
            }

            _selectedCompareMode = value;
            OnPropertyChanged();
            AdvanceCompareRevision();
        }
    }

    public ImageCompareToggleState CompareToggleState => _compareToggleState;

    public bool ShowCheckerboard => _showCheckerboard;

    public double CompareDivider => _compareDivider;

    public double CompareZoom => _compareZoom;

    public ViewportVector ComparePan => _comparePan;

    public string ComparePanSummary => $"X {_comparePan.X:0.#}, Y {_comparePan.Y:0.#}";

    public string AfterPreviewStatus => _afterPreviewStatus;

    public bool IsAfterPreviewLoading => _isAfterPreviewLoading;

    public bool IsApplying => _isApplying;

    public bool CanApply => HasImage
                            && _afterImage is not null
                            && !_isAfterPreviewLoading
                            && !_isApplying
                            && _applyService is not null
                            && _projectSession is not null
                            && _taskManager is not null;

    public bool CanCancelApply => _isApplying && _activeApplyTaskId.IsValid;

    public string ApplyStatus => _applyStatus;

    public double ApplyProgress => _applyProgress;

    public int CompareRevision => _compareRevision;

    public bool IsLoading => _isLoading;

    public bool CanCancel => _isLoading;

    public bool HasImage => _sourceImage is not null && _transform is not null;

    public bool CanEdit => HasImage && !_isApplying;

    public string TextureName => _selection.SelectedTexture?.DisplayName ?? "Chưa chọn Texture";

    public int TargetWidth => _selection.SelectedTexture?.Width ?? 0;

    public int TargetHeight => _selection.SelectedTexture?.Height ?? 0;

    public string TargetDimensions => HasImage ? $"{TargetWidth} × {TargetHeight} px" : "—";

    public string StatusMessage => _statusMessage;

    public double Zoom => _transform?.Zoom ?? 1;

    public string PanSummary => _transform is null
        ? "—"
        : $"X {_transform.Pan.X:0.#}, Y {_transform.Pan.Y:0.#}";

    public string CropSummary
    {
        get
        {
            if (_transform is null)
            {
                return "—";
            }

            var crop = _transformService.ToPixelCrop(_transform);
            return crop.Succeeded && crop.Value is { } value
                ? $"{value.X}, {value.Y} · {value.Width} × {value.Height} px"
            : "Chưa thể cắt ảnh";
        }
    }

    public string ModeDescription => _selectedMode.Mode switch
    {
        ImageResizeMode.ManualCrop => "Cắt vùng đã chọn theo đúng khung đích.",
        ImageResizeMode.Fit => "Đưa toàn bộ ảnh vào khung đích và giữ nguyên tỷ lệ.",
        ImageResizeMode.Fill => "Lấp đầy khung đích, giữ tỷ lệ và cắt phần dư.",
        ImageResizeMode.Stretch => "Kéo giãn ảnh theo đúng kích thước đích.",
        ImageResizeMode.CanvasResize => "Giữ tỷ lệ ảnh và đổi kích thước khung xung quanh.",
        ImageResizeMode.TransparentPadding => "Thêm khoảng trong suốt mà không đổi tỷ lệ ảnh.",
        _ => "Chế độ xem trước không được hỗ trợ."
    };

    public int TransformRevision => _transformRevision;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        var activationVersion = Interlocked.Increment(ref _activationVersion);
        var selected = _selection.SelectedTexture;
        if (selected is null)
        {
            ResetEditor("Hãy chọn Texture trong dự án trước khi mở Trình chỉnh sửa ảnh.");
            return;
        }

        if (_sourceImage is not null
            && string.Equals(_loadedRelativePath, selected.RelativePath, StringComparison.Ordinal))
        {
            RaiseSelectionProperties();
            return;
        }

        PrepareForSelectionLoad();
        SetLoading(true, "Đang tải Texture đã chọn để xem trước.");
        var image = await _selection.LoadSelectedImageAsync(cancellationToken);
        if (activationVersion != Volatile.Read(ref _activationVersion))
        {
            return;
        }

        if (image is null || !ReferenceEquals(selected, _selection.SelectedTexture))
        {
            ResetEditor("Không thể tải Texture đã chọn. Hãy quay lại Dự án rồi thử lại.");
            return;
        }

        var create = _transformService.Create(image);
        if (!create.Succeeded || create.Value is null)
        {
            ResetEditor("Không thể khởi tạo công cụ chỉnh sửa cho Texture này.");
            return;
        }

        _sourceImage = image;
        _transform = create.Value;
        _compareZoom = 1;
        _comparePan = default;
        _loadedRelativePath = selected.RelativePath;
        SetLoading(false, "Texture đã sẵn sàng. Hãy xem trước rồi áp dụng vào dự án.");
        RaiseSelectionProperties();
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(BeforeImage));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PanSummary));
        OnPropertyChanged(nameof(CropSummary));
        AdvanceTransformRevision();
        AdvanceCompareRevision();
        await RefreshAfterPreviewAsync();
    }

    public bool CancelLoading() => _selection.CancelSelectedImageLoading();

    public bool CancelApply() =>
        _activeApplyTaskId.IsValid && _taskManager?.TryCancel(_activeApplyTaskId) == true;

    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!CanApply
            || _applyService is null
            || _projectSession is null
            || _taskManager is null
            || _selection.SelectedTexture is not { } selected
            || _projectSession.Project is not { } project
            || _projectSession.Workspace is not { } workspace
            || CreateResizeRequest() is not { } resizeRequest)
        {
            return false;
        }

        ModRelativePath texturePath;
        try
        {
            texturePath = new ModRelativePath(selected.RelativePath);
        }
        catch (ArgumentException)
        {
            _applyStatus = "Đường dẫn Texture đã chọn không hợp lệ.";
            OnPropertyChanged(nameof(ApplyStatus));
            return false;
        }

        TextureApplyResult? applyResult = null;
        _isApplying = true;
        _applyProgress = 0;
        _applyStatus = "Đang chuẩn bị áp dụng Texture.";
        RaiseApplyProperties();
        try
        {
            IProgress<TextureApplyProgress> uiProgress = new Progress<TextureApplyProgress>(UpdateApplyProgress);
            var enqueue = await _taskManager.EnqueueAsync(new(
                BackgroundTaskKind.Convert,
                async (taskProgress, taskCancellationToken) =>
                {
                    var progress = new CallbackProgress<TextureApplyProgress>(value =>
                    {
                        taskProgress.Report(new(
                            value.CompletedSteps,
                            value.TotalSteps,
                            value.Phase.ToString()));
                        uiProgress.Report(value);
                    });
                    applyResult = await _applyService.ApplyAsync(new(
                        project,
                        workspace,
                        texturePath,
                        resizeRequest), progress, taskCancellationToken).ConfigureAwait(false);
                    return applyResult.Succeeded
                        ? BackgroundTaskExecutionResult.Success()
                        : BackgroundTaskExecutionResult.Failure(
                            applyResult.DiagnosticCode ?? "texture.apply_failed");
                }), cancellationToken);
            if (!enqueue.Succeeded)
            {
                _applyStatus = "Không thể đưa tác vụ áp dụng Texture vào hàng đợi.";
                OnPropertyChanged(nameof(ApplyStatus));
                return false;
            }

            _activeApplyTaskId = enqueue.TaskId;
            OnPropertyChanged(nameof(CanCancelApply));
            var snapshot = await _taskManager.WaitForCompletionAsync(enqueue.TaskId, cancellationToken);
            if (snapshot?.State != BackgroundTaskState.Succeeded
                || applyResult?.Succeeded != true
                || applyResult.Project is null
                || !ReferenceEquals(_projectSession.Project, project)
                || !ReferenceEquals(_projectSession.Workspace, workspace))
            {
                _applyStatus = snapshot?.State == BackgroundTaskState.Cancelled || applyResult?.Cancelled == true
                ? "Đã hủy áp dụng; Texture làm việc đã được khôi phục."
                : "Không thể áp dụng; Texture làm việc đã được khôi phục.";
                OnPropertyChanged(nameof(ApplyStatus));
                return false;
            }

            await _projectSession.ActivateAsync(applyResult.Project, workspace);
            await _selection.RefreshAfterApplyAsync(texturePath, cancellationToken);
            ResetEditor("Đang tải lại Texture vừa áp dụng.");
            await ActivateAsync(cancellationToken);
            _applyProgress = 100;
            _applyStatus = applyResult.CleanupPending
                ? "Đã áp dụng Texture và lưu dự án. Một số dữ liệu tạm vẫn đang được dọn dẹp."
                : "Đã áp dụng Texture an toàn; lịch sử, trạng thái thay đổi và ảnh thu nhỏ đã được lưu.";
            OnPropertyChanged(nameof(ApplyProgress));
            OnPropertyChanged(nameof(ApplyStatus));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _applyStatus = "Đã hủy áp dụng Texture.";
            OnPropertyChanged(nameof(ApplyStatus));
            return false;
        }
        finally
        {
            _activeApplyTaskId = default;
            _isApplying = false;
            RaiseApplyProperties();
        }
    }

    public void Unload()
    {
        Interlocked.Increment(ref _activationVersion);
        CancelAfterPreview();
        if (_isLoading)
        {
            _selection.CancelSelectedImageLoading();
        }

        CancelApply();

        ResetEditor("Đã đóng bản xem trước. Hãy chọn Texture và mở lại Trình chỉnh sửa ảnh để tiếp tục.");
    }

    public bool SetZoom(double zoom)
    {
        if (_transform is null)
        {
            return false;
        }

        var result = _transformService.SetViewportTransform(_transform, zoom, _transform.Pan);
        return PublishTransform(result);
    }

    public bool PanBy(double horizontal, double vertical)
    {
        if (_transform is null || !double.IsFinite(horizontal) || !double.IsFinite(vertical))
        {
            return false;
        }

        var pan = new ViewportVector(_transform.Pan.X + horizontal, _transform.Pan.Y + vertical);
        return PublishTransform(_transformService.SetViewportTransform(_transform, _transform.Zoom, pan));
    }

    public bool SetCropPercent(double x, double y, double width, double height)
    {
        if (_transform is null)
        {
            return false;
        }

        var requested = new NormalizedImageRectangle(x / 100, y / 100, width / 100, height / 100);
        return PublishTransform(_transformService.SetCrop(_transform, requested));
    }

    public bool ResetTransform()
    {
        if (_transform is null)
        {
            return false;
        }

        return PublishTransform(_transformService.Reset(_transform));
    }

    public bool SetCompareDivider(double divider)
    {
        if (!double.IsFinite(divider))
        {
            return false;
        }

        var normalized = Math.Clamp(divider, 0, 1);
        if (_compareDivider == normalized)
        {
            return true;
        }

        _compareDivider = normalized;
        OnPropertyChanged(nameof(CompareDivider));
        AdvanceCompareRevision();
        return true;
    }

    public bool SetCompareToggleState(ImageCompareToggleState state)
    {
        if (!Enum.IsDefined(state))
        {
            return false;
        }

        if (_compareToggleState == state)
        {
            return true;
        }

        _compareToggleState = state;
        OnPropertyChanged(nameof(CompareToggleState));
        AdvanceCompareRevision();
        return true;
    }

    public void SetCheckerboard(bool visible)
    {
        if (_showCheckerboard == visible)
        {
            return;
        }

        _showCheckerboard = visible;
        OnPropertyChanged(nameof(ShowCheckerboard));
        AdvanceCompareRevision();
    }

    public bool SetCompareZoom(double zoom)
    {
        if (!double.IsFinite(zoom) || zoom < 0.1 || zoom > 8)
        {
            return false;
        }

        _compareZoom = zoom;
        OnPropertyChanged(nameof(CompareZoom));
        AdvanceCompareRevision();
        return true;
    }

    public bool PanCompareBy(double horizontal, double vertical)
    {
        if (!double.IsFinite(horizontal) || !double.IsFinite(vertical))
        {
            return false;
        }

        _comparePan = new ViewportVector(_comparePan.X + horizontal, _comparePan.Y + vertical);
        OnPropertyChanged(nameof(ComparePan));
        OnPropertyChanged(nameof(ComparePanSummary));
        AdvanceCompareRevision();
        return true;
    }

    public void ResetCompareCamera()
    {
        _compareZoom = 1;
        _comparePan = default;
        OnPropertyChanged(nameof(CompareZoom));
        OnPropertyChanged(nameof(ComparePan));
        OnPropertyChanged(nameof(ComparePanSummary));
        AdvanceCompareRevision();
    }

    public EditorCanvasRectangle? GetCompareProjection(
        InternalImage image,
        double viewportWidth,
        double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!double.IsFinite(viewportWidth)
            || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0
            || viewportHeight <= 0)
        {
            return null;
        }

        var created = _transformService.Create(image);
        if (!created.Succeeded || created.Value is null)
        {
            return null;
        }

        var camera = _transformService.SetViewportTransform(
            created.Value,
            _compareZoom,
            _comparePan);
        if (!camera.Succeeded || camera.Value is null)
        {
            return null;
        }

        var viewport = new ViewportSize(viewportWidth, viewportHeight);
        var topLeft = _transformService.MapImageToViewport(
            camera.Value,
            viewport,
            new ImagePixelPoint(0, 0));
        var bottomRight = _transformService.MapImageToViewport(
            camera.Value,
            viewport,
            new ImagePixelPoint(image.Width, image.Height));
        return topLeft.Succeeded && bottomRight.Succeeded
            ? ToRectangle(topLeft.Value!, bottomRight.Value!)
            : null;
    }

    public Task WaitForAfterPreviewAsync() => _afterPreviewTask;

    public ImageResizeRequest? CreateResizeRequest()
    {
        if (_sourceImage is null || _transform is null || TargetWidth <= 0 || TargetHeight <= 0)
        {
            return null;
        }

        if (_selectedMode.Mode == ImageResizeMode.ManualCrop)
        {
            var manual = _transformService.ToManualCropResizeRequest(
                _transform,
                _sourceImage,
                TargetWidth,
                TargetHeight);
            return manual.Succeeded ? manual.Value : null;
        }

        return new ImageResizeRequest(
            _sourceImage,
            TargetWidth,
            TargetHeight,
            new ImageResizeOptions(_selectedMode.Mode));
    }

    public EditorCanvasProjection? GetProjection(double viewportWidth, double viewportHeight)
    {
        if (_transform is null
            || !double.IsFinite(viewportWidth)
            || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0
            || viewportHeight <= 0)
        {
            return null;
        }

        if (_selectedMode.Mode == ImageResizeMode.Stretch)
        {
            var stretchCrop = _transform.Crop;
            return new EditorCanvasProjection(
                new EditorCanvasRectangle(0, 0, viewportWidth, viewportHeight),
                new EditorCanvasRectangle(
                    stretchCrop.X * viewportWidth,
                    stretchCrop.Y * viewportHeight,
                    stretchCrop.Width * viewportWidth,
                    stretchCrop.Height * viewportHeight));
        }

        if (_selectedMode.Mode is ImageResizeMode.CanvasResize or ImageResizeMode.TransparentPadding)
        {
            var targetScale = viewportWidth / TargetWidth;
            var imageWidth = _transform.ImageWidth * targetScale * _transform.Zoom;
            var imageHeight = _transform.ImageHeight * targetScale * _transform.Zoom;
            var canvasImageBounds = new EditorCanvasRectangle(
                (viewportWidth - imageWidth) / 2 + _transform.Pan.X,
                (viewportHeight - imageHeight) / 2 + _transform.Pan.Y,
                imageWidth,
                imageHeight);
            return new EditorCanvasProjection(
                canvasImageBounds,
                ProjectCrop(canvasImageBounds, _transform.Crop));
        }

        var viewport = new ViewportSize(viewportWidth, viewportHeight);
        var imageTopLeft = _transformService.MapImageToViewport(_transform, viewport, new ImagePixelPoint(0, 0));
        var imageBottomRight = _transformService.MapImageToViewport(
            _transform,
            viewport,
            new ImagePixelPoint(_transform.ImageWidth, _transform.ImageHeight));
        if (!imageTopLeft.Succeeded || !imageBottomRight.Succeeded)
        {
            return null;
        }

        var imageBounds = ToRectangle(imageTopLeft.Value!, imageBottomRight.Value!);
        if (_selectedMode.Mode == ImageResizeMode.Fill)
        {
            var fillScale = Math.Max(
                imageBounds.Width > 0 ? viewportWidth / imageBounds.Width : 1,
                imageBounds.Height > 0 ? viewportHeight / imageBounds.Height : 1);
            if (fillScale > 1)
            {
                var filledWidth = imageBounds.Width * fillScale;
                var filledHeight = imageBounds.Height * fillScale;
                imageBounds = new EditorCanvasRectangle(
                    imageBounds.X - (filledWidth - imageBounds.Width) / 2,
                    imageBounds.Y - (filledHeight - imageBounds.Height) / 2,
                    filledWidth,
                    filledHeight);
            }
        }

        return new EditorCanvasProjection(imageBounds, ProjectCrop(imageBounds, _transform.Crop));
    }

    private bool PublishTransform(ImageTransformResult<InteractiveImageTransformState> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            _statusMessage = "Thao tác này vượt quá giới hạn ảnh được hỗ trợ.";
            OnPropertyChanged(nameof(StatusMessage));
            return false;
        }

        _transform = result.Value;
        _statusMessage = "Đã cập nhật bản xem trước. File dự án chưa bị thay đổi.";
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PanSummary));
        OnPropertyChanged(nameof(CropSummary));
        AdvanceTransformRevision();
        StartAfterPreviewRefresh();
        return true;
    }

    private void ResetEditor(string message)
    {
        CancelAfterPreview();
        _sourceImage = null;
        _afterImage = null;
        _transform = null;
        _compareZoom = 1;
        _comparePan = default;
        _loadedRelativePath = string.Empty;
        _afterPreviewStatus = "Bản xem trước chỉ khả dụng sau khi tải Texture.";
        SetLoading(false, message);
        RaiseSelectionProperties();
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(BeforeImage));
        OnPropertyChanged(nameof(AfterImage));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PanSummary));
        OnPropertyChanged(nameof(CropSummary));
        OnPropertyChanged(nameof(CompareZoom));
        OnPropertyChanged(nameof(ComparePan));
        OnPropertyChanged(nameof(ComparePanSummary));
        OnPropertyChanged(nameof(AfterPreviewStatus));
        AdvanceCompareRevision();
        AdvanceTransformRevision();
    }

    private void PrepareForSelectionLoad()
    {
        CancelAfterPreview();
        _sourceImage = null;
        _afterImage = null;
        _transform = null;
        _loadedRelativePath = string.Empty;
        _afterPreviewStatus = "Đang chờ ảnh gốc của Texture đã chọn.";
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(BeforeImage));
        OnPropertyChanged(nameof(AfterImage));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(AfterPreviewStatus));
        OnPropertyChanged(nameof(CanApply));
        AdvanceCompareRevision();
        AdvanceTransformRevision();
    }

    private void StartAfterPreviewRefresh() => _afterPreviewTask = RefreshAfterPreviewAsync();

    private async Task RefreshAfterPreviewAsync()
    {
        var request = CreateResizeRequest();
        if (request is null)
        {
            return;
        }

        CancelAfterPreview();
        var version = Interlocked.Increment(ref _afterPreviewVersion);
        var cancellation = new CancellationTokenSource();
        _afterPreviewCancellation = cancellation;
        _isAfterPreviewLoading = true;
        _afterPreviewStatus = "Đang tạo bản xem trước sau chỉnh sửa…";
        OnPropertyChanged(nameof(IsAfterPreviewLoading));
        OnPropertyChanged(nameof(AfterPreviewStatus));
        OnPropertyChanged(nameof(CanApply));

        try
        {
            var result = await Task.Run(
                () => _resizeService.ResizeAsync(request, cancellation.Token),
                cancellation.Token);
            if (version != Volatile.Read(ref _afterPreviewVersion)
                || cancellation.IsCancellationRequested
                || !ReferenceEquals(request.Source, _sourceImage))
            {
                return;
            }

            if (!result.Succeeded || result.Image is null)
            {
                _afterImage = null;
                _afterPreviewStatus = result.Cancelled
                ? "Đã hủy tạo bản xem trước sau chỉnh sửa."
                : $"Không thể tạo bản xem trước sau chỉnh sửa ({result.DiagnosticCode ?? "lỗi chưa xác định"}).";
                OnPropertyChanged(nameof(AfterImage));
                OnPropertyChanged(nameof(AfterPreviewStatus));
                AdvanceCompareRevision();
                return;
            }

            _afterImage = result.Image;
            _afterPreviewStatus = "Bản xem trước sau chỉnh sửa đã sẵn sàng. Chế độ so sánh chỉ để xem.";
            OnPropertyChanged(nameof(AfterImage));
            OnPropertyChanged(nameof(AfterPreviewStatus));
            OnPropertyChanged(nameof(CanApply));
            AdvanceCompareRevision();
        }
        catch (OperationCanceledException)
        {
            if (version == Volatile.Read(ref _afterPreviewVersion))
            {
                _afterPreviewStatus = "Đã hủy tạo bản xem trước sau chỉnh sửa.";
                OnPropertyChanged(nameof(AfterPreviewStatus));
            }
        }
        catch (Exception exception)
        {
            if (version == Volatile.Read(ref _afterPreviewVersion))
            {
                _afterImage = null;
                _afterPreviewStatus = $"Không thể tạo bản xem trước sau chỉnh sửa ({exception.GetType().Name}).";
                OnPropertyChanged(nameof(AfterImage));
                OnPropertyChanged(nameof(AfterPreviewStatus));
                AdvanceCompareRevision();
                OnPropertyChanged(nameof(CanApply));
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _afterPreviewVersion))
            {
                _isAfterPreviewLoading = false;
                OnPropertyChanged(nameof(IsAfterPreviewLoading));
                OnPropertyChanged(nameof(CanApply));
            }

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _afterPreviewCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void CancelAfterPreview()
    {
        Interlocked.Increment(ref _afterPreviewVersion);
        var cancellation = Interlocked.Exchange(ref _afterPreviewCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        if (_isAfterPreviewLoading)
        {
            _isAfterPreviewLoading = false;
            OnPropertyChanged(nameof(IsAfterPreviewLoading));
            OnPropertyChanged(nameof(CanApply));
        }
    }

    private void UpdateApplyProgress(TextureApplyProgress progress)
    {
        _applyProgress = progress.TotalSteps <= 0
            ? 0
            : Math.Clamp((double)progress.CompletedSteps / progress.TotalSteps * 100, 0, 100);
        _applyStatus = progress.Phase switch
        {
            TextureApplyPhase.ValidatingTarget => "Đang kiểm tra Texture DDS đích.",
            TextureApplyPhase.Resizing => "Đang dựng trạng thái cắt và đổi kích thước hiện tại.",
            TextureApplyPhase.Encoding => "Đang mã hóa file DDS tạm theo ảnh gốc.",
            TextureApplyPhase.ValidatingOutput => "Đang kiểm tra file DDS tạm.",
            TextureApplyPhase.Replacing => "Đang thay Texture làm việc một cách an toàn.",
            TextureApplyPhase.UpdatingHistory => "Đang ghi lịch sử chỉnh sửa và trạng thái thay đổi.",
            TextureApplyPhase.RegeneratingThumbnail => "Đang tạo lại ảnh thu nhỏ.",
            TextureApplyPhase.SavingProject => "Đang lưu dự án an toàn.",
            _ => "Đang áp dụng Texture."
        };
        OnPropertyChanged(nameof(ApplyProgress));
        OnPropertyChanged(nameof(ApplyStatus));
    }

    private void RaiseApplyProperties()
    {
        OnPropertyChanged(nameof(IsApplying));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanCancelApply));
        OnPropertyChanged(nameof(ApplyStatus));
        OnPropertyChanged(nameof(ApplyProgress));
    }

    private void SetLoading(bool loading, string message)
    {
        _isLoading = loading;
        _statusMessage = message;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void RaiseSelectionProperties()
    {
        OnPropertyChanged(nameof(TextureName));
        OnPropertyChanged(nameof(TargetWidth));
        OnPropertyChanged(nameof(TargetHeight));
        OnPropertyChanged(nameof(TargetDimensions));
    }

    private void AdvanceTransformRevision()
    {
        _transformRevision++;
        OnPropertyChanged(nameof(TransformRevision));
    }

    private void AdvanceCompareRevision()
    {
        _compareRevision++;
        OnPropertyChanged(nameof(CompareRevision));
    }

    private static EditorCanvasRectangle ToRectangle(ViewportPoint first, ViewportPoint second) =>
        new(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y));

    private static EditorCanvasRectangle ProjectCrop(
        EditorCanvasRectangle imageBounds,
        NormalizedImageRectangle crop) => new(
            imageBounds.X + crop.X * imageBounds.Width,
            imageBounds.Y + crop.Y * imageBounds.Height,
            crop.Width * imageBounds.Width,
            crop.Height * imageBounds.Height);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
