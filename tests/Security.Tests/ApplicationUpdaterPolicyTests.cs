using System.Collections.Immutable;
using AuditionModStudio.Core.Tasks;
using AuditionModStudio.Updater;

namespace Security.Tests;

public sealed class ApplicationUpdaterPolicyTests
{
    [Fact]
    public async Task Unconfigured_production_updater_fails_closed_without_breaking_local_services()
    {
        var service = new UnavailableAppUpdateService();

        var result = await service.VerifyAndInstallAsync(new(new Version(1, 0, 0, 0), new byte[] { 1 }));

        Assert.Equal(AppUpdateStatus.Failed, result.Status);
        Assert.Equal(AppUpdateFailureReason.InstallUnavailable, result.FailureReason);
        Assert.Equal("APP_UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED", result.DiagnosticCode);
    }

    [Theory]
    [InlineData(BackgroundTaskKind.Build, BackgroundTaskState.Queued, true)]
    [InlineData(BackgroundTaskKind.Build, BackgroundTaskState.Running, true)]
    [InlineData(BackgroundTaskKind.Build, BackgroundTaskState.Succeeded, false)]
    [InlineData(BackgroundTaskKind.Ai, BackgroundTaskState.Running, false)]
    public void Active_build_export_work_is_the_only_background_deferral(
        BackgroundTaskKind kind, BackgroundTaskState state, bool expected)
    {
        var manager = new FakeTaskManager(CreateSnapshot(kind, state));

        Assert.Equal(expected, new BackgroundTaskUpdateActivityGuard(manager).MustDeferInstallation());
    }

    [Fact]
    public void Windows_handoff_is_typed_rehashes_candidate_and_preserves_as_invoker()
    {
        var repository = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(repository, "src", "AuditionModStudio.App", "Updates",
            "WindowsMsixUpdateInstaller.cs"));
        var runtimeManifest = File.ReadAllText(Path.Combine(repository, "src", "AuditionModStudio.App", "app.manifest"));

        Assert.Contains("SHA256.HashDataAsync", installer, StringComparison.Ordinal);
        Assert.Contains("PackageManager", installer, StringComparison.Ordinal);
        Assert.Contains("DeploymentOptions.None", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ForceUpdateFromAnyVersion", installer, StringComparison.Ordinal);
        Assert.Contains("requestedExecutionLevel level=\"asInvoker\"", runtimeManifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Updater_source_has_no_game_update_authority()
    {
        var updaterRoot = Path.Combine(FindRepositoryRoot(), "src", "AuditionModStudio.Updater");
        var source = string.Join('\n', Directory.EnumerateFiles(updaterRoot, "*.cs")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(File.ReadAllText));

        Assert.DoesNotContain("GameUpdate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PatchGame", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GameDirectory", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Registry", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
    }

    private static BackgroundTaskSnapshot CreateSnapshot(BackgroundTaskKind kind, BackgroundTaskState state) =>
        new(new BackgroundTaskId(Guid.NewGuid()), kind, state, null, null, DateTimeOffset.UtcNow, null, null);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AuditionModStudio.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("REPOSITORY_ROOT_NOT_FOUND");
    }

    private sealed class FakeTaskManager(params BackgroundTaskSnapshot[] snapshots) : IBackgroundTaskManager
    {
        public event EventHandler<BackgroundTaskNotification>? Notification { add { } remove { } }
        public ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(BackgroundTaskRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool TryCancel(BackgroundTaskId taskId) => false;
        public bool TryGetSnapshot(BackgroundTaskId taskId, out BackgroundTaskSnapshot snapshot)
        {
            snapshot = snapshots.FirstOrDefault(item => item.TaskId == taskId)!;
            return snapshot is not null;
        }
        public ImmutableArray<BackgroundTaskSnapshot> GetSnapshots() => [.. snapshots];
        public Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(BackgroundTaskId taskId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
