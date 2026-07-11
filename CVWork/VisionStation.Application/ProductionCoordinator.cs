using VisionStation.Devices;
using VisionStation.Domain;

namespace VisionStation.Application;

public sealed record ProductionSnapshot(
    ProductionState State,
    int TotalCount,
    int OkCount,
    int NgCount,
    double YieldRate,
    TimeSpan LastCycleTime,
    DateTimeOffset UpdatedAt,
    Guid? ActiveSessionId = null);

public sealed class ProductionCoordinator
{
    private const string LogSource = "Production";

    private readonly IInspectionExecution _inspectionExecution;
    private readonly ICameraDevice _camera;
    private readonly IPlcClient _plc;
    private readonly IAxisController _axis;
    private readonly IAppLogService _log;
    private readonly IAlarmService _alarms;
    private readonly ICommunicationChannelRuntime _communicationChannels;
    private readonly ProductionSettingsConfiguration _productionSettings;
    private readonly object _syncRoot = new();
    private readonly object _snapshotQueueRoot = new();
    private readonly SortedDictionary<long, SnapshotUpdate> _snapshotQueue = [];

    private ActiveProductionOperation? _activeOperation;
    private ProductionSnapshot _snapshot = new(
        ProductionState.Stopped,
        0,
        0,
        0,
        100,
        TimeSpan.Zero,
        DateTimeOffset.Now);
    private long _snapshotRevision;
    private long _nextSnapshotRevisionToPublish = 1;
    private bool _publishingSnapshots;

    public ProductionCoordinator(
        IInspectionExecution inspectionExecution,
        ICameraDevice camera,
        IPlcClient plc,
        IAxisController axis,
        IAppLogService log,
        IAlarmService alarms,
        ICommunicationChannelRuntime communicationChannels,
        DeviceConfiguration configuration)
    {
        _inspectionExecution = inspectionExecution;
        _camera = camera;
        _plc = plc;
        _axis = axis;
        _log = log;
        _alarms = alarms;
        _communicationChannels = communicationChannels;
        _productionSettings = configuration.SystemSettings.Production;

        _camera.StateChanged += OnDeviceStateChanged;
        _plc.StateChanged += OnDeviceStateChanged;
        _axis.StateChanged += OnDeviceStateChanged;
    }

    public event EventHandler<ProductionSnapshot>? SnapshotChanged;

    public event EventHandler<InspectionRunResult>? InspectionCompleted;

    public event EventHandler<DeviceSnapshot>? DeviceStateChanged;

