using System.Threading.Channels;

namespace VisionStation.Vision;

internal interface IHalconOperationScheduler : IAsyncDisposable
{
    /// <summary>
    /// Runs one synchronous HALCON operation on a fixed dedicated worker thread. Cancellation
    /// applies only while the item is waiting; after execution admission it must return safely.
    /// </summary>
    Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken);
}

internal sealed class HalconOperationScheduler : IHalconOperationScheduler
{
    private const int DefaultWorkerCount = 2;
    private const int DefaultCapacity = 64;

    private readonly object _lifecycleSync = new();
    private readonly Channel<IHalconWorkItem> _channel;
    private readonly Task[] _workers;
    private bool _shutdownStarted;
    private int _pendingEnqueueCount;
    private TaskCompletionSource? _pendingEnqueuesDrained;
    private Task? _disposeTask;

    internal HalconOperationScheduler()
        : this(DefaultWorkerCount, DefaultCapacity)
    {
    }

    internal HalconOperationScheduler(int workerCount, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _channel = Channel.CreateBounded<IHalconWorkItem>(
            new BoundedChannelOptions(capacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
        _workers = Enumerable.Range(0, workerCount)
            .Select(workerIndex => Task.Factory.StartNew(
                () => WorkerLoop(workerIndex),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default))
            .ToArray();
    }

    public Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        HalconWorkItem<T> item;
        lock (_lifecycleSync)
        {
            if (_shutdownStarted)
            {
                return Task.FromException<T>(CreateDisposedException());
            }

            item = new HalconWorkItem<T>(operation, cancellationToken);
            _pendingEnqueueCount++;
        }

        return QueueAndAwaitAsync(item, cancellationToken);
    }

    /// <summary>
    /// Rejects new and queued work, then waits for admitted operations and fixed workers to exit.
    /// Concurrent callers share the same shutdown task. This wait is intentionally unbounded:
    /// callers must configure an independent native operator timeout and must never release
    /// dependent resources or report successful shutdown before this operation completes.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _shutdownStarted = true;
            _channel.Writer.TryComplete();
            while (_channel.Reader.TryRead(out IHalconWorkItem? item))
            {
                item.RejectForShutdown();
            }

            Task pendingEnqueues;
            if (_pendingEnqueueCount == 0)
            {
                pendingEnqueues = Task.CompletedTask;
            }
            else
            {
                _pendingEnqueuesDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pendingEnqueues = _pendingEnqueuesDrained.Task;
            }

            _disposeTask = CompleteShutdownAsync(pendingEnqueues);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task<T> QueueAndAwaitAsync<T>(
        HalconWorkItem<T> item,
        CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                if (item.IsWaiting)
                {
                    await _channel.Writer
                        .WriteAsync(item, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                item.CancelBeforeStart();
            }
            catch (ChannelClosedException)
            {
                item.RejectForShutdown();
            }
            finally
            {
                MarkEnqueueFinished();
            }

            return await item.Completion.ConfigureAwait(false);
        }
        finally
        {
            item.DisposeCancellationRegistration();
        }
    }

    private async Task CompleteShutdownAsync(Task pendingEnqueues)
    {
        await pendingEnqueues.ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
    }

    private void MarkEnqueueFinished()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleSync)
        {
            _pendingEnqueueCount--;
            if (_pendingEnqueueCount == 0 && _pendingEnqueuesDrained is not null)
            {
                drained = _pendingEnqueuesDrained;
                _pendingEnqueuesDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private void WorkerLoop(int workerIndex)
    {
        if (Thread.CurrentThread.Name is null)
        {
            Thread.CurrentThread.Name = $"HALCON Operation Worker {workerIndex + 1}";
        }

        while (_channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_channel.Reader.TryRead(out IHalconWorkItem? item))
            {
                bool execute;
                lock (_lifecycleSync)
                {
                    // Execution admission and shutdown use the same gate. Whichever wins defines
                    // whether this item is rejected or must be allowed to return safely.
                    if (_shutdownStarted)
                    {
                        item.RejectForShutdown();
                        execute = false;
                    }
                    else
                    {
                        execute = item.TryBeginExecution();
                    }
                }

                if (execute)
                {
                    item.Execute();
                }
            }
        }
    }

    private static ObjectDisposedException CreateDisposedException()
    {
        return new ObjectDisposedException(
            nameof(HalconOperationScheduler),
            "The HALCON operation scheduler is shutting down and no longer accepts work.");
    }

    private interface IHalconWorkItem
    {
        bool TryBeginExecution();

        void Execute();

        void RejectForShutdown();
    }

    private sealed class HalconWorkItem<T> : IHalconWorkItem
    {
        private const int Waiting = 0;
        private const int Running = 1;
        private const int Completed = 2;

        private readonly Func<T> _operation;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _state = Waiting;

        public HalconWorkItem(Func<T> operation, CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationToken = cancellationToken;
            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.UnsafeRegister(
                    static state => ((HalconWorkItem<T>)state!).CancelBeforeStart(),
                    this);
            }
        }

        public bool IsWaiting => Volatile.Read(ref _state) == Waiting;

        public Task<T> Completion => _completion.Task;

        public bool TryBeginExecution()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                CancelBeforeStart();
                return false;
            }

            return Interlocked.CompareExchange(ref _state, Running, Waiting) == Waiting;
        }

        public void Execute()
        {
            try
            {
                T result = _operation();
                Volatile.Write(ref _state, Completed);
                _completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _state, Completed);
                _completion.TrySetException(exception);
            }
        }

        public void CancelBeforeStart()
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Waiting) == Waiting)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
        }

        public void RejectForShutdown()
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Waiting) == Waiting)
            {
                _completion.TrySetException(CreateDisposedException());
            }
        }

        public void DisposeCancellationRegistration()
        {
            _cancellationRegistration.Dispose();
        }
    }
}
