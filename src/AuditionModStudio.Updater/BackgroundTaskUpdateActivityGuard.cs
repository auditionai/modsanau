using AuditionModStudio.Core.Tasks;

namespace AuditionModStudio.Updater;

public sealed class BackgroundTaskUpdateActivityGuard(IBackgroundTaskManager taskManager) : IAppUpdateActivityGuard
{
    public bool MustDeferInstallation() => taskManager.GetSnapshots().Any(static task =>
        task.Kind == BackgroundTaskKind.Build
        && task.State is BackgroundTaskState.Queued or BackgroundTaskState.Running);
}
