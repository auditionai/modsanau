using System.Collections.Concurrent;
using AuditionModStudio.Core.Tasks;
using AuditionModStudio.Infrastructure.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationTests;

public sealed class BackgroundTaskManagerTests
{
    [Fact]
    public async Task Every_declared_job_kind_runs_through_the_same_central_queue()
    {
        await using var manager = await CreateAsync(new(2, 16, 32));
        var kinds = Enum.GetValues<BackgroundTaskKind>().Where(kind => kind != BackgroundTaskKind.None).ToArray();
        var taskIds = new List<BackgroundTaskId>();

        foreach (var kind in kinds)
        {
            var queued = await manager.EnqueueAsync(new(kind, Success));
            Assert.True(queued.Succeeded, queued.DiagnosticCode);
            taskIds.Add(queued.TaskId);
        }

        var completed = await Task.WhenAll(taskIds.Select(id => manager.WaitForCompletionAsync(id)));

        Assert.Equal(kinds, completed.Select(snapshot => snapshot!.Kind));
        Assert.All(completed, snapshot => Assert.Equal(BackgroundTaskState.Succeeded, snapshot!.State));
    }

    [Fact]
    public async Task Notifications_publish_queued_running_progress_and_terminal_snapshots()
    {
        await using var manager = await CreateAsync(new(1, 4, 8));
        var notifications = new ConcurrentQueue<BackgroundTaskNotification>();
        manager.Notification += (_, notification) => notifications.Enqueue(notification);

        var queued = await manager.EnqueueAsync(new(BackgroundTaskKind.Scan, (progress, _) =>
        {
            progress.Report(new(2, 4, "SCANNING_ASSETS"));
            progress.Report(new(5, 4, "INVALID_PROGRESS"));
            return Task.FromResult(BackgroundTaskExecutionResult.Success());
        }));
        var completed = await manager.WaitForCompletionAsync(queued.TaskId);

        Assert.Equal(BackgroundTaskState.Succeeded, completed!.State);
        Assert.Equal(new(2, 4, "SCANNING_ASSETS"), completed.Progress);
        var states = notifications.Where(item => item.Snapshot.TaskId == queued.TaskId)
            .Select(item => item.Snapshot.State).ToArray();
        Assert.Equal([BackgroundTaskState.Queued, BackgroundTaskState.Running,
            BackgroundTaskState.Running, BackgroundTaskState.Succeeded], states);
    }

    [Fact]
    public async Task Queue_is_bounded_and_rejects_overflow_without_losing_accepted_jobs()
    {
        await using var manager = await CreateAsync(new(1, 1, 8));
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await manager.EnqueueAsync(new(BackgroundTaskKind.Extract, async (_, token) =>
        {
            running.TrySetResult();
            await release.Task.WaitAsync(token);
            return BackgroundTaskExecutionResult.Success();
        }));
        await running.Task;

        var second = await manager.EnqueueAsync(new(BackgroundTaskKind.Scan, Success));
        var rejected = await manager.EnqueueAsync(new(BackgroundTaskKind.Build, Success));
        release.TrySetResult();

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.False(rejected.Succeeded);
        Assert.Equal(BackgroundTaskEnqueueFailureReason.QueueFull, rejected.FailureReason);
        Assert.Equal(BackgroundTaskState.Succeeded,
            (await manager.WaitForCompletionAsync(second.TaskId))!.State);
    }

    [Fact]
    public async Task Queued_and_running_jobs_are_cancellable_and_never_report_success()
    {
        await using var manager = await CreateAsync(new(1, 4, 8));
        var firstRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await manager.EnqueueAsync(new(BackgroundTaskKind.Build, async (_, token) =>
        {
            firstRunning.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return BackgroundTaskExecutionResult.Success();
        }));
        await firstRunning.Task;
        var queuedExecuted = 0;
        var second = await manager.EnqueueAsync(new(BackgroundTaskKind.Download, (_, _) =>
        {
            Interlocked.Increment(ref queuedExecuted);
            return Task.FromResult(BackgroundTaskExecutionResult.Success());
        }));

        Assert.True(manager.TryCancel(second.TaskId));
        Assert.True(manager.TryCancel(first.TaskId));
        var results = await Task.WhenAll(
            manager.WaitForCompletionAsync(first.TaskId),
            manager.WaitForCompletionAsync(second.TaskId));

        Assert.All(results, result => Assert.Equal(BackgroundTaskState.Cancelled, result!.State));
        Assert.Equal(0, queuedExecuted);
    }

