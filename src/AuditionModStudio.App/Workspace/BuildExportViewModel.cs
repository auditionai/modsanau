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
    private string _statusMessage = "No active project is available to build.";
    private string _stageLabel = "Ready";
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
        ? $"Project: {project.Name} · Build status: {project.BuildState.Status}"
        : "No active project";

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
            SetStatus("Ready", "No active project is available to build.", 0);
        }
        else if (_loadedProjectId != project.ProjectId)
        {
            _loadedProjectId = project.ProjectId;
            _outputFileName = workspace.ArchiveWorkspace.WorkingArchiveRelativePath;
            ClearResult();
            SetStatus("Ready", "Choose an export folder, then validate, build, and export the archive.", 0);
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
            SetStatus("Action required", "Create or open a project before building an archive.", 0);
            return;
        }

        if (!ArchiveExportFileContract.TryCreate(
                workspace.ArchiveWorkspace.WorkingArchiveRelativePath, out var fileContract))
        {
            SetStatus("Validation failed", "The project archive type is not valid for export.", 0);
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
        SetStatus("Queued", "Build and export is waiting for a background worker.", 0);

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
            SetStatus("Not started", ToEnqueueMessage(enqueue.FailureReason), 0);
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
            _finalSize = $"{exportResult.Size:N0} bytes";
            _finalSha256 = exportResult.Sha256!.Value.Value;
            OnPropertyChanged(nameof(FinalOutputPath));
            OnPropertyChanged(nameof(FinalSize));
            OnPropertyChanged(nameof(FinalSha256));
            OnPropertyChanged(nameof(ProjectStatus));
            SetStatus("Completed", "Archive built and exported successfully.", 100);
            return;
        }

        if (snapshot?.State == BackgroundTaskState.Cancelled
            || buildResult?.Cancelled == true
            || exportResult?.Cancelled == true)
        {
            SetStatus("Cancelled", "Build and export was cancelled. No partial final archive was published.",
                _progressPercentage);
            return;
        }

        if (destinationValidation is { Succeeded: false })
        {
            SetStatus("Validation failed", ToDestinationMessage(destinationValidation.FailureReason), 0);
            return;
        }

        SetStatus("Failed", ToFailureMessage(buildResult, exportResult), _progressPercentage);
    }

    public bool Cancel() => _activeTaskId.IsValid && _taskManager.TryCancel(_activeTaskId);

    public void ReportFolderSelectionUnavailable() =>
        SetStatus("Folder unavailable", "The folder picker could not be opened. Try again.", _progressPercentage);

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
        ProjectBuildPhase.Validating => "Validating",
        ProjectBuildPhase.Packing => "Building",
        ProjectBuildPhase.Verifying => "Verifying build",
        ProjectBuildPhase.Completed => "Build complete",
        _ => "Preparing build"
    };

    private static string ToBuildMessage(ProjectBuildPhase phase) => phase switch
    {
        ProjectBuildPhase.Validating => "Checking project state and archive inputs.",
        ProjectBuildPhase.Packing => "Packing the validated project archive.",
        ProjectBuildPhase.Verifying => "Verifying the final build artifact.",
        ProjectBuildPhase.Completed => "Build verified. Preparing the export transaction.",
        _ => "Saving the project and preparing an isolated build workspace."
    };

    private static string ToExportLabel(ArchiveExportPhase phase) => phase switch
    {
        ArchiveExportPhase.Copying => "Exporting",
        ArchiveExportPhase.VerifyingCandidate => "Verifying export",
        ArchiveExportPhase.Promoting => "Publishing archive",
        ArchiveExportPhase.Completed => "Export complete",
        _ => "Preparing export"
    };

    private static string ToExportMessage(ArchiveExportPhase phase) => phase switch
    {
        ArchiveExportPhase.VerifyingSource => "Verifying the exact build artifact before export.",
        ArchiveExportPhase.Copying => "Writing a temporary archive in the selected destination.",
        ArchiveExportPhase.VerifyingCandidate => "Checking exported bytes and SHA-256.",
        ArchiveExportPhase.Promoting => "Atomically publishing the final archive.",
        ArchiveExportPhase.Completed => "The final archive is ready.",
        _ => "Preparing the selected export destination."
    };

    private static string ToDestinationMessage(ArchiveExportDestinationFailureReason reason) => reason switch
    {
        ArchiveExportDestinationFailureReason.DirectoryNotFound => "Choose an existing export folder.",
        ArchiveExportDestinationFailureReason.DirectoryAccessDenied => "The selected folder cannot be accessed.",
        ArchiveExportDestinationFailureReason.DirectoryUnavailable => "The selected folder is unavailable.",
        ArchiveExportDestinationFailureReason.ReparsePointNotAllowed => "The selected folder is not allowed by path safety policy.",
        ArchiveExportDestinationFailureReason.InvalidFileName => "Enter one valid archive filename without a folder path.",
        ArchiveExportDestinationFailureReason.InvalidArchiveExtension => "The filename extension must match the project archive type.",
        ArchiveExportDestinationFailureReason.DestinationCollision => "That archive already exists. Enable explicit replacement or choose another name.",
        _ => "Choose a valid absolute export folder and archive filename."
    };

    private static string ToFailureMessage(ProjectBuildResult? build, ArchiveExportResult? export) =>
        build is not null && !build.Succeeded
            ? "The project could not be validated or built. Review the application log and correct the project before retrying."
            : export?.FailureReason == ArchiveExportFailureReason.DestinationInvalid
                ? "The export destination changed or became unavailable. Review it and try again."
                : "The archive could not be exported safely. The previous destination was preserved where recovery was possible.";

    private static string ToEnqueueMessage(BackgroundTaskEnqueueFailureReason reason) => reason switch
    {
        BackgroundTaskEnqueueFailureReason.QueueFull => "The task queue is full. Try again after another task finishes.",
        BackgroundTaskEnqueueFailureReason.ShuttingDown => "The application is shutting down and cannot start this operation.",
        BackgroundTaskEnqueueFailureReason.Cancelled => "Build and export was cancelled before it started.",
        _ => "Build and export could not be queued."
    };

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
