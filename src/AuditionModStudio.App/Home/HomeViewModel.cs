using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Shell;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;

namespace AuditionModStudio.App.Home;

public sealed class HomeViewModel : INotifyPropertyChanged
{
    private readonly IModCatalog _modCatalog;
    private readonly IProjectCreationService _projectCreationService;
    private readonly IBackgroundTaskManager _taskManager;
    private readonly IApplicationProjectSession _projectSession;
    private HomeGameOption? _selectedGame;
    private HomeModOption? _selectedMod;
    private ImmutableArray<HomeModOption> _compatibleMods = [];
    private string _projectName = string.Empty;
    private string _statusMessage = "Chọn game để xem các loại Mod tương thích.";
    private double _progressPercentage;
    private BackgroundTaskId _activeTaskId;
    private bool _isCreating;

    public HomeViewModel(
        IGameCatalog gameCatalog,
        IModCatalog modCatalog,
        IProjectCreationService projectCreationService,
        IBackgroundTaskManager taskManager,
        IApplicationProjectSession projectSession)
    {
        ArgumentNullException.ThrowIfNull(gameCatalog);
        _modCatalog = modCatalog ?? throw new ArgumentNullException(nameof(modCatalog));
        _projectCreationService = projectCreationService ?? throw new ArgumentNullException(nameof(projectCreationService));
        _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
        _projectSession = projectSession ?? throw new ArgumentNullException(nameof(projectSession));

        Games = gameCatalog.GetGames()
            .Select(game => new HomeGameOption(game.Id, game.DisplayName))
            .ToImmutableArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<HomeGameOption> Games { get; }

    public ImmutableArray<HomeModOption> CompatibleMods => _compatibleMods;

    public HomeGameOption? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (Equals(_selectedGame, value) || _isCreating)
            {
                return;
            }

            _selectedGame = value;
            _selectedMod = null;
            _compatibleMods = value is null
                ? []
                : _modCatalog.GetMods(value.GameId)
                    .Select(mod => new HomeModOption(
                        mod.Id,
                        mod.DisplayName,
                        mod.Category.Value,
                        mod.Description,
                        mod.CompatibilityInformation))
                    .ToImmutableArray();
            _statusMessage = value is null
            ? "Chọn game để xem các loại Mod tương thích."
                : _compatibleMods.IsEmpty
                ? "Game này chưa có loại Mod tương thích."
                : "Chọn một loại Mod tương thích.";

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMod));
            OnPropertyChanged(nameof(CompatibleMods));
            OnPropertyChanged(nameof(HasCompatibleMods));
            OnPropertyChanged(nameof(CanSelectMod));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(CanCreate));
        }
    }

    public HomeModOption? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (Equals(_selectedMod, value) || _isCreating)
            {
                return;
            }

            _selectedMod = value is not null && _compatibleMods.Contains(value) ? value : null;
            _statusMessage = _selectedMod is null
                ? HasCompatibleMods
            ? "Chọn một loại Mod tương thích."
                    : StatusMessage
                : "Đặt tên dự án rồi chọn Tạo dự án.";
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(CanCreate));
        }
    }

    public string ProjectName
    {
        get => _projectName;
        set
        {
            var normalized = value ?? string.Empty;
            if (_projectName == normalized || _isCreating)
            {
                return;
            }

            _projectName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(ProjectNameValidationMessage));
        }
    }

    public bool HasCompatibleMods => !_compatibleMods.IsEmpty;

    public bool CanSelectGame => !_isCreating;

    public bool CanSelectMod => !_isCreating && HasCompatibleMods;

    public bool CanEditProjectName => !_isCreating;

    public bool IsCreating => _isCreating;

    public bool CanCancel => _isCreating && _activeTaskId.IsValid;

    public bool CanCreate => !_isCreating
                             && _selectedGame is not null
                             && _selectedMod is not null
                             && IsValidProjectName(_projectName);

    public string ProjectNameValidationMessage =>
        string.IsNullOrEmpty(_projectName) || IsValidProjectName(_projectName)
            ? string.Empty
            : $"Tên dự án phải có từ 1–{AuditionProject.MaximumNameLength} ký tự và không chứa ký tự điều khiển.";

    public string StatusMessage => _statusMessage;

    public double ProgressPercentage => _progressPercentage;

    public async Task CreateProjectAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCreate)
        {
            return;
        }

        var request = new ProjectCreationRequest(
            _selectedGame!.GameId,
            _selectedMod!.ModId,
            _projectName);
        ProjectCreationResult? creationResult = null;
        IProgress<ProjectCreationProgress> uiProgress = new Progress<ProjectCreationProgress>(UpdateProgress);

        SetCreatingState(true, "Dự án đang chờ được tạo.", 0);
        var enqueue = await _taskManager.EnqueueAsync(
            new BackgroundTaskRequest(
                BackgroundTaskKind.Extract,
                async (taskProgress, taskCancellationToken) =>
                {
                    var projectProgress = new CallbackProgress<ProjectCreationProgress>(progress =>
                    {
                        taskProgress.Report(new BackgroundTaskProgress(
                            progress.CompletedSteps,
                            progress.TotalSteps,
                            ToStageCode(progress.Phase)));
                        uiProgress.Report(progress);
                    });

                    creationResult = await _projectCreationService.CreateAsync(
                        request,
                        projectProgress,
                        taskCancellationToken).ConfigureAwait(false);

                    if (!creationResult.Succeeded)
                    {
                        return BackgroundTaskExecutionResult.Failure(
                            ToDiagnosticCode(creationResult.FailureReason));
                    }

                    await _projectSession.ActivateAsync(
                        creationResult.Project!,
                        creationResult.Workspace!).ConfigureAwait(false);
                    return BackgroundTaskExecutionResult.Success();
                }),
            cancellationToken);

        if (!enqueue.Succeeded)
        {
            SetCreatingState(false, ToEnqueueMessage(enqueue.FailureReason), 0);
            return;
        }

        _activeTaskId = enqueue.TaskId;
        OnPropertyChanged(nameof(CanCancel));
        var snapshot = await _taskManager.WaitForCompletionAsync(enqueue.TaskId, cancellationToken);
        _activeTaskId = default;

        if (snapshot is null)
        {
            SetCreatingState(false, "Tác vụ tạo dự án không còn khả dụng.", 0);
            return;
        }

        switch (snapshot.State)
        {
            case BackgroundTaskState.Succeeded when creationResult?.Succeeded == true:
                SetCreatingState(false, $"Đã tạo dự án “{creationResult.Project!.Name}”.", 100);
                break;
            case BackgroundTaskState.Cancelled:
                SetCreatingState(false, "Đã hủy tạo dự án.", _progressPercentage);
                break;
            default:
                SetCreatingState(
                    false,
                    ToFailureMessage(creationResult?.FailureReason),
                    _progressPercentage);
                break;
        }
    }

    public bool CancelCreation() =>
        _activeTaskId.IsValid && _taskManager.TryCancel(_activeTaskId);

    private static bool IsValidProjectName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= AuditionProject.MaximumNameLength
        && !value.Any(char.IsControl);

    private static string ToStageCode(ProjectCreationPhase phase) => phase switch
    {
        ProjectCreationPhase.ValidatingSelection => "validating_selection",
        ProjectCreationPhase.CheckingEntitlement => "checking_entitlement",
        ProjectCreationPhase.AcquiringTemplate => "acquiring_template",
        ProjectCreationPhase.CreatingWorkspace => "creating_workspace",
        ProjectCreationPhase.PreparingKeydat => "preparing_keydat",
        ProjectCreationPhase.ExtractingArchive => "extracting_archive",
        ProjectCreationPhase.ScanningTextures => "scanning_textures",
        ProjectCreationPhase.CachingMetadata => "caching_metadata",
        ProjectCreationPhase.SavingProject => "saving_project",
        ProjectCreationPhase.Completed => "completed",
        _ => "working"
    };

    private static string ToDiagnosticCode(ProjectCreationFailureReason reason) => reason switch
    {
        ProjectCreationFailureReason.Cancelled => "project.cancelled",
        ProjectCreationFailureReason.EntitlementDenied => "project.entitlement_denied",
        ProjectCreationFailureReason.EntitlementUnavailable => "project.entitlement_unavailable",
        ProjectCreationFailureReason.UnknownGame => "project.unknown_game",
        ProjectCreationFailureReason.UnknownMod => "project.unknown_mod",
        _ => "project.creation_failed"
    };

    private static string ToFailureMessage(ProjectCreationFailureReason? reason) => reason switch
    {
        ProjectCreationFailureReason.EntitlementDenied => "Tài khoản này không có quyền dùng mẫu đã chọn.",
        ProjectCreationFailureReason.EntitlementUnavailable => "Chưa thể xác minh quyền dùng mẫu. Vui lòng thử lại sau.",
        ProjectCreationFailureReason.UnknownGame => "Game đã chọn không còn khả dụng.",
        ProjectCreationFailureReason.UnknownMod => "Loại Mod đã chọn không còn khả dụng.",
        ProjectCreationFailureReason.Cancelled => "Đã hủy tạo dự án.",
        _ => "Không thể tạo dự án. Hãy xem nhật ký ứng dụng rồi thử lại."
    };

    private static string ToEnqueueMessage(BackgroundTaskEnqueueFailureReason reason) => reason switch
    {
        BackgroundTaskEnqueueFailureReason.QueueFull => "Hàng đợi đang đầy. Hãy thử lại khi một tác vụ khác hoàn tất.",
        BackgroundTaskEnqueueFailureReason.ShuttingDown => "Ứng dụng đang đóng nên không thể tạo dự án.",
        BackgroundTaskEnqueueFailureReason.Cancelled => "Đã hủy tạo dự án trước khi bắt đầu.",
        _ => "Không thể đưa tác vụ tạo dự án vào hàng đợi."
    };

    private void UpdateProgress(ProjectCreationProgress progress)
    {
        _progressPercentage = progress.TotalSteps <= 0
            ? 0
            : Math.Clamp((double)progress.CompletedSteps / progress.TotalSteps * 100, 0, 100);
        _statusMessage = progress.Phase switch
        {
            ProjectCreationPhase.ValidatingSelection => "Đang kiểm tra game và loại Mod.",
            ProjectCreationPhase.CheckingEntitlement => "Đang kiểm tra quyền dùng mẫu.",
            ProjectCreationPhase.AcquiringTemplate => "Đang tải mẫu tin cậy.",
            ProjectCreationPhase.CreatingWorkspace => "Đang tạo không gian dự án an toàn.",
            ProjectCreationPhase.PreparingKeydat => "Đang chuẩn bị dữ liệu đi kèm tệp nguồn.",
            ProjectCreationPhase.ExtractingArchive => "Đang giải nén tệp nguồn làm việc.",
            ProjectCreationPhase.ScanningTextures => "Đang quét Texture.",
            ProjectCreationPhase.CachingMetadata => "Đang lưu thông tin Texture.",
            ProjectCreationPhase.SavingProject => "Đang lưu dự án.",
            ProjectCreationPhase.Completed => "Đang hoàn tất tạo dự án.",
            _ => "Đang tạo dự án."
        };
        OnPropertyChanged(nameof(ProgressPercentage));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void SetCreatingState(bool isCreating, string message, double progress)
    {
        _isCreating = isCreating;
        _statusMessage = message;
        _progressPercentage = progress;
        OnPropertyChanged(nameof(IsCreating));
        OnPropertyChanged(nameof(CanSelectGame));
        OnPropertyChanged(nameof(CanSelectMod));
        OnPropertyChanged(nameof(CanEditProjectName));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ProgressPercentage));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