    [Fact]
    public async Task Structured_and_unhandled_failures_do_not_stop_following_jobs_or_leak_exception_text()
    {
        await using var manager = await CreateAsync(new(1, 8, 8));
        var expected = await manager.EnqueueAsync(new(BackgroundTaskKind.Convert, (_, _) =>
            Task.FromResult(BackgroundTaskExecutionResult.Failure("CONVERSION_REJECTED"))));
        var thrown = await manager.EnqueueAsync(new(BackgroundTaskKind.Ai, (_, _) =>
            throw new InvalidOperationException("sensitive local detail")));
        var following = await manager.EnqueueAsync(new(BackgroundTaskKind.Resize, Success));

        var expectedResult = await manager.WaitForCompletionAsync(expected.TaskId);
        var thrownResult = await manager.WaitForCompletionAsync(thrown.TaskId);
        var followingResult = await manager.WaitForCompletionAsync(following.TaskId);

        Assert.Equal("CONVERSION_REJECTED", expectedResult!.DiagnosticCode);
        Assert.Equal("BACKGROUND_TASK_UNHANDLED_FAILURE", thrownResult!.DiagnosticCode);
        Assert.DoesNotContain("sensitive", thrownResult.DiagnosticCode, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BackgroundTaskState.Succeeded, followingResult!.State);
    }

    [Fact]
    public async Task Worker_concurrency_never_exceeds_configured_limit()
    {
        await using var manager = await CreateAsync(new(2, 16, 16));
        var active = 0;
        var maximum = 0;
        var requests = Enumerable.Range(0, 8).Select(_ => new BackgroundTaskRequest(
            BackgroundTaskKind.Thumbnail,
            async (_, token) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, current);
                await Task.Delay(30, token);
                Interlocked.Decrement(ref active);
                return BackgroundTaskExecutionResult.Success();
            }));

        var queued = new List<BackgroundTaskEnqueueResult>();
        foreach (var request in requests)
        {
            queued.Add(await manager.EnqueueAsync(request));
        }
        await Task.WhenAll(queued.Select(item => manager.WaitForCompletionAsync(item.TaskId)));

        Assert.Equal(2, maximum);
    }

    [Fact]
    public async Task Completed_history_is_bounded_and_snapshots_are_deterministically_ordered()
    {
        await using var manager = await CreateAsync(new(1, 8, 2));
        var ids = new List<BackgroundTaskId>();
        for (var index = 0; index < 4; index++)
        {
            var queued = await manager.EnqueueAsync(new(BackgroundTaskKind.Scan, Success));
            ids.Add(queued.TaskId);
            await manager.WaitForCompletionAsync(queued.TaskId);
        }

        var snapshots = manager.GetSnapshots();

        Assert.Equal(2, snapshots.Length);
        Assert.False(manager.TryGetSnapshot(ids[0], out _));
        Assert.Equal(snapshots.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.TaskId.Value), snapshots);
    }

    [Fact]
    public async Task Subscriber_failure_cannot_break_job_execution()
    {
        await using var manager = await CreateAsync(new(1, 4, 4));
        manager.Notification += (_, _) => throw new InvalidOperationException("subscriber failure");

        var queued = await manager.EnqueueAsync(new(BackgroundTaskKind.Resize, Success));
        var result = await manager.WaitForCompletionAsync(queued.TaskId);

        Assert.Equal(BackgroundTaskState.Succeeded, result!.State);
    }

    private static async Task<BackgroundTaskManager> CreateAsync(BackgroundTaskManagerOptions options)
    {
        var manager = new BackgroundTaskManager(options, NullLogger<BackgroundTaskManager>.Instance);
        await manager.StartAsync(CancellationToken.None);
        return manager;
    }

    private static Task<BackgroundTaskExecutionResult> Success(
        IProgress<BackgroundTaskProgress> progress,
        CancellationToken cancellationToken) => Task.FromResult(BackgroundTaskExecutionResult.Success());

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}
