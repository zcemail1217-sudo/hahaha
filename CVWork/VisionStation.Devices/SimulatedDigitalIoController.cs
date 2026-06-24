using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class SimulatedDigitalIoController : IDigitalIoController
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, SimulatedIoPoint> _points = new(StringComparer.OrdinalIgnoreCase);
    private DeviceSnapshot _snapshot = new("模拟IO", DeviceConnectionState.Disconnected, "未连接", DateTimeOffset.Now);

    public SimulatedDigitalIoController()
        : this(new DeviceConfiguration())
    {
    }

    public SimulatedDigitalIoController(DeviceConfiguration configuration)
    {
        ApplyPoints(configuration);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public DeviceSnapshot Snapshot => _snapshot;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(DeviceConnectionState.Connected, $"模拟IO已连接，点位 {_points.Count} 个");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(DeviceConnectionState.Disconnected, "模拟IO已断开");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IoPointStatus>> GetAllPointStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            return Task.FromResult<IReadOnlyList<IoPointStatus>>(_points.Values
                .OrderBy(point => point.Definition.Direction)
                .ThenBy(point => point.Definition.PointNo)
                .ThenBy(point => point.Definition.Key, StringComparer.OrdinalIgnoreCase)
                .Select(point => point.ToStatus())
                .ToArray());
        }
    }

    public Task<IoPointStatus> GetPointStatusAsync(string pointKey, CancellationToken cancellationToken = default)
    {
        var point = GetPoint(pointKey);
        return Task.FromResult(point.ToStatus());
    }

    public Task WritePointAsync(string pointKey, bool value, CancellationToken cancellationToken = default)
    {
        var point = GetPoint(pointKey);
        if (point.Definition.Direction != IoPointDirection.Output)
        {
            throw new InvalidOperationException($"{point.Definition.Key} 是输入点，不能写入。");
        }

        if (!point.Definition.Enabled)
        {
            throw new InvalidOperationException($"{point.Definition.Key} 已禁用。");
        }

        point.Value = value;
        SetState(DeviceConnectionState.Connected, $"{point.Definition.Key} 写入 {(value ? "ON" : "OFF")}");
        return Task.CompletedTask;
    }

    public Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            ApplyPoints(configuration);
        }

        SetState(DeviceConnectionState.Connected, $"模拟IO配置已刷新，点位 {_points.Count} 个");
        return Task.CompletedTask;
    }

    private void ApplyPoints(DeviceConfiguration configuration)
    {
        var existingValues = _points.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase);
        _points.Clear();

        foreach (var definition in configuration.IoPoints.Where(point => point.Enabled))
        {
            _points[definition.Key] = new SimulatedIoPoint(definition)
            {
                Value = existingValues.TryGetValue(definition.Key, out var value) ? value : definition.InitialValue
            };
        }
    }

    private SimulatedIoPoint GetPoint(string pointKey)
    {
        lock (_syncRoot)
        {
            if (_points.TryGetValue(pointKey, out var point))
            {
                return point;
            }
        }

        throw new InvalidOperationException($"未配置IO点：{pointKey}");
    }

    private void SetState(DeviceConnectionState state, string message)
    {
        _snapshot = new DeviceSnapshot("模拟IO", state, message, DateTimeOffset.Now);
        StateChanged?.Invoke(this, _snapshot);
    }

    private sealed class SimulatedIoPoint
    {
        public SimulatedIoPoint(IoPointDefinition definition)
        {
            Definition = definition;
        }

        public IoPointDefinition Definition { get; }

        public bool Value { get; set; }

        public IoPointStatus ToStatus()
        {
            return new IoPointStatus
            {
                Key = Definition.Key,
                Name = Definition.Name,
                Direction = Definition.Direction,
                Address = Definition.Address,
                CardNo = Definition.CardNo,
                PointNo = Definition.PointNo,
                ActiveLow = Definition.ActiveLow,
                Enabled = Definition.Enabled,
                Value = Value,
                Message = "模拟IO状态已刷新",
                Timestamp = DateTimeOffset.Now
            };
        }
    }
}
