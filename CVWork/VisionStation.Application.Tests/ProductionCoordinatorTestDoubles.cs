using System.Collections.Concurrent;
using VisionStation.Application;
using VisionStation.Devices;
using VisionStation.Domain;

namespace VisionStation.Application.Tests;

internal sealed class CoordinatorHarness
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private CoordinatorHarness(
        ProductionCoordinator coordinator,
        IInspectionExecution execution,
        TestInspectionExecutor executor,
        FakeCameraDevice camera,
        FakePlcClient plc,
        FakeAxisController axis,
        FakeCommunicationChannels communicationChannels,
        FakeAppLogService log,
        FakeAlarmService alarms)
    {
        Coordinator = coordinator;
        Execution = execution;
        Executor = executor;
        Camera = camera;
        Plc = plc;
        Axis = axis;
        CommunicationChannels = communicationChannels;
        Log = log;
        Alarms = alarms;
    }

    public ProductionCoordinator Coordinator { get; }

    public IInspectionExecution Execution { get; }

    public TestInspectionExecutor Executor { get; }

    public FakeCameraDevice Camera { get; }

    public FakePlcClient Plc { get; }

    public FakeAxisController Axis { get; }

    public FakeCommunicationChannels CommunicationChannels { get; }

    public FakeAppLogService Log { get; }

    public FakeAlarmService Alarms { get; }

    public static CoordinatorHarness Create(
        int stopWaitTimeoutMs = 1000,
        IInspectionExecution? inspectionExecution = null)
    {
        var log = new FakeAppLogService();
        var alarms = new FakeAlarmService();
        var executor = new TestInspectionExecutor();
        var execution = inspectionExecution ?? new InspectionExecution(executor, log);
        var camera = new FakeCameraDevice();
        var plc = new FakePlcClient();
        var axis = new FakeAxisController();
        var communicationChannels = new FakeCommunicationChannels();
        var configuration = new DeviceConfiguration
        {
            SystemSettings = new SystemSettingsConfiguration
            {
                Production = new ProductionSettingsConfiguration
                {
                    CycleDelayMs = 1,
                    MaxConsecutiveFailures = 1,
                    AutoStopOnAlarm = true,
                    CleanupTimeoutMs = 1000,
                    StopWaitTimeoutMs = stopWaitTimeoutMs
                }
            }
        };
        var coordinator = new ProductionCoordinator(
            execution,
            camera,
            plc,
            axis,
            log,
            alarms,
            communicationChannels,
            configuration);

        return new CoordinatorHarness(
            coordinator,
            execution,
            executor,
            camera,
            plc,
            axis,
            communicationChannels,
            log,
            alarms);
    }

    public static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (condition())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(Timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(timeout.Token))
            {
                if (condition())
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Condition was not satisfied within two seconds.");
        }
    }
}

internal sealed class TestInspectionExecutor : IInspectionExecutor
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Func<InspectionRequest, CancellationToken, Task<InspectionRunResult>> Handler { get; set; } =
        static (_, _) => Task.FromResult(TestRunResults.Ok());

    public Task<InspectionRunResult> ExecuteAsync(
        InspectionRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Handler(request, cancellationToken);
    }
}

internal sealed class BlockingRejectedInspectionExecution : IInspectionExecution
{
    public BlockingRejectedInspectionExecution(ActiveInspectionRun external)
    {
        Current = external;
    }

    public ActiveInspectionRun Current { get; }

    public TaskCompletionSource TryBeginEntered { get; } = CoordinatorHarness.NewSignal();

    public TaskCompletionSource AllowTryBeginReturn { get; } = CoordinatorHarness.NewSignal();

    public event EventHandler<InspectionExecutionChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    public event EventHandler<InspectionRunResult>? RunCompleted
    {
        add { }
        remove { }
    }

    public RunAdmission TryBegin(InspectionRunIntent intent)
    {
        TryBeginEntered.TrySetResult();
        AllowTryBeginReturn.Task.GetAwaiter().GetResult();
        return new RunAdmission.Rejected(
            new RunRejection(RunRejectionReason.Busy, Current));
    }
}

internal sealed class DisposeAfterReleaseFailingInspectionExecution : IInspectionExecution
{
    private readonly IInspectionExecution _inner;
    private readonly Action? _afterRelease;

    public DisposeAfterReleaseFailingInspectionExecution(
        IInspectionExecution inner,
        Action? afterRelease = null)
    {
        _inner = inner;
        _afterRelease = afterRelease;
    }

    public ActiveInspectionRun? Current => _inner.Current;

    public event EventHandler<InspectionExecutionChangedEventArgs>? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public event EventHandler<InspectionRunResult>? RunCompleted
    {
        add => _inner.RunCompleted += value;
        remove => _inner.RunCompleted -= value;
    }