    public ProductionSnapshot Snapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _snapshot;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ConnectDevicesAsync(cancellationToken).ConfigureAwait(false);
        LogInfoSafely("Production devices initialized.");
    }

    public async Task<ProductionCommandResult<InspectionRunResult>> RunSingleAsync(
        CancellationToken cancellationToken = default)
    {
        var intent = new InspectionRunIntent(
            InspectionRunModes.ManualSingle,
            nameof(ProductionCoordinator));
        var operation = TryReserveOperation(intent, cancellationToken, out var localRejection);
        if (operation is null)
        {
            return new ProductionCommandResult<InspectionRunResult>(
                ProductionCommandDisposition.Rejected,
                Rejection: localRejection);
        }

        var finalFaulted = false;
        try
        {
            var admission = await AcquireSessionAsync(operation).ConfigureAwait(false);
            if (admission is RunAdmission.Rejected rejected)
            {
                return new ProductionCommandResult<InspectionRunResult>(
                    ProductionCommandDisposition.Rejected,
                    Rejection: rejected.Rejection);
            }

            var session = ((RunAdmission.Acquired)admission).Session;
            await InitializeProductionAsync(operation.Token).ConfigureAwait(false);
            if (!TryTransitionToRunning(operation))
            {
                return new ProductionCommandResult<InspectionRunResult>(
                    ProductionCommandDisposition.Canceled);
            }

            var result = await RunSingleCoreAsync(session, operation.Token).ConfigureAwait(false);
            return new ProductionCommandResult<InspectionRunResult>(
                ProductionCommandDisposition.Completed,
                result);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return new ProductionCommandResult<InspectionRunResult>(
                ProductionCommandDisposition.Canceled);
        }
        catch (Exception exception)
        {
            finalFaulted = true;
            RaiseSingleRunFailure(exception);
            throw;
        }
        finally
        {
            await CompleteOperationAsync(operation, finalFaulted).ConfigureAwait(false);
        }
    }

    public async Task<ProductionCommandResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var intent = new InspectionRunIntent(
            InspectionRunModes.Continuous,
            nameof(ProductionCoordinator));
        var operation = TryReserveOperation(intent, cancellationToken, out var localRejection);
        if (operation is null)
        {
            return new ProductionCommandResult(
                ProductionCommandDisposition.Rejected,
                localRejection);
        }

        try
        {
            var admission = await AcquireSessionAsync(operation).ConfigureAwait(false);
            if (admission is RunAdmission.Rejected rejected)
            {
                return new ProductionCommandResult(
                    ProductionCommandDisposition.Rejected,
                    MapStartRejection(rejected.Rejection));
            }

            await InitializeProductionAsync(operation.Token).ConfigureAwait(false);
            if (!TryTransitionToRunning(operation))
            {
                await CompleteOperationAsync(operation, false).ConfigureAwait(false);
                return new ProductionCommandResult(ProductionCommandDisposition.Canceled);
            }

            _ = Task.Run(
                () => RunContinuousAsync(operation),
                CancellationToken.None);
            LogInfoSafely("Continuous production started.");
            return new ProductionCommandResult(ProductionCommandDisposition.Completed);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            await CompleteOperationAsync(operation, false).ConfigureAwait(false);
            return new ProductionCommandResult(ProductionCommandDisposition.Canceled);
        }
        catch (Exception exception)
        {
            RaiseContinuousFailure(exception);
            await CompleteOperationAsync(operation, true).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ProductionCommandResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        ActiveProductionOperation? operation;
        lock (_syncRoot)
        {
            operation = _activeOperation;
        }

        if (operation is null)
        {
            var current = _inspectionExecution.Current;
            return current is null
                ? new ProductionCommandResult(ProductionCommandDisposition.NoOp)
                : new ProductionCommandResult(
                    ProductionCommandDisposition.Rejected,
                    new RunRejection(RunRejectionReason.NotOwner, current));
        }

        PublishSnapshot(TryCommitStopping(operation));
        operation.RequestCancellation();
        await operation.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        LogInfoSafely("Continuous production stopped.");
        return new ProductionCommandResult(ProductionCommandDisposition.Completed);
    }

    private ActiveProductionOperation? TryReserveOperation(
        InspectionRunIntent intent,
        CancellationToken callerToken,
        out RunRejection? rejection)
    {
        lock (_syncRoot)
        {
            if (_activeOperation is not null)
            {
                var reason = IsCoordinatorContinuous(intent) &&
                             IsCoordinatorContinuous(_activeOperation.ActiveRun.Intent)
                    ? RunRejectionReason.AlreadyRunning
                    : RunRejectionReason.Busy;
                rejection = new RunRejection(reason, _activeOperation.ActiveRun);
                return null;
            }

            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            var operation = new ActiveProductionOperation(intent, linkedCancellation);
            _activeOperation = operation;
            rejection = null;
            return operation;
        }
    }

    private async Task<RunAdmission> AcquireSessionAsync(ActiveProductionOperation operation)
    {
        var admission = _inspectionExecution.TryBegin(operation.Intent);
        if (admission is RunAdmission.Rejected)
        {
            CompleteRejectedReservation(operation);
            return admission;
        }

        var acquired = (RunAdmission.Acquired)admission;
        SnapshotUpdate? update = null;
        var attached = false;
        lock (_syncRoot)
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                operation.AttachSession(acquired.Session);
                update = CommitStateLocked(
                    operation.StopTimedOut ? ProductionState.Faulted : ProductionState.Starting,
                    acquired.Session.Run.SessionId);
                attached = true;
            }
        }

        if (!attached)
        {
            try
            {
                await acquired.Session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportCleanupFailureSafely(exception);
            }

            CompleteRejectedReservation(operation);
            throw new InvalidOperationException(
                "Production reservation was lost before inspection ownership could be attached.");
        }

        PublishSnapshot(update);
        return admission;
    }

    private void CompleteRejectedReservation(ActiveProductionOperation operation)
    {
        if (!operation.TryBeginCompletion())
        {
            return;
        }

        SnapshotUpdate? update = null;
        lock (_syncRoot)
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation = null;
                if (operation.StopTimedOut)
                {
                    update = CommitStateLocked(ProductionState.Faulted, null);
                }
            }
        }

        try
        {
            PublishSnapshot(update);
        }
        finally
        {
            operation.DisposeCancellation();
            operation.CompletionSource.TrySetResult();
        }
    }

    private async Task ConnectDevicesAsync(CancellationToken cancellationToken)
    {
        await _camera.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await _plc.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await _axis.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeProductionAsync(CancellationToken cancellationToken)
    {
        await ConnectDevicesAsync(cancellationToken).ConfigureAwait(false);
        await _communicationChannels
            .ConnectAsync(CommunicationChannelConnectionPolicies.Production, cancellationToken)
            .ConfigureAwait(false);
        LogInfoSafely("Production runtime initialized.");
    }

    private async Task<InspectionRunResult> RunSingleCoreAsync(
        IInspectionSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            await _plc.SetInspectionBusyAsync(true, cancellationToken).ConfigureAwait(false);
            var request = new InspectionRequest
            {
                RecipeId = string.Empty,
                BatchId = DateTimeOffset.Now.ToString("yyyyMMdd"),
                OperatorName = Environment.UserName,
                TriggeredByPlc = false
            };
            var result = await session.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            await _plc.WriteInspectionResultAsync(result.Result, cancellationToken).ConfigureAwait(false);
            ApplyResult(result.Result);
            PublishInspectionCompleted(result);
            return result;
        }
        finally
        {
            await ClearInspectionBusyAsync().ConfigureAwait(false);
        }
    }

    private async Task RunContinuousAsync(ActiveProductionOperation operation)
    {
        var finalFaulted = false;
        var consecutiveFailures = 0;
        try
        {
            var session = operation.Session ?? throw new InvalidOperationException(
                "Continuous production started without an inspection session.");
            while (!operation.Token.IsCancellationRequested)
            {
                try
                {
                    await RunSingleCoreAsync(session, operation.Token).ConfigureAwait(false);
                    consecutiveFailures = 0;
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(_productionSettings.CycleDelayMs),
                            operation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    RaiseContinuousFailure(exception);
                    consecutiveFailures++;
                    if (_productionSettings.AutoStopOnAlarm ||
                        consecutiveFailures >= _productionSettings.MaxConsecutiveFailures)
                    {
                        finalFaulted = true;
                        break;
                    }

                    await Task.Delay(
                            TimeSpan.FromMilliseconds(_productionSettings.CycleDelayMs),
                            operation.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            finalFaulted = true;
            RaiseContinuousFailure(exception);
        }
        finally
        {
            await CompleteOperationAsync(operation, finalFaulted).ConfigureAwait(false);
        }
    }

    private async Task CompleteOperationAsync(
        ActiveProductionOperation operation,
        bool finalFaulted)
    {
        if (!operation.TryBeginCompletion())
        {
            await operation.Completion.ConfigureAwait(false);
            return;
        }

        try
        {
            PublishSnapshot(TryCommitStopping(operation));
            try
            {
                await DisconnectProductionSafelyAsync().ConfigureAwait(false);
                if (operation.Session is not null)
                {
                    await operation.Session.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                finalFaulted = true;
                ReportCleanupFailureSafely(exception);
            }
        }
        finally
        {
            try
            {
                SnapshotUpdate? update = null;
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_activeOperation, operation))
                    {
                        _activeOperation = null;
                        var finalState = operation.StopTimedOut || finalFaulted
                            ? ProductionState.Faulted
                            : ProductionState.Stopped;
                        update = CommitStateLocked(finalState, null);
                    }
                }

                PublishSnapshot(update);
            }
            finally
            {
                operation.DisposeCancellation();
                operation.CompletionSource.TrySetResult();
            }
        }
    }

    private SnapshotUpdate? TryCommitStopping(ActiveProductionOperation operation)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_activeOperation, operation) ||
                operation.StopTimedOut ||
                _snapshot.State is not (ProductionState.Starting or
                    ProductionState.Running or
                    ProductionState.Paused))
            {
                return null;
            }

            var sessionId = operation.Session?.Run.SessionId ?? _snapshot.ActiveSessionId;
            return CommitStateLocked(ProductionState.Stopping, sessionId);
        }
    }

    private bool TryTransitionToRunning(ActiveProductionOperation operation)
    {
        SnapshotUpdate? update = null;
        lock (_syncRoot)
        {
            if (ReferenceEquals(_activeOperation, operation) &&
                operation.Session is not null &&
                !operation.Token.IsCancellationRequested &&
                _snapshot.State == ProductionState.Starting)
            {
                update = CommitStateLocked(
                    ProductionState.Running,
                    operation.Session.Run.SessionId);
            }
        }

        PublishSnapshot(update);
        return update is not null;
    }

    private SnapshotUpdate CommitStateLocked(ProductionState state, Guid? activeSessionId)
    {
        _snapshot = _snapshot with
        {
            State = state,
            ActiveSessionId = activeSessionId,
            UpdatedAt = DateTimeOffset.Now
        };
        return new SnapshotUpdate(_snapshot, ++_snapshotRevision);
    }

    private void ApplyResult(InspectionResult result)
    {
        SnapshotUpdate update;
        lock (_syncRoot)
        {
            var total = _snapshot.TotalCount + 1;
            var ok = _snapshot.OkCount + (result.Outcome == InspectionOutcome.Ok ? 1 : 0);
            var ng = _snapshot.NgCount + (result.Outcome == InspectionOutcome.Ng ? 1 : 0);
            _snapshot = _snapshot with
            {
                TotalCount = total,
                OkCount = ok,
                NgCount = ng,
                YieldRate = total == 0 ? 100 : ok * 100.0 / total,
                LastCycleTime = result.CycleTime,
                UpdatedAt = DateTimeOffset.Now
            };
            update = new SnapshotUpdate(_snapshot, ++_snapshotRevision);
        }

        PublishSnapshot(update);
    }

    private void PublishSnapshot(SnapshotUpdate? update)
    {
        if (update is null)
        {
            return;
        }

        lock (_snapshotQueueRoot)
        {
            _snapshotQueue.Add(update.Value.Revision, update.Value);
            if (_publishingSnapshots)
            {
                return;
            }

            _publishingSnapshots = true;
        }

        while (true)
        {
            SnapshotUpdate next;
            lock (_snapshotQueueRoot)
            {
                if (!_snapshotQueue.TryGetValue(_nextSnapshotRevisionToPublish, out next))
                {
                    _publishingSnapshots = false;
                    return;
                }

                _snapshotQueue.Remove(_nextSnapshotRevisionToPublish);
                _nextSnapshotRevisionToPublish++;
            }

            PublishSafely(SnapshotChanged, next.Snapshot, nameof(SnapshotChanged));
        }
    }

    private void PublishInspectionCompleted(InspectionRunResult result)
    {
        PublishSafely(InspectionCompleted, result, nameof(InspectionCompleted));
    }

    private void OnDeviceStateChanged(object? sender, DeviceSnapshot snapshot)
    {
        try
        {
            var alarmId = $"device:{snapshot.Name}";
            switch (snapshot.State)
            {
                case DeviceConnectionState.Faulted:
                    _alarms.Raise(
                        AlarmSeverity.Critical,
                        snapshot.Name,
                        snapshot.Message,
                        $"Device {snapshot.Name} is faulted.",
                        alarmId);
                    break;
                case DeviceConnectionState.Disconnected:
                    _alarms.Raise(
                        AlarmSeverity.Warning,
                        snapshot.Name,
                        snapshot.Message,
                        $"Device {snapshot.Name} is disconnected.",
                        alarmId);
                    break;
                case DeviceConnectionState.Connected:
                    _alarms.Clear(alarmId);
                    break;
            }
        }
        catch (Exception exception)
        {
            LogWarningSafely($"Device alarm update failed: {exception.Message}");
        }

        PublishSafely(DeviceStateChanged, snapshot, nameof(DeviceStateChanged));
    }

    private async Task ClearInspectionBusyAsync()
    {
        try
        {
            using var cleanup = CreateCleanupCancellation();
            await _plc.SetInspectionBusyAsync(false, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningSafely($"Failed to clear inspection busy flag: {exception.Message}");
        }
    }

    private async Task DisconnectProductionSafelyAsync()
    {
        try
        {
            using var cleanup = CreateCleanupCancellation();
            await _communicationChannels
                .DisconnectAsync(CommunicationChannelConnectionPolicies.Production, cleanup.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningSafely($"Failed to disconnect production channels: {exception.Message}");
        }
    }

    private CancellationTokenSource CreateCleanupCancellation()
    {
        return new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_productionSettings.CleanupTimeoutMs));
    }

    private void RaiseSingleRunFailure(Exception exception)
    {
        LogErrorSafely(exception.Message);
        RaiseAlarmSafely(
            AlarmSeverity.Error,
            $"Single inspection failed: {exception.Message}",
            exception.ToString(),
            "production:single-run");
    }

    private void RaiseContinuousFailure(Exception exception)
    {
        LogErrorSafely($"Continuous production failure: {exception.Message}");
        RaiseAlarmSafely(
            AlarmSeverity.Critical,
            $"Continuous production stopped: {exception.Message}",
            exception.ToString(),
            "production:continuous-run");
    }

    private void ReportCleanupFailureSafely(Exception exception)
    {
        try
        {
            _log.Critical(LogSource, $"Production cleanup failed: {exception.Message}");
        }
        catch
        {
        }

        RaiseAlarmSafely(
            AlarmSeverity.Critical,
            $"Production cleanup failed: {exception.Message}",
            exception.ToString(),
            "production:cleanup");
    }

    private void RaiseAlarmSafely(
        AlarmSeverity severity,
        string message,
        string details,
        string alarmId)
    {
        try
        {
            _alarms.Raise(severity, LogSource, message, details, alarmId);
        }
        catch
        {
        }
    }

    private void LogInfoSafely(string message)
    {
        try
        {
            _log.Info(LogSource, message);
        }
        catch
        {
        }
    }

    private void LogWarningSafely(string message)
    {
        try
        {
            _log.Warning(LogSource, message);
        }
        catch
        {
        }
    }

    private void LogErrorSafely(string message)
    {
        try
        {
            _log.Error(LogSource, message);
        }
        catch
        {
        }
    }

    private void PublishSafely<T>(
        EventHandler<T>? handlers,
        T value,
        string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, value);
            }
            catch (Exception exception)
            {
                LogWarningSafely(
                    $"{eventName} event subscriber failure: {exception.Message}");
            }
        }
    }

    private static RunRejection MapStartRejection(RunRejection rejection)
    {
        return IsCoordinatorContinuous(rejection.Active.Intent)
            ? rejection with { Reason = RunRejectionReason.AlreadyRunning }
            : rejection;
    }

    private static bool IsCoordinatorContinuous(InspectionRunIntent intent)
    {
        return string.Equals(
                   intent.Mode.Key,
                   InspectionRunModes.Continuous.Key,
                   StringComparison.Ordinal) &&
               string.Equals(
                   intent.EntryPoint,
                   nameof(ProductionCoordinator),
                   StringComparison.Ordinal);
    }

    private readonly record struct SnapshotUpdate(
        ProductionSnapshot Snapshot,
        long Revision);

    private sealed class ActiveProductionOperation
    {
        private readonly object _cancellationRoot = new();
        private readonly CancellationTokenSource _linkedCancellation;
        private int _completionStarted;
        private bool _cancellationRequested;
        private bool _cancellationDisposed;

        public ActiveProductionOperation(
            InspectionRunIntent intent,
            CancellationTokenSource linkedCancellation)
        {
            Intent = intent;
            _linkedCancellation = linkedCancellation;
            Token = linkedCancellation.Token;
            ReservationRun = new ActiveInspectionRun(
                Guid.NewGuid(),
                intent,
                DateTimeOffset.UtcNow);
        }

        public InspectionRunIntent Intent { get; }

        public ActiveInspectionRun ReservationRun { get; }

        public CancellationToken Token { get; }

        public IInspectionSession? Session { get; private set; }

        public ActiveInspectionRun ActiveRun => Session?.Run ?? ReservationRun;

        public TaskCompletionSource CompletionSource { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => CompletionSource.Task;

        public bool StopTimedOut { get; set; }

        public void AttachSession(IInspectionSession session)
        {
            if (Session is not null)
            {
                throw new InvalidOperationException(
                    "An inspection session is already attached to this production operation.");
            }

            Session = session;
        }

        public bool TryBeginCompletion()
        {
            return Interlocked.CompareExchange(ref _completionStarted, 1, 0) == 0;
        }

        public void RequestCancellation()
        {
            lock (_cancellationRoot)
            {
                if (_cancellationRequested || _cancellationDisposed)
                {
                    return;
                }

                _cancellationRequested = true;
                try
                {
                    _linkedCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (AggregateException)
                {
                }
            }
        }

        public void DisposeCancellation()
        {
            lock (_cancellationRoot)
            {
                if (_cancellationDisposed)
                {
                    return;
                }

                _cancellationDisposed = true;
                try
                {
                    _linkedCancellation.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}
