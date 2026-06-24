using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class AxisManager : IAxisController
{
    private readonly object _syncRoot = new();
    private readonly IReadOnlyList<IAxisDriver> _drivers;
    private DeviceSnapshot _snapshot = new("轴管理器", DeviceConnectionState.Disconnected, "轴管理器未连接", DateTimeOffset.Now);

    public AxisManager(IEnumerable<IAxisDriver> drivers)
    {
        _drivers = drivers.ToArray();
        foreach (var driver in _drivers)
        {
            driver.StateChanged += OnDriverStateChanged;
        }

        RefreshSnapshot("轴管理器已加载");
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public DeviceSnapshot Snapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _snapshot;
            }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_drivers.Count == 0)
        {
            throw new AxisControllerException("没有可用的轴卡驱动。");
        }

        RefreshSnapshot("正在连接轴卡驱动...");
        foreach (var driver in _drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RefreshSnapshot($"{driver.DriverId} 连接失败：{ex.Message}");
                throw;
            }
        }

        RefreshSnapshot("轴卡驱动已连接");
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var driver in _drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await driver.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }

        RefreshSnapshot("轴卡驱动已断开");
    }

    public Task ServoOnAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(axisKey).ServoOnAsync(axisKey, cancellationToken);
    }

    public Task ServoOffAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(axisKey).ServoOffAsync(axisKey, cancellationToken);
    }

    public Task ClearAlarmAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(axisKey).ClearAlarmAsync(axisKey, cancellationToken);
    }

    public Task ZeroPositionAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(axisKey).ZeroPositionAsync(axisKey, cancellationToken);
    }

    public Task HomeAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(axisKey).HomeAsync(axisKey, cancellationToken);
    }

    public Task HomeAsync(AxisHomeCommand command, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(command.AxisKey).HomeAsync(command, cancellationToken);
    }

    public Task MoveAbsoluteAsync(AxisMoveCommand command, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(command.AxisKey).MoveAbsoluteAsync(command, cancellationToken);
    }

    public Task MoveLinearInterpolationAsync(AxisLinearInterpolationCommand command, CancellationToken cancellationToken = default)
    {
        var axisKeys = command.Targets.Select(target => target.AxisKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (axisKeys.Length == 0)
        {
            throw new AxisControllerException("插补运动至少需要一个轴。");
        }

        var drivers = axisKeys.Select(ResolveDriver).Distinct().ToArray();
        if (drivers.Length != 1)
        {
            throw new AxisControllerException("插补运动要求所有轴属于同一个轴卡驱动；跨轴卡/跨驱动插补暂不支持。");
        }

        return drivers[0].MoveLinearInterpolationAsync(command, cancellationToken);
    }

    public Task StartJogAsync(AxisJogCommand command, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(command.AxisKey).StartJogAsync(command, cancellationToken);
    }

    public Task StopJogAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(axisKey).StopJogAsync(axisKey, cancellationToken);
    }

    public Task StopAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        AxisStopMode stopMode = AxisStopMode.Smooth,
        CancellationToken cancellationToken = default)
    {
        return ResolveDriver(axisKey).StopAsync(axisKey, stopMode, cancellationToken);
    }

    public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        foreach (var driver in _drivers)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await driver.EmergencyStopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count != 0)
        {
            throw new AxisControllerException($"部分轴卡急停失败：{string.Join("; ", failures.Select(error => error.Message))}");
        }
    }

    public Task<AxisStatus> GetAxisStatusAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default)
    {
        return ResolveDriver(axisKey).GetAxisStatusAsync(axisKey, cancellationToken);
    }

    public async Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        foreach (var driver in _drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await driver.ApplyConfigurationAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        RefreshSnapshot("轴卡配置已更新");
    }

    private IAxisDriver ResolveDriver(string axisKey)
    {
        var driver = _drivers.FirstOrDefault(item => item.ContainsAxis(axisKey));
        if (driver is not null)
        {
            return driver;
        }

        throw new AxisControllerException($"未找到轴 {axisKey} 对应的轴卡驱动。");
    }

    private void OnDriverStateChanged(object? sender, DeviceSnapshot snapshot)
    {
        RefreshSnapshot($"{snapshot.Name}: {snapshot.Message}");
    }

    private void RefreshSnapshot(string message)
    {
        DeviceSnapshot snapshot;
        lock (_syncRoot)
        {
            var state = ResolveAggregateState();
            var details = _drivers.Count == 0
                ? "未配置轴卡驱动"
                : string.Join(" | ", _drivers.Select(driver => $"{driver.DriverId}:{driver.Snapshot.State}"));
            _snapshot = new DeviceSnapshot("轴管理器", state, $"{message}（{details}）", DateTimeOffset.Now);
            snapshot = _snapshot;
        }

        StateChanged?.Invoke(this, snapshot);
    }

    private DeviceConnectionState ResolveAggregateState()
    {
        if (_drivers.Count == 0)
        {
            return DeviceConnectionState.Disconnected;
        }

        var states = _drivers.Select(driver => driver.Snapshot.State).ToArray();
        if (states.Any(state => state == DeviceConnectionState.Faulted))
        {
            return DeviceConnectionState.Faulted;
        }

        if (states.Any(state => state == DeviceConnectionState.Connecting))
        {
            return DeviceConnectionState.Connecting;
        }

        if (states.Any(state => state == DeviceConnectionState.Connected))
        {
            return DeviceConnectionState.Connected;
        }

        return DeviceConnectionState.Disconnected;
    }
}