    public RunAdmission TryBegin(InspectionRunIntent intent)
    {
        var admission = _inner.TryBegin(intent);
        return admission is RunAdmission.Acquired acquired
            ? new RunAdmission.Acquired(
                new DisposeAfterReleaseFailingSession(acquired.Session, _afterRelease))
            : admission;
    }

    private sealed class DisposeAfterReleaseFailingSession : IInspectionSession
    {
        private readonly IInspectionSession _inner;
        private readonly Action? _afterRelease;

        public DisposeAfterReleaseFailingSession(
            IInspectionSession inner,
            Action? afterRelease)
        {
            _inner = inner;
            _afterRelease = afterRelease;
        }

        public ActiveInspectionRun Run => _inner.Run;

        public Task<InspectionRunResult> ExecuteAsync(
            InspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _inner.ExecuteAsync(request, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _afterRelease?.Invoke();
            throw new InvalidOperationException("session-dispose-failure");
        }
    }
}

internal sealed class DisposeBeforeReleaseFailingInspectionExecution : IInspectionExecution
{
    private readonly IInspectionExecution _inner;
    private readonly bool _completeDisposeSuccessfully;
    private IInspectionSession? _innerSession;
    private int _sessionDisposeCount;

    public DisposeBeforeReleaseFailingInspectionExecution(
        IInspectionExecution inner,
        bool completeDisposeSuccessfully = false)
    {
        _inner = inner;
        _completeDisposeSuccessfully = completeDisposeSuccessfully;
    }

    public ActiveInspectionRun? Current => _inner.Current;

    public int SessionDisposeCount => Volatile.Read(ref _sessionDisposeCount);

    public event EventHandler<InspectionExecutionChangedEventArgs>? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public event EventHandler<InspectionRunResult>? RunCompleted
    {
        add => _inner.RunCompleted += value;
        remove => _inner.RunCompleted -= value;
    }

    public RunAdmission TryBegin(InspectionRunIntent intent)
    {
        var admission = _inner.TryBegin(intent);
        if (admission is not RunAdmission.Acquired acquired)
        {
            return admission;
        }

        _innerSession = acquired.Session;
        return new RunAdmission.Acquired(
            new DisposeBeforeReleaseFailingSession(acquired.Session, this));
    }

    public async Task ReleaseAsync()
    {
        var session = _innerSession;
        if (session is null)
        {
            return;
        }

        await session.DisposeAsync();
        _innerSession = null;
    }

    private sealed class DisposeBeforeReleaseFailingSession : IInspectionSession
    {
        private readonly IInspectionSession _inner;
        private readonly DisposeBeforeReleaseFailingInspectionExecution _owner;

        public DisposeBeforeReleaseFailingSession(
            IInspectionSession inner,
            DisposeBeforeReleaseFailingInspectionExecution owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public ActiveInspectionRun Run => _inner.Run;

        public Task<InspectionRunResult> ExecuteAsync(
            InspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _inner.ExecuteAsync(request, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _owner._sessionDisposeCount);
            if (_owner._completeDisposeSuccessfully)
            {
                return ValueTask.CompletedTask;
            }

            return ValueTask.FromException(
                new InvalidOperationException("session-dispose-before-release-failure"));
        }
    }
}

internal sealed class DistinctStateRecorder : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly ProductionCoordinator _coordinator;
    private readonly List<ProductionState> _states = [];

    public DistinctStateRecorder(ProductionCoordinator coordinator)
    {
        _coordinator = coordinator;
        _coordinator.SnapshotChanged += OnSnapshotChanged;
    }

    public IReadOnlyList<ProductionState> States
    {
        get
        {
            lock (_syncRoot)
            {
                return _states.ToArray();
            }
        }
    }

    public void Dispose()
    {
        _coordinator.SnapshotChanged -= OnSnapshotChanged;
    }

    private void OnSnapshotChanged(object? sender, ProductionSnapshot snapshot)
    {
        lock (_syncRoot)
        {
            if (_states.Count == 0 || _states[^1] != snapshot.State)
            {
                _states.Add(snapshot.State);
            }
        }
    }
}

internal sealed class FakeCameraDevice : ICameraDevice
{
    private int _connectCount;

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public string DeviceId => "test-camera";

    public DeviceSnapshot Snapshot { get; private set; } = new(
        "Test Camera",
        DeviceConnectionState.Disconnected,
        string.Empty,
        DateTimeOffset.UtcNow);

    public TaskCompletionSource ConnectEntered { get; } = CoordinatorHarness.NewSignal();

    public Func<CancellationToken, Task> ConnectHandler { get; set; } = static cancellationToken =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    };

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCount);
        ConnectEntered.TrySetResult();
        await ConnectHandler(cancellationToken);
        Snapshot = Snapshot with
        {
            State = DeviceConnectionState.Connected,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Snapshot = Snapshot with
        {
            State = DeviceConnectionState.Disconnected,
            Timestamp = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task<ImageFrame> GrabAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TestRunResults.Ok().OriginalFrame);
    }

    public void PublishState(DeviceSnapshot snapshot)
    {
        Snapshot = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }
}

