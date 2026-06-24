using System.Runtime.InteropServices;
using VisionStation.Domain;

namespace VisionStation.Devices;

public sealed record GoogolAxisControllerOptions
{
    public short CardNo { get; init; }

    public int AxisCount { get; init; } = 1;

    public string ConfigPath { get; init; } = string.Empty;

    public IReadOnlyList<GoogolCardOptions> Cards { get; init; } = Array.Empty<GoogolCardOptions>();

    public IReadOnlyList<GoogolAxisDefinition> Axes { get; init; } =
    [
        new GoogolAxisDefinition()
    ];
}

public sealed record GoogolCardOptions
{
    public short CardNo { get; init; }

    public int AxisCount { get; init; } = 8;

    public string ConfigPath { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
}

public sealed record GoogolAxisDefinition
{
    public string AxisKey { get; init; } = AxisDefaults.PrimaryAxisKey;

    public short CardNo { get; init; } = -1;

    public short AxisNo { get; init; } = 1;

    public double PulsesPerUnit { get; init; } = 1;

    public double PositionBand { get; init; } = 0.01;

    public AxisHomeMode HomeMode { get; init; } = AxisHomeMode.LimitHomeIndex;

    public bool HomePositive { get; init; }

    public double HomeOffset { get; init; }

    public double EscapeDistance { get; init; } = 10;

    public double HomeHighSpeed { get; init; } = 20;

    public double HomeLowSpeed { get; init; } = 5;

    public double HomeAcceleration { get; init; } = 100;

    public double HomeDeceleration { get; init; } = 100;
}

public class AxisControllerException : Exception
{
    public AxisControllerException(string message)
        : base(message)
    {
    }

    public AxisControllerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AxisControllerDisconnectedException : AxisControllerException
{
    public AxisControllerDisconnectedException(string message)
        : base(message)
    {
    }
}

public sealed class AxisControllerBusyException : AxisControllerException
{
    public AxisControllerBusyException(string message)
        : base(message)
    {
    }
}

public sealed class GoogolAxisController : IAxisController
{
    private readonly object _syncRoot = new();
    private GoogolAxisControllerOptions _options;
    private Dictionary<string, GoogolAxisDefinition> _axes;
    private short[] _openedCards = Array.Empty<short>();
    private bool _connected;
    private DeviceSnapshot _snapshot = new("Googol axis card", DeviceConnectionState.Disconnected, "Disconnected", DateTimeOffset.Now);

    public GoogolAxisController()
        : this(new GoogolAxisControllerOptions())
    {
    }

    public GoogolAxisController(GoogolAxisControllerOptions options)
    {
        _options = options;
        _axes = BuildAxisMap(options.Axes);
    }

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public DeviceSnapshot Snapshot => _snapshot;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunHardware("Connect Googol axis cards", ConnectCardsCore);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected)
        {
            SetState(DeviceConnectionState.Disconnected, "Googol axis cards disconnected");
            return Task.CompletedTask;
        }

        RunHardware("Disconnect Googol axis cards", () =>
        {
            CloseOpenedCardsCore();
            _connected = false;
            SetState(DeviceConnectionState.Disconnected, "Googol axis cards disconnected");
        });
        return Task.CompletedTask;
    }

    public Task ServoOnAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        var cardNo = GetAxisCardNo(axis);
        cancellationToken.ThrowIfCancellationRequested();

