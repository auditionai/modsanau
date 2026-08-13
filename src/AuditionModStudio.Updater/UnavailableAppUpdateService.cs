namespace AuditionModStudio.Updater;

public sealed class UnavailableAppUpdateService : IAppUpdateService
{
    public Task<AppUpdateResult> VerifyAndInstallAsync(
        AppUpdateRequest request,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(cancellationToken.IsCancellationRequested
            ? AppUpdateResult.Failure(AppUpdateFailureReason.Cancelled, "APP_UPDATE_CANCELLED")
            : AppUpdateResult.Failure(AppUpdateFailureReason.InstallUnavailable,
                "APP_UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED"));
}
