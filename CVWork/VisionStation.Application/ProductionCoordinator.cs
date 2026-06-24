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
    private readonly ICameraDevice _camera;
    private readonly IPlcClient _plc;
    private readonly IAxisController _axis;
    private readonly IAppLogService _log;
    private readonly IAlarmService _alarms;
    private readonly ICommunicationChannelRuntime _communicationChannels;
    private readonly ProductionSettingsConfiguration _productionSettings;
    private readonly object _syncRoot = new();

    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private ProductionSnapshot _snapshot = new(ProductionState.Stopped, 0, 0, 0, 100, TimeSpan.Zero, DateTimeOffset.Now);

    public ProductionCoordinator(
        IInspectionRunner runner,
        ICameraDevice camera,
        IPlcClient plc,
        IAxisController axis,
        IAppLogService log,
        IAlarmService alarms,
        ICommunicationChannelRuntime communicationChannels,
        DeviceConfiguration configuration)
    {
        _runner = runner;
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
        await _camera.ConnectAsync(cancellationToken);
        await _plc.ConnectAsync(cancellationToken);
        await _axis.ConnectAsync(cancellationToken);
        _log.Info("Production", "设备模拟层初始化完成");
    }

    public async Task<InspectionRunResult> RunSingleAsync(CancellationToken cancellationToken = default)
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
        if (_loopTask is { IsCompleted: false })
        {
            return;
        }

        await InitializeAsync(cancellationToken);
        await _communicationChannels.ConnectAsync(CommunicationChannelConnectionPolicies.Production, cancellationToken);
        _loopCancellation = new CancellationTokenSource();
        SetState(ProductionState.Running);
        _log.Info("Production", "连续生产启动");

        _loopTask = Task.Run(async () =>
        {
            var consecutiveFailures = 0;
            try
            {
                while (!_loopCancellation.IsCancellationRequested)
                {
                    try
                    {
                        await RunSingleAsync(_loopCancellation.Token);
                        consecutiveFailures = 0;
                        await Task.Delay(TimeSpan.FromMilliseconds(_productionSettings.CycleDelayMs), _loopCancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        SetState(ProductionState.Faulted);
                        _log.Error("Production", $"连续生产异常：{ex.Message}");
                        _alarms.Raise(
                            AlarmSeverity.Critical,
                            "Production",
                            $"Continuous production stopped: {ex.Message}",
                            ex.ToString(),
                            "production:continuous-run");
                        consecutiveFailures++;
                        if (_productionSettings.AutoStopOnAlarm ||
                            consecutiveFailures >= _productionSettings.MaxConsecutiveFailures)
                        {
                            break;
                        }

                        SetState(ProductionState.Running);
                        await Task.Delay(TimeSpan.FromMilliseconds(_productionSettings.CycleDelayMs), _loopCancellation.Token);
                    }
                }
            }
            finally
            {
                await DisconnectProductionAsync();
            }
        }, _loopCancellation.Token);
    }

    public async Task StopAsync()
    {
        _loopCancellation?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
                // The loop is expected to cancel during stop.
            }
        }

        SetState(ProductionState.Stopped);
        await DisconnectProductionAsync();
        _log.Info("Production", "连续生产停止");
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
