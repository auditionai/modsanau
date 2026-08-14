using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using AuditionModStudio.Core.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuditionModStudio.Infrastructure.Tasks;

public sealed class BackgroundTaskManager : IBackgroundTaskManager, IHostedService, IAsyncDisposable
{
    private readonly BackgroundTaskManagerOptions _options;
    private readonly ILogger<BackgroundTaskManager> _logger;
    private readonly Channel<WorkItem> _queue;
    private readonly ConcurrentDictionary<BackgroundTaskId, WorkItem> _items = [];
    private readonly ConcurrentQueue<BackgroundTaskId> _completedOrder = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleSync = new();
    private ImmutableArray<Task> _workers = [];
    private long _notificationSequence;
    private int _accepting = 1;
    private int _started;
    private int _disposed;

    public BackgroundTaskManager(
        BackgroundTaskManagerOptions options,
        ILogger<BackgroundTaskManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        if (!options.IsValid)
        {
            throw new ArgumentException("Background task manager options are invalid.", nameof(options));
        }

        _options = options;
        _logger = logger;
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(options.MaximumQueuedTasks)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = options.MaximumConcurrency == 1,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public event EventHandler<BackgroundTaskNotification>? Notification;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleSync)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _workers = Enumerable.Range(0, _options.MaximumConcurrency)
                    .Select(_ => Task.Run(ProcessQueueAsync))
                    .ToImmutableArray();
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _accepting, 0);
        _queue.Writer.TryComplete();
        _shutdown.Cancel();

        if (!_workers.IsDefaultOrEmpty)
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            while (_queue.Reader.TryRead(out var item))
            {
                Complete(item, BackgroundTaskState.Cancelled, "BACKGROUND_TASK_MANAGER_STOPPED");
            }
        }
    }

    public ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(
        BackgroundTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(BackgroundTaskEnqueueResult.Failure(
                BackgroundTaskEnqueueFailureReason.Cancelled, "BACKGROUND_TASK_ENQUEUE_CANCELLED"));
        }

        if (request is null || request.Operation is null || request.Kind == BackgroundTaskKind.None
            || !Enum.IsDefined(request.Kind))
        {
            return ValueTask.FromResult(BackgroundTaskEnqueueResult.Failure(
                BackgroundTaskEnqueueFailureReason.InvalidRequest, "BACKGROUND_TASK_REQUEST_INVALID"));
        }

        if (Volatile.Read(ref _accepting) == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.FromResult(BackgroundTaskEnqueueResult.Failure(
                BackgroundTaskEnqueueFailureReason.ShuttingDown, "BACKGROUND_TASK_MANAGER_STOPPING"));
        }

        var id = new BackgroundTaskId(Guid.NewGuid());
        var snapshot = new BackgroundTaskSnapshot(
            id, request.Kind, BackgroundTaskState.Queued, null, null,
            DateTimeOffset.UtcNow, null, null);
        var item = new WorkItem(request, snapshot);
        if (!_items.TryAdd(id, item))
        {
            item.Dispose();
            return ValueTask.FromResult(BackgroundTaskEnqueueResult.Failure(
                BackgroundTaskEnqueueFailureReason.QueueFull, "BACKGROUND_TASK_ID_COLLISION"));
        }

        if (!_queue.Writer.TryWrite(item))
        {
            _items.TryRemove(id, out _);
            item.Dispose();
            var reason = Volatile.Read(ref _accepting) == 0
                ? BackgroundTaskEnqueueFailureReason.ShuttingDown
                : BackgroundTaskEnqueueFailureReason.QueueFull;
            return ValueTask.FromResult(BackgroundTaskEnqueueResult.Failure(
                reason, reason == BackgroundTaskEnqueueFailureReason.QueueFull
                    ? "BACKGROUND_TASK_QUEUE_FULL"
                    : "BACKGROUND_TASK_MANAGER_STOPPING"));
        }

        Publish(snapshot);
        item.Ready.TrySetResult();
        return ValueTask.FromResult(BackgroundTaskEnqueueResult.Success(id));
    }

    public bool TryCancel(BackgroundTaskId taskId)
    {
        if (!taskId.IsValid || !_items.TryGetValue(taskId, out var item))
        {
            return false;
        }

        item.Cancellation.Cancel();
        lock (item.Sync)
        {
            if (item.Snapshot.State == BackgroundTaskState.Queued)
            {
                CompleteLocked(item, BackgroundTaskState.Cancelled, "BACKGROUND_TASK_CANCELLED");
            }
            else if (IsTerminal(item.Snapshot.State))
            {
                return false;
            }
        }

        return true;
    }

    public bool TryGetSnapshot(BackgroundTaskId taskId, out BackgroundTaskSnapshot snapshot)
    {
        if (_items.TryGetValue(taskId, out var item))
        {
            lock (item.Sync)
            {
                snapshot = item.Snapshot;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    public ImmutableArray<BackgroundTaskSnapshot> GetSnapshots() => _items.Values
        .Select(GetSnapshot)
        .OrderBy(snapshot => snapshot.CreatedAtUtc)
        .ThenBy(snapshot => snapshot.TaskId.Value)
        .ToImmutableArray();

    public async Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(
        BackgroundTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        if (!taskId.IsValid || !_items.TryGetValue(taskId, out var item))
        {
            return null;
        }

        return await item.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var item in _items.Values)
        {
            item.Dispose();
        }
        _shutdown.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await item.Ready.Task.ConfigureAwait(false);
            lock (item.Sync)
            {
                if (IsTerminal(item.Snapshot.State))
                {
                    continue;
                }

                if (item.Cancellation.IsCancellationRequested || _shutdown.IsCancellationRequested)
                {
                    CompleteLocked(item, BackgroundTaskState.Cancelled, "BACKGROUND_TASK_CANCELLED");
                    continue;
                }

                item.Snapshot = item.Snapshot with
                {
                    State = BackgroundTaskState.Running,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                };
                Publish(item.Snapshot);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                item.Cancellation.Token, _shutdown.Token);
            try
            {
                var progress = new InlineProgress(value => ReportProgress(item, value));
                var result = await item.Request.Operation(progress, linked.Token).ConfigureAwait(false);
                if (result is null)
                {
                    Complete(item, BackgroundTaskState.Failed, "BACKGROUND_TASK_RESULT_MISSING");
                }
                else if (result.Succeeded)
                {
                    Complete(item, BackgroundTaskState.Succeeded, null);
                }
                else
                {
                    Complete(item, BackgroundTaskState.Failed,
                        IsDiagnosticCodeValid(result.DiagnosticCode)
                            ? result.DiagnosticCode
                            : "BACKGROUND_TASK_FAILED");
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                Complete(item, BackgroundTaskState.Cancelled, "BACKGROUND_TASK_CANCELLED");
            }
            catch (Exception exception)
            {
                _logger.LogError("Background task {TaskId} ({TaskKind}) failed with {ExceptionType}",
                    item.Snapshot.TaskId, item.Snapshot.Kind, exception.GetType().Name);
                Complete(item, BackgroundTaskState.Failed, "BACKGROUND_TASK_UNHANDLED_FAILURE");
            }
        }
    }

    private void ReportProgress(WorkItem item, BackgroundTaskProgress progress)
    {
        if (!IsProgressValid(progress))
        {
            return;
        }

        lock (item.Sync)
        {
            if (item.Snapshot.State != BackgroundTaskState.Running)
            {
                return;
            }

            item.Snapshot = item.Snapshot with { Progress = progress };
            Publish(item.Snapshot);
        }
    }

    private void Complete(WorkItem item, BackgroundTaskState state, string? diagnosticCode)
    {
        lock (item.Sync)
        {
            if (!IsTerminal(item.Snapshot.State))
            {
                CompleteLocked(item, state, diagnosticCode);
            }
        }
    }

    private void CompleteLocked(WorkItem item, BackgroundTaskState state, string? diagnosticCode)
    {
        item.Snapshot = item.Snapshot with
        {
            State = state,
            DiagnosticCode = diagnosticCode,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        item.Completion.TrySetResult(item.Snapshot);
        _completedOrder.Enqueue(item.Snapshot.TaskId);
        Publish(item.Snapshot);
        PruneHistory();
    }

    private void PruneHistory()
    {
        while (_completedOrder.Count > _options.MaximumCompletedHistory
               && _completedOrder.TryDequeue(out var oldest)
               && _items.TryRemove(oldest, out var removed))
        {
            removed.Dispose();
        }
    }

    private void Publish(BackgroundTaskSnapshot snapshot)
    {
        var handlers = Notification;
        if (handlers is null)
        {
            return;
        }

        var notification = new BackgroundTaskNotification(
            Interlocked.Increment(ref _notificationSequence), snapshot);
        foreach (EventHandler<BackgroundTaskNotification> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, notification);
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Background task notification subscriber failed with {ExceptionType}",
                    exception.GetType().Name);
            }
        }
    }

    private static BackgroundTaskSnapshot GetSnapshot(WorkItem item)
    {
        lock (item.Sync)
        {
            return item.Snapshot;
        }
    }

    private static bool IsTerminal(BackgroundTaskState state) => state is
        BackgroundTaskState.Succeeded or BackgroundTaskState.Failed or BackgroundTaskState.Cancelled;

    private static bool IsProgressValid(BackgroundTaskProgress? progress) => progress is not null
        && progress.TotalUnits >= 0
        && progress.CompletedUnits >= 0
        && progress.CompletedUnits <= progress.TotalUnits
        && (progress.StageCode is null || IsDiagnosticCodeValid(progress.StageCode));

    private static bool IsDiagnosticCodeValid(string? code) => code is not null
        && code.Length is > 0 and <= 128
        && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private sealed class WorkItem(BackgroundTaskRequest request, BackgroundTaskSnapshot snapshot) : IDisposable
    {
        public object Sync { get; } = new();
        public BackgroundTaskRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<BackgroundTaskSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BackgroundTaskSnapshot Snapshot { get; set; } = snapshot;
        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class InlineProgress(Action<BackgroundTaskProgress> report) : IProgress<BackgroundTaskProgress>
    {
        public void Report(BackgroundTaskProgress value) => report(value);
    }
}