        RunHardware($"{axis.AxisKey} servo on", () =>
        {
            EnsureConnected();
            Check(Native.GT_AxisOn(cardNo, axis.AxisNo), $"{axis.AxisKey} servo on");
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} servo enabled");
        });
        return Task.CompletedTask;
    }

    public Task ServoOffAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        var cardNo = GetAxisCardNo(axis);
        cancellationToken.ThrowIfCancellationRequested();

        RunHardware($"{axis.AxisKey} servo off", () =>
        {
            EnsureConnected();
            Check(Native.GT_AxisOff(cardNo, axis.AxisNo), $"{axis.AxisKey} servo off");
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} servo disabled");
        });
        return Task.CompletedTask;
    }

    public async Task ClearAlarmAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        var cardNo = GetAxisCardNo(axis);
        cancellationToken.ThrowIfCancellationRequested();

        RunHardware($"{axis.AxisKey} clear alarm", () =>
        {
            EnsureConnected();
            Check(Native.GT_ClrSts(cardNo, axis.AxisNo, 1), $"{axis.AxisKey} clear status");
            Check(Native.GT_SetDoBit(cardNo, Native.McClear, axis.AxisNo, 0), $"{axis.AxisKey} clear output on");
        });

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        RunHardware($"{axis.AxisKey} reset clear output", () =>
        {
            Check(Native.GT_SetDoBit(cardNo, Native.McClear, axis.AxisNo, 1), $"{axis.AxisKey} clear output off");
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} alarm cleared");
        });
    }

    public Task ZeroPositionAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        var cardNo = GetAxisCardNo(axis);
        cancellationToken.ThrowIfCancellationRequested();

        RunHardware($"{axis.AxisKey} zero position", () =>
        {
            EnsureConnected();
            var status = ReadAxisStatusCore(axis);
            EnsureAxisHasNoBlockingFault(axis, status);

            Check(Native.GT_ZeroPos(cardNo, axis.AxisNo, 1), $"{axis.AxisKey} zero position");
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} position zeroed");
        });

        return Task.CompletedTask;
    }

    public Task HomeAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        return HomeAsync(CreateHomeCommand(axis), cancellationToken);
    }

    public async Task HomeAsync(AxisHomeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var axis = GetAxis(command.AxisKey);
        var cardNo = GetAxisCardNo(axis);
        var homePrm = BuildHomePrm(axis, command);

        RunHardware($"{axis.AxisKey} home", () =>
        {
            EnsureConnected();
            Check(Native.GT_ZeroPos(cardNo, axis.AxisNo, 1), $"{axis.AxisKey} zero before home");
            Check(Native.GT_ClrSts(cardNo, axis.AxisNo, 1), $"{axis.AxisKey} clear before home");
            StopAxisCore(axis, AxisStopMode.Immediate);
            Check(Native.GT_GoHome(cardNo, axis.AxisNo, ref homePrm), $"{axis.AxisKey} start SmartHome");
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} homing");
        });

        await WaitHomeAsync(axis, command.Timeout, cancellationToken).ConfigureAwait(false);

        RunHardware($"{axis.AxisKey} finish home", () =>
        {
            Check(Native.GT_ZeroPos(cardNo, axis.AxisNo, 1), $"{axis.AxisKey} zero after home");
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} home completed");
        });
    }

    public async Task MoveAbsoluteAsync(AxisMoveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var axis = GetAxis(command.AxisKey);
        var cardNo = GetAxisCardNo(axis);
        var targetPulse = ToPulse(axis, command.Position);
        var velocity = ToVelocity(axis, command.Speed);
        var acceleration = ToAcceleration(axis, command.Acceleration);
        var deceleration = ToAcceleration(axis, command.Deceleration <= 0 ? command.Acceleration : command.Deceleration);

        if (velocity <= 0 || acceleration <= 0 || deceleration <= 0)
        {
            throw new AxisControllerException($"{axis.AxisKey} invalid speed or acceleration.");
        }

        RunHardware($"{axis.AxisKey} absolute move", () =>
        {
            EnsureConnected();
            var status = ReadAxisStatusCore(axis);
            if (status.Alarm || status.EmergencyStop)
            {
                throw new AxisControllerException($"{axis.AxisKey} has alarm or emergency stop.");
            }

            Check(Native.GT_ClrSts(cardNo, 1, (short)GetAxisCount(cardNo)), $"Clear card {cardNo} status");
            Check(Native.GT_PrfTrap(cardNo, axis.AxisNo), $"{axis.AxisKey} set trap mode");
            Check(Native.GT_GetTrapPrm(cardNo, axis.AxisNo, out var trapPrm), $"{axis.AxisKey} get trap parameters");
            trapPrm.acc = acceleration;
            trapPrm.dec = deceleration;
            trapPrm.velStart = 0;
            Check(Native.GT_SetTrapPrm(cardNo, axis.AxisNo, ref trapPrm), $"{axis.AxisKey} set trap parameters");
            Check(Native.GT_SetPos(cardNo, axis.AxisNo, targetPulse), $"{axis.AxisKey} set target position");
            Check(Native.GT_SetVel(cardNo, axis.AxisNo, velocity), $"{axis.AxisKey} set target velocity");
            Check(Native.GT_Update(cardNo, 1 << (axis.AxisNo - 1)), $"{axis.AxisKey} start absolute move");
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} moving to {command.Position:0.###}");
        });

        if (command.WaitForCompletion)
        {
            await WaitReadyAsync(axis, command.Position, command.Timeout, cancellationToken).ConfigureAwait(false);
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} reached {command.Position:0.###}");
        }
    }

    public async Task MoveLinearInterpolationAsync(AxisLinearInterpolationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var axes = ResolveInterpolationAxes(command);
        var cardNo = GetAxisCardNo(axes[0]);
        var targetPulses = command.Targets.Zip(axes, (target, axis) => ToPulse(axis, target.Position)).ToArray();
        var velocity = ToVelocity(axes[0], command.Speed);
        var acceleration = ToAcceleration(axes[0], command.Acceleration);
        var endVelocity = ToVelocity(axes[0], command.EndVelocity);

        cancellationToken.ThrowIfCancellationRequested();
        ValidateInterpolationCommand(command, velocity, acceleration);

        RunHardware($"{axes.Length}-axis linear interpolation", () =>
        {
            EnsureConnected();
            EnsureInterpolationAxesReady(axes);

            Check(Native.GT_ClrSts(cardNo, 1, (short)GetAxisCount(cardNo)), $"Clear card {cardNo} status");
            var crdPrm = BuildCrdPrm(axes, velocity, acceleration);
            Check(Native.GT_SetCrdPrm(cardNo, command.CoordinateSystem, ref crdPrm), "Set coordinate parameters");
            Check(Native.GT_CrdClear(cardNo, command.CoordinateSystem, command.Fifo), "Clear coordinate FIFO");
            Check(Native.GT_CrdSpace(cardNo, command.CoordinateSystem, out var space, command.Fifo), "Read coordinate FIFO space");
            var requiredSpace = GetRequiredCrdSpace(command);
            if (space < requiredSpace)
            {
                throw new AxisControllerException($"Coordinate FIFO space is not enough. Required={requiredSpace}, available={space}.");
            }

            EnqueueBufferedOutputActions(cardNo, command, AxisInterpolationBufferedActionTiming.BeforeMotion);
            EnqueueLinearSegment(cardNo, command, targetPulses, velocity, acceleration, endVelocity);
            EnqueueBufferedOutputActions(cardNo, command, AxisInterpolationBufferedActionTiming.AfterMotion);
            Check(
                Native.GT_CrdStart(cardNo, GetCoordinateStartMask(command.CoordinateSystem), GetCoordinateStartOption(command.CoordinateSystem, command.Fifo)),
                "Start coordinate interpolation");
            SetState(DeviceConnectionState.Connected, $"{axes.Length}-axis linear interpolation started");
        });

        if (command.WaitForCompletion)
        {
            await WaitInterpolationReadyAsync(axes, command.CoordinateSystem, command.Fifo, command.Timeout, cancellationToken).ConfigureAwait(false);
            SetState(DeviceConnectionState.Connected, $"{axes.Length}-axis linear interpolation completed");
        }
    }

    public Task StartJogAsync(AxisJogCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var axis = GetAxis(command.AxisKey);
        var cardNo = GetAxisCardNo(axis);
        var velocity = ToVelocity(axis, command.Speed);
        var acceleration = ToAcceleration(axis, command.Acceleration);
        var deceleration = ToAcceleration(axis, command.Deceleration <= 0 ? command.Acceleration : command.Deceleration);
        cancellationToken.ThrowIfCancellationRequested();

        if (velocity <= 0 || acceleration <= 0 || deceleration <= 0)
        {
            throw new AxisControllerException($"{axis.AxisKey} invalid Jog speed or acceleration.");
        }

        RunHardware($"{axis.AxisKey} Jog", () =>
        {
            EnsureConnected();
            var status = ReadAxisStatusCore(axis);
            if (status.Alarm || status.EmergencyStop)
            {
                throw new AxisControllerException($"{axis.AxisKey} has alarm or emergency stop.");
            }

            Check(Native.GT_ClrSts(cardNo, 1, (short)GetAxisCount(cardNo)), $"Clear card {cardNo} status");
            Check(Native.GT_PrfJog(cardNo, axis.AxisNo), $"{axis.AxisKey} set Jog mode");
            Check(Native.GT_GetJogPrm(cardNo, axis.AxisNo, out var jogPrm), $"{axis.AxisKey} get Jog parameters");
            jogPrm.acc = acceleration;
            jogPrm.dec = deceleration;
            jogPrm.smooth = 0;
            Check(Native.GT_SetJogPrm(cardNo, axis.AxisNo, ref jogPrm), $"{axis.AxisKey} set Jog parameters");
            Check(Native.GT_SetVel(cardNo, axis.AxisNo, command.Direction == AxisJogDirection.Positive ? velocity : -velocity), $"{axis.AxisKey} set Jog velocity");
            Check(Native.GT_Update(cardNo, 1 << (axis.AxisNo - 1)), $"{axis.AxisKey} start Jog");
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} Jog {(command.Direction == AxisJogDirection.Positive ? "+" : "-")} running");
        });

        return Task.CompletedTask;
    }

    public Task StopJogAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default)
    {
        return StopAsync(axisKey, AxisStopMode.Smooth, cancellationToken);
    }

    public Task StopAsync(string axisKey = AxisDefaults.PrimaryAxisKey, AxisStopMode stopMode = AxisStopMode.Smooth, CancellationToken cancellationToken = default)
    {
        var axis = GetAxis(axisKey);
        cancellationToken.ThrowIfCancellationRequested();

        RunHardware($"{axis.AxisKey} stop", () =>
        {
            EnsureConnected();
            StopAxisCore(axis, stopMode);
            SetState(DeviceConnectionState.Connected, $"{axis.AxisKey} stopped");
        });

        return Task.CompletedTask;
    }

    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RunHardware("Emergency stop Googol axis cards", () =>
        {
            EnsureConnected();
            StopAxisGroupCore(_axes.Values, AxisStopMode.Immediate);
            SetState(DeviceConnectionState.Faulted, "Googol axis emergency stop triggered");
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

    public Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (configuration.AxisController != AxisControllerKind.Googol)
        {
            ApplyDisabledConfiguration(configuration.AxisController);
            return Task.CompletedTask;
        }

        lock (_syncRoot)
        {
            Exception? closeFailure = null;
            if (_connected)
            {
                try
                {
                    CloseOpenedCardsCore();
                }
                catch (Exception ex)
                {
                    closeFailure = ex;
                }
                finally
                {
                    _connected = false;
                    _openedCards = Array.Empty<short>();
                }
            }

            _options = CreateOptions(configuration);
            _axes = BuildAxisMap(_options.Axes);
            var message = closeFailure is null
                ? "固高轴卡配置已更新，请重新连接轴卡。"
                : $"固高轴卡配置已更新，请重新连接轴卡。上一次硬件会话关闭不完整：{DescribeHardwareException(closeFailure)}";
            SetState(DeviceConnectionState.Disconnected, message);
        }

        return Task.CompletedTask;
    }

    private void ApplyDisabledConfiguration(AxisControllerKind configuredKind)
    {
        lock (_syncRoot)
        {
            Exception? closeFailure = null;
            if (_connected)
            {
                try
                {
                    CloseOpenedCardsCore();
                }
                catch (Exception ex)
                {
                    closeFailure = ex;
                }
            }

            _connected = false;
            _openedCards = Array.Empty<short>();
            _options = new GoogolAxisControllerOptions();
            _axes = BuildAxisMap(_options.Axes);

            var message = closeFailure is null
                ? $"当前配置为 {configuredKind}，固高轴卡控制器已停用；切换控制器类型需要重启软件。"
                : $"当前配置为 {configuredKind}，固高轴卡控制器已停用；切换控制器类型需要重启软件。上一次硬件会话关闭不完整：{DescribeHardwareException(closeFailure)}";
            SetState(DeviceConnectionState.Disconnected, message);
        }
    }

    private AxisStatus ReadAxisStatusCore(GoogolAxisDefinition axis)
    {
        var cardNo = GetAxisCardNo(axis);
        Check(Native.GT_GetSts(cardNo, axis.AxisNo, out var axisSts, 1, out _), $"{axis.AxisKey} read axis status");
        Check(Native.GT_GetDi(cardNo, Native.McHome, out var homeSts), $"{axis.AxisKey} read home input");
        Check(Native.GT_GetPrfPos(cardNo, axis.AxisNo, out var commandPulse, 1, out _), $"{axis.AxisKey} read command position");
        Check(Native.GT_GetEncPos(cardNo, axis.AxisNo, out var encoderPulse, 1, out _), $"{axis.AxisKey} read encoder position");

        var home = !IsBitSet(homeSts, axis.AxisNo - 1);
        return new AxisStatus
        {
            AxisKey = axis.AxisKey,
            CommandPosition = FromPulse(axis, commandPulse),
            EncoderPosition = FromPulse(axis, encoderPulse),
            Alarm = IsBitSet(axisSts, 1),
            PositiveLimit = IsBitSet(axisSts, 5),
            NegativeLimit = IsBitSet(axisSts, 6),
            EmergencyStop = IsBitSet(axisSts, 8),
            ServoOn = IsBitSet(axisSts, 9),
            Ready = !IsBitSet(axisSts, 10),
            InPosition = IsBitSet(axisSts, 11),
            Home = home,
            Homed = home,
            Message = "Googol axis status refreshed",
            Timestamp = DateTimeOffset.Now
        };
    }

    private static void EnsureAxisHasNoBlockingFault(GoogolAxisDefinition axis, AxisStatus status)
    {
        if (status.Alarm || status.EmergencyStop)
        {
            throw new AxisControllerException($"{axis.AxisKey} has alarm or emergency stop.");
        }
    }

    private static bool IsAxisAtTarget(GoogolAxisDefinition axis, AxisStatus status, double targetPosition)
    {
        var positionBand = Math.Max(Math.Abs(axis.PositionBand), 0.001);
        return
            Math.Abs(status.CommandPosition - targetPosition) <= positionBand &&
            Math.Abs(status.EncoderPosition - targetPosition) <= positionBand;
    }

    private async Task WaitReadyAsync(GoogolAxisDefinition axis, double targetPosition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = ReadAxisStatusCore(axis);
            if (status.Alarm || status.EmergencyStop)
            {
                throw new AxisControllerException($"{axis.AxisKey} alarm or emergency stop during motion.");
            }

            if (IsAxisAtTarget(axis, status, targetPosition))
            {
                return;
            }

            if (DateTimeOffset.Now - startedAt > timeout)
            {
                await StopAsync(axis.AxisKey, AxisStopMode.Smooth, CancellationToken.None).ConfigureAwait(false);
                throw new TimeoutException($"{axis.AxisKey} move timeout.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitHomeAsync(GoogolAxisDefinition axis, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var cardNo = GetAxisCardNo(axis);
        var startedAt = DateTimeOffset.Now;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(Native.GT_GetHomeStatus(cardNo, axis.AxisNo, out var homeStatus), $"{axis.AxisKey} read home status");
            if (homeStatus.error != 0)
            {
                throw new AxisControllerException($"{axis.AxisKey} home failed. Error={homeStatus.error}.");
            }

            if (homeStatus.run != 1 || homeStatus.stage == 100)
            {
                return;
            }

            if (DateTimeOffset.Now - startedAt > timeout)
            {
                await StopAsync(axis.AxisKey, AxisStopMode.Immediate, CancellationToken.None).ConfigureAwait(false);
                throw new TimeoutException($"{axis.AxisKey} home timeout.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitInterpolationReadyAsync(IReadOnlyList<GoogolAxisDefinition> axes, short coordinateSystem, short fifo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var cardNo = GetAxisCardNo(axes[0]);
        var startedAt = DateTimeOffset.Now;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(Native.GT_CrdStatus(cardNo, coordinateSystem, out var run, out var segment, fifo), "Read interpolation status");

            foreach (var axis in axes)
            {
                var status = ReadAxisStatusCore(axis);
                if (status.Alarm || status.EmergencyStop || status.PositiveLimit || status.NegativeLimit)
                {
                    StopAxisGroupCore(axes, AxisStopMode.Immediate);
                    var reason = status.Alarm ? "alarm" : status.EmergencyStop ? "emergency stop" : status.PositiveLimit ? "positive limit" : "negative limit";
                    var message = $"{axis.AxisKey} triggered {reason} during interpolation; stopped immediately.";
                    SetState(DeviceConnectionState.Faulted, message);
                    throw new AxisControllerException(message);
                }
            }

            if (run == 0)
            {
                return;
            }

            if (DateTimeOffset.Now - startedAt > timeout)
            {
                StopAxisGroupCore(axes, AxisStopMode.Immediate);
                var message = $"Coordinate system {coordinateSystem} interpolation timeout; stopped immediately. Last segment={segment}.";
                SetState(DeviceConnectionState.Faulted, message);
                throw new TimeoutException(message);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private GoogolAxisDefinition[] ResolveInterpolationAxes(AxisLinearInterpolationCommand command)
    {
        if (command.Targets.Count is < 2 or > 4)
        {
            throw new AxisControllerException("Googol linear interpolation requires 2 to 4 axes.");
        }

        var duplicate = command.Targets.GroupBy(target => target.AxisKey, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new AxisControllerException($"Duplicate interpolation axis: {duplicate.Key}");
        }

        var axes = command.Targets.Select(target => GetAxis(target.AxisKey)).ToArray();
        var cardNos = axes.Select(GetAxisCardNo).Distinct().ToArray();
        if (cardNos.Length > 1)
        {
            throw new AxisControllerException($"Googol linear interpolation requires axes on the same card. Selected cards: {string.Join(", ", cardNos)}");
        }

        return axes;
    }

    private static void ValidateInterpolationCommand(AxisLinearInterpolationCommand command, double velocity, double acceleration)
    {
        if (command.CoordinateSystem is < 1 or > 2)
        {
            throw new AxisControllerException("Googol coordinate system must be 1 or 2.");
        }

        if (command.Fifo is < 0 or > 1)
        {
            throw new AxisControllerException("Googol interpolation FIFO must be 0 or 1.");
        }

        if (velocity <= 0 || acceleration <= 0)
        {
            throw new AxisControllerException("Invalid interpolation speed or acceleration.");
        }
    }

    private void EnsureInterpolationAxesReady(IReadOnlyList<GoogolAxisDefinition> axes)
    {
        foreach (var axis in axes)
        {
            var status = ReadAxisStatusCore(axis);
            if (status.Alarm || status.EmergencyStop || status.PositiveLimit || status.NegativeLimit)
            {
                var reason = status.Alarm ? "alarm" : status.EmergencyStop ? "emergency stop" : status.PositiveLimit ? "positive limit" : "negative limit";
                throw new AxisControllerException($"{axis.AxisKey} currently has {reason}; interpolation is not allowed.");
            }
        }
    }

    private Native.TCrdPrm BuildCrdPrm(IReadOnlyList<GoogolAxisDefinition> axes, double velocity, double acceleration)
    {
        var prm = new Native.TCrdPrm
        {
            dimension = (short)axes.Count,
            synVelMax = velocity,
            synAccMax = acceleration,
            evenTime = 0,
            setOriginFlag = 1
        };

        for (var i = 0; i < axes.Count; i++)
        {
            SetCrdProfile(ref prm, axes[i].AxisNo, (short)(i + 1));
        }

        return prm;
    }

    private static void SetCrdProfile(ref Native.TCrdPrm prm, short axisNo, short coordinateIndex)
    {
        switch (axisNo)
        {
            case 1: prm.profile1 = coordinateIndex; break;
            case 2: prm.profile2 = coordinateIndex; break;
            case 3: prm.profile3 = coordinateIndex; break;
            case 4: prm.profile4 = coordinateIndex; break;
            case 5: prm.profile5 = coordinateIndex; break;
            case 6: prm.profile6 = coordinateIndex; break;
            case 7: prm.profile7 = coordinateIndex; break;
            case 8: prm.profile8 = coordinateIndex; break;
            default:
                throw new AxisControllerException($"Googol interpolation currently supports axes 1 to 8. Axis={axisNo}.");
        }
    }

    private void EnqueueLinearSegment(short cardNo, AxisLinearInterpolationCommand command, IReadOnlyList<int> targetPulses, double velocity, double acceleration, double endVelocity)
    {
        switch (targetPulses.Count)
        {
            case 2:
                Check(Native.GT_LnXY(cardNo, command.CoordinateSystem, targetPulses[0], targetPulses[1], velocity, acceleration, endVelocity, command.Fifo), "Enqueue XY linear segment");
                break;
            case 3:
                Check(Native.GT_LnXYZ(cardNo, command.CoordinateSystem, targetPulses[0], targetPulses[1], targetPulses[2], velocity, acceleration, endVelocity, command.Fifo), "Enqueue XYZ linear segment");
                break;
            case 4:
                Check(Native.GT_LnXYZA(cardNo, command.CoordinateSystem, targetPulses[0], targetPulses[1], targetPulses[2], targetPulses[3], velocity, acceleration, endVelocity, command.Fifo), "Enqueue XYZA linear segment");
                break;
            default:
                throw new AxisControllerException("Googol linear interpolation requires 2 to 4 axes.");
        }
    }

    private void EnqueueBufferedOutputActions(short cardNo, AxisLinearInterpolationCommand command, AxisInterpolationBufferedActionTiming timing)
    {
        foreach (var action in command.BufferedOutputActions.Where(action => action.Timing == timing))
        {
            if (action.PointNo is < 1 or > 16)
            {
                throw new AxisControllerException($"Buffered IO point number must be 1 to 16. Point={action.PointNo}.");
            }

            var bit = action.PointNo - 1;
            var mask = (ushort)(1 << bit);
            var value = action.Value ? mask : (ushort)0;
            Check(Native.GT_BufIO(cardNo, command.CoordinateSystem, (ushort)action.DoType, mask, value, command.Fifo), $"Enqueue buffered IO DO{action.PointNo}");
            if (action.DelayMilliseconds > 0)
            {
                Check(Native.GT_BufDelay(cardNo, command.CoordinateSystem, action.DelayMilliseconds, command.Fifo), $"Enqueue buffered delay {action.DelayMilliseconds}ms");
            }
        }
    }

    private static int GetRequiredCrdSpace(AxisLinearInterpolationCommand command)
    {
        var ioActions = command.BufferedOutputActions.Count;
        var delays = command.BufferedOutputActions.Count(action => action.DelayMilliseconds > 0);
        return 1 + ioActions + delays;
    }

    private static short GetCoordinateStartMask(short coordinateSystem) => (short)(1 << (coordinateSystem - 1));

    private static short GetCoordinateStartOption(short coordinateSystem, short fifo) => fifo == 0 ? (short)0 : (short)(1 << (coordinateSystem - 1));

    private void StopAxisCore(GoogolAxisDefinition axis, AxisStopMode stopMode)
    {
        StopAxisGroupCore(new[] { axis }, stopMode);
    }

    private void StopAxisGroupCore(IEnumerable<GoogolAxisDefinition> axes, AxisStopMode stopMode)
    {
        foreach (var group in axes.GroupBy(GetAxisCardNo))
        {
            var mask = group.Aggregate(0, (current, axis) => current | (1 << (axis.AxisNo - 1)));
            var option = stopMode == AxisStopMode.Immediate ? mask : 0;
            Check(Native.GT_Stop(group.Key, mask, option), $"Stop card {group.Key} axes");
        }
    }

    private Native.THomePrm BuildHomePrm(GoogolAxisDefinition axis, AxisHomeCommand command)
    {
        return new Native.THomePrm
        {
            mode = MapHomeMode(command.HomeMode),
            moveDir = command.HomePositive ? (short)1 : (short)-1,
            indexDir = command.HomePositive ? (short)-1 : (short)1,
            edge = 0,
            velHigh = ToVelocity(axis, command.HighSpeed),
            velLow = ToVelocity(axis, command.LowSpeed),
            acc = ToAcceleration(axis, command.Acceleration),
            dec = ToAcceleration(axis, command.Deceleration),
            smoothTime = 10,
            pad1_1 = 1,
            pad2_1 = 1,
            homeOffset = ToPulse(axis, command.HomeOffset),
            escapeStep = ToPulse(axis, command.EscapeDistance)
        };
    }

    private static AxisHomeCommand CreateHomeCommand(GoogolAxisDefinition axis)
    {
        return new AxisHomeCommand
        {
            AxisKey = axis.AxisKey,
            HomeMode = axis.HomeMode,
            HomePositive = axis.HomePositive,
            HomeOffset = axis.HomeOffset,
            EscapeDistance = axis.EscapeDistance,
            HighSpeed = axis.HomeHighSpeed,
            LowSpeed = axis.HomeLowSpeed,
            Acceleration = axis.HomeAcceleration,
            Deceleration = axis.HomeDeceleration
        };
    }

    private void ConnectCardsCore()
    {
        var cards = ResolveCardOptions().ToArray();
        if (cards.Length == 0)
        {
            throw new AxisControllerException("No Googol axis card is configured.");
        }

        var opened = new List<short>();
        try
        {
            foreach (var card in cards)
            {
                if (string.IsNullOrWhiteSpace(card.ConfigPath) || !File.Exists(card.ConfigPath))
                {
                    throw new AxisControllerException($"Googol card {card.CardNo} config file does not exist: {card.ConfigPath}");
                }

                Check(Native.GT_SetCardNo(card.CardNo, card.CardNo), $"Set Googol card number {card.CardNo} from physical index {card.CardNo}");
                var openDetails = OpenCardCore(card.CardNo);
                opened.Add(card.CardNo);
                Check(Native.GT_Reset(card.CardNo), $"Reset Googol card {card.CardNo} after {openDetails}. gts.dll={GetLoadedNativeModulePath()}");
                Check(Native.GT_LoadConfig(card.CardNo, card.ConfigPath), $"Load Googol card {card.CardNo} config: {card.ConfigPath}");
                Check(Native.GT_ClrSts(card.CardNo, 1, (short)Math.Max(card.AxisCount, 1)), $"Clear Googol card {card.CardNo} status");
            }
        }
        catch
        {
            foreach (var cardNo in opened)
            {
                TryCloseCardSilently(cardNo);
            }

            _openedCards = Array.Empty<short>();
            _connected = false;
            throw;
        }

        _openedCards = opened.Distinct().OrderBy(cardNo => cardNo).ToArray();
        _connected = true;
        SetState(DeviceConnectionState.Connected, $"Googol axis cards connected: {string.Join(", ", _openedCards)}");
    }

    private static string OpenCardCore(short cardNo)
    {
        const short channel = 0;
        const short param = 1;
        Check(
            Native.GT_Open(cardNo, channel, param),
            $"Open Googol card {cardNo} with GT_Open. gts.dll={GetLoadedNativeModulePath()}");
        return $"GT_Open(cardNo={cardNo}, channel={channel}, param={param})";
    }

    private void CloseOpenedCardsCore()
    {
        foreach (var cardNo in _openedCards)
        {
            Check(Native.GT_Close(cardNo), $"Close Googol card {cardNo}");
        }

        _openedCards = Array.Empty<short>();
    }

    private IReadOnlyList<GoogolCardOptions> ResolveCardOptions()
    {
        if (_options.Cards.Count != 0)
        {
            return _options.Cards
                .GroupBy(card => card.CardNo)
                .Select(group => group.First() with
                {
                    AxisCount = group.First().AxisCount <= 0 ? 8 : group.First().AxisCount,
                    ConfigPath = string.IsNullOrWhiteSpace(group.First().ConfigPath) ? _options.ConfigPath : group.First().ConfigPath
                })
                .OrderBy(card => card.CardNo)
                .ToArray();
        }

        return
        [
            new GoogolCardOptions
            {
                CardNo = _options.CardNo,
                AxisCount = _options.AxisCount,
                ConfigPath = _options.ConfigPath
            }
        ];
    }

    private short GetAxisCardNo(GoogolAxisDefinition axis) => axis.CardNo < 0 ? _options.CardNo : axis.CardNo;

    private int GetAxisCount(short cardNo)
    {
        return ResolveCardOptions().FirstOrDefault(card => card.CardNo == cardNo)?.AxisCount ?? Math.Max(_options.AxisCount, 1);
    }

    private GoogolAxisDefinition GetAxis(string axisKey)
    {
        if (_axes.TryGetValue(axisKey, out var axis))
        {
            return axis;
        }

        throw new AxisControllerException($"Axis is not configured: {axisKey}");
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new AxisControllerDisconnectedException($"Googol axis card is not connected. Last state: {_snapshot.Message}");
        }
    }

    private void RunHardware(string operation, Action action)
    {
        lock (_syncRoot)
        {
            try
            {
                action();
            }
            catch (AxisControllerDisconnectedException ex)
            {
                SetState(DeviceConnectionState.Disconnected, $"{operation} failed: {ex.Message}");
                throw;
            }
            catch (AxisControllerBusyException ex)
            {
                SetState(DeviceConnectionState.Connected, $"{operation} ignored: {ex.Message}");
                throw;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                var message = $"{operation} failed: cannot load gts.dll. Check process bitness and PATH.";
                SetState(DeviceConnectionState.Faulted, message);
                throw new AxisControllerException(message, ex);
            }
            catch (SEHException ex)
            {
                var message = $"{operation} failed: gts.dll raised an unmanaged exception. Check the Googol driver, card state, config file, and whether another process is using the card.";
                SetState(DeviceConnectionState.Faulted, message);
                throw new AxisControllerException(message, ex);
            }
            catch (Exception ex)
            {
                SetState(DeviceConnectionState.Faulted, $"{operation} failed: {ex.Message}");
                throw;
            }
        }
    }

    private void SetState(DeviceConnectionState state, string message)
    {
        _snapshot = new DeviceSnapshot("Googol axis card", state, message, DateTimeOffset.Now);
        StateChanged?.Invoke(this, _snapshot);
    }

    private static void Check(short result, string operation)
    {
        if (result != 0)
        {
            throw new AxisControllerException($"{operation} failed. Googol return code={result} ({DescribeReturnCode(result)}).");
        }
    }

    private static string DescribeHardwareException(Exception exception)
    {
        return exception is SEHException
            ? "gts.dll raised an unmanaged exception; verify the Googol driver/card state and close any other process using the card."
            : exception.Message;
    }

    private static void TryCloseCardSilently(short cardNo)
    {
        try
        {
            Native.GT_Close(cardNo);
        }
        catch
        {
            // Best-effort cleanup after a failed open; keep the original hardware error visible.
        }
    }

    private static string GetLoadedNativeModulePath()
    {
        var handle = Native.GetModuleHandle("gts.dll");
        if (handle == IntPtr.Zero)
        {
            return "not loaded";
        }

        var buffer = new char[1024];
        var length = Native.GetModuleFileName(handle, buffer, buffer.Length);
        return length == 0 ? "loaded path unavailable" : new string(buffer, 0, length);
    }

    private static string DescribeReturnCode(short result)
    {
        return result switch
        {
            -2 => "read data length error",
            -3 => "read data checksum error",
            -4 => "write data block error",
            -5 => "read data block error",
            -6 => "open/close device error: card missing, driver unavailable, card count mismatch, or PCI communication creation failed",
            -7 => "DSP busy",
            -8 => "multithread resource busy or PCI communication timeout",
            -16 => "GT_OpenDevice is unavailable for the current driver, DLL, card type, or card index; falling back to GT_Open may be required",
            1 => "invalid command call or prerequisite command not completed",
            7 => "parameter error",
            8 => "DSP firmware does not support this command",
            _ => "see Googol GTS manual"
        };
    }

    private static int ToPulse(GoogolAxisDefinition axis, double position)
    {
        return checked((int)Math.Round(position * axis.PulsesPerUnit, MidpointRounding.AwayFromZero));
    }

    private static double FromPulse(GoogolAxisDefinition axis, double pulse)
    {
        return axis.PulsesPerUnit == 0 ? pulse : pulse / axis.PulsesPerUnit;
    }

    private static double ToVelocity(GoogolAxisDefinition axis, double speed)
    {
        return Math.Abs(speed * axis.PulsesPerUnit / 1000.0);
    }

    private static double ToAcceleration(GoogolAxisDefinition axis, double acceleration)
    {
        return Math.Abs(acceleration * axis.PulsesPerUnit / 1_000_000.0);
    }

    private static bool IsBitSet(int value, int bit) => ((value >> bit) & 0x01) == 1;

    private static short MapHomeMode(AxisHomeMode mode)
    {
        return mode switch
        {
            AxisHomeMode.Home => Native.HomeModeHome,
            AxisHomeMode.HomeIndex => Native.HomeModeHomeIndex,
            AxisHomeMode.Index => Native.HomeModeIndex,
            AxisHomeMode.Limit => Native.HomeModeLimit,
            AxisHomeMode.LimitHome => Native.HomeModeLimitHome,
            AxisHomeMode.LimitIndex => Native.HomeModeLimitIndex,
            AxisHomeMode.LimitHomeIndex => Native.HomeModeLimitHomeIndex,
            _ => Native.HomeModeLimitHomeIndex
        };
    }

    private static Dictionary<string, GoogolAxisDefinition> BuildAxisMap(IReadOnlyList<GoogolAxisDefinition> axes)
    {
        return axes.Count == 0
            ? new Dictionary<string, GoogolAxisDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                [AxisDefaults.PrimaryAxisKey] = new GoogolAxisDefinition()
            }
            : axes.ToDictionary(axis => axis.AxisKey, StringComparer.OrdinalIgnoreCase);
    }

    private static GoogolAxisControllerOptions CreateOptions(DeviceConfiguration configuration)
    {
        var cards = ResolveGoogolPulseAxisCards(configuration);
        var axes = ResolveGoogolPulseAxes(configuration, cards).ToArray();
        return new GoogolAxisControllerOptions
        {
            CardNo = configuration.GoogolCardNo,
            AxisCount = axes.Length == 0 ? 1 : axes.Max(axis => axis.AxisNo),
            ConfigPath = configuration.GoogolConfigPath,
            Cards = CreateGoogolPulseCardOptions(cards).ToArray(),
            Axes = axes.Select(axis => new GoogolAxisDefinition
            {
                AxisKey = axis.Key,
                CardNo = ResolveAxisCardNo(axis, cards, configuration.GoogolCardNo),
                AxisNo = axis.AxisNo,
                PulsesPerUnit = axis.PulsesPerUnit,
                PositionBand = axis.PositionBand <= 0 ? 0.01 : axis.PositionBand,
                HomeMode = ResolveHomeMode(axis.HomeMode),
                HomePositive = axis.HomePositive,
                HomeOffset = axis.HomeOffset,
                EscapeDistance = axis.EscapeDistance,
                HomeHighSpeed = Math.Max(axis.DefaultSpeed, 1),
                HomeLowSpeed = Math.Max(axis.DefaultSpeed / 4, 1),
                HomeAcceleration = Math.Max(axis.DefaultAcceleration, 1),
                HomeDeceleration = Math.Max(axis.DefaultAcceleration, 1)
            }).ToArray()
        };
    }

    private static IEnumerable<AxisPointDefinition> ResolveGoogolPulseAxes(DeviceConfiguration configuration)
    {
        return ResolveGoogolPulseAxes(configuration, ResolveGoogolPulseAxisCards(configuration));
    }

    private static IEnumerable<AxisPointDefinition> ResolveGoogolPulseAxes(
        DeviceConfiguration configuration,
        IReadOnlyList<AxisCardDefinition> cards)
    {
        return configuration.Axes
            .Where(axis => axis.Enabled)
            .Where(axis => ResolveAxisCard(axis, cards, configuration.GoogolCardNo) is not null);
    }

    private static IEnumerable<GoogolCardOptions> CreateGoogolPulseCardOptions(IEnumerable<AxisCardDefinition> cards)
    {
        return cards.Select(card => new GoogolCardOptions
        {
            CardNo = card.CardNo,
            AxisCount = card.AxisCount,
            ConfigPath = card.ConfigPath,
            Description = string.IsNullOrWhiteSpace(card.Description) ? card.Name : card.Description
        });
    }

    private static IReadOnlyList<AxisCardDefinition> ResolveGoogolPulseAxisCards(DeviceConfiguration configuration)
    {
        if (configuration.AxisCards.Count != 0)
        {
            return configuration.AxisCards
                .Where(card => card.Driver == AxisCardDriverKind.GoogolPulse)
                .ToArray();
        }

        if (configuration.GoogolCards.Count == 0)
        {
            return
            [
                new AxisCardDefinition
                {
                    Key = "card1",
                    Name = $"Googol pulse card {configuration.GoogolCardNo}",
                    Driver = AxisCardDriverKind.GoogolPulse,
                    Vendor = "Googol",
                    CardNo = configuration.GoogolCardNo,
                    AxisCount = Math.Max(8, configuration.Axes.Select(axis => (int)axis.AxisNo).DefaultIfEmpty(1).Max()),
                    ConfigPath = configuration.GoogolConfigPath
                }
            ];
        }

        return configuration.GoogolCards
            .Select(card => new AxisCardDefinition
            {
                Key = card.Key,
                Name = string.IsNullOrWhiteSpace(card.Description)
                    ? $"Googol pulse card {card.CardNo}"
                    : card.Description,
                Driver = AxisCardDriverKind.GoogolPulse,
                Vendor = "Googol",
                CardNo = card.CardNo,
                AxisCount = card.AxisCount,
                InputCount = card.InputCount,
                OutputCount = card.OutputCount,
                ConfigPath = card.ConfigPath,
                Description = card.Description
            })
            .ToArray();
    }

    private static short ResolveAxisCardNo(
        AxisPointDefinition axis,
        IReadOnlyList<AxisCardDefinition> cards,
        short defaultCardNo)
    {
        return ResolveAxisCard(axis, cards, defaultCardNo)?.CardNo
            ?? (axis.CardNo < 0 ? defaultCardNo : axis.CardNo);
    }

    private static AxisCardDefinition? ResolveAxisCard(
        AxisPointDefinition axis,
        IReadOnlyList<AxisCardDefinition> cards,
        short defaultCardNo)
    {
        var cardKey = axis.CardKey?.Trim();
        if (!string.IsNullOrWhiteSpace(cardKey))
        {
            var card = cards.FirstOrDefault(
                item => string.Equals(item.Key, cardKey, StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                return card;
            }
        }

        var cardNo = axis.CardNo < 0 ? defaultCardNo : axis.CardNo;
        return cards.FirstOrDefault(card => card.CardNo == cardNo);
    }

    private static AxisHomeMode ResolveHomeMode(string homeMode)
    {
        return Enum.TryParse<AxisHomeMode>(homeMode, true, out var mode) ? mode : AxisHomeMode.LimitHomeIndex;
    }

    private static class Native
    {
        public const short McHome = 3;
        public const short McClear = 11;
        public const short HomeModeLimit = 10;
        public const short HomeModeLimitHome = 11;
        public const short HomeModeLimitIndex = 12;
        public const short HomeModeLimitHomeIndex = 13;
        public const short HomeModeHome = 20;
        public const short HomeModeHomeIndex = 22;
        public const short HomeModeIndex = 30;

        [StructLayout(LayoutKind.Sequential)]
        public struct TTrapPrm
        {
            public double acc;
            public double dec;
            public double velStart;
            public short smoothTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TJogPrm
        {
            public double acc;
            public double dec;
            public double smooth;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct THomePrm
        {
            public short mode;
            public short moveDir;
            public short indexDir;
            public short edge;
            public short triggerIndex;
            public short pad1_1;
            public short pad1_2;
            public short pad1_3;
            public double velHigh;
            public double velLow;
            public double acc;
            public double dec;
            public short smoothTime;
            public short pad2_1;
            public short pad2_2;
            public short pad2_3;
            public int homeOffset;
            public int searchHomeDistance;
            public int searchIndexDistance;
            public int escapeStep;
            public int pad3_1;
            public int pad3_2;
            public int pad3_3;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct THomeStatus
        {
            public short run;
            public short stage;
            public short error;
            public short pad1;
            public int capturePos;
            public int targetPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TCrdPrm
        {
            public short dimension;
            public short profile1;
            public short profile2;
            public short profile3;
            public short profile4;
            public short profile5;
            public short profile6;
            public short profile7;
            public short profile8;
            public double synVelMax;
            public double synAccMax;
            public short evenTime;
            public short setOriginFlag;
            public int originPos1;
            public int originPos2;
            public int originPos3;
            public int originPos4;
            public int originPos5;
            public int originPos6;
            public int originPos7;
            public int originPos8;
        }

        [DllImport("gts.dll")]
        public static extern short GT_SetCardNo(short cardNum, short index);

        [DllImport("gts.dll")]
        public static extern short GT_Open(short cardNum, short channel, short param);

        [DllImport("gts.dll")]
        public static extern short GT_OpenDevice(short cardNum, short channel, short param);

        [DllImport("gts.dll")]
        public static extern short GT_Close(short cardNum);

        [DllImport("gts.dll")]
        public static extern short GT_LoadConfig(short cardNum, string pFile);

        [DllImport("gts.dll")]
        public static extern short GT_SetDoBit(short cardNum, short doType, short doIndex, short value);

        [DllImport("gts.dll")]
        public static extern short GT_GetDi(short cardNum, short diType, out int pValue);

        [DllImport("gts.dll")]
        public static extern short GT_GetEncPos(short cardNum, short encoder, out double pValue, short count, out uint pClock);

        [DllImport("gts.dll")]
        public static extern short GT_Reset(short cardNum);

        [DllImport("gts.dll")]
        public static extern short GT_GetSts(short cardNum, short axis, out int pSts, short count, out uint pClock);

        [DllImport("gts.dll")]
        public static extern short GT_ClrSts(short cardNum, short axis, short count);

        [DllImport("gts.dll")]
        public static extern short GT_AxisOn(short cardNum, short axis);

        [DllImport("gts.dll")]
        public static extern short GT_AxisOff(short cardNum, short axis);

        [DllImport("gts.dll")]
        public static extern short GT_Stop(short cardNum, int mask, int option);

        [DllImport("gts.dll")]
        public static extern short GT_ZeroPos(short cardNum, short axis, short count);

        [DllImport("gts.dll")]
        public static extern short GT_GetPrfPos(short cardNum, short profile, out double pValue, short count, out uint pClock);

        [DllImport("gts.dll")]
        public static extern short GT_Update(short cardNum, int mask);

        [DllImport("gts.dll")]
        public static extern short GT_SetPos(short cardNum, short profile, int pos);

        [DllImport("gts.dll")]
        public static extern short GT_SetVel(short cardNum, short profile, double vel);

        [DllImport("gts.dll")]
        public static extern short GT_PrfTrap(short cardNum, short profile);

        [DllImport("gts.dll")]
        public static extern short GT_PrfJog(short cardNum, short profile);

        [DllImport("gts.dll")]
        public static extern short GT_SetTrapPrm(short cardNum, short profile, ref TTrapPrm pPrm);

        [DllImport("gts.dll")]
        public static extern short GT_GetTrapPrm(short cardNum, short profile, out TTrapPrm pPrm);

        [DllImport("gts.dll")]
        public static extern short GT_SetJogPrm(short cardNum, short profile, ref TJogPrm pPrm);

        [DllImport("gts.dll")]
        public static extern short GT_GetJogPrm(short cardNum, short profile, out TJogPrm pPrm);

        [DllImport("gts.dll")]
        public static extern short GT_SetCrdPrm(short cardNum, short crd, ref TCrdPrm pCrdPrm);

        [DllImport("gts.dll")]
        public static extern short GT_CrdClear(short cardNum, short crd, short fifo);

        [DllImport("gts.dll")]
        public static extern short GT_CrdSpace(short cardNum, short crd, out int pSpace, short fifo);

        [DllImport("gts.dll")]
        public static extern short GT_LnXY(short cardNum, short crd, int x, int y, double synVel, double synAcc, double velEnd, short fifo);

        [DllImport("gts.dll")]
        public static extern short GT_LnXYZ(short cardNum, short crd, int x, int y, int z, double synVel, double synAcc, double velEnd, short fifo);

        [DllImport("gts.dll")]
        public static extern short GT_LnXYZA(short cardNum, short crd, int x, int y, int z, int a, double synVel, double synAcc, double velEnd, short fifo);

        [DllImport("gts.dll")]
        public static extern short GT_BufIO(short cardNum, short crd, ushort doType, ushort doMask, ushort doValue, short fifo);

        [DllImport("gts.dll")]
        public static extern short GT_BufDelay(short cardNum, short crd, ushort delayTime, short fifo);

        [DllImport("gts.dll")]
        public static extern short GT_CrdStart(short cardNum, short mask, short option);

        [DllImport("gts.dll")]
        public static extern short GT_CrdStatus(short cardNum, short crd, out short pRun, out int pSegment, short fifo);

        [DllImport("gts.dll")]
        public static extern short GT_GoHome(short cardNum, short axis, ref THomePrm pHomePrm);

        [DllImport("gts.dll")]
        public static extern short GT_GetHomeStatus(short cardNum, short axis, out THomeStatus pHomeStatus);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetModuleFileName(IntPtr hModule, char[] lpFilename, int nSize);
    }
}
