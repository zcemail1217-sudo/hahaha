using System.Collections.Concurrent;
using System.Threading.Channels;

namespace VisionStation.Vision;

internal sealed class HalconModelLoader : IHalconModelLoader
{
    private static readonly Lazy<IHalconRejectedHandleOwner> ProcessLifetimeRejectedHandleOwner = new(
        static () => new ProcessLifetimeCleanupOwner(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IHalconOperationScheduler _scheduler;
    private readonly IHalconOperatorBackend _operators;
    private readonly IHalconRejectedHandleOwner _rejectedHandleOwner;

    public HalconModelLoader(
        IHalconOperationScheduler scheduler,
        IHalconOperatorBackend operators)
        : this(scheduler, operators, DeferredProcessLifetimeCleanupOwner.Instance)
    {
    }

    internal HalconModelLoader(
        IHalconOperationScheduler scheduler,
        IHalconOperatorBackend operators,
        IHalconRejectedHandleOwner rejectedHandleOwner)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _operators = operators ?? throw new ArgumentNullException(nameof(operators));
        _rejectedHandleOwner = rejectedHandleOwner
            ?? throw new ArgumentNullException(nameof(rejectedHandleOwner));
    }

    public async Task<IHalconModelHandle> LoadAsync(
        ValidatedHalconModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            IHalconRawModelHandle handle = await _scheduler.RunAsync(
                () => _operators.LoadShapeModelAndValidate(descriptor.ModelPath),
                cancellationToken).ConfigureAwait(false);
            if (handle is null)
            {
                throw new InvalidOperationException("The HALCON operator backend returned a null model handle.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Exception? cleanupFailure = null;
                try
                {
                    await DisposeOrTransferAsync(
                        _scheduler,
                        _rejectedHandleOwner,
                        handle).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }

                throw new OperationCanceledException(
                    "HALCON model loading was canceled after native admission.",
                    cleanupFailure,
                    cancellationToken);
            }

            return new ScheduledModelHandle(_scheduler, _rejectedHandleOwner, handle);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            string type = exception.GetType().Name;
            if (exception is HalconOperatorFailure failure && failure.InnerException is not null)
            {
                type = failure.InnerException.GetType().Name;
            }

            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ModelLoadFailed,
                    $"HALCON model readback/health check failed; ExceptionType={type}."));
        }
    }

    private static async Task DisposeOrTransferAsync(
        IHalconOperationScheduler primaryScheduler,
        IHalconRejectedHandleOwner rejectedHandleOwner,
        IHalconRawModelHandle handle)
    {
        var admitted = 0;
        try
        {
            await primaryScheduler.RunAsync(
                () =>
                {
                    Volatile.Write(ref admitted, 1);
                    handle.Dispose();
                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (Exception primaryRejection) when (Volatile.Read(ref admitted) == 0)
        {
            try
            {
                rejectedHandleOwner.TakeOwnership(handle);
                return;
            }
            catch (Exception ownershipFailure)
            {
                try
                {
                    // TakeOwnership throwing means ownership never moved. Finish the last-resort
                    // cleanup synchronously before this stack is allowed to release the reference.
                    handle.Dispose();
                    return;
                }
                catch (Exception synchronousCleanupFailure)
                {
                    throw new AggregateException(
                        "The rejected HALCON handle could not be transferred or synchronously disposed.",
                        primaryRejection,
                        ownershipFailure,
                        synchronousCleanupFailure);
                }
            }
        }
    }

    private sealed class ScheduledModelHandle(
        IHalconOperationScheduler scheduler,
        IHalconRejectedHandleOwner rejectedHandleOwner,
        IHalconRawModelHandle inner) : IHalconModelHandle
    {
        private IHalconRawModelHandle? _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public Task<T> InvokeAsync<T>(
            Func<IHalconModelBorrow, T> invocation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            cancellationToken.ThrowIfCancellationRequested();
            IHalconRawModelHandle owned = Volatile.Read(ref _inner)
                ?? throw new ObjectDisposedException(nameof(IHalconModelHandle));
            return scheduler.RunAsync(() => invocation(owned), cancellationToken);
        }

        public void Dispose()
        {
            IHalconRawModelHandle? owned = Interlocked.Exchange(ref _inner, null);
            if (owned is null)
            {
                return;
            }

            DisposeOrTransferAsync(scheduler, rejectedHandleOwner, owned)
                .GetAwaiter()
                .GetResult();
        }
    }

    /// <summary>
    /// Process-lifetime last owner. The unbounded channel is never completed and therefore never
    /// rejects. The owned-item table retains each handle and its completion until disposal ends.
    /// </summary>
    private sealed class ProcessLifetimeCleanupOwner : IHalconRejectedHandleOwner
    {
        private readonly Channel<CleanupItem> _queue = Channel.CreateUnbounded<CleanupItem>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false
            });
        private readonly ConcurrentDictionary<long, CleanupItem> _ownedItems = new();
        private readonly ConcurrentQueue<Exception> _disposalFailures = new();
        private readonly Task _worker;
        private long _nextId;

        internal ProcessLifetimeCleanupOwner()
        {
            _worker = Task.Factory.StartNew(
                WorkerLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        public void TakeOwnership(IHalconRawModelHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            long id = Interlocked.Increment(ref _nextId);
            var item = new CleanupItem(id, handle);
            if (!_ownedItems.TryAdd(id, item))
            {
                throw new InvalidOperationException("A HALCON cleanup ownership identifier was reused.");
            }

            if (_queue.Writer.TryWrite(item))
            {
                return;
            }

            _ownedItems.TryRemove(id, out _);
            throw new InvalidOperationException(
                "The process-lifetime HALCON cleanup owner unexpectedly rejected a handle.");
        }

        private void WorkerLoop()
        {
            if (Thread.CurrentThread.Name is null)
            {
                Thread.CurrentThread.Name = "HALCON Rejected Handle Cleanup Owner";
            }

            while (true)
            {
                CleanupItem item = _queue.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
                try
                {
                    item.Handle.Dispose();
                    item.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    _disposalFailures.Enqueue(exception);
                    item.Completion.TrySetException(exception);
                    _ = item.Completion.Task.Exception;
                }
                finally
                {
                    _ownedItems.TryRemove(item.Id, out _);
                }
            }
        }

        private sealed class CleanupItem(long id, IHalconRawModelHandle handle)
        {
            internal long Id { get; } = id;

            internal IHalconRawModelHandle Handle { get; } = handle;

            internal TaskCompletionSource Completion { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class DeferredProcessLifetimeCleanupOwner : IHalconRejectedHandleOwner
    {
        internal static DeferredProcessLifetimeCleanupOwner Instance { get; } = new();

        private DeferredProcessLifetimeCleanupOwner()
        {
        }

        public void TakeOwnership(IHalconRawModelHandle handle)
        {
            ProcessLifetimeRejectedHandleOwner.Value.TakeOwnership(handle);
        }
    }
}
