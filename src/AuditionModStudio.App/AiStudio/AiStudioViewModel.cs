using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Tasks;

namespace AuditionModStudio.App.AiStudio;

public sealed class AiStudioViewModel : INotifyPropertyChanged
{
    private readonly IAiService _aiService;
    private readonly IAiStudioService _studioService;
    private readonly IBackgroundTaskManager _taskManager;
    private readonly IWorkspaceTextureSelection _selection;
    private AiStudioOperationOption _selectedOperation = AiStudioOptions.Operations[0];
    private AiStudioOption _selectedModel = AiStudioOptions.Models[0];
    private AiStudioOption _selectedQuality = AiStudioOptions.Qualities[0];
    private AiStudioAspectOption _selectedAspect = AiStudioOptions.Aspects[0];
    private string _prompt = string.Empty;
    private string _negativePrompt = string.Empty;
    private string _statusMessage = "AI Studio is ready. Server availability is checked when this page opens.";
    private string _quoteText = "Price unavailable";
    private string _historyMessage = "Loading server job history…";
    private IReadOnlyList<AiStudioJobSummary> _history = [];
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
        IWorkspaceTextureSelection selection)
    {
        _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
        _studioService = studioService ?? throw new ArgumentNullException(nameof(studioService));
        _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<AiStudioOperationOption> Operations => AiStudioOptions.Operations;
    public IReadOnlyList<AiStudioOption> Models => AiStudioOptions.Models;
    public IReadOnlyList<AiStudioOption> Qualities => AiStudioOptions.Qualities;
    public IReadOnlyList<AiStudioAspectOption> Aspects => AiStudioOptions.Aspects;

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

    public string OperationDescription => SelectedOperation.Description;
    public string PromptValidationMessage => Prompt.Length > AiPrompt.MaximumLength
        ? $"Prompt exceeds {AiPrompt.MaximumLength:N0} characters."
        : string.IsNullOrWhiteSpace(Prompt) && SelectedOperation.Operation != AiStudioOperation.Upscale
            ? "Enter a prompt for this operation."
            : string.Empty;
    public string NegativePromptValidationMessage => NegativePrompt.Length > AiPrompt.MaximumLength
        ? $"Negative prompt exceeds {AiPrompt.MaximumLength:N0} characters."
        : string.Empty;
    public string ReferenceMessage => SelectedOperation.RequiresReference
        ? _selection.SelectedTexture is null
            ? "Select a project texture in Projects before submitting."
            : $"Reference: {_selection.SelectedTexture.DisplayLabel}"
        : "No reference image is required.";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string QuoteText { get => _quoteText; private set => Set(ref _quoteText, value); }
    public string HistoryMessage { get => _historyMessage; private set => Set(ref _historyMessage, value); }
    public IReadOnlyList<AiStudioJobSummary> History { get => _history; private set => Set(ref _history, value); }
    public InternalImage? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }
    public bool HasPreview => PreviewImage is not null;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) NotifyValidationChanged(); } }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public int ProgressPercentage { get => _progressPercentage; private set => Set(ref _progressPercentage, value); }
    public bool CanCancel => IsBusy && _activeTaskId.IsValid;
    public bool CanSubmit => !IsBusy
        && Prompt.Length <= AiPrompt.MaximumLength
        && NegativePrompt.Length <= AiPrompt.MaximumLength
        && (SelectedOperation.Operation == AiStudioOperation.Upscale || !string.IsNullOrWhiteSpace(Prompt))
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
                RefreshHistoryAsync(_activationCancellation.Token));
        }
        finally
        {
            IsLoading = false;
        }
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
            ? $"Estimated {quote.CreditCost:N0} credits · {quote.PricingVersion}"
            : "Price unavailable · server offline";
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        var result = await _studioService.GetHistoryAsync(cancellationToken);
        History = result.Succeeded ? result.Jobs.Take(100).ToArray() : [];
        HistoryMessage = !result.Succeeded
            ? "History unavailable while the trusted server is offline."
            : History.Count == 0
                ? "No AI jobs yet. Submitted jobs will appear here."
                : $"{History.Count:N0} recent server jobs";
    }

    public async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSubmit)
        {
            StatusMessage = "Review the highlighted input requirements before submitting.";
            return;
        }

        InternalImage? reference = null;
        if (SelectedOperation.RequiresReference)
        {
            reference = await _selection.LoadSelectedImageAsync(cancellationToken);
            if (reference is null)
            {
                StatusMessage = "The selected reference image could not be loaded.";
                return;
            }
        }

        AiPrompt? prompt = string.IsNullOrWhiteSpace(Prompt) ? null : new AiPrompt(Prompt);
        AiPrompt? negative = string.IsNullOrWhiteSpace(NegativePrompt) ? null : new AiPrompt(NegativePrompt);
        var preferences = new AiRequestPreferences(negative, SelectedModel.Value, SelectedQuality.Value);
        if (!preferences.IsValid)
        {
            StatusMessage = "Model or quality preference is invalid.";
            return;
        }

        AiImageResult? aiResult = null;
        IsBusy = true;
        ProgressPercentage = 0;
        StatusMessage = "Submitting to the trusted AI backend…";
        var progress = new Progress<AiOperationProgress>(value =>
        {
            ProgressPercentage = Math.Clamp(value.Percentage, 0, 100);
            StatusMessage = value.Phase switch
            {
                AiOperationPhase.Validating => "Validating request…",
                AiOperationPhase.Submitting => "Submitting request…",
                AiOperationPhase.Processing => "AI job is processing…",
                AiOperationPhase.Receiving => "Receiving preview…",
                _ => StatusMessage,
            };
        });
        var enqueue = await _taskManager.EnqueueAsync(new BackgroundTaskRequest(
            BackgroundTaskKind.Ai,
            async (_, token) =>
            {
                aiResult = await ExecuteAsync(reference, prompt, preferences, progress, token);
                return aiResult.Succeeded
                    ? BackgroundTaskExecutionResult.Success()
                    : BackgroundTaskExecutionResult.Failure(aiResult.DiagnosticCode);
            }), cancellationToken);
        if (!enqueue.Succeeded)
        {
            IsBusy = false;
            StatusMessage = "AI task could not be queued. Try again.";
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
            StatusMessage = "AI request cancelled. No project content was changed.";
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
            StatusMessage = "AI request cancelled. No project content was changed.";
        }
        else if (aiResult?.Succeeded == true && aiResult.Image is not null)
        {
            PreviewImage = aiResult.Image;
            OnPropertyChanged(nameof(HasPreview));
            ProgressPercentage = 100;
            StatusMessage = "Preview ready. Review it here; the project has not been changed.";
        }
        else
        {
            StatusMessage = aiResult?.FailureReason == AiServiceFailureReason.Unavailable
                ? "Trusted AI backend is offline. Local editing remains available."
                : "AI request failed safely. No project content was changed.";
        }
        await RefreshHistoryAsync(cancellationToken);
    }

    public bool CancelCurrent()
    {
        var cancelled = _activeTaskId.IsValid && _taskManager.TryCancel(_activeTaskId);
        if (cancelled) StatusMessage = "Cancelling AI request…";
        return cancelled;
    }

    public async Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) return;
        var result = await _studioService.CancelJobAsync(jobId, cancellationToken);
        StatusMessage = result.Succeeded
            ? "Cancellation requested on the trusted server."
            : "The server job could not be cancelled.";
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
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(CanCancel));
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
}