internal sealed class FakePlcClient : IPlcClient
{
    private int _connectCount;

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public DeviceSnapshot Snapshot { get; private set; } = new(
        "Test PLC",
        DeviceConnectionState.Disconnected,
        string.Empty,
        DateTimeOffset.UtcNow);

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public ConcurrentQueue<bool> BusyWrites { get; } = new();

    public Func<bool, CancellationToken, Task> SetBusyHandler { get; set; } =
        static (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _connectCount);
        Snapshot = Snapshot with
        {
            State = DeviceConnectionState.Connected,
            Timestamp = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Snapshot = Snapshot with
        {
            State = DeviceConnectionState.Disconnected,
            Timestamp = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task SetInspectionBusyAsync(bool busy, CancellationToken cancellationToken = default)
    {
        BusyWrites.Enqueue(busy);
        return SetBusyHandler(busy, cancellationToken);
    }

    public Task<string> ReadAddressAsync(string address, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(string.Empty);
    }

    public Task WriteAddressAsync(
        string address,
        string value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task WriteInspectionResultAsync(
        InspectionResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAlarmAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void PublishState(DeviceSnapshot snapshot)
    {
        Snapshot = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }
}

internal sealed class FakeAxisController : IAxisController
{
    private int _connectCount;

    public event EventHandler<DeviceSnapshot>? StateChanged;

    public DeviceSnapshot Snapshot { get; private set; } = new(
        "Test Axis",
        DeviceConnectionState.Disconnected,
        string.Empty,
        DateTimeOffset.UtcNow);

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _connectCount);
        Snapshot = Snapshot with
        {
            State = DeviceConnectionState.Connected,
            Timestamp = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Snapshot = Snapshot with
        {
            State = DeviceConnectionState.Disconnected,
            Timestamp = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task ServoOnAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task ServoOffAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task ClearAlarmAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task ZeroPositionAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task HomeAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task HomeAsync(
        AxisHomeCommand command,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task MoveAbsoluteAsync(
        AxisMoveCommand command,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task MoveLinearInterpolationAsync(
        AxisLinearInterpolationCommand command,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task StartJogAsync(
        AxisJogCommand command,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task StopJogAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task StopAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        AxisStopMode stopMode = AxisStopMode.Smooth,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public Task EmergencyStopAsync(CancellationToken cancellationToken = default) =>
        Completed(cancellationToken);

    public Task<AxisStatus> GetAxisStatusAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AxisStatus
        {
            AxisKey = axisKey,
            Ready = true,
            InPosition = true,
            Message = "Ready",
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public Task ApplyConfigurationAsync(
        DeviceConfiguration configuration,
        CancellationToken cancellationToken = default) => Completed(cancellationToken);

    public void PublishState(DeviceSnapshot snapshot)
    {
        Snapshot = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private static Task Completed(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class FakeCommunicationChannels : ICommunicationChannelRuntime
{
    private int _connectCount;
    private int _disconnectCount;

    public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived;

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public int DisconnectCount => Volatile.Read(ref _disconnectCount);

    public TaskCompletionSource ConnectEntered { get; } = CoordinatorHarness.NewSignal();

    public Func<CancellationToken, Task> ConnectHandler { get; set; } =
        static cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

    public Func<CancellationToken, Task> DisconnectHandler { get; set; } =
        static cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

    public async Task ConnectAsync(
        string connectionPolicy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _connectCount);
        ConnectEntered.TrySetResult();
        await ConnectHandler(cancellationToken);
    }

    public Task DisconnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _disconnectCount);
        return DisconnectHandler(cancellationToken);
    }

    public Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(
        TcpCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) => Snapshot("tcp", cancellationToken);

    public Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(
        SerialCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) => Snapshot("serial", cancellationToken);

    public Task<CommunicationChannelRuntimeSnapshot> ReconnectTcpAsync(
        TcpCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) => Snapshot("tcp", cancellationToken);

    public Task<CommunicationChannelRuntimeSnapshot> ReconnectSerialAsync(
        SerialCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) => Snapshot("serial", cancellationToken);

    public Task<byte[]?> TryExchangeTcpAsync(
        TcpCommunicationChannelSettings channel,
        byte[] payload,
        int timeoutMs,
        bool waitResponse,
        CancellationToken cancellationToken = default) => NoResponse(cancellationToken);

    public Task<bool> TrySendTcpAsync(
        TcpCommunicationChannelSettings channel,
        byte[] payload,
        CancellationToken cancellationToken = default) => Sent(cancellationToken);

    public Task<byte[]?> TryExchangeSerialAsync(
        SerialCommunicationChannelSettings channel,
        byte[] payload,
        int timeoutMs,
        bool waitResponse,
        CancellationToken cancellationToken = default) => NoResponse(cancellationToken);

    public Task<bool> TrySendSerialAsync(
        SerialCommunicationChannelSettings channel,
        byte[] payload,
        CancellationToken cancellationToken = default) => Sent(cancellationToken);

    public void Dispose()
    {
    }

    public void PublishFrame(CommunicationChannelRuntimeFrame frame)
    {
        FrameReceived?.Invoke(this, frame);
    }

    private static Task<CommunicationChannelRuntimeSnapshot> Snapshot(
        string kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CommunicationChannelRuntimeSnapshot(
            kind,
            "test",
            CommunicationChannelConnectionPolicies.Production,
            true,
            true,
            false,
            string.Empty,
            "Connected"));
    }

    private static Task<byte[]?> NoResponse(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<byte[]?>(null);
    }

    private static Task<bool> Sent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}

internal sealed class FakeAppLogService : IAppLogService
{
    private readonly ConcurrentQueue<AppLogEntry> _entries = new();

    public event EventHandler<AppLogEntry>? LogWritten;

    public IReadOnlyList<AppLogEntry> Entries => _entries.ToArray();

    public void Info(string source, string message) => Write("Info", source, message);

    public void Warning(string source, string message) => Write("Warning", source, message);

    public void Error(string source, string message) => Write("Error", source, message);

    public void Critical(string source, string message) => Write("Critical", source, message);

    public IReadOnlyList<AppLogEntry> Recent(int count)
    {
        return _entries.Reverse().Take(Math.Max(0, count)).ToArray();
    }

    private void Write(string level, string source, string message)
    {
        var entry = new AppLogEntry(DateTimeOffset.UtcNow, level, source, message);
        _entries.Enqueue(entry);
        var handlers = LogWritten;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<AppLogEntry> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, entry);
            }
            catch
            {
            }
        }
    }
}

internal sealed class FakeAlarmService : IAlarmService
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, AlarmEvent> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AlarmEvent> _raised = [];

    public event EventHandler<AlarmEvent>? AlarmRaised;

    public event EventHandler<AlarmEvent>? AlarmChanged;

    public IReadOnlyList<AlarmEvent> Raised
    {
        get
        {
            lock (_syncRoot)
            {
                return _raised.ToArray();
            }
        }
    }

    public AlarmEvent Raise(
        AlarmSeverity severity,
        string source,
        string message,
        string details = "",
        string? alarmId = null)
    {
        var alarm = new AlarmEvent(
            string.IsNullOrWhiteSpace(alarmId) ? Guid.NewGuid().ToString("N") : alarmId,
            severity,
            source,
            message,
            DateTimeOffset.UtcNow,
            Details: details);
        lock (_syncRoot)
        {
            _raised.Add(alarm);
            if (severity != AlarmSeverity.Info)
            {
                _active[alarm.Id] = alarm;
            }
        }

        Publish(AlarmRaised, alarm);
        return alarm;
    }

    public void Acknowledge(string alarmId)
    {
        AlarmEvent? changed = null;
        lock (_syncRoot)
        {
            if (_active.TryGetValue(alarmId, out var alarm) && !alarm.Acknowledged)
            {
                changed = alarm with
                {
                    Acknowledged = true,
                    AcknowledgedAt = DateTimeOffset.UtcNow
                };
                _active[alarmId] = changed;
                ReplaceRaised(changed);
            }
        }

        if (changed is not null)
        {
            Publish(AlarmChanged, changed);
        }
    }

    public void Clear(string alarmId)
    {
        AlarmEvent? changed = null;
        lock (_syncRoot)
        {
            if (_active.Remove(alarmId, out var alarm))
            {
                changed = alarm with { ClearedAt = DateTimeOffset.UtcNow };
                ReplaceRaised(changed);
            }
        }

        if (changed is not null)
        {
            Publish(AlarmChanged, changed);
        }
    }

    public IReadOnlyList<AlarmEvent> Active()
    {
        lock (_syncRoot)
        {
            return _active.Values.ToArray();
        }
    }

    public IReadOnlyList<AlarmEvent> Recent(int count)
    {
        lock (_syncRoot)
        {
            return _raised
                .OrderByDescending(alarm => alarm.Timestamp)
                .Take(Math.Max(0, count))
                .ToArray();
        }
    }

    private void ReplaceRaised(AlarmEvent changed)
    {
        var index = _raised.FindIndex(
            alarm => string.Equals(alarm.Id, changed.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _raised[index] = changed;
        }
    }

    private void Publish(EventHandler<AlarmEvent>? handlers, AlarmEvent alarm)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<AlarmEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, alarm);
            }
            catch
            {
            }
        }
    }
}
