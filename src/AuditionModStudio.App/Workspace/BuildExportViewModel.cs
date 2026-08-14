using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Shell;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;

namespace AuditionModStudio.App.Workspace;

public sealed class BuildExportViewModel : INotifyPropertyChanged
{
    private readonly IApplicationProjectSession _projectSession;
    private readonly IProjectBuildService _buildService;
    private readonly IArchiveExportDestinationValidator _destinationValidator;
    private readonly IArchiveExportService _exportService;
    private readonly IBackgroundTaskManager _taskManager;
    private Guid? _loadedProjectId;
    private BackgroundTaskId _activeTaskId;
    private string _outputDirectory = string.Empty;
    private string _outputFileName = string.Empty;
    private string _statusMessage = "Chưa có dự án để Build.";
    private string _stageLabel = "Sẵn sàng";
    private string _finalOutputPath = "—";
    private string _finalSize = "—";
    private string _finalSha256 = "—";
    private double _progressPercentage;
    private bool _replaceExisting;
    private bool _isBusy;

    public BuildExportViewModel(
        IApplicationProjectSession projectSession,
        IProjectBuildService buildService,
        IArchiveExportDestinationValidator destinationValidator,
        IArchiveExportService exportService,
        IBackgroundTaskManager taskManager)
    {
        _projectSession = projectSession ?? throw new ArgumentNullException(nameof(projectSession));
        _buildService = buildService ?? throw new ArgumentNullException(nameof(buildService));
        _destinationValidator = destinationValidator ?? throw new ArgumentNullException(nameof(destinationValidator));
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProjectStatus => _projectSession.Project is { } project
            ? $"Dự án: {project.Name} · Trạng thái Build: {ToBuildStatusLabel(project.BuildState.Status)}"
            : "Chưa mở dự án";

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (SetField(ref _outputDirectory, value?.Trim() ?? string.Empty))
            {
                RaiseCommandState();
            }
        }
    }

    public string OutputFileName
    {
        get => _outputFileName;
        set
        {
            if (SetField(ref _outputFileName, value?.Trim() ?? string.Empty))
            {
                RaiseCommandState();
            }
        }
    }

    public bool ReplaceExisting
    {
        get => _replaceExisting;
        set => SetField(ref _replaceExisting, value);
    }

    public bool IsBusy => _isBusy;

    public bool CanStart => !_isBusy
                            && _projectSession.Project is not null
                            && _projectSession.Workspace is not null
                            && !string.IsNullOrWhiteSpace(_outputDirectory)
                            && !string.IsNullOrWhiteSpace(_outputFileName);

    public bool CanCancel => _isBusy && _activeTaskId.IsValid;

    public double ProgressPercentage => _progressPercentage;

    public string StatusMessage => _statusMessage;

    public string StageLabel => _stageLabel;

    public string FinalOutputPath => _finalOutputPath;

    public string FinalSize => _finalSize;

    public string FinalSha256 => _finalSha256;

    public void RefreshProject()
    {
        var project = _projectSession.Project;
        var workspace = _projectSession.Workspace;
        if (project is null || workspace is null)
        {
            _loadedProjectId = null;
            _outputFileName = string.Empty;
            SetStatus("Sẵn sàng", "Chưa có dự án để Build.", 0);
        }
        else if (_loadedProjectId != project.ProjectId)
        {
            _loadedProjectId = project.ProjectId;
            _outputFileName = workspace.ArchiveWorkspace.WorkingArchiveRelativePath;
            ClearResult();
            SetStatus("Sẵn sàng", "Chọn thư mục xuất, sau đó kiểm tra, Build và xuất tệp Mod.", 0);
        }

        OnPropertyChanged(nameof(OutputFileName));
        OnPropertyChanged(nameof(ProjectStatus));
        RaiseCommandState();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isBusy)
        {
            return;
        }

        var project = _projectSession.Project;
        var workspace = _projectSession.Workspace;
        if (project is null || workspace is null)
        {
            SetStatus("Cần thao tác", "Hãy tạo hoặc mở dự án trước khi Build tệp Mod.", 0);
            return;
        }

        if (!ArchiveExportFileContract.TryCreate(
                workspace.ArchiveWorkspace.WorkingArchiveRelativePath, out var fileContract))
        {
            SetStatus("Kiểm tra không đạt", "Loại tệp nguồn của dự án không hợp lệ để xuất.", 0);
            return;
        }

        var outputDirectory = _outputDirectory;
        var outputFileName = _outputFileName;
        var overwritePolicy = _replaceExisting
            ? ArchiveExportOverwritePolicy.ReplaceExisting
            : ArchiveExportOverwritePolicy.RejectExisting;

        ArchiveExportDestinationResult? destinationValidation = null;
        ProjectBuildResult? buildResult = null;
        ArchiveExportResult? exportResult = null;
        IProgress<ProjectBuildProgress> uiBuildProgress = new Progress<ProjectBuildProgress>(UpdateBuildProgress);
        IProgress<ArchiveExportProgress> uiExportProgress = new Progress<ArchiveExportProgress>(UpdateExportProgress);
        ClearResult();
        SetBusy(true);
        SetStatus("Đang chờ", "Tác vụ Build và xuất file đang chờ xử lý.", 0);

        var enqueue = await _taskManager.EnqueueAsync(
            new BackgroundTaskRequest(
                BackgroundTaskKind.Build,
                async (taskProgress, taskCancellationToken) =>
                {
                    taskProgress.Report(new BackgroundTaskProgress(2, 100, "build_export.destination.validating"));
                    destinationValidation = _destinationValidator.Validate(new(
                        outputDirectory, outputFileName, fileContract, overwritePolicy));
                    if (!destinationValidation.Succeeded)
                    {
                        return BackgroundTaskExecutionResult.Failure(destinationValidation.DiagnosticCode);
                    }

                    var buildProgress = new CallbackProgress<ProjectBuildProgress>(value =>
                    {
                        taskProgress.Report(new BackgroundTaskProgress(
                            BuildCompletedUnits(value.Phase), 100, ToBuildStageCode(value.Phase)));
                        uiBuildProgress.Report(value);
                    });
                    buildResult = await _buildService.BuildAsync(
                        new(project, workspace), buildProgress, taskCancellationToken).ConfigureAwait(false);
                    if (!buildResult.Succeeded || buildResult.Project is null)
                    {
                        return BackgroundTaskExecutionResult.Failure(buildResult.DiagnosticCode);
                    }

                    var exportProgress = new CallbackProgress<ArchiveExportProgress>(value =>
                    {
                        taskProgress.Report(new BackgroundTaskProgress(
                            ExportCompletedUnits(value.Phase), 100, ToExportStageCode(value.Phase)));
                        uiExportProgress.Report(value);
                    });
                    exportResult = await _exportService.ExportAsync(
                        new(buildResult.Project, workspace, outputDirectory, outputFileName, overwritePolicy),
                        exportProgress,
                        taskCancellationToken).ConfigureAwait(false);
                    if (!exportResult.Succeeded)
                    {
                        return BackgroundTaskExecutionResult.Failure(exportResult.DiagnosticCode);
                    }

                    await _projectSession.TryUpdateProjectAsync(
                        project, workspace, buildResult.Project).ConfigureAwait(false);
                    return BackgroundTaskExecutionResult.Success();
                }),
            cancellationToken);

        if (!enqueue.Succeeded)
        {
            SetBusy(false);
            SetStatus("Chưa bắt đầu", ToEnqueueMessage(enqueue.FailureReason), 0);
            return;
        }

        _activeTaskId = enqueue.TaskId;
        OnPropertyChanged(nameof(CanCancel));
        var snapshot = await _taskManager.WaitForCompletionAsync(enqueue.TaskId, cancellationToken);
        _activeTaskId = default;
        SetBusy(false);

        if (snapshot?.State == BackgroundTaskState.Succeeded && exportResult?.Succeeded == true)
        {
            _loadedProjectId = buildResult!.Project!.ProjectId;
            _finalOutputPath = exportResult.Destination!.FullPath;
            _finalSize = $"{exportResult.Size:N0} byte";
            _finalSha256 = exportResult.Sha256!.Value.Value;
            OnPropertyChanged(nameof(FinalOutputPath));
            OnPropertyChanged(nameof(FinalSize));
            OnPropertyChanged(nameof(FinalSha256));
            OnPropertyChanged(nameof(ProjectStatus));
            SetStatus("Hoàn tất", "Đã Build và xuất tệp Mod thành công.", 100);
            return;
        }

        if (snapshot?.State == BackgroundTaskState.Cancelled
            || buildResult?.Cancelled == true
            || exportResult?.Cancelled == true)
        {
            SetStatus("Đã hủy", "Đã hủy Build và xuất file. Không có tệp chưa hoàn chỉnh nào được xuất.",
                _progressPercentage);
            return;
        }

        if (destinationValidation is { Succeeded: false })
        {
            SetStatus("Kiểm tra không đạt", ToDestinationMessage(destinationValidation.FailureReason), 0);
            return;
        }

        SetStatus("Không thành công", ToFailureMessage(buildResult, exportResult), _progressPercentage);
    }

    public bool Cancel() => _activeTaskId.IsValid && _taskManager.TryCancel(_activeTaskId);

    public void ReportFolderSelectionUnavailable() =>
            SetStatus("Không mở được thư mục", "Không thể mở trình chọn thư mục. Vui lòng thử lại.", _progressPercentage);

    private void UpdateBuildProgress(ProjectBuildProgress progress) =>
        SetStatus(ToBuildLabel(progress.Phase), ToBuildMessage(progress.Phase), BuildCompletedUnits(progress.Phase));

    private void UpdateExportProgress(ArchiveExportProgress progress) =>
        SetStatus(ToExportLabel(progress.Phase), ToExportMessage(progress.Phase), ExportCompletedUnits(progress.Phase));

    private void SetBusy(bool value)
    {
        if (_isBusy == value)
        {
            return;
        }

        _isBusy = value;
        OnPropertyChanged(nameof(IsBusy));
        RaiseCommandState();
    }

    private void SetStatus(string stage, string message, double percentage)
    {
        _stageLabel = stage;
        _statusMessage = message;
        _progressPercentage = Math.Clamp(percentage, 0, 100);
        OnPropertyChanged(nameof(StageLabel));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ProgressPercentage));
    }

    private void ClearResult()
    {
        _finalOutputPath = "—";
        _finalSize = "—";
        _finalSha256 = "—";
        OnPropertyChanged(nameof(FinalOutputPath));
        OnPropertyChanged(nameof(FinalSize));
        OnPropertyChanged(nameof(FinalSha256));
    }

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static int BuildCompletedUnits(ProjectBuildPhase phase) => phase switch
    {
        ProjectBuildPhase.Preparing => 10,
        ProjectBuildPhase.Validating => 20,
        ProjectBuildPhase.Packing => 45,
        ProjectBuildPhase.Verifying => 65,
        ProjectBuildPhase.Completed => 70,
        _ => 0
    };

    private static int ExportCompletedUnits(ArchiveExportPhase phase) => phase switch
    {
        ArchiveExportPhase.Preparing => 72,
        ArchiveExportPhase.VerifyingSource => 76,
        ArchiveExportPhase.Copying => 82,
        ArchiveExportPhase.VerifyingCandidate => 88,
        ArchiveExportPhase.Promoting => 94,
        ArchiveExportPhase.Completed => 100,
        _ => 70
    };

    private static string ToBuildStageCode(ProjectBuildPhase phase) =>
        $"build_export.build.{phase.ToString().ToLowerInvariant()}";

    private static string ToExportStageCode(ArchiveExportPhase phase) =>
        $"build_export.export.{phase.ToString().ToLowerInvariant()}";

    private static string ToBuildLabel(ProjectBuildPhase phase) => phase switch
    {
        ProjectBuildPhase.Validating => "Đang kiểm tra",
        ProjectBuildPhase.Packing => "Đang Build",
        ProjectBuildPhase.Verifying => "Đang xác minh bản Build",
        ProjectBuildPhase.Completed => "Build hoàn tất",
        _ => "Đang chuẩn bị Build"
    };

    private static string ToBuildMessage(ProjectBuildPhase phase) => phase switch
    {
        ProjectBuildPhase.Validating => "Đang kiểm tra trạng thái dự án và dữ liệu tệp nguồn.",
        ProjectBuildPhase.Packing => "Đang đóng gói tệp Mod đã kiểm tra.",
        ProjectBuildPhase.Verifying => "Đang xác minh file Build cuối.",
        ProjectBuildPhase.Completed => "Đã xác minh bản Build. Đang chuẩn bị xuất file.",
        _ => "Đang lưu dự án và chuẩn bị vùng Build riêng."
    };

    private static string ToExportLabel(ArchiveExportPhase phase) => phase switch
    {
        ArchiveExportPhase.Copying => "Đang xuất file",
        ArchiveExportPhase.VerifyingCandidate => "Đang xác minh file xuất",
        ArchiveExportPhase.Promoting => "Đang hoàn tất tệp Mod",
        ArchiveExportPhase.Completed => "Xuất file hoàn tất",
        _ => "Đang chuẩn bị xuất file"
    };

    private static string ToExportMessage(ArchiveExportPhase phase) => phase switch
    {
        ArchiveExportPhase.VerifyingSource => "Đang xác minh file Build trước khi xuất.",
        ArchiveExportPhase.Copying => "Đang ghi tệp tạm vào thư mục đã chọn.",
        ArchiveExportPhase.VerifyingCandidate => "Đang kiểm tra dữ liệu và SHA-256 của file xuất.",
        ArchiveExportPhase.Promoting => "Đang hoàn tất tệp Mod một cách an toàn.",
        ArchiveExportPhase.Completed => "Tệp Mod đã sẵn sàng.",
        _ => "Đang chuẩn bị thư mục xuất đã chọn."
    };

    private static string ToDestinationMessage(ArchiveExportDestinationFailureReason reason) => reason switch
    {
        ArchiveExportDestinationFailureReason.DirectoryNotFound => "Hãy chọn một thư mục xuất đang tồn tại.",
        ArchiveExportDestinationFailureReason.DirectoryAccessDenied => "Không thể truy cập thư mục đã chọn.",
        ArchiveExportDestinationFailureReason.DirectoryUnavailable => "Thư mục đã chọn không khả dụng.",
        ArchiveExportDestinationFailureReason.ReparsePointNotAllowed => "Chính sách an toàn đường dẫn không cho phép dùng thư mục này.",
        ArchiveExportDestinationFailureReason.InvalidFileName => "Hãy nhập tên file hợp lệ, không kèm đường dẫn thư mục.",
        ArchiveExportDestinationFailureReason.InvalidArchiveExtension => "Phần mở rộng của file phải khớp loại tệp nguồn trong dự án.",
        ArchiveExportDestinationFailureReason.DestinationCollision => "Tệp này đã tồn tại. Hãy bật thay thế hoặc chọn tên khác.",
        _ => "Hãy chọn thư mục xuất tuyệt đối và tên file hợp lệ."
    };

    private static string ToFailureMessage(ProjectBuildResult? build, ArchiveExportResult? export) =>
        build is not null && !build.Succeeded
            ? "Không thể kiểm tra hoặc Build dự án. Hãy xem nhật ký ứng dụng, sửa dự án rồi thử lại."
            : export?.FailureReason == ArchiveExportFailureReason.DestinationInvalid
                ? "Thư mục xuất đã thay đổi hoặc không còn khả dụng. Hãy kiểm tra rồi thử lại."
                : "Không thể xuất tệp Mod an toàn. Dữ liệu cũ tại thư mục đích vẫn được giữ nguyên khi có thể.";

    private static string ToEnqueueMessage(BackgroundTaskEnqueueFailureReason reason) => reason switch
    {
        BackgroundTaskEnqueueFailureReason.QueueFull => "Hàng đợi đang đầy. Hãy thử lại khi một tác vụ khác hoàn tất.",
        BackgroundTaskEnqueueFailureReason.ShuttingDown => "Ứng dụng đang đóng nên không thể bắt đầu tác vụ này.",
        BackgroundTaskEnqueueFailureReason.Cancelled => "Đã hủy Build và xuất file trước khi bắt đầu.",
        _ => "Không thể đưa tác vụ Build và xuất file vào hàng đợi."
    };

    private static string ToBuildStatusLabel(ProjectBuildStatus status) => status switch
    {
        ProjectBuildStatus.Succeeded => "Thành công",
        ProjectBuildStatus.Failed => "Không thành công",
        ProjectBuildStatus.Dirty => "Có thay đổi chưa Build",
        _ => "Chưa Build"
    };

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
