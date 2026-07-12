using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Application;

internal interface IInspectionExecutor
{
    Task<InspectionRunResult> ExecuteAsync(
        InspectionRequest request,
        CancellationToken cancellationToken);
}

public sealed class InspectionExecution : IInspectionExecution
{
    private const string LogSource = "InspectionExecution";

    private readonly object _syncRoot = new();
    private readonly object _changedQueueRoot = new();
    private readonly Queue<(ActiveInspectionRun? Current, long Revision)> _changedQueue = new();
    private readonly IInspectionExecutor _executor;
    private readonly IAppLogService? _log;
    private SessionState? _current;
    private long _changeRevision;
    private bool _publishingChanged;

    internal InspectionExecution(IInspectionExecutor executor, IAppLogService? log = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _log = log;
    }

    public InspectionExecution(
        ICameraDevice camera,
        IConfigurableCameraDevice configurableCamera,
        IAxisController axis,
        IPlcClient plc,
        IDeviceRuntime devices,
        DeviceConfiguration configuration,
        IDeviceConfigurationRepository configurationRepository,
        IVisionPipeline pipeline,
        IRecipeRepository recipes,
        IInspectionRecordRepository records,
        IImageTraceStore traceStore,
        IAppLogService log,
        ICommunicationChannelRuntime communicationChannels,
        IInspectionRunControl runControl)
        : this(
            new InspectionRunner(
                camera,
                configurableCamera,
                axis,
                plc,
                devices,
                configuration,
                configurationRepository,
                pipeline,
                recipes,
                records,
                traceStore,
                log,
                communicationChannels,
                runControl),
            log)
    {
    }

    public ActiveInspectionRun? Current
    {
        get
        {
            lock (_syncRoot)
            {
                return _current?.Run;
            }
        }
    }

    public event EventHandler<InspectionExecutionChangedEventArgs>? Changed;

    public event EventHandler<InspectionRunResult>? RunCompleted;

    public RunAdmission TryBegin(InspectionRunIntent intent)
    {
        ValidateIntent(intent);

        SessionState state;
        long revision;
        lock (_syncRoot)
        {
            if (_current is not null)
            {
                return new RunAdmission.Rejected(
                    new RunRejection(RunRejectionReason.Busy, _current.Run));
            }

            var run = new ActiveInspectionRun(
                Guid.NewGuid(),
                intent,
                DateTimeOffset.UtcNow);
            state = new SessionState(run);
            _current = state;
            revision = ++_changeRevision;
        }

        PublishChanged(state.Run, revision);
        return new RunAdmission.Acquired(new InspectionSession(this, state));
    }

    private static void ValidateIntent(InspectionRunIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(intent.Mode.Key) ||
            string.IsNullOrWhiteSpace(intent.Mode.DisplayName))
        {
            throw new ArgumentException("A valid run mode is required.", nameof(intent));
        }

