using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.App.Shell;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;
using AuditionModStudio.Core.Subscriptions;

namespace AuditionModStudio.App.AiStudio;

public sealed class AiStudioViewModel : INotifyPropertyChanged
{
    private readonly IAiService _aiService;
    private readonly IAiStudioService _studioService;
    private readonly IBackgroundTaskManager _taskManager;
    private readonly IWorkspaceTextureSelection _selection;
    private readonly ITextureApplyService? _applyService;
    private readonly IApplicationProjectSession? _projectSession;
    private readonly ILocalPromptPresetStore? _localPresets;
    private readonly ICloudPromptPresetService? _cloudPresets;
    private readonly ICapabilityAuthorizationService? _capabilities;
    private AiStudioOperationOption _selectedOperation = AiStudioOptions.Operations[0];
    private AiStudioOption _selectedModel = AiStudioOptions.Models[0];
    private AiStudioOption _selectedQuality = AiStudioOptions.Qualities[0];
    private AiStudioAspectOption _selectedAspect = AiStudioOptions.Aspects[0];
    private string _prompt = string.Empty;
    private string _negativePrompt = string.Empty;
    private string _outputWidth = "1024";
    private string _outputHeight = "1024";
    private string _statusMessage = "AI Studio đã sẵn sàng. Kết nối dịch vụ sẽ được kiểm tra khi mở trang.";
    private string _quoteText = "Chưa có thông tin chi phí";
    private string _historyMessage = "Đang tải lịch sử tạo…";
    private IReadOnlyList<AiStudioJobPresentation> _history = [];
    private IReadOnlyList<PromptPreset> _presets = [];
    private PromptPreset? _selectedPreset;
    private InternalImage? _previewImage;
    private bool _isBusy;
    private bool _isLoading;
    private int _progressPercentage;
    private BackgroundTaskId _activeTaskId;
    private CancellationTokenSource? _activationCancellation;

