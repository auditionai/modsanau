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
    private readonly IImageImportService? _imageImportService;
    private readonly IUserActivityService? _activity;
    private AiStudioOperationOption _selectedOperation = AiStudioOptions.Operations[0];
    private IReadOnlyList<AiStudioOption> _models = AiStudioOptions.Models;
    private IReadOnlyList<AiStudioOption> _qualities = AiStudioOptions.Qualities;
    private IReadOnlyList<AiStudioAspectOption> _aspects = AiStudioOptions.Aspects;
    private IReadOnlyList<AiStudioOption> _resolutions = [];
    private AiStudioOption _selectedResolution = new("", "");
    private readonly Dictionary<string, AiStudioModelOption> _modelCatalog = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AiModelSettingViewModel> _modelSettings = [];
    private AiStudioOption _selectedModel = AiStudioOptions.Models[0];
    private AiStudioOption _selectedQuality = AiStudioOptions.Qualities[0];
    private AiStudioAspectOption _selectedAspect = AiStudioOptions.Aspects[0];
    private AiCreationModeOption _selectedCreationMode = AiCreationModes.Supported[0];
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
    private string _elapsedText = "00:00";
    private DateTimeOffset _operationStarted;
    private BackgroundTaskId _activeTaskId;
    private CancellationTokenSource? _activationCancellation;
    private IReadOnlyList<InternalImage> _additionalReferences = [];
    private string _theme = string.Empty;
    private string _visualStyle = string.Empty;
    private string _composition = string.Empty;
    private string _selectedPromptIdea = string.Empty;

    private static readonly IReadOnlyList<string> PromptIdeas =
    [
        "Neon cyberpunk, ánh sáng xanh tím, vật liệu bóng, chi tiết sắc nét",
        "Fantasy cổ điển, ánh sáng vàng ấm, hoa văn thủ công, chất liệu giàu chiều sâu",
        "Khoa học viễn tưởng, kim loại hiện đại, ánh sáng lạnh, bề mặt có phản xạ",
        "Tối giản cao cấp, nền sạch, hình khối rõ ràng, màu sắc tinh tế",
        "Phong cách anime, đường nét gọn, màu sắc tươi, ánh sáng mềm",
        "Hoạt hình 3D, hình khối đáng yêu, chất liệu mềm, màu pastel",
        "Steampunk, đồng thau, bánh răng, vết xước nhẹ, ánh sáng điện ảnh",
        "Horror u tối, sương mù, tương phản mạnh, bề mặt cũ kỹ",
        "Retro arcade, màu neon, pixel art hiện đại, tương phản cao",
        "Thiên nhiên hữu cơ, gỗ và đá, ánh sáng ban ngày, chi tiết chân thực",
        "Vũ trụ, bụi sao, màu xanh sâu, điểm sáng lấp lánh",
        "Đô thị hiện đại, bê tông và kính, ánh sáng hoàng hôn, bố cục cân đối",
        "Dệt may thủ công, sợi vải rõ nét, hoa văn tinh xảo, màu ấm",
        "Đá cẩm thạch sang trọng, đường vân tự nhiên, ánh sáng studio",
        "Băng tuyết, tinh thể trong suốt, ánh sáng xanh lạnh, chi tiết sắc nét",
        "Lửa và dung nham, màu đỏ cam, nhiệt phát sáng, bề mặt nứt",
        "Quân sự thực dụng, sơn sần, dấu hiệu sử dụng, màu olive",
        "Biển sâu, san hô, ánh sáng xanh, chất liệu ướt và trong",
        "Kiến trúc cổ điển, hoa văn đối xứng, đá chạm khắc, ánh sáng tự nhiên",
        "Phong cách game cao cấp, vật liệu PBR, ánh sáng điện ảnh, độ chi tiết cao",
    ];
    private static readonly IReadOnlyList<string> ThemeIdeas = ["Neon", "Fantasy", "Khoa học viễn tưởng", "Tối giản", "Anime", "Hoạt hình 3D", "Steampunk", "Horror", "Retro", "Thiên nhiên", "Vũ trụ", "Đô thị", "Dệt may", "Cẩm thạch", "Băng tuyết", "Dung nham", "Quân sự", "Biển sâu", "Cổ điển", "Game cao cấp"];
    private static readonly IReadOnlyList<string> StyleIdeas = ["Cyberpunk", "Điện ảnh", "Anime", "Minh họa", "3D PBR", "Pixel art", "Low poly", "Tả thực", "Sơn dầu", "Màu nước", "Baroque", "Art deco", "Vaporwave", "Dark fantasy", "Sci-fi", "Retro", "Tối giản", "Thủ công", "Hoạt hình", "Concept art"];
    private static readonly IReadOnlyList<string> CompositionIdeas = ["Cân đối trung tâm", "Toàn cảnh", "Cận cảnh", "Góc thấp", "Góc cao", "Đối xứng", "Đường dẫn thị giác", "Tiền cảnh rõ", "Hậu cảnh mờ", "Chéo năng động", "Khung trong khung", "Quy tắc một phần ba", "Hình học", "Nhiều lớp chiều sâu", "Không gian âm", "Nhân vật chính giữa", "Nhóm đối tượng", "Vật thể nổi bật", "Bố cục dọc", "Bố cục ngang"];

    public AiStudioViewModel(
        IAiService aiService,
        IAiStudioService studioService,
        IBackgroundTaskManager taskManager,
        IWorkspaceTextureSelection selection,
        ITextureApplyService? applyService = null,
        IApplicationProjectSession? projectSession = null,
        ILocalPromptPresetStore? localPresets = null,
        ICloudPromptPresetService? cloudPresets = null,
        ICapabilityAuthorizationService? capabilities = null,
        IImageImportService? imageImportService = null,
        IUserActivityService? activity = null)
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
        _imageImportService = imageImportService;
        _activity = activity;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<AiStudioOperationOption> Operations => AiStudioOptions.Operations;
    public IReadOnlyList<AiStudioOption> Models { get => _models; private set => Set(ref _models, value); }
    public IReadOnlyList<AiStudioOption> Qualities { get => _qualities; private set => Set(ref _qualities, value); }
    public IReadOnlyList<AiStudioAspectOption> Aspects { get => _aspects; private set => Set(ref _aspects, value); }
    public IReadOnlyList<AiStudioOption> Resolutions { get => _resolutions; private set => Set(ref _resolutions, value); }
    public bool HasQualitySettings => Qualities.Count > 0;
    public bool HasResolutionSettings => Resolutions.Count > 0;
    public IReadOnlyList<AiModelSettingViewModel> ModelSettings { get => _modelSettings; private set => Set(ref _modelSettings, value); }
    public IReadOnlyList<AiCreationModeOption> CreationModes => AiCreationModes.Supported;

    public AiCreationModeOption SelectedCreationMode
    {
        get => _selectedCreationMode;
        set
        {
            if (value is null || !CreationModes.Contains(value) || !Set(ref _selectedCreationMode, value)) return;
            SelectedOperation = Operations.First(item => item.Operation ==
                (value.Mode == AiCreationMode.BasedOnSelectedDds ? AiStudioOperation.Edit : AiStudioOperation.Generate));
            OnPropertyChanged(nameof(ComposedPrompt));
        }
    }
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
        set
        {
            if (value is null || !Models.Contains(value) || !Set(ref _selectedModel, value)) return;
            OnPropertyChanged(nameof(ModelDescription));
            OnPropertyChanged(nameof(ModelPriceText));
            ApplyModelSettings(value.Value);
            NotifyValidationChanged();
            _ = RefreshQuoteAsync();
        }
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
        set { if (Set(ref _prompt, value ?? string.Empty)) { OnPropertyChanged(nameof(ComposedPrompt)); NotifyValidationChanged(); } }
    }

    public string Theme { get => _theme; set { if (Set(ref _theme, value ?? string.Empty)) OnPropertyChanged(nameof(ComposedPrompt)); } }
    public string VisualStyle { get => _visualStyle; set { if (Set(ref _visualStyle, value ?? string.Empty)) OnPropertyChanged(nameof(ComposedPrompt)); } }
    public string Composition { get => _composition; set { if (Set(ref _composition, value ?? string.Empty)) OnPropertyChanged(nameof(ComposedPrompt)); } }
    public int AdditionalReferenceCount => _additionalReferences.Count;
    public bool HasAdditionalReferences => AdditionalReferenceCount > 0;
    public string AdditionalReferencesMessage => AdditionalReferenceCount == 0
        ? "Ch\u01B0a c\u00F3 \u1EA3nh tham chi\u1EBFu b\u1ED5 sung."
        : $"\u0110\u00E3 ch\u1ECDn {AdditionalReferenceCount} \u1EA3nh tham chi\u1EBFu b\u1ED5 sung.";
    public string ComposedPrompt => BuildComposedPrompt();
    public IReadOnlyList<string> PromptIdeasList => PromptIdeas;
    public IReadOnlyList<string> ThemeIdeasList => ThemeIdeas;
    public IReadOnlyList<string> StyleIdeasList => StyleIdeas;
    public IReadOnlyList<string> CompositionIdeasList => CompositionIdeas;
    public string SelectedPromptIdea
    {
        get => _selectedPromptIdea;
        set { if (Set(ref _selectedPromptIdea, value ?? string.Empty) && !string.IsNullOrWhiteSpace(value)) Prompt = value; }
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
    public string ModelDescription => SelectedModel.Description;
    public string ModelPriceText => SelectedModel.PriceText;
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
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
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
                RefreshModelsAsync(_activationCancellation.Token),
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
        var settings = ModelSettings.ToDictionary(item => item.Key, item => item.Selected.Value,
            StringComparer.OrdinalIgnoreCase);
        var result = await _studioService.GetQuoteAsync(SelectedOperation.Operation, SelectedModel.Value, settings, cancellationToken);
        QuoteText = result.Succeeded && result.Quote is { CreditCost: > 0 } quote
            ? $"Ước tính {quote.CreditCost:N0} Credits"
            : "Giá sẽ xác nhận theo model và setting khi tạo ảnh";
    }

    public AiStudioOption SelectedResolution
    {
        get => _selectedResolution;
        set { if (value is not null && Resolutions.Contains(value) && Set(ref _selectedResolution, value)) NotifyValidationChanged(); }
    }

    private async Task RefreshModelsAsync(CancellationToken cancellationToken)
    {
        var result = await _studioService.GetModelsAsync(cancellationToken);
        if (!result.Succeeded || result.Models.Count == 0) return;

        var models = result.Models.Select(model => new AiStudioOption(
            model.Id,
            model.Name,
            BuildModelDescription(model),
            BuildModelPriceText(model))).ToArray();
        _modelCatalog.Clear();
        foreach (var model in result.Models) _modelCatalog[model.Id] = model;
        Models = models;
        SelectedModel = models.FirstOrDefault(model => model.Value == _selectedModel.Value) ?? models[0];
        ApplyModelSettings(SelectedModel.Value);
        OnPropertyChanged(nameof(ModelDescription));
        OnPropertyChanged(nameof(ModelPriceText));
    }

    private void ApplyModelSettings(string modelId)
    {
        if (!_modelCatalog.TryGetValue(modelId, out var model)) return;
        ModelSettings = model.Settings.Select(item => new AiModelSettingViewModel(
            item.Key, GetSettingLabel(item.Key), item.Value.Select(value => new AiStudioOption(value, value)).ToArray(),
            () => _ = RefreshQuoteAsync())).ToArray();
        var qualities = model.Qualities.Select(value => new AiStudioOption(value, value.ToUpperInvariant())).ToArray();
        Qualities = qualities.Length > 0 ? qualities : AiStudioOptions.Qualities;
        SelectedQuality = Qualities.FirstOrDefault(item => item.Value == SelectedQuality.Value) ?? Qualities[0];
        var resolutions = model.Resolutions.Select(value => new AiStudioOption(value, value.ToUpperInvariant())).ToArray();
        Resolutions = resolutions;
        SelectedResolution = resolutions.FirstOrDefault(item => item.Value == SelectedResolution.Value) ?? (resolutions.Length > 0 ? resolutions[0] : new AiStudioOption("", ""));
        OnPropertyChanged(nameof(HasQualitySettings));
        OnPropertyChanged(nameof(HasResolutionSettings));
    }

    private static string GetSettingLabel(string key) => key.ToLowerInvariant() switch
    {
        "quality" => "Chất lượng",
        "resolution" or "size" => "Độ phân giải",
        "aspect_ratio" => "Tỷ lệ khung hình",
        "speed" or "processing_speed" => "Tốc độ xử lý",
        "server" or "server_id" => "Server model",
        "n" or "count" or "quantity" => "Số lượng",
        _ => key.Replace('_', ' '),
    };

    private static string BuildModelDescription(AiStudioModelOption model)
    {
        var details = new List<string>();
        if (model.Qualities.Count > 0) details.Add($"Chất lượng: {string.Join(", ", model.Qualities)}");
        if (model.AspectRatios.Count > 0) details.Add($"Tỷ lệ: {string.Join(", ", model.AspectRatios)}");
        if (model.Resolutions.Count > 0) details.Add($"Kích thước: {string.Join(", ", model.Resolutions)}");
        return details.Count == 0 ? "Thiết lập được xác nhận bởi dịch vụ AI." : string.Join(" · ", details);
    }

    private static string BuildModelPriceText(AiStudioModelOption model) => model.CreditCosts.Count == 0
        ? "Giá được xác nhận khi tạo ảnh"
        : model.CreditCosts.Count == 1
            ? $"Từ {model.CreditCosts[0]:N0} Credits / ảnh"
            : $"Từ {model.CreditCosts.Min():N0} đến {model.CreditCosts.Max():N0} Credits / ảnh";

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

    public async Task AddReferenceImagesAsync(IEnumerable<string> sourcePaths, CancellationToken cancellationToken = default)
    {
        if (_imageImportService is null) { StatusMessage = "Khong the tai anh tham chieu tren thiet bi nay."; return; }
        var images = _additionalReferences.ToList();
        var sourceSlots = SelectedCreationMode.Mode == AiCreationMode.BasedOnSelectedDds ? 1 : 0;
        foreach (var path in sourcePaths.Take(Math.Max(0, 5 - sourceSlots - images.Count)))
        {
            var imported = await _imageImportService.ImportAsync(new(path), cancellationToken).ConfigureAwait(false);
            if (imported.Succeeded && imported.Image is not null) images.Add(imported.Image);
        }
        _additionalReferences = images;
        OnPropertyChanged(nameof(AdditionalReferenceCount));
        OnPropertyChanged(nameof(HasAdditionalReferences));
        OnPropertyChanged(nameof(AdditionalReferencesMessage));
        StatusMessage = images.Count == 0 ? "Khong the doc anh tham chieu da chon." : "Da them anh tham chieu vao yeu cau AI.";
    }

    public void ClearReferenceImages()
    {
        _additionalReferences = [];
        OnPropertyChanged(nameof(AdditionalReferenceCount));
        OnPropertyChanged(nameof(HasAdditionalReferences));
        OnPropertyChanged(nameof(AdditionalReferencesMessage));
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

        var composedPrompt = BuildComposedPrompt();
        AiPrompt? prompt = SelectedOperation.Operation is AiStudioOperation.Upscale
                or AiStudioOperation.RemoveObject
            || string.IsNullOrWhiteSpace(composedPrompt)
            ? null
            : new AiPrompt(composedPrompt);
        AiPrompt? negative = string.IsNullOrWhiteSpace(NegativePrompt) ? null : new AiPrompt(NegativePrompt);
        var preferences = new AiRequestPreferences(negative, SelectedModel.Value, SelectedQuality.Value);
        if (!preferences.IsValid)
        {
            StatusMessage = "Mô hình hoặc mức chất lượng không hợp lệ.";
            return;
        }

        AiImageResult? aiResult = null;
        var operationId = Guid.NewGuid().ToString("N");
        var operationStarted = DateTimeOffset.UtcNow;
        var operationLabel = SelectedOperation.Label;
        _activity?.Publish("INFO", $"Bắt đầu {operationLabel.ToLowerInvariant()}.", "AI / hình ảnh", "Chuẩn bị yêu cầu", 0, "Running", operationId);
        IsBusy = true;
        ProgressPercentage = 0;
        _operationStarted = DateTimeOffset.UtcNow;
        ElapsedText = "00:00";
        StatusMessage = "Đang gửi yêu cầu đến dịch vụ AI…";
        var progress = new Progress<AiOperationProgress>(value =>
        {
            ProgressPercentage = Math.Clamp(value.Percentage, 0, 100);
            ElapsedText = FormatElapsed(DateTimeOffset.UtcNow - _operationStarted);
            StatusMessage = value.Phase switch
            {
                AiOperationPhase.Validating => "Đang kiểm tra yêu cầu…",
                AiOperationPhase.Submitting => "Đang gửi yêu cầu…",
                AiOperationPhase.Processing => "AI đang xử lý…",
                AiOperationPhase.Receiving => "Đang nhận bản xem trước…",
                _ => StatusMessage,
            };
            _activity?.Publish("INFO", StatusMessage, "AI / hình ảnh", value.Phase.ToString(), ProgressPercentage,
                "Running", operationId, DateTimeOffset.UtcNow - operationStarted,
                value.Phase == AiOperationPhase.Processing && value.Percentage <= 0);
        });
        var enqueue = await _taskManager.EnqueueAsync(new BackgroundTaskRequest(
            BackgroundTaskKind.Ai,
            async (_, token) =>
            {
                if ((_studioService.SupportsGenerationWithReferences
                        && SelectedOperation.Operation is AiStudioOperation.Generate or AiStudioOperation.Edit)
                    || SelectedOperation.Operation is AiStudioOperation.Inpaint or AiStudioOperation.Outpaint
                    or AiStudioOperation.RemoveObject or AiStudioOperation.ReplaceObject or AiStudioOperation.Upscale)
                {
                    var source = reference ?? CreateBlankSource();
                    var execution = await _studioService.ExecuteAsync(new(
                        SelectedOperation.Operation,
                        source,
                        mask,
                        prompt,
                        SelectedOperation.Operation is AiStudioOperation.Outpaint or AiStudioOperation.Upscale
                            ? TryCreateExplicitTargetSize()
                            : null,
                        SelectedModel.Value,
                        $"desktop-{Guid.NewGuid():N}",
                        _additionalReferences,
                        ModelSettings.ToDictionary(item => item.Key, item => item.Selected.Value,
                            StringComparer.OrdinalIgnoreCase)), progress, token).ConfigureAwait(false);
                    aiResult = execution.Succeeded && execution.Preview is not null
                        ? AiImageResult.Success(execution.Preview)
                        : execution.Cancelled ? AiImageResult.CancelledResult()
                        : AiImageResult.Failure(AiServiceFailureReason.Failed, execution.DiagnosticCode);
                }
                else
                {
                    aiResult = await ExecuteAsync(reference, prompt, preferences, progress, token);
                }
                return aiResult is { Succeeded: true }
                    ? BackgroundTaskExecutionResult.Success()
                    : BackgroundTaskExecutionResult.Failure(aiResult?.DiagnosticCode ?? "AI_OPERATION_FAILED");
            }), cancellationToken);
        if (!enqueue.Succeeded)
        {
            _activity?.Publish("ERROR", "Không thể đưa tác vụ hình ảnh vào hàng đợi.", "AI / hình ảnh", "Xếp hàng", 0, "Error", operationId, DateTimeOffset.UtcNow - operationStarted);
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
            _activity?.Publish("WARNING", "Đã hủy tác vụ hình ảnh theo yêu cầu.", "AI / hình ảnh", "Đã hủy", ProgressPercentage, "Cancelled", operationId, DateTimeOffset.UtcNow - operationStarted);
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
            _activity?.Publish("WARNING", "Tác vụ hình ảnh đã hủy; dự án chưa bị thay đổi.", "AI / hình ảnh", "Đã hủy", ProgressPercentage, "Cancelled", operationId, DateTimeOffset.UtcNow - operationStarted);
            StatusMessage = "Đã hủy yêu cầu AI. Nội dung dự án không bị thay đổi.";
        }
        else if (aiResult?.Succeeded == true && aiResult.Image is not null)
        {
            _activity?.Publish("SUCCESS", $"Đã hoàn tất {operationLabel.ToLowerInvariant()} và tạo bản xem trước.", "AI / hình ảnh", "Hoàn tất", 100, "Success", operationId, DateTimeOffset.UtcNow - operationStarted);
            PreviewImage = aiResult.Image;
            OnPropertyChanged(nameof(HasPreview));
            ProgressPercentage = 100;
            ElapsedText = FormatElapsed(DateTimeOffset.UtcNow - _operationStarted);
            StatusMessage = "Bản xem trước đã sẵn sàng. Dự án chưa bị thay đổi.";
        }
        else
        {
            _activity?.Publish("ERROR", "Tác vụ hình ảnh không hoàn tất; dự án chưa bị thay đổi.", "AI / hình ảnh", "Kết thúc", ProgressPercentage, "Error", operationId, DateTimeOffset.UtcNow - operationStarted);
            StatusMessage = aiResult?.FailureReason == AiServiceFailureReason.Unavailable
                ? "Dịch vụ AI đang ngoại tuyến. Hãy thử tạo lại khi dịch vụ hoạt động."
                : "Tạo ảnh thất bại. Hãy thử tạo lại ảnh; hệ thống không tự động tạo lại.";
        }
        await RefreshHistoryAsync(cancellationToken);
    }

    private static string FormatElapsed(TimeSpan elapsed) => $"{Math.Max(0, (int)elapsed.TotalMinutes):00}:{elapsed.Seconds:00}";

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

    private string BuildComposedPrompt()
    {
        var parts = new List<string>
        {
            "Create a production-ready game texture image.",
            SelectedCreationMode.Mode == AiCreationMode.BasedOnSelectedDds
                ? "Use the supplied DDS texture as the visual base; preserve its readable layout and only apply the requested creative changes."
                : "Create an original image from the requested creative direction."
        };
        if (!string.IsNullOrWhiteSpace(Theme)) parts.Add($"Theme: {Theme.Trim()}.");
        if (!string.IsNullOrWhiteSpace(VisualStyle)) parts.Add($"Design style: {VisualStyle.Trim()}.");
        if (!string.IsNullOrWhiteSpace(Composition)) parts.Add($"Composition: {Composition.Trim()}.");
        if (!string.IsNullOrWhiteSpace(Prompt)) parts.Add($"User requirements: {Prompt.Trim()}.");
        if (_additionalReferences.Count > 0) parts.Add("Use the uploaded reference images only for their requested visual details.");
        return string.Join(" ", parts);
    }

    private static InternalImage CreateBlankSource() => new(
        1, 1, 4, [0, 0, 0, 0],
        new ImageSourceMetadata(ImageSourceFormat.Png, 1, 1, ImageSourceOrientation.Normal, true, false));

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

public enum AiCreationMode
{
    NewImage,
    BasedOnSelectedDds
}

public sealed record AiCreationModeOption(AiCreationMode Mode, string Label, string Description);

public static class AiCreationModes
{
    public static IReadOnlyList<AiCreationModeOption> Supported { get; } =
    [
        new(AiCreationMode.NewImage, "T\u1EA1o m\u1EDBi", "T\u1EA1o \u1EA3nh m\u1EDBi t\u1EEB \u00FD t\u01B0\u1EDFng v\u00E0 \u1EA3nh tham chi\u1EBFu t\u00F9y ch\u1ECDn."),
        new(AiCreationMode.BasedOnSelectedDds, "T\u1EEB DDS \u0111ang ch\u1ECDn", "D\u00F9ng DDS \u0111ang ch\u1ECDn l\u00E0m \u1EA3nh tham chi\u1EBFu \u0111\u1EC3 t\u1EA1o phi\u00EAn b\u1EA3n m\u1EDBi.")
    ];
}
