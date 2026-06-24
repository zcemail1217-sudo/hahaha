using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class SimulatedAxisController : IAxisController
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, SimulatedAxisState> _axes = new(StringComparer.OrdinalIgnoreCase);
    private DeviceSnapshot _snapshot = new("模拟轴卡", DeviceConnectionState.Disconnected, "未连接", DateTimeOffset.Now);

    public SimulatedAxisController()
        : this(new DeviceConfiguration())
    {
    }

    public SimulatedAxisController(DeviceConfiguration configuration)
    {
        ApplyAxes(configuration.Axes);
        if (_axes.Count == 0)
        {
            _axes[AxisDefaults.PrimaryAxisKey] = new SimulatedAxisState(AxisDefaults.PrimaryAxisKey);
        }
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public DeviceSnapshot Snapshot => _snapshot;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            foreach (var axis in _axes.Values)
            {
                axis.Ready = true;
                axis.InPosition = true;
                axis.Message = "模拟轴就绪";
            }
        }

        SetState(DeviceConnectionState.Connected, "模拟轴卡已连接");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            foreach (var axis in _axes.Values)
            {
                axis.ServoOn = false;
                axis.Ready = false;
                axis.Message = "轴卡已断开";
            }
        }

        SetState(DeviceConnectionState.Disconnected, "模拟轴卡已断开");
        return Task.CompletedTask;
    }

    public Task ServoOnAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        axis.ServoOn = true;
        axis.Ready = true;
        axis.Message = "伺服已使能";
        SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} 伺服已使能");
        return Task.CompletedTask;
    }

    public Task ServoOffAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        axis.ServoOn = false;
        axis.Message = "伺服已关闭";
        SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} 伺服已关闭");
        return Task.CompletedTask;
    }

    public Task ClearAlarmAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        axis.Alarm = false;
        axis.EmergencyStop = false;
        axis.Ready = true;
        axis.Message = "报警已清除";
        SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} 报警已清除");
        return Task.CompletedTask;
    }

    public Task ZeroPositionAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var axis = GetAxis(axisKey);
        if (!IsAxisSettled(axis))
        {
            throw new AxisControllerBusyException($"{axis.AxisKey} is not ready or position has not settled; stop the axis before zeroing position.");
        }

        axis.CommandPosition = 0;
        axis.EncoderPosition = 0;
        axis.Message = "position zeroed";
        SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} position zeroed");
        return Task.CompletedTask;
    }

    public async Task HomeAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        await HomeAsync(new AxisHomeCommand { AxisKey = axisKey }, cancellationToken);
    }

    public async Task HomeAsync(AxisHomeCommand command, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var axis = GetAxis(command.AxisKey);
        axis.Ready = false;
        axis.InPosition = false;
        axis.Message = $"模拟{command.HomeMode}回零中";
        SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} 模拟回零中");

        await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken);

        axis.CommandPosition = 0;
        axis.EncoderPosition = 0;
        axis.Home = true;
        axis.Homed = true;
        axis.Ready = true;
        axis.InPosition = true;
        axis.Message = "模拟回零完成";
        SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} 模拟回零完成");
    }

    public async Task MoveAbsoluteAsync(AxisMoveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureConnected();

        var axis = GetAxis(command.AxisKey);
        if (axis.Alarm || axis.EmergencyStop)
        {
            throw new InvalidOperationException($"{axis.AxisKey} 当前存在报警或急停，不能运动。");
        }

        if (!IsAxisSettled(axis))
        {
            throw new AxisControllerBusyException($"{axis.AxisKey} is not ready or position has not settled; wait for the current motion to finish or stop the axis first.");
        }

        if (IsSoftLimitEnabled(axis) && (command.Position < axis.SoftLimitNegative || command.Position > axis.SoftLimitPositive))
        {
            throw new InvalidOperationException($"{axis.AxisKey} 目标位置超出软件限位。");
        }

        if (!axis.ServoOn)
        {
            axis.ServoOn = true;
        }

        var distance = Math.Abs(command.Position - axis.CommandPosition);
        var speed = Math.Max(Math.Abs(command.Speed), 1);
        var estimatedMilliseconds = Math.Clamp((int)(distance / speed * 1000), 120, 2000);

        axis.Ready = false;
        axis.InPosition = false;
        axis.Message = $"模拟移动到 {command.Position:0.###}";
        SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} {axis.Message}");

        if (command.WaitForCompletion)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(command.Timeout);
            await Task.Delay(estimatedMilliseconds, timeout.Token);
            axis.CommandPosition = command.Position;
            axis.EncoderPosition = command.Position;
            axis.Ready = true;
            axis.InPosition = true;
            axis.Message = $"已到达 {command.Position:0.###}";
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} {axis.Message}");
        }
    }

    public async Task MoveLinearInterpolationAsync(AxisLinearInterpolationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureConnected();

        if (command.Targets.Count is < 2 or > 4)
        {
            throw new InvalidOperationException("直线插补需要 2~4 根轴。");
        }

        var axes = command.Targets.Select(target => GetAxis(target.AxisKey)).ToArray();
        foreach (var item in command.Targets.Zip(axes))
        {
            var target = item.First;
            var axis = item.Second;
            if (axis.Alarm || axis.EmergencyStop)
            {
                throw new InvalidOperationException($"{axis.AxisKey} 当前存在报警或急停，不能插补。");
            }

            if (IsSoftLimitEnabled(axis) && (target.Position < axis.SoftLimitNegative || target.Position > axis.SoftLimitPositive))
            {
                throw new InvalidOperationException($"{axis.AxisKey} 插补目标位置超出软件限位。");
            }

            if (!axis.ServoOn)
            {
                axis.ServoOn = true;
            }
        }

        var maxDistance = command.Targets
            .Zip(axes, (target, axis) => Math.Abs(target.Position - axis.CommandPosition))
            .DefaultIfEmpty(0)
            .Max();
        var speed = Math.Max(Math.Abs(command.Speed), 1);
        var estimatedMilliseconds = Math.Clamp((int)(maxDistance / speed * 1000), 120, 3000);

        foreach (var axis in axes)
        {
            axis.Ready = false;
            axis.InPosition = false;
            axis.Message = "模拟直线插补中";
        }

        SetState(DeviceConnectionState.Connected, $"模拟{axes.Length}轴直线插补中");

        if (command.WaitForCompletion)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(command.Timeout);
            await Task.Delay(estimatedMilliseconds, timeout.Token);

            foreach (var item in command.Targets.Zip(axes))
            {
                var target = item.First;
                var axis = item.Second;
                axis.CommandPosition = target.Position;
                axis.EncoderPosition = target.Position;
                axis.Ready = true;
                axis.InPosition = true;
                axis.Message = "模拟直线插补完成";
            }

            SetState(DeviceConnectionState.Connected, $"模拟{axes.Length}轴直线插补完成");
        }
    }

    public Task StartJogAsync(AxisJogCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureConnected();

        var axis = GetAxis(command.AxisKey);
        axis.Ready = false;
        axis.InPosition = false;
        axis.Message = $"模拟Jog {(command.Direction == AxisJogDirection.Positive ? "+" : "-")} 运行中";
        SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} {axis.Message}");
        return Task.CompletedTask;
    }

    public Task StopJogAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return StopAsync(axisKey, AxisStopMode.Smooth, cancellationToken);
    }

    public Task StopAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        AxisStopMode stopMode = AxisStopMode.Smooth,
        CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        axis.Ready = true;
        axis.InPosition = true;
        axis.Message = stopMode == AxisStopMode.Immediate ? "模拟立即停止" : "模拟减速停止";
        SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} {axis.Message}");
        return Task.CompletedTask;
    }

    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            foreach (var axis in _axes.Values)
            {
                axis.EmergencyStop = true;
                axis.Ready = false;
                axis.InPosition = false;
                axis.Message = "模拟急停触发";
            }
        }

        SetState(DeviceConnectionState.Faulted, "模拟轴卡急停触发");
        return Task.CompletedTask;
    }

    public Task<AxisStatus> GetAxisStatusAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        return Task.FromResult(axis.ToStatus());
    }

    public Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            _axes.Clear();
            ApplyAxes(configuration.Axes);
            if (_axes.Count == 0)
            {
                _axes[AxisDefaults.PrimaryAxisKey] = new SimulatedAxisState(AxisDefaults.PrimaryAxisKey);
            }
        }

        SetState(Snapshot.State, "模拟轴配置已更新");
        return Task.CompletedTask;
    }

    private void ApplyAxes(IReadOnlyList<AxisPointDefinition> axes)
    {
        foreach (var definition in axes.Where(axis => axis.Enabled))
        {
            _axes[definition.Key] = new SimulatedAxisState(definition.Key)
            {
                SoftLimitNegative = definition.SoftLimitNegative,
                SoftLimitPositive = definition.SoftLimitPositive
            };
        }
    }

    private void EnsureConnected()
    {
        if (Snapshot.State != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException("模拟轴卡未连接。");
        }
    }

    private SimulatedAxisState GetAxis(string axisKey)
    {
        lock (_syncRoot)
        {
            if (_axes.TryGetValue(axisKey, out var axis))
            {
                return axis;
            }

            axis = new SimulatedAxisState(axisKey);
            _axes[axisKey] = axis;
            return axis;
        }
    }

    private static bool IsSoftLimitEnabled(SimulatedAxisState axis)
    {
        return axis.SoftLimitPositive > axis.SoftLimitNegative;
    }

    private static bool IsAxisSettled(SimulatedAxisState axis)
    {
        if (!axis.Ready)
        {
            return false;
        }

        return axis.InPosition || Math.Abs(axis.CommandPosition - axis.EncoderPosition) <= 0.001;
    }

    private void SetState(DeviceConnectionState state, string message)
    {
        _snapshot = new DeviceSnapshot("模拟轴卡", state, message, DateTimeOffset.Now);
        StateChanged?.Invoke(this, _snapshot);
    }

    private sealed class SimulatedAxisState
    {
        public SimulatedAxisState(string axisKey)
        {
            AxisKey = axisKey;
        }

        public string AxisKey { get; }

        public double CommandPosition { get; set; }

        public double EncoderPosition { get; set; }

        public bool ServoOn { get; set; }

        public bool Alarm { get; set; }

        public bool PositiveLimit { get; set; }

        public bool NegativeLimit { get; set; }

        public bool Home { get; set; } = true;

        public bool EmergencyStop { get; set; }

        public bool Ready { get; set; }

        public bool InPosition { get; set; } = true;

        public bool Homed { get; set; }

        public double SoftLimitNegative { get; set; } = -1000;

        public double SoftLimitPositive { get; set; } = 1000;

        public string Message { get; set; } = "未连接";

        public AxisStatus ToStatus()
        {
            return new AxisStatus
            {
                AxisKey = AxisKey,
                CommandPosition = CommandPosition,
                EncoderPosition = EncoderPosition,
                ServoOn = ServoOn,
                Alarm = Alarm,
                PositiveLimit = PositiveLimit,
                NegativeLimit = NegativeLimit,
                Home = Home,
                EmergencyStop = EmergencyStop,
                Ready = Ready,
                InPosition = InPosition,
                Homed = Homed,
                Message = Message,
                Timestamp = DateTimeOffset.Now
            };
        }
    }
}