    public AiStudioViewModel(
        IAiService aiService,
        IAiStudioService studioService,
        IBackgroundTaskManager taskManager,
        IWorkspaceTextureSelection selection,
        ITextureApplyService? applyService = null,
        IApplicationProjectSession? projectSession = null,
        ILocalPromptPresetStore? localPresets = null,
        ICloudPromptPresetService? cloudPresets = null,
        ICapabilityAuthorizationService? capabilities = null)
    {
        _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
        _studioService = studioService ?? throw new ArgumentNullException(nameof(studioService));
        _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _applyService = applyService;
        _projectSession = projectSession;
        _localPresets = localPresets;
        _cloudPresets = cloudPresets;
        _capabilities = capabilities;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<AiStudioOperationOption> Operations => AiStudioOptions.Operations;
    public IReadOnlyList<AiStudioOption> Models => AiStudioOptions.Models;
    public IReadOnlyList<AiStudioOption> Qualities => AiStudioOptions.Qualities;
    public IReadOnlyList<AiStudioAspectOption> Aspects => AiStudioOptions.Aspects;
    public IReadOnlyList<PromptPreset> Presets { get => _presets; private set => Set(ref _presets, value); }
    public PromptPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!Set(ref _selectedPreset, value) || value is null) return;
            if (!value.ApplicableOperations.Contains(SelectedOperation.Operation))
            {
                StatusMessage = "Mẫu gợi ý này không phù hợp với tác vụ đã chọn.";
                return;
            }
            Prompt = value.Prompt.Value;
            NegativePrompt = value.NegativePrompt?.Value ?? string.Empty;
            StatusMessage = "Đã tải mẫu gợi ý. Hãy xem lại trước khi gửi; chưa dùng tác vụ AI hoặc Credits.";
        }
    }

    public AiStudioOperationOption SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (value is null || !Operations.Contains(value) || value == _selectedOperation) return;
            _selectedOperation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OperationDescription));
            OnPropertyChanged(nameof(ReferenceMessage));
            NotifyValidationChanged();
            _ = RefreshQuoteAsync();
        }
    }

    public AiStudioOption SelectedModel
    {
        get => _selectedModel;
        set { if (value is not null && Models.Contains(value) && Set(ref _selectedModel, value)) NotifyValidationChanged(); }
    }

    public AiStudioOption SelectedQuality
    {
        get => _selectedQuality;
        set { if (value is not null && Qualities.Contains(value) && Set(ref _selectedQuality, value)) NotifyValidationChanged(); }
    }

    public AiStudioAspectOption SelectedAspect
    {
        get => _selectedAspect;
        set { if (value is not null && Aspects.Contains(value) && Set(ref _selectedAspect, value)) NotifyValidationChanged(); }
    }

    public string Prompt
    {
        get => _prompt;
        set { if (Set(ref _prompt, value ?? string.Empty)) NotifyValidationChanged(); }
    }

    public string NegativePrompt
    {
        get => _negativePrompt;
        set { if (Set(ref _negativePrompt, value ?? string.Empty)) NotifyValidationChanged(); }
    }
    public string OutputWidth
    {
        get => _outputWidth;
        set { if (Set(ref _outputWidth, value ?? string.Empty)) NotifyValidationChanged(); }
    }
    public string OutputHeight
    {
        get => _outputHeight;
        set { if (Set(ref _outputHeight, value ?? string.Empty)) NotifyValidationChanged(); }
    }

    public string OperationDescription => SelectedOperation.Description;
    public string PromptValidationMessage => Prompt.Length > AiPrompt.MaximumLength
            ? $"Nội dung mô tả vượt quá {AiPrompt.MaximumLength:N0} ký tự."
        : string.IsNullOrWhiteSpace(Prompt) && SelectedOperation.Operation is not
            (AiStudioOperation.Upscale or AiStudioOperation.RemoveObject)
                ? "Hãy nhập nội dung mô tả cho tác vụ này."
            : string.Empty;
    public string NegativePromptValidationMessage => NegativePrompt.Length > AiPrompt.MaximumLength
            ? $"Nội dung cần tránh vượt quá {AiPrompt.MaximumLength:N0} ký tự."
        : string.Empty;
    public string OutputValidationMessage => SelectedOperation.Operation is not
        (AiStudioOperation.Outpaint or AiStudioOperation.Upscale) ? string.Empty
        : TryCreateExplicitTargetSize() is null
            ? "Hãy nhập kích thước đầu ra hợp lệ và lớn hơn ảnh nguồn đã chọn."
            : string.Empty;
    public string ReferenceMessage => SelectedOperation.RequiresReference
        ? _selection.SelectedTexture is null
            ? "Hãy chọn một Texture trong Dự án trước khi gửi."
            : $"Ảnh tham chiếu: {_selection.SelectedTexture.DisplayLabel}"
            : "Tác vụ này không cần ảnh tham chiếu.";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string QuoteText { get => _quoteText; private set => Set(ref _quoteText, value); }
    public string HistoryMessage { get => _historyMessage; private set => Set(ref _historyMessage, value); }
    public IReadOnlyList<AiStudioJobPresentation> History { get => _history; private set => Set(ref _history, value); }
    public InternalImage? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }
    public bool HasPreview => PreviewImage is not null;
    public bool CanApprovePreview => HasPreview && !IsBusy && _selection.SelectedTexture is not null
        && _applyService is not null && _projectSession?.Project is not null
        && _projectSession.Workspace is not null;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) NotifyValidationChanged(); } }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public int ProgressPercentage { get => _progressPercentage; private set => Set(ref _progressPercentage, value); }
    public bool CanCancel => IsBusy && _activeTaskId.IsValid;
    public bool CanSubmit => !IsBusy
        && Prompt.Length <= AiPrompt.MaximumLength
        && NegativePrompt.Length <= AiPrompt.MaximumLength
        && (SelectedOperation.Operation is AiStudioOperation.Upscale or AiStudioOperation.RemoveObject
            || !string.IsNullOrWhiteSpace(Prompt))
        && (SelectedOperation.Operation is not (AiStudioOperation.Outpaint or AiStudioOperation.Upscale)
            || TryCreateExplicitTargetSize() is not null)
        && (!SelectedOperation.RequiresReference || _selection.SelectedTexture is not null);

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsLoading = true;
        OnPropertyChanged(nameof(ReferenceMessage));
        NotifyValidationChanged();
        try
        {
            await Task.WhenAll(
                RefreshQuoteAsync(_activationCancellation.Token),
                RefreshHistoryAsync(_activationCancellation.Token),
                RefreshPresetsAsync(_activationCancellation.Token));
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RefreshPresetsAsync(CancellationToken cancellationToken = default)
    {
        var localTask = _localPresets?.LoadAsync(cancellationToken)
            ?? Task.FromResult(new PromptPresetCollectionResult(true, "PROMPT_PRESETS_EMPTY", [], []));
        var cloudTask = _cloudPresets?.ListOwnedAsync(cancellationToken)
            ?? Task.FromResult(new PromptPresetCollectionResult(false, "PROMPT_PRESET_CLOUD_UNAVAILABLE", [], []));
        await Task.WhenAll(localTask, cloudTask);
        var local = await localTask;
        var cloud = await cloudTask;
        var merged = PromptPresetMerge.Merge(local.Succeeded ? local.Presets : [], cloud.Succeeded ? cloud.Presets : []);
        Presets = merged.Presets;
        if (!local.Succeeded) StatusMessage = "Các mẫu gợi ý trên máy không hợp lệ và đã được cách ly.";
    }

    public void Deactivate()
    {
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationCancellation = null;
    }

    public async Task RefreshQuoteAsync(CancellationToken cancellationToken = default)
    {
        var result = await _studioService.GetQuoteAsync(SelectedOperation.Operation, cancellationToken);
        QuoteText = result.Succeeded && result.Quote is { CreditCost: > 0 } quote
            ? $"Ước tính {quote.CreditCost:N0} Credits · {quote.PricingVersion}"
            : "Chưa có chi phí · dịch vụ đang ngoại tuyến";
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        var result = await _studioService.GetHistoryAsync(cancellationToken);
        History = result.Succeeded
            ? result.Jobs.Take(100).Select(AiStudioJobPresentation.From).ToArray()
            : [];
        HistoryMessage = !result.Succeeded
            ? "Chưa thể tải lịch sử khi dịch vụ đang ngoại tuyến."
            : History.Count == 0
                ? "Chưa có tác vụ AI. Các tác vụ đã gửi sẽ xuất hiện tại đây."
                : $"{History.Count:N0} tác vụ gần đây";
    }

    public Task SubmitAsync(CancellationToken cancellationToken = default) =>
        SubmitAsync(null, cancellationToken);

    public async Task SubmitAsync(AiMask? mask, CancellationToken cancellationToken = default)
    {
        if (_capabilities is not null && !_capabilities.Current.CanUseAi)
        {
            StatusMessage = "Gói sử dụng chưa cho phép dùng AI. Hãy mở Tài khoản để làm mới quyền sử dụng.";
            return;
        }
        if (!CanSubmit)
        {
            StatusMessage = "Hãy kiểm tra các yêu cầu đầu vào được đánh dấu trước khi gửi.";
            return;
        }

        InternalImage? reference = null;
        if (SelectedOperation.RequiresReference)
        {
            reference = await _selection.LoadSelectedImageAsync(cancellationToken);
            if (reference is null)
            {
                StatusMessage = "Không thể tải ảnh tham chiếu đã chọn.";
                return;
            }
        }

        if (SelectedOperation.Operation is AiStudioOperation.Inpaint or AiStudioOperation.RemoveObject
                or AiStudioOperation.ReplaceObject
            && (mask is null || reference is null || mask.Width != reference.Width || mask.Height != reference.Height))
        {
            StatusMessage = "Hãy khởi tạo Mask theo ảnh nguồn trước khi gửi tác vụ này.";
            return;
        }

        AiPrompt? prompt = SelectedOperation.Operation is AiStudioOperation.Upscale
                or AiStudioOperation.RemoveObject
            || string.IsNullOrWhiteSpace(Prompt)
            ? null
            : new AiPrompt(Prompt);
        AiPrompt? negative = string.IsNullOrWhiteSpace(NegativePrompt) ? null : new AiPrompt(NegativePrompt);
        var preferences = new AiRequestPreferences(negative, SelectedModel.Value, SelectedQuality.Value);
        if (!preferences.IsValid)
        {
            StatusMessage = "Mô hình hoặc mức chất lượng không hợp lệ.";
            return;
        }

        AiImageResult? aiResult = null;
        IsBusy = true;
        ProgressPercentage = 0;
        StatusMessage = "Đang gửi yêu cầu đến dịch vụ AI…";
        var progress = new Progress<AiOperationProgress>(value =>
        {
            ProgressPercentage = Math.Clamp(value.Percentage, 0, 100);
            StatusMessage = value.Phase switch
            {
                AiOperationPhase.Validating => "Đang kiểm tra yêu cầu…",
                AiOperationPhase.Submitting => "Đang gửi yêu cầu…",
                AiOperationPhase.Processing => "AI đang xử lý…",
                AiOperationPhase.Receiving => "Đang nhận bản xem trước…",
                _ => StatusMessage,
            };
        });
        var enqueue = await _taskManager.EnqueueAsync(new BackgroundTaskRequest(
            BackgroundTaskKind.Ai,
            async (_, token) =>
            {
                if (SelectedOperation.Operation is AiStudioOperation.Inpaint or AiStudioOperation.Outpaint
                    or AiStudioOperation.RemoveObject or AiStudioOperation.ReplaceObject or AiStudioOperation.Upscale)
                {
                    var execution = await _studioService.ExecuteAsync(new(
                        SelectedOperation.Operation,
                        reference!,
                        mask,
                        prompt,
                        SelectedOperation.Operation is AiStudioOperation.Outpaint or AiStudioOperation.Upscale
                            ? TryCreateExplicitTargetSize()
                            : null,
                        SelectedModel.Value,
                        $"desktop-{Guid.NewGuid():N}"), progress, token).ConfigureAwait(false);
                    aiResult = execution.Succeeded && execution.Preview is not null
                        ? AiImageResult.Success(execution.Preview)
                        : execution.Cancelled ? AiImageResult.CancelledResult()
                        : AiImageResult.Failure(AiServiceFailureReason.Failed, execution.DiagnosticCode);
                }
                else
                {
                    aiResult = await ExecuteAsync(reference, prompt, preferences, progress, token);
                }
                return aiResult.Succeeded
                    ? BackgroundTaskExecutionResult.Success()
                    : BackgroundTaskExecutionResult.Failure(aiResult.DiagnosticCode);
            }), cancellationToken);
        if (!enqueue.Succeeded)
        {
            IsBusy = false;
            StatusMessage = "Không thể đưa tác vụ AI vào hàng đợi. Vui lòng thử lại.";
            return;
        }

        _activeTaskId = enqueue.TaskId;
        OnPropertyChanged(nameof(CanCancel));
        BackgroundTaskSnapshot? completion;
        try
        {
            completion = await _taskManager.WaitForCompletionAsync(enqueue.TaskId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _taskManager.TryCancel(enqueue.TaskId);
            StatusMessage = "Đã hủy yêu cầu AI. Nội dung dự án không bị thay đổi.";
            return;
        }
        finally
        {
            _activeTaskId = default;
            IsBusy = false;
            OnPropertyChanged(nameof(CanCancel));
        }
        if (completion?.State == BackgroundTaskState.Cancelled || aiResult?.Cancelled == true)
        {
            StatusMessage = "Đã hủy yêu cầu AI. Nội dung dự án không bị thay đổi.";
        }
        else if (aiResult?.Succeeded == true && aiResult.Image is not null)
        {
            PreviewImage = aiResult.Image;
            OnPropertyChanged(nameof(HasPreview));
            ProgressPercentage = 100;
            StatusMessage = "Bản xem trước đã sẵn sàng. Dự án chưa bị thay đổi.";
        }
        else
        {
            StatusMessage = aiResult?.FailureReason == AiServiceFailureReason.Unavailable
                ? "Dịch vụ AI đang ngoại tuyến. Bạn vẫn có thể chỉnh sửa trên máy."
                : "Không thể xử lý yêu cầu AI. Nội dung dự án không bị thay đổi.";
        }
        await RefreshHistoryAsync(cancellationToken);
    }

    public async Task<bool> ApprovePreviewAsync(CancellationToken cancellationToken = default)
    {
        if (!CanApprovePreview || PreviewImage is null || _applyService is null
            || _projectSession?.Project is not { } project || _projectSession.Workspace is not { } workspace
            || _selection.SelectedTexture is not { } selected)
            return false;
        ModRelativePath path;
        try { path = new(selected.RelativePath); }
        catch (ArgumentException)
        {
            StatusMessage = "Đường dẫn Texture đã chọn không hợp lệ.";
            return false;
        }
        TextureApplyResult? result = null;
        IsBusy = true;
        StatusMessage = "Đang kiểm tra DDS và áp dụng kết quả AI đã chọn…";
        try
        {
            var enqueue = await _taskManager.EnqueueAsync(new(BackgroundTaskKind.Convert,
                async (taskProgress, token) =>
                {
                    result = await _applyService.ApplyAsync(new(project, workspace, path,
                        new(PreviewImage, selected.Width, selected.Height,
                            new(ImageResizeMode.Stretch))),
                        new CallbackProgress<TextureApplyProgress>(progress => taskProgress.Report(new(
                            progress.CompletedSteps, progress.TotalSteps, progress.Phase.ToString()))), token)
                        .ConfigureAwait(false);
                    return result.Succeeded ? BackgroundTaskExecutionResult.Success()
                        : BackgroundTaskExecutionResult.Failure(result.DiagnosticCode ?? "AI_APPLY_FAILED");
                }), cancellationToken);
            if (!enqueue.Succeeded) return false;
            _activeTaskId = enqueue.TaskId;
            var completion = await _taskManager.WaitForCompletionAsync(enqueue.TaskId, cancellationToken);
            if (completion?.State != BackgroundTaskState.Succeeded || result?.Succeeded != true
                || result.Project is null || !ReferenceEquals(_projectSession.Project, project)
                || !ReferenceEquals(_projectSession.Workspace, workspace))
            {
                StatusMessage = result?.Cancelled == true
                ? "Đã hủy áp dụng kết quả AI và khôi phục thay đổi."
                : "Kết quả AI không đạt kiểm tra nên thay đổi đã được khôi phục.";
                return false;
            }
            await _projectSession.ActivateAsync(result.Project, workspace);
            await _selection.RefreshAfterApplyAsync(path, cancellationToken);
            StatusMessage = "Đã áp dụng kết quả AI an toàn; lịch sử dự án và kiểm tra DDS đã được cập nhật.";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Đã hủy áp dụng kết quả AI.";
            return false;
        }
        finally
        {
            _activeTaskId = default;
            IsBusy = false;
            OnPropertyChanged(nameof(CanApprovePreview));
        }
    }

    public bool CancelCurrent()
    {
        var cancelled = _activeTaskId.IsValid && _taskManager.TryCancel(_activeTaskId);
        if (cancelled) StatusMessage = "Đang hủy yêu cầu AI…";
        return cancelled;
    }

    public async Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) return;
        var result = await _studioService.CancelJobAsync(jobId, cancellationToken);
        StatusMessage = result.Succeeded
            ? "Đã gửi yêu cầu hủy đến dịch vụ AI."
            : "Không thể hủy tác vụ trên dịch vụ AI.";
        await RefreshHistoryAsync(cancellationToken);
    }

    private Task<AiImageResult> ExecuteAsync(
        InternalImage? reference,
        AiPrompt? prompt,
        AiRequestPreferences preferences,
        IProgress<AiOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var size = new AiTargetSize(SelectedAspect.Width, SelectedAspect.Height);
        return SelectedOperation.Operation switch
        {
            AiStudioOperation.Generate => _aiService.GenerateAsync(
                new(prompt!.Value, size, preferences), progress, cancellationToken),
            AiStudioOperation.Edit => _aiService.EditAsync(
                new(reference!, prompt!.Value, preferences), progress, cancellationToken),
            AiStudioOperation.Outpaint => _aiService.OutpaintAsync(
                new(reference!, prompt!.Value, size, preferences), progress, cancellationToken),
            AiStudioOperation.Upscale => _aiService.UpscaleAsync(
                new(reference!, size, preferences), progress, cancellationToken),
            _ => Task.FromResult(AiImageResult.Failure(
                AiServiceFailureReason.InvalidRequest, "AI_MASK_REQUIRED")),
        };
    }

    private void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(PromptValidationMessage));
        OnPropertyChanged(nameof(NegativePromptValidationMessage));
        OnPropertyChanged(nameof(OutputValidationMessage));
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanApprovePreview));
    }

    private AiTargetSize? TryCreateExplicitTargetSize()
    {
        if (!int.TryParse(OutputWidth, out var width) || !int.TryParse(OutputHeight, out var height)
            || width is <= 0 or > AiTargetSize.MaximumDimension
            || height is <= 0 or > AiTargetSize.MaximumDimension
            || (long)width * height > 100_000_000) return null;
        var selected = _selection.SelectedTexture;
        if (selected is null || width < selected.Width || height < selected.Height
            || width == selected.Width && height == selected.Height) return null;
        return new(width, height);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
