using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class DigitalIoManager : IDigitalIoController
{
    private readonly object _syncRoot = new();
    private readonly IReadOnlyList<IDigitalIoDriver> _drivers;
    private DeviceSnapshot _snapshot = new("数字 IO 管理器", DeviceConnectionState.Disconnected, "数字 IO 管理器未连接", DateTimeOffset.Now);

    public DigitalIoManager(IEnumerable<IDigitalIoDriver> drivers)
    {
        _drivers = drivers.ToArray();
        foreach (var driver in _drivers)
        {
            driver.StateChanged += OnDriverStateChanged;
        }

        RefreshSnapshot("数字 IO 驱动已加载");
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
            RefreshSnapshot("未配置数字 IO 驱动或 IO 点表为空");
            return;
        }

        RefreshSnapshot("正在连接数字 IO 驱动...");
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

        RefreshSnapshot("数字 IO 驱动已连接");
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var driver in _drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await driver.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }

        RefreshSnapshot("数字 IO 驱动已断开");
    }

    public async Task<IReadOnlyList<IoPointStatus>> GetAllPointStatusAsync(CancellationToken cancellationToken = default)
    {
        var statuses = new List<IoPointStatus>();
        foreach (var driver in _drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses.AddRange(await driver.GetAllPointStatusAsync(cancellationToken).ConfigureAwait(false));
        }

        return statuses
            .OrderBy(status => status.Direction)
            .ThenBy(status => status.CardNo)
            .ThenBy(status => status.PointNo)
            .ThenBy(status => status.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<IoPointStatus> GetPointStatusAsync(string pointKey, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(pointKey).GetPointStatusAsync(pointKey, cancellationToken);
    }

    public Task WritePointAsync(string pointKey, bool value, CancellationToken cancellationToken = default)
    {
        return ResolveDriver(pointKey).WritePointAsync(pointKey, value, cancellationToken);
    }

    public async Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        foreach (var driver in _drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await driver.ApplyConfigurationAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        RefreshSnapshot("数字 IO 配置已更新");
    }

    private IDigitalIoDriver ResolveDriver(string pointKey)
    {
        var driver = _drivers.FirstOrDefault(item => item.ContainsPoint(pointKey));
        if (driver is not null)
        {
            return driver;
        }

        throw new AxisControllerException($"未找到 IO 点 {pointKey} 对应的数字 IO 驱动。");
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
                ? "未配置数字 IO 驱动"
                : string.Join(" | ", _drivers.Select(driver => $"{driver.DriverId}:{driver.Snapshot.State}"));
            _snapshot = new DeviceSnapshot("数字 IO 管理器", state, $"{message}（{details}）", DateTimeOffset.Now);
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
