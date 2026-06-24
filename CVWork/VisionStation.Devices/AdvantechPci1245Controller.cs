using System.Runtime.InteropServices;
using Advantech.Motion;
using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed class AdvantechPci1245Controller : IAxisDriver, IDigitalIoDriver
{
    private const int MaxAxesPerCard = 4;
    private readonly object _syncRoot = new();
    private IReadOnlyList<AxisCardDefinition> _cards;
    private Dictionary<string, AdvantechAxisRuntime> _axes;
    private Dictionary<string, IoPointDefinition> _points;
    private readonly Dictionary<string, AdvantechCardSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _connected;
    private DeviceSnapshot _snapshot = new("研华 PCI-1245", DeviceConnectionState.Disconnected, "研华 PCI-1245 驱动未连接", DateTimeOffset.Now);

    private sealed record AdvantechAxisRuntime(
        AxisPointDefinition Definition,
        AxisCardDefinition Card,
        int AxisIndex);

    private sealed class AdvantechCardSession
    {
        public AdvantechCardSession(AxisCardDefinition definition)
        {
            Definition = definition;
            AxisHandles = new IntPtr[ResolveAxisCount(definition)];
        }

        public AxisCardDefinition Definition { get; }

        public IntPtr DeviceHandle { get; set; }

        public IntPtr[] AxisHandles { get; }
    }

    public AdvantechPci1245Controller(DeviceConfiguration configuration)
    {
        _cards = ResolveCards(configuration);
        _axes = BuildAxisMap(configuration, _cards);
        _points = BuildPointMap(configuration, _cards);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string DriverId => "AdvantechPci1245";

    public AxisCardDriverKind DriverKind => AxisCardDriverKind.AdvantechPci1245;

    public IReadOnlyCollection<string> AxisKeys
    {
        get
        {
            lock (_syncRoot)
            {
                return _axes.Keys.ToArray();
            }
        }
    }

    public IReadOnlyCollection<string> PointKeys
    {
        get
        {
            lock (_syncRoot)
            {
                return _points.Keys.ToArray();
            }
        }
    }

    public DeviceSnapshot Snapshot => _snapshot;

    public bool ContainsAxis(string axisKey)
    {
        lock (_syncRoot)
        {
            return _axes.ContainsKey(axisKey);
        }
    }

    public bool ContainsPoint(string pointKey)
    {
        lock (_syncRoot)
        {
            return _points.ContainsKey(pointKey);
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware("连接研华 PCI-1245", ConnectCore);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected)
        {
            SetState(DeviceConnectionState.Disconnected, "研华 PCI-1245 已断开");
            return Task.CompletedTask;
        }

        RunHardware("断开研华 PCI-1245", DisconnectCore);
        return Task.CompletedTask;
    }

    public Task ServoOnAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware($"{axis.Definition.Name} 伺服 ON", () =>
        {
            Check(Motion.mAcm_AxSetSvOn(GetAxisHandle(axis), 1), $"{axis.Definition.Name} 伺服 ON");
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} 伺服已使能");
        });
        return Task.CompletedTask;
    }

    public Task ServoOffAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware($"{axis.Definition.Name} 伺服 OFF", () =>
        {
            Check(Motion.mAcm_AxSetSvOn(GetAxisHandle(axis), 0), $"{axis.Definition.Name} 伺服 OFF");
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} 伺服已关闭");
        });
        return Task.CompletedTask;
    }

    public Task ClearAlarmAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware($"{axis.Definition.Name} 清报警", () =>
        {
            Check(Motion.mAcm_AxResetError(GetAxisHandle(axis)), $"{axis.Definition.Name} 清报警");
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} 报警已清除");
        });
        return Task.CompletedTask;
    }

    public Task ZeroPositionAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware($"{axis.Definition.Name} 位置清零", () =>
        {
            var status = ReadAxisStatusCore(axis);
            EnsureAxisCanZero(axis, status);
            var handle = GetAxisHandle(axis);
            Check(Motion.mAcm_AxSetCmdPosition(handle, 0), $"{axis.Definition.Name} 指令位置清零");
            Check(Motion.mAcm_AxSetActualPosition(handle, 0), $"{axis.Definition.Name} 反馈位置清零");
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} 指令/反馈位置已清零");
        });
        return Task.CompletedTask;
    }

    public Task HomeAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return HomeAsync(new AxisHomeCommand { AxisKey = axisKey }, cancellationToken);
    }

    public async Task HomeAsync(AxisHomeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var axis = GetAxis(command.AxisKey);
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware($"{axis.Definition.Name} 回原", () =>
        {
            var status = ReadAxisStatusCore(axis);
            EnsureAxisCanStartMotion(axis, status);
            var handle = GetAxisHandle(axis);
            SetAxisSpeedCore(handle, command.LowSpeed, command.HighSpeed, command.Acceleration, command.Deceleration, axis.Definition.PulsesPerUnit);
            Check(Motion.mAcm_AxResetError(handle), $"{axis.Definition.Name} 回原前清报警");
            Check(Motion.mAcm_AxHome(handle, (uint)MapHomeMode(command.HomeMode), command.HomePositive ? 0u : 1u), $"{axis.Definition.Name} 启动回原");
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} 正在回原");
        });

        await WaitHomeAsync(axis, command.Timeout, cancellationToken).ConfigureAwait(false);

        RunHardware($"{axis.Definition.Name} 回原完成后清零", () =>
        {
            var handle = GetAxisHandle(axis);
            Check(Motion.mAcm_AxSetCmdPosition(handle, command.HomeOffset * axis.Definition.PulsesPerUnit), $"{axis.Definition.Name} 设置回原指令位置");
            Check(Motion.mAcm_AxSetActualPosition(handle, command.HomeOffset * axis.Definition.PulsesPerUnit), $"{axis.Definition.Name} 设置回原反馈位置");
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} 回原完成");
        });
    }

    public async Task MoveAbsoluteAsync(AxisMoveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var axis = GetAxis(command.AxisKey);
        var targetPulse = ToPulse(axis.Definition, command.Position);
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware($"{axis.Definition.Name} 绝对移动", () =>
        {
            ValidateTargetWithinSoftLimit(axis.Definition, command.Position);
            var status = ReadAxisStatusCore(axis);
            EnsureAxisCanStartMotion(axis, status);
            var handle = GetAxisHandle(axis);
            SetAxisSpeedCore(handle, 0, command.Speed, command.Acceleration, command.Deceleration <= 0 ? command.Acceleration : command.Deceleration, axis.Definition.PulsesPerUnit);
            Check(Motion.mAcm_AxMoveAbs(handle, targetPulse), $"{axis.Definition.Name} 绝对移动到 {command.Position:0.###}");
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} 正在移动到 {command.Position:0.###}");
        });

        if (command.WaitForCompletion)
        {
            await WaitReadyAsync(axis, command.Position, command.Timeout, cancellationToken).ConfigureAwait(false);
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} 已到达 {command.Position:0.###}");
        }
    }

    public Task MoveLinearInterpolationAsync(AxisLinearInterpolationCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = "研华 PCI-1245 直线插补尚未接入。当前只接入了单轴点位、Jog、回原、清零和板载 DI/DO；需要 PCI-1245 群组插补示例后才能安全启用。";
        SetState(DeviceConnectionState.Faulted, message);
        throw new AxisControllerException(message);
    }

    public Task StartJogAsync(AxisJogCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var axis = GetAxis(command.AxisKey);
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware($"{axis.Definition.Name} Jog", () =>
        {
            var status = ReadAxisStatusCore(axis);
            EnsureAxisCanStartMotion(axis, status);
            var handle = GetAxisHandle(axis);
            SetAxisSpeedCore(handle, 0, command.Speed, command.Acceleration, command.Deceleration <= 0 ? command.Acceleration : command.Deceleration, axis.Definition.PulsesPerUnit);
            var direction = command.Direction == AxisJogDirection.Positive ? (ushort)0 : (ushort)1;
            Check(Motion.mAcm_AxMoveVel(handle, direction), $"{axis.Definition.Name} Jog {(direction == 0 ? "+" : "-")}");
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} Jog {(direction == 0 ? "正向" : "负向")}运行");
        });
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
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware($"{axis.Definition.Name} 停止", () =>
        {
            StopAxisCore(axis, stopMode);
            SetState(DeviceConnectionState.Connected, $"{axis.Definition.Name} 已停止");
        });
        return Task.CompletedTask;
    }

    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware("研华 PCI-1245 急停", () =>
        {
            foreach (var axis in _axes.Values)
            {
                Check(Motion.mAcm_AxStopEmg(GetAxisHandle(axis)), $"{axis.Definition.Name} 急停");
            }

            SetState(DeviceConnectionState.Connected, "研华 PCI-1245 已执行急停");
        });
        return Task.CompletedTask;
    }

    public Task<AxisStatus> GetAxisStatusAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        return Task.FromResult(ReadAxisStatusCore(axis));
    }

    public Task<IReadOnlyList<IoPointStatus>> GetAllPointStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();

        IoPointDefinition[] points;
        lock (_syncRoot)
        {
            points = _points.Values.ToArray();
        }

        return Task.FromResult<IReadOnlyList<IoPointStatus>>(points
            .OrderBy(point => point.Direction)
            .ThenBy(point => point.CardNo)
            .ThenBy(point => point.PointNo)
            .ThenBy(point => point.Key, StringComparer.OrdinalIgnoreCase)
            .Select(ReadPointCore)
            .ToArray());
    }

    public Task<IoPointStatus> GetPointStatusAsync(string pointKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        return Task.FromResult(ReadPointCore(GetPoint(pointKey)));
    }

    public Task WritePointAsync(string pointKey, bool value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        var point = GetPoint(pointKey);
        if (point.Direction != IoPointDirection.Output)
        {
            throw new InvalidOperationException($"{point.Key} 是输入点，不能写输出。");
        }

        RunHardware($"{point.Key} 写输出", () =>
        {
            var rawValue = ToRawValue(point, value);
            if (point.Source == IoPointSource.AxisOnboard)
            {
                Check(Motion.mAcm_AxDoSetBit(GetAxisIoHandle(point), ResolveAxisOutputBitIndex(point), rawValue), $"{point.Key} 写轴上 DO");
            }
            else
            {
                Check(Motion.mAcm_DaqDoSetBit(GetDeviceHandle(point), ResolveBitIndex(point), rawValue), $"{point.Key} 写板载 DO");
            }

            SetState(DeviceConnectionState.Connected, $"{point.Key} 已写 {(value ? "ON" : "OFF")}");
        });
        return Task.CompletedTask;
    }

    public Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            _cards = ResolveCards(configuration);
            _axes = BuildAxisMap(configuration, _cards);
            _points = BuildPointMap(configuration, _cards);
            SetState(
                _connected ? DeviceConnectionState.Connected : DeviceConnectionState.Disconnected,
                _connected
                    ? $"研华 PCI-1245 配置已更新，当前轴数 {_axes.Count}、IO 点数 {_points.Count}；卡号或配置文件变更需要重启软件。"
                    : $"研华 PCI-1245 配置已加载，当前轴数 {_axes.Count}、IO 点数 {_points.Count}。");
        }

        return Task.CompletedTask;
    }

    private void ConnectCore()
    {
        if (_connected)
        {
            SetState(DeviceConnectionState.Connected, $"研华 PCI-1245 已连接，轴数 {_axes.Count}、IO 点数 {_points.Count}");
            return;
        }

        if (_cards.Count == 0)
        {
            SetState(DeviceConnectionState.Disconnected, "未配置研华 PCI-1245 轴卡");
            return;
        }

        var deviceList = new DEV_LIST[Motion.MAX_DEVICES];
        uint deviceCount = 0;
        Check(Motion.mAcm_GetAvailableDevs(deviceList, (uint)Motion.MAX_DEVICES, ref deviceCount), "枚举研华运动控制卡");
        foreach (var card in _cards)
        {
            if (card.CardNo < 0 || card.CardNo >= deviceCount)
            {
                throw new AxisControllerException($"研华 PCI-1245 卡 C{card.CardNo} 不存在。当前 Advantech Motion 可用设备数：{deviceCount}。");
            }

            var session = new AdvantechCardSession(card);
            var deviceHandle = IntPtr.Zero;
            Check(Motion.mAcm_DevOpen(deviceList[card.CardNo].DeviceNum, ref deviceHandle), $"打开研华 PCI-1245 卡 C{card.CardNo}");
            session.DeviceHandle = deviceHandle;
            for (ushort axisIndex = 0; axisIndex < session.AxisHandles.Length; axisIndex++)
            {
                var handle = IntPtr.Zero;
                Check(Motion.mAcm_AxOpen(session.DeviceHandle, axisIndex, ref handle), $"打开研华 PCI-1245 C{card.CardNo} 轴 {axisIndex + 1}");
                session.AxisHandles[axisIndex] = handle;
            }

            if (!string.IsNullOrWhiteSpace(card.ConfigPath))
            {
                var configPath = card.ConfigPath.Trim();
                if (!File.Exists(configPath))
                {
                    throw new AxisControllerException($"研华 PCI-1245 卡 C{card.CardNo} 配置文件不存在：{configPath}");
                }

                Check(Motion.mAcm_DevLoadConfig(session.DeviceHandle, configPath), $"加载研华 PCI-1245 C{card.CardNo} 配置文件");
            }

            _sessions[ResolveCardKey(card)] = session;
        }

        _connected = true;
        SetState(DeviceConnectionState.Connected, $"研华 PCI-1245 已连接，卡数 {_sessions.Count}、轴数 {_axes.Count}、IO 点数 {_points.Count}");
    }

    private void DisconnectCore()
    {
        foreach (var session in _sessions.Values)
        {
            CloseSessionCore(session);
        }

        _sessions.Clear();
        _connected = false;
        SetState(DeviceConnectionState.Disconnected, "研华 PCI-1245 已断开");
    }

    private static void CloseSessionCore(AdvantechCardSession session)
    {
        for (var index = 0; index < session.AxisHandles.Length; index++)
        {
            var handle = session.AxisHandles[index];
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            ushort state = 0;
            Check(Motion.mAcm_AxGetState(handle, ref state), $"读取研华 PCI-1245 轴 {index + 1} 关闭前状态");
            if (state == (ushort)AxisState.STA_AX_ERROR_STOP)
            {
                Check(Motion.mAcm_AxResetError(handle), $"清除研华 PCI-1245 轴 {index + 1} 关闭前错误");
            }

            Check(Motion.mAcm_AxStopDec(handle), $"停止研华 PCI-1245 轴 {index + 1}");
            Check(Motion.mAcm_AxClose(ref handle), $"关闭研华 PCI-1245 轴 {index + 1}");
            session.AxisHandles[index] = IntPtr.Zero;
        }

        if (session.DeviceHandle != IntPtr.Zero)
        {
            var handle = session.DeviceHandle;
            Check(Motion.mAcm_DevClose(ref handle), $"关闭研华 PCI-1245 卡 C{session.Definition.CardNo}");
            session.DeviceHandle = IntPtr.Zero;
        }
    }

    private AxisStatus ReadAxisStatusCore(AdvantechAxisRuntime axis)
    {
        var handle = GetAxisHandle(axis);
        uint motionIo = 0;
        ushort state = 0;
        double commandPulse = 0;
        double encoderPulse = 0;
        Check(Motion.mAcm_AxGetMotionIO(handle, ref motionIo), $"{axis.Definition.Name} 读取 Motion IO 状态");
        Check(Motion.mAcm_AxGetState(handle, ref state), $"{axis.Definition.Name} 读取轴状态");
        Check(Motion.mAcm_AxGetCmdPosition(handle, ref commandPulse), $"{axis.Definition.Name} 读取指令位置");
        Check(Motion.mAcm_AxGetActualPosition(handle, ref encoderPulse), $"{axis.Definition.Name} 读取反馈位置");

        var servoAlarm = (motionIo & 0x2) != 0;
        var positiveLimit = (motionIo & 0x4) != 0;
        var negativeLimit = (motionIo & 0x8) != 0;
        var home = (motionIo & 0x10) != 0;
        var emergencyStop = (motionIo & 0x40) != 0;
        var inPosition = (motionIo & 0x2000) != 0;
        var servoOn = (motionIo & 0x4000) != 0;
        var ready = state == (ushort)AxisState.STA_AX_READY;

        return new AxisStatus
        {
            AxisKey = axis.Definition.Key,
            CommandPosition = FromPulse(axis.Definition, commandPulse),
            EncoderPosition = FromPulse(axis.Definition, encoderPulse),
            ServoOn = servoOn,
            Alarm = servoAlarm || state == (ushort)AxisState.STA_AX_ERROR_STOP,
            PositiveLimit = positiveLimit,
            NegativeLimit = negativeLimit,
            Home = home,
            EmergencyStop = emergencyStop,
            Ready = ready,
            InPosition = inPosition || ready,
            Homed = home,
            Message = $"研华 PCI-1245 状态：{DescribeAxisState(state)}",
            Timestamp = DateTimeOffset.Now
        };
    }

    private IoPointStatus ReadPointCore(IoPointDefinition point)
    {
        return RunHardware($"{point.Key} 读取 IO 状态", () =>
        {
            byte rawValue = 0;
            if (point.Source == IoPointSource.AxisOnboard && point.Direction == IoPointDirection.Output)
            {
                Check(Motion.mAcm_AxDoGetBit(GetAxisIoHandle(point), ResolveAxisOutputBitIndex(point), ref rawValue), $"{point.Key} 读取轴上 DO");
            }
            else if (point.Source == IoPointSource.AxisOnboard)
            {
                Check(Motion.mAcm_AxDiGetBit(GetAxisIoHandle(point), ResolveAxisInputBitIndex(point), ref rawValue), $"{point.Key} 读取轴上 DI");
            }
            else if (point.Direction == IoPointDirection.Output)
            {
                Check(Motion.mAcm_DaqDoGetBit(GetDeviceHandle(point), ResolveBitIndex(point), ref rawValue), $"{point.Key} 读取板载 DO");
            }
            else
            {
                Check(Motion.mAcm_DaqDiGetBit(GetDeviceHandle(point), ResolveBitIndex(point), ref rawValue), $"{point.Key} 读取板载 DI");
            }

            return new IoPointStatus
            {
                Key = point.Key,
                Name = point.Name,
                Direction = point.Direction,
                Address = point.Address,
                CardNo = point.CardNo,
                PointNo = point.PointNo,
                ActiveLow = point.ActiveLow,
                Enabled = point.Enabled,
                Value = FromRawValue(point, rawValue),
                Message = point.Source == IoPointSource.AxisOnboard
                    ? "研华 PCI-1245 轴上 IO 状态已刷新"
                    : "研华 PCI-1245 IO 状态已刷新",
                Timestamp = DateTimeOffset.Now
            };
        });
    }

    private async Task WaitReadyAsync(
        AdvantechAxisRuntime axis,
        double targetPosition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var effectiveTimeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = RunHardware($"{axis.Definition.Name} 等待到位", () => ReadAxisStatusCore(axis));
            if (status.Alarm || status.EmergencyStop || status.PositiveLimit || status.NegativeLimit)
            {
                StopAxisCore(axis, AxisStopMode.Immediate);
                throw new AxisControllerException($"{axis.Definition.Name} 运动中断：{DescribeBlockingStatus(status)}。");
            }

            var encoderError = Math.Abs(status.EncoderPosition - targetPosition);
            var commandError = Math.Abs(status.CommandPosition - targetPosition);
            var band = axis.Definition.PositionBand <= 0 ? 0.01 : axis.Definition.PositionBand;
            if (status.Ready && status.InPosition && Math.Min(encoderError, commandError) <= band)
            {
                return;
            }

            if (DateTimeOffset.Now - startedAt > effectiveTimeout)
            {
                StopAxisCore(axis, AxisStopMode.Smooth);
                throw new AxisControllerException($"{axis.Definition.Name} 运动超时：目标 {targetPosition:0.###}，反馈 {status.EncoderPosition:0.###}，指令 {status.CommandPosition:0.###}。");
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitHomeAsync(AdvantechAxisRuntime axis, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var effectiveTimeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = RunHardware($"{axis.Definition.Name} 等待回原", () => ReadAxisStatusCore(axis));
            if (status.Alarm || status.EmergencyStop)
            {
                StopAxisCore(axis, AxisStopMode.Immediate);
                throw new AxisControllerException($"{axis.Definition.Name} 回原中断：{DescribeBlockingStatus(status)}。");
            }

            if (status.Ready && status.Home)
            {
                return;
            }

            if (DateTimeOffset.Now - startedAt > effectiveTimeout)
            {
                StopAxisCore(axis, AxisStopMode.Smooth);
                throw new AxisControllerException($"{axis.Definition.Name} 回原超时。");
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void SetAxisSpeedCore(
        IntPtr axisHandle,
        double lowSpeed,
        double highSpeed,
        double acceleration,
        double deceleration,
        double pulsesPerUnit)
    {
        var velLow = Math.Max(0, lowSpeed) * pulsesPerUnit;
        var velHigh = Math.Max(1, highSpeed) * pulsesPerUnit;
        var acc = Math.Max(1, acceleration) * pulsesPerUnit;
        var dec = Math.Max(1, deceleration) * pulsesPerUnit;
        var size = (uint)Marshal.SizeOf<double>();
        Check(Motion.mAcm_SetProperty(axisHandle, (uint)PropertyID.PAR_AxVelLow, ref velLow, size), "设置研华轴低速");
        Check(Motion.mAcm_SetProperty(axisHandle, (uint)PropertyID.PAR_AxVelHigh, ref velHigh, size), "设置研华轴高速");
        Check(Motion.mAcm_SetProperty(axisHandle, (uint)PropertyID.PAR_AxAcc, ref acc, size), "设置研华轴加速度");
        Check(Motion.mAcm_SetProperty(axisHandle, (uint)PropertyID.PAR_AxDec, ref dec, size), "设置研华轴减速度");
    }

    private void StopAxisCore(AdvantechAxisRuntime axis, AxisStopMode stopMode)
    {
        var handle = GetAxisHandle(axis);
        var result = stopMode == AxisStopMode.Immediate
            ? Motion.mAcm_AxStopEmg(handle)
            : Motion.mAcm_AxStopDec(handle);
        Check(result, $"{axis.Definition.Name} 停止");
    }

    private AdvantechAxisRuntime GetAxis(string axisKey)
    {
        lock (_syncRoot)
        {
            if (_axes.TryGetValue(axisKey, out var axis))
            {
                return axis;
            }
        }

        throw new AxisControllerException($"未配置研华 PCI-1245 轴：{axisKey}");
    }

    private IoPointDefinition GetPoint(string pointKey)
    {
        lock (_syncRoot)
        {
            if (_points.TryGetValue(pointKey, out var point))
            {
                return point;
            }
        }

        throw new AxisControllerException($"未配置研华 PCI-1245 IO 点：{pointKey}");
    }

    private IntPtr GetAxisHandle(AdvantechAxisRuntime axis)
    {
        EnsureConnected();
        var session = GetSession(axis.Card);
        if (axis.AxisIndex < 0 || axis.AxisIndex >= session.AxisHandles.Length)
        {
            throw new AxisControllerException($"{axis.Definition.Name} 的轴号 {axis.Definition.AxisNo} 超出 PCI-1245 支持范围 1-{session.AxisHandles.Length}。");
        }

        var handle = session.AxisHandles[axis.AxisIndex];
        if (handle == IntPtr.Zero)
        {
            throw new AxisControllerException($"{axis.Definition.Name} 的研华轴句柄未打开。");
        }

        return handle;
    }

    private IntPtr GetDeviceHandle(IoPointDefinition point)
    {
        EnsureConnected();
        var card = ResolveCard(point.CardKey, point.CardNo, _cards, point.CardNo);
        if (card is null)
        {
            throw new AxisControllerException($"{point.Key} 未找到对应的研华 PCI-1245 卡。");
        }

        var session = GetSession(card);
        if (session.DeviceHandle == IntPtr.Zero)
        {
            throw new AxisControllerException($"{point.Key} 对应的研华 PCI-1245 卡 C{card.CardNo} 未打开。");
        }

        return session.DeviceHandle;
    }

    private IntPtr GetAxisIoHandle(IoPointDefinition point)
    {
        EnsureConnected();
        var card = ResolveCard(point.CardKey, point.CardNo, _cards, point.CardNo);
        if (card is null)
        {
            throw new AxisControllerException($"{point.Key} 未找到对应的研华 PCI-1245 卡。");
        }

        var session = GetSession(card);
        var axisNo = point.AxisNo <= 0 ? (short)1 : point.AxisNo;
        var axisIndex = axisNo - 1;
        if (axisIndex < 0 || axisIndex >= session.AxisHandles.Length)
        {
            throw new AxisControllerException($"{point.Key} 的轴上 IO 轴号 {axisNo} 超出 PCI-1245 支持范围 1-{session.AxisHandles.Length}。");
        }

        var handle = session.AxisHandles[axisIndex];
        if (handle == IntPtr.Zero)
        {
            throw new AxisControllerException($"{point.Key} 对应的研华轴 C{card.CardNo}/CH-{axisNo:00} 未打开。");
        }

        return handle;
    }

    private AdvantechCardSession GetSession(AxisCardDefinition card)
    {
        var cardKey = ResolveCardKey(card);
        if (_sessions.TryGetValue(cardKey, out var session))
        {
            return session;
        }

        throw new AxisControllerException($"研华 PCI-1245 卡 {cardKey} / C{card.CardNo} 未连接。");
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new AxisControllerException("研华 PCI-1245 尚未连接。");
        }
    }

    private static void EnsureAxisCanStartMotion(AdvantechAxisRuntime axis, AxisStatus status)
    {
        if (status.Alarm || status.EmergencyStop)
        {
            throw new AxisControllerException($"{axis.Definition.Name} 有报警或急停，不能运动：{DescribeBlockingStatus(status)}。");
        }

        if (!status.Ready || !status.InPosition)
        {
            throw new AxisControllerException($"{axis.Definition.Name} 未就绪或上一段运动未结束，请先等待到位或停止轴。");
        }
    }

    private static void EnsureAxisCanZero(AdvantechAxisRuntime axis, AxisStatus status)
    {
        if (status.Alarm || status.EmergencyStop)
        {
            throw new AxisControllerException($"{axis.Definition.Name} 有报警或急停，不能清零：{DescribeBlockingStatus(status)}。");
        }

        if (!status.Ready || !status.InPosition)
        {
            throw new AxisControllerException($"{axis.Definition.Name} 未就绪或位置未稳定，不能清零；请先等待到位或停止轴。");
        }
    }

    private static void ValidateTargetWithinSoftLimit(AxisPointDefinition axis, double position)
    {
        if (axis.SoftLimitNegative < axis.SoftLimitPositive &&
            (position < axis.SoftLimitNegative || position > axis.SoftLimitPositive))
        {
            throw new AxisControllerException($"{axis.Name} 目标位置 {position:0.###} 超出软限位 [{axis.SoftLimitNegative:0.###}, {axis.SoftLimitPositive:0.###}]。");
        }
    }

    private static IReadOnlyList<AxisCardDefinition> ResolveCards(DeviceConfiguration configuration)
    {
        return configuration.AxisCards
            .Where(card => card.Driver == AxisCardDriverKind.AdvantechPci1245)
            .Where(card => !string.IsNullOrWhiteSpace(card.Key))
            .Select(card => card with
            {
                AxisCount = ResolveAxisCount(card),
                InputCount = card.InputCount < 0 ? 0 : card.InputCount,
                OutputCount = card.OutputCount < 0 ? 0 : card.OutputCount
            })
            .OrderBy(card => card.CardNo)
            .ThenBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, AdvantechAxisRuntime> BuildAxisMap(
        DeviceConfiguration configuration,
        IReadOnlyList<AxisCardDefinition> cards)
    {
        return configuration.Axes
            .Where(axis => axis.Enabled && !string.IsNullOrWhiteSpace(axis.Key))
            .Select(axis => (Axis: axis, Card: ResolveAxisCard(axis, cards, configuration.GoogolCardNo)))
            .Where(item => item.Card is not null)
            .Select(item => new AdvantechAxisRuntime(
                item.Axis with
                {
                    Key = item.Axis.Key.Trim(),
                    Name = string.IsNullOrWhiteSpace(item.Axis.Name) ? item.Axis.Key.Trim() : item.Axis.Name.Trim(),
                    AxisNo = item.Axis.AxisNo <= 0 ? (short)1 : item.Axis.AxisNo,
                    PulsesPerUnit = item.Axis.PulsesPerUnit <= 0 ? 1 : item.Axis.PulsesPerUnit,
                    PositionBand = item.Axis.PositionBand <= 0 ? 0.01 : item.Axis.PositionBand
                },
                item.Card!,
                Math.Max(0, item.Axis.AxisNo - 1)))
            .GroupBy(axis => axis.Definition.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(axis => axis.Definition.Key, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, IoPointDefinition> BuildPointMap(
        DeviceConfiguration configuration,
        IReadOnlyList<AxisCardDefinition> cards)
    {
        return configuration.IoPoints
            .Where(point => point.Enabled
                && point.Source is IoPointSource.Onboard or IoPointSource.AxisOnboard
                && !string.IsNullOrWhiteSpace(point.Key))
            .Select(point => (Point: point, Card: ResolveCard(point.CardKey, point.CardNo, cards, configuration.GoogolCardNo)))
            .Where(item => item.Card is not null)
            .Select(item => item.Point with
            {
                Key = item.Point.Key.Trim(),
                Name = string.IsNullOrWhiteSpace(item.Point.Name) ? item.Point.Key.Trim() : item.Point.Name.Trim(),
                CardKey = ResolveCardKey(item.Card!),
                CardNo = item.Card!.CardNo,
                AxisNo = item.Point.Source == IoPointSource.AxisOnboard
                    ? item.Point.AxisNo <= 0 ? (short)1 : item.Point.AxisNo
                    : (short)-1,
                PointNo = item.Point.PointNo <= 0 ? (short)1 : item.Point.PointNo
            })
            .GroupBy(point => point.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(point => point.Key, StringComparer.OrdinalIgnoreCase);
    }

    private static AxisCardDefinition? ResolveAxisCard(
        AxisPointDefinition axis,
        IReadOnlyList<AxisCardDefinition> cards,
        short defaultCardNo)
    {
        return ResolveCard(axis.CardKey, axis.CardNo, cards, defaultCardNo);
    }

    private static AxisCardDefinition? ResolveCard(
        string? cardKey,
        short cardNo,
        IReadOnlyList<AxisCardDefinition> cards,
        short defaultCardNo)
    {
        cardKey = cardKey?.Trim();
        if (!string.IsNullOrWhiteSpace(cardKey))
        {
            var card = cards.FirstOrDefault(
                item => string.Equals(item.Key, cardKey, StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                return card;
            }
        }

        var resolvedCardNo = cardNo < 0 ? defaultCardNo : cardNo;
        return cards.FirstOrDefault(card => card.CardNo == resolvedCardNo);
    }

    private static int ResolveAxisCount(AxisCardDefinition card)
    {
        return Math.Clamp(card.AxisCount <= 0 ? MaxAxesPerCard : card.AxisCount, 1, MaxAxesPerCard);
    }

    private static string ResolveCardKey(AxisCardDefinition card)
    {
        return string.IsNullOrWhiteSpace(card.Key) ? $"card{card.CardNo}" : card.Key.Trim();
    }

    private static double ToPulse(AxisPointDefinition axis, double position)
    {
        return position * axis.PulsesPerUnit;
    }

    private static double FromPulse(AxisPointDefinition axis, double pulse)
    {
        return pulse / axis.PulsesPerUnit;
    }

    private static Advantech.Motion.HomeMode MapHomeMode(AxisHomeMode mode)
    {
        return mode switch
        {
            AxisHomeMode.Home => Advantech.Motion.HomeMode.MODE1_Abs,
            AxisHomeMode.HomeIndex => Advantech.Motion.HomeMode.MODE4_Abs_Ref,
            AxisHomeMode.Index => Advantech.Motion.HomeMode.MODE3_Ref,
            AxisHomeMode.Limit => Advantech.Motion.HomeMode.MODE2_Lmt,
            AxisHomeMode.LimitHome => Advantech.Motion.HomeMode.MODE8_LmtSearch,
            AxisHomeMode.LimitIndex => Advantech.Motion.HomeMode.MODE6_Lmt_Ref,
            AxisHomeMode.LimitHomeIndex => Advantech.Motion.HomeMode.MODE11_LmtSearch_Ref,
            _ => Advantech.Motion.HomeMode.MODE11_LmtSearch_Ref
        };
    }

    private static ushort ResolveBitIndex(IoPointDefinition point)
    {
        var bitIndex = point.PointNo - 1;
        if (bitIndex < 0)
        {
            throw new AxisControllerException($"{point.Key} 的点号必须从 1 开始。");
        }

        return (ushort)bitIndex;
    }

    private static ushort ResolveAxisInputBitIndex(IoPointDefinition point)
    {
        var bitIndex = point.PointNo - 1;
        if (bitIndex < 0)
        {
            throw new AxisControllerException($"{point.Key} 的轴上 DI 点号必须从 1 开始。");
        }

        return (ushort)bitIndex;
    }

    private static ushort ResolveAxisOutputBitIndex(IoPointDefinition point)
    {
        if (point.PointNo < 0)
        {
            throw new AxisControllerException($"{point.Key} 的轴上 DO 点号不能小于 0。");
        }

        return (ushort)point.PointNo;
    }

    private static bool FromRawValue(IoPointDefinition point, byte rawValue)
    {
        var value = rawValue != 0;
        return point.ActiveLow ? !value : value;
    }

    private static byte ToRawValue(IoPointDefinition point, bool value)
    {
        return (byte)(point.ActiveLow ? value ? 0 : 1 : value ? 1 : 0);
    }

    private T RunHardware<T>(string operation, Func<T> action)
    {
        lock (_syncRoot)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or FileNotFoundException or FileLoadException)
            {
                var message = $"{operation}失败：无法加载研华 AdvMotAPI 或底层运动控制驱动。请确认 Advantech Motion 驱动已安装、程序为 x64，并且 AdvMotAPI.dll 已在输出目录。";
                SetState(DeviceConnectionState.Faulted, message);
                throw new AxisControllerException(message, ex);
            }
            catch (SEHException ex)
            {
                var message = $"{operation}失败：研华运动控制库抛出非托管异常。请检查 PCI-1245 驱动、板卡状态、配置文件以及是否被其他软件占用。";
                SetState(DeviceConnectionState.Faulted, message);
                throw new AxisControllerException(message, ex);
            }
            catch (Exception ex)
            {
                SetState(DeviceConnectionState.Faulted, $"{operation}失败：{ex.Message}");
                throw;
            }
        }
    }

    private void RunHardware(string operation, Action action)
    {
        RunHardware(operation, () =>
        {
            action();
            return true;
        });
    }

    private void SetState(DeviceConnectionState state, string message)
    {
        _snapshot = new DeviceSnapshot("研华 PCI-1245", state, message, DateTimeOffset.Now);
        StateChanged?.Invoke(this, _snapshot);
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
        {
            throw new AxisControllerException($"{operation}失败：研华返回码={result}（{DescribeReturnCode(result)}）。");
        }
    }

    private static void Check(uint result, string operation)
    {
        if (result != (uint)ErrorCode.SUCCESS)
        {
            throw new AxisControllerException($"{operation}失败：研华返回码={result}（{DescribeReturnCode(result)}）。");
        }
    }

    private static string DescribeReturnCode(long result)
    {
        return Enum.GetName(typeof(ErrorCode), result) ?? "未知错误";
    }

    private static string DescribeAxisState(ushort state)
    {
        return Enum.GetName(typeof(AxisState), state) ?? $"未知状态 {state}";
    }

    private static string DescribeBlockingStatus(AxisStatus status)
    {
        if (status.Alarm)
        {
            return "伺服报警";
        }

        if (status.EmergencyStop)
        {
            return "急停";
        }

        if (status.PositiveLimit)
        {
            return "正限位";
        }

        if (status.NegativeLimit)
        {
            return "负限位";
        }

        return status.Message;
    }
}
