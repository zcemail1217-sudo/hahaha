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
    DateTimeOffset UpdatedAt);

public sealed class ProductionCoordinator
{
    private readonly IInspectionRunner _runner;
    private readonly IInspectionRunLifetime _runLifetime;
    private readonly ICameraDevice _camera;
    private readonly IPlcClient _plc;
    private readonly IAxisController _axis;
    private readonly IAppLogService _log;
    private readonly IAlarmService _alarms;
    private readonly ICommunicationChannelRuntime _communicationChannels;
    private readonly ProductionSettingsConfiguration _productionSettings;
    private readonly object _syncRoot = new();
    private readonly object _lifecycleSyncRoot = new();
    private readonly SemaphoreSlim _startTransitionGate = new(1, 1);

    private CancellationTokenSource? _startupCancellation;
    private Task? _startTransitionTask;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private Task? _stopTask;
    private long _stopVersion;
    private ProductionSnapshot _snapshot = new(ProductionState.Stopped, 0, 0, 0, 100, TimeSpan.Zero, DateTimeOffset.Now);

    public ProductionCoordinator(
        IInspectionRunner runner,
        IInspectionRunLifetime runLifetime,
        ICameraDevice camera,
        IPlcClient plc,
        IAxisController axis,
        IAppLogService log,
        IAlarmService alarms,
        ICommunicationChannelRuntime communicationChannels,
        DeviceConfiguration configuration)
    {
        _runner = runner;
        _runLifetime = runLifetime;
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

    internal IInspectionRunLifetime RunLifetime => _runLifetime;

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
        await _camera.ConnectAsync(cancellationToken);
        await _plc.ConnectAsync(cancellationToken);
        await _axis.ConnectAsync(cancellationToken);
        _log.Info("Production", "设备模拟层初始化完成");
    }

    public Task<InspectionRunResult> RunSingleAsync(CancellationToken cancellationToken = default)
    {
        return _runLifetime.RunTrackedAsync(RunSingleCoreAsync, cancellationToken);
    }

    private async Task<InspectionRunResult> RunSingleCoreAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _communicationChannels.ConnectAsync(CommunicationChannelConnectionPolicies.Production, cancellationToken);
        await _plc.SetInspectionBusyAsync(true, cancellationToken);