        if (string.IsNullOrWhiteSpace(intent.EntryPoint))
        {
            throw new ArgumentException("An entry point is required.", nameof(intent));
        }
    }

    private async Task<InspectionRunResult> ExecuteAsync(
        SessionState state,
        InspectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        TaskCompletionSource executionCompletion;
        lock (_syncRoot)
        {
            if (state.DisposeRequested ||
                state.Released ||
                !ReferenceEquals(_current, state))
            {
                throw new ObjectDisposedException(nameof(InspectionSession));
            }

            if (state.IsExecuting)
            {
                throw new InvalidOperationException(
                    "Another inspection is already executing in this session.");
            }

            state.IsExecuting = true;
            executionCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            state.ExecutionCompletion = executionCompletion;
        }

        try
        {
            var result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            PublishRunCompleted(result);
            return result;
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(state.ExecutionCompletion, executionCompletion))
                {
                    state.IsExecuting = false;
                    state.ExecutionCompletion = null;
                }
            }

            executionCompletion.TrySetResult();
        }
    }

    private ValueTask DisposeAsync(SessionState state)
    {
        var startDispose = false;
        Task disposeCompletion;
        lock (_syncRoot)
        {
            state.DisposeRequested = true;
            if (!state.DisposeStarted)
            {
                state.DisposeStarted = true;
                startDispose = true;
            }

            disposeCompletion = state.DisposeCompletion.Task;
        }

        if (startDispose)
        {
            _ = DisposeCoreAsync(state);
        }

        return new ValueTask(disposeCompletion);
    }

    private async Task DisposeCoreAsync(SessionState state)
    {
        try
        {
            Task? executionCompletion;
            lock (_syncRoot)
            {
                executionCompletion = state.ExecutionCompletion?.Task;
            }

            if (executionCompletion is not null)
            {
                await executionCompletion.ConfigureAwait(false);
            }

            long? revision = null;
            lock (_syncRoot)
            {
                if (!state.Released)
                {
                    state.Released = true;
                    if (ReferenceEquals(_current, state))
                    {
                        _current = null;
                        revision = ++_changeRevision;
                    }
                }
            }

            if (revision is not null)
            {
                PublishChanged(null, revision.Value);
            }
        }
        finally
        {
            state.DisposeCompletion.TrySetResult();
        }
    }

    private void PublishChanged(ActiveInspectionRun? current, long revision)
    {
        lock (_changedQueueRoot)
        {
            _changedQueue.Enqueue((current, revision));
            if (_publishingChanged)
            {
                return;
            }

            _publishingChanged = true;
        }

        while (true)
        {
            (ActiveInspectionRun? Current, long Revision) item;
            lock (_changedQueueRoot)
            {
                if (_changedQueue.Count == 0)
                {
                    _publishingChanged = false;
                    return;
                }

                item = _changedQueue.Dequeue();
            }

            PublishChangedItem(item.Current, item.Revision);
        }
    }

    private void PublishChangedItem(ActiveInspectionRun? current, long revision)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var args = new InspectionExecutionChangedEventArgs(current);
        foreach (EventHandler<InspectionExecutionChangedEventArgs> handler in handlers.GetInvocationList())
        {
            lock (_syncRoot)
            {
                if (revision != _changeRevision)
                {
                    return;
                }
            }

            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                LogSubscriberFailure(nameof(Changed), exception);
            }
        }
    }

    private void PublishRunCompleted(InspectionRunResult result)
    {
        var handlers = RunCompleted;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<InspectionRunResult> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, result);
            }
            catch (Exception exception)
            {
                LogSubscriberFailure(nameof(RunCompleted), exception);
            }
        }
    }

    private void LogSubscriberFailure(string eventName, Exception exception)
    {
        try
        {
            _log?.Warning(
                LogSource,
                $"{eventName} event subscriber failure: {exception.Message}");
        }
        catch
        {
            // Logging failures must not interrupt execution or event delivery.
        }
    }

    private sealed class InspectionSession : IInspectionSession
    {
        private readonly InspectionExecution _owner;
        private readonly SessionState _state;

        public InspectionSession(InspectionExecution owner, SessionState state)
        {
            _owner = owner;
            _state = state;
        }

        public ActiveInspectionRun Run => _state.Run;

        public Task<InspectionRunResult> ExecuteAsync(
            InspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _owner.ExecuteAsync(_state, request, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return _owner.DisposeAsync(_state);
        }
    }

    private sealed class SessionState
    {
        public SessionState(ActiveInspectionRun run)
        {
            Run = run;
        }

        public ActiveInspectionRun Run { get; }

        public bool IsExecuting { get; set; }

        public bool DisposeRequested { get; set; }

        public bool DisposeStarted { get; set; }

        public bool Released { get; set; }

        public TaskCompletionSource? ExecutionCompletion { get; set; }

        public TaskCompletionSource DisposeCompletion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