        try
        {
            var request = new InspectionRequest
            {
                RecipeId = string.Empty,
                BatchId = DateTimeOffset.Now.ToString("yyyyMMdd"),
                OperatorName = Environment.UserName,
                TriggeredByPlc = false
            };
            var result = await _runner.RunAsync(request, cancellationToken);
            await _plc.WriteInspectionResultAsync(result.Result, cancellationToken);
            ApplyResult(result.Result);
            InspectionCompleted?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetState(ProductionState.Faulted);
            _log.Error("Production", ex.Message);
            _alarms.Raise(
                AlarmSeverity.Error,
                "Production",
                $"Single inspection failed: {ex.Message}",
                ex.ToString(),
                "production:single-run");
            throw;
        }
        finally
        {
            await ClearInspectionBusyAsync();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _runLifetime.RunTrackedAsync(
            async runCancellationToken =>
            {
                await StartCoreAsync(runCancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        long admissionStopVersion;
        lock (_lifecycleSyncRoot)
        {
            admissionStopVersion = _stopVersion;
        }

        await _startTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource? transitionCompletion = null;
        try
        {
            lock (_lifecycleSyncRoot)
            {
                if (admissionStopVersion != _stopVersion)
                {
                    return;
                }

                transitionCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _startTransitionTask = transitionCompletion.Task;
            }

            await StartSerializedCoreAsync(
                    admissionStopVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (transitionCompletion is not null)
            {
                transitionCompletion.TrySetResult();
                lock (_lifecycleSyncRoot)
                {
                    if (ReferenceEquals(_startTransitionTask, transitionCompletion.Task))
                    {
                        _startTransitionTask = null;
                    }
                }
            }

            _startTransitionGate.Release();
        }
    }

    private async Task StartSerializedCoreAsync(
        long observedStopVersion,
        CancellationToken cancellationToken)
    {
        var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            CancellationToken.None);
        CancellationTokenSource? completedLoopCancellation = null;

        lock (_lifecycleSyncRoot)
        {
            if (observedStopVersion != _stopVersion ||
                _stopTask is { IsCompleted: false } ||
                _loopTask is { IsCompleted: false })
            {
                startupCancellation.Dispose();
                return;
            }

            if (_loopTask is { IsCompleted: true })
            {
                completedLoopCancellation = _loopCancellation;
                _loopCancellation = null;
                _loopTask = null;
            }
            _startupCancellation = startupCancellation;
        }

        completedLoopCancellation?.Dispose();

        var productionConnectAttempted = false;
        CancellationTokenSource? loopCancellation = null;
        Task? loopTask = null;

        try
        {
            await InitializeAsync(startupCancellation.Token).ConfigureAwait(false);
            productionConnectAttempted = true;
            await _communicationChannels.ConnectAsync(
                CommunicationChannelConnectionPolicies.Production,
                startupCancellation.Token).ConfigureAwait(false);
            startupCancellation.Token.ThrowIfCancellationRequested();
            loopCancellation = new CancellationTokenSource();
            SetState(ProductionState.Running);
            _log.Info("Production", "连续生产启动");
            startupCancellation.Token.ThrowIfCancellationRequested();

            var loopToken = loopCancellation.Token;
            lock (_lifecycleSyncRoot)
            {
                var stopSupersededStartup = observedStopVersion != _stopVersion ||
                                            _stopTask is { IsCompleted: false };
                if (!stopSupersededStartup && !startupCancellation.IsCancellationRequested)
                {
                    loopTask = Task.Run(
                        () => RunProductionLoopAsync(loopToken),
                        loopToken);
                    _loopCancellation = loopCancellation;
                    _loopTask = loopTask;
                }

            }

            if (loopTask is null)
            {
                throw new OperationCanceledException(
                    "Continuous production startup was superseded by a stop request.",
                    startupCancellation.Token);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception startupException)
        {
            IReadOnlyList<Exception> rollbackFailures =
                await RollbackFailedStartAsync(
                        productionConnectAttempted,
                        loopCancellation,
                        loopTask)
                    .ConfigureAwait(false);
            if (rollbackFailures.Count > 0)
            {
                throw new AggregateException(
                    "Continuous production startup failed and rollback was incomplete.",
                    new[] { startupException }.Concat(rollbackFailures));
            }

            if (startupException is OperationCanceledException &&
                !cancellationToken.IsCancellationRequested &&
                WasStopRequestedAfter(observedStopVersion))
            {
                return;
            }

            throw;
        }
        finally
        {
            lock (_lifecycleSyncRoot)
            {
                if (ReferenceEquals(_startupCancellation, startupCancellation))
                {
                    _startupCancellation = null;
                }
            }

            startupCancellation.Dispose();
        }
    }

    public Task StopAsync()
    {
        return StopCoreAsync(disconnectProduction: true);
    }

    internal Task StopForShutdownAsync()
    {
        return StopCoreAsync(disconnectProduction: false);
    }

    private Task StopCoreAsync(bool disconnectProduction)
    {
        TaskCompletionSource completion;
        CancellationTokenSource? startupCancellation;
        Task? startTransitionTask;
        CancellationTokenSource? loopCancellation;
        Task? loopTask;

        lock (_lifecycleSyncRoot)
        {
            _stopVersion++;
            if (_stopTask is { IsCompleted: false })
            {
                return _stopTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            startupCancellation = _startupCancellation;
            startTransitionTask = _startTransitionTask;
            loopCancellation = _loopCancellation;
            loopTask = _loopTask;
        }

        _ = CompleteStopAsync(
            disconnectProduction,
            startupCancellation,
            disconnectProduction ? startTransitionTask : null,
            loopCancellation,
            loopTask,
            completion);
        return completion.Task;
    }

    private async Task CompleteStopAsync(
        bool disconnectProduction,
        CancellationTokenSource? startupCancellation,
        Task? startTransitionTask,
        CancellationTokenSource? loopCancellation,
        Task? loopTask,
        TaskCompletionSource completion)
    {
        Exception? completionFailure = null;
        CancellationToken? completionCancellation = null;
        try
        {
            var failures = new List<Exception>();
            TryCancel(startupCancellation, failures);
            TryCancel(loopCancellation, failures);

            if (startTransitionTask is not null)
            {
                try
                {
                    await startTransitionTask.ConfigureAwait(false);
                }
                catch
                {
                    // The StartAsync caller observes startup failure; StopAsync only waits for settlement.
                }
            }

            if (loopTask is not null)
            {
                try
                {
                    await loopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (loopCancellation?.IsCancellationRequested == true)
                {
                    // The loop is expected to cancel during stop.
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                SetState(ProductionState.Stopped);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (disconnectProduction && !_runLifetime.IsShutdownRequested)
            {
                try
                {
                    await DisconnectProductionAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                _log.Info("Production", "连续生产停止");
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("Continuous production stop completed with failures.", failures);
            }
        }
        catch (OperationCanceledException exception)
        {
            completionCancellation = exception.CancellationToken;
        }
        catch (Exception exception)
        {
            completionFailure = exception;
        }
        finally
        {
            lock (_lifecycleSyncRoot)
            {
                if (ReferenceEquals(_startupCancellation, startupCancellation))
                {
                    _startupCancellation = null;
                }

                if (ReferenceEquals(_loopCancellation, loopCancellation) &&
                    ReferenceEquals(_loopTask, loopTask))
                {
                    _loopCancellation = null;
                    _loopTask = null;
                }
            }

            loopCancellation?.Dispose();
        }

        if (completionCancellation is CancellationToken cancellationToken)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        else if (completionFailure is not null)
        {
            completion.TrySetException(completionFailure);
        }
        else
        {
            completion.TrySetResult();
        }
    }

    private async Task RunProductionLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunSingleAsync(cancellationToken).ConfigureAwait(false);
                    consecutiveFailures = 0;
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(_productionSettings.CycleDelayMs),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SetState(ProductionState.Faulted);
                    _log.Error("Production", $"连续生产异常：{exception.Message}");
                    _alarms.Raise(
                        AlarmSeverity.Critical,
                        "Production",
                        $"Continuous production stopped: {exception.Message}",
                        exception.ToString(),
                        "production:continuous-run");
                    consecutiveFailures++;
                    if (_productionSettings.AutoStopOnAlarm ||
                        consecutiveFailures >= _productionSettings.MaxConsecutiveFailures)
                    {
                        break;
                    }

                    SetState(ProductionState.Running);
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(_productionSettings.CycleDelayMs),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (!_runLifetime.IsShutdownRequested)
            {
                await DisconnectProductionAsync().ConfigureAwait(false);
            }
        }
    }

    private bool WasStopRequestedAfter(long observedStopVersion)
    {
        lock (_lifecycleSyncRoot)
        {
            return observedStopVersion != _stopVersion;
        }
    }

    private static void TryCancel(
        CancellationTokenSource? cancellation,
        ICollection<Exception> failures)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owner completed between capture and cancellation; no work remains to cancel.
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async Task<IReadOnlyList<Exception>> RollbackFailedStartAsync(
        bool productionConnectAttempted,
        CancellationTokenSource? loopCancellation,
        Task? loopTask)
    {
        var failures = new List<Exception>();
        try
        {
            loopCancellation?.Cancel();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (loopCancellation?.IsCancellationRequested == true)
            {
                // Startup rollback requested the loop cancellation.
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (loopTask is null)
        {
            loopCancellation?.Dispose();
        }
        else
        {
            var stopOwnsLoopCancellation = false;
            lock (_lifecycleSyncRoot)
            {
                stopOwnsLoopCancellation = _stopTask is { IsCompleted: false } &&
                                           ReferenceEquals(_loopCancellation, loopCancellation) &&
                                           ReferenceEquals(_loopTask, loopTask);
                if (ReferenceEquals(_loopCancellation, loopCancellation) &&
                    ReferenceEquals(_loopTask, loopTask))
                {
                    _loopCancellation = null;
                    _loopTask = null;
                }
            }

            if (!stopOwnsLoopCancellation)
            {
                loopCancellation?.Dispose();
            }
        }

        try
        {
            SetState(ProductionState.Stopped);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (productionConnectAttempted &&
            (loopTask is null || loopTask.IsCanceled) &&
            !_runLifetime.IsShutdownRequested)
        {
            try
            {
                await DisconnectProductionAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private async Task ClearInspectionBusyAsync()
    {
        try
        {
            using var cleanup = CreateCleanupCancellation();
            await _plc.SetInspectionBusyAsync(false, cleanup.Token);
        }
        catch (Exception ex)
        {
            _log.Warning("Production", $"Failed to clear inspection busy flag: {ex.Message}");
        }
    }

    private async Task DisconnectProductionAsync()
    {
        using var cleanup = CreateCleanupCancellation();
        await _communicationChannels.DisconnectAsync(CommunicationChannelConnectionPolicies.Production, cleanup.Token);
    }

    private CancellationTokenSource CreateCleanupCancellation()
    {
        return new CancellationTokenSource(TimeSpan.FromMilliseconds(_productionSettings.CleanupTimeoutMs));
    }

    private void ApplyResult(InspectionResult result)
    {
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
        }

        SnapshotChanged?.Invoke(this, Snapshot);
    }

    private void SetState(ProductionState state)
    {
        lock (_syncRoot)
        {
            _snapshot = _snapshot with
            {
                State = state,
                UpdatedAt = DateTimeOffset.Now
            };
        }

        SnapshotChanged?.Invoke(this, Snapshot);
    }

    private void OnDeviceStateChanged(object? sender, DeviceSnapshot snapshot)
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

        DeviceStateChanged?.Invoke(this, snapshot);
    }
}
