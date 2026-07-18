using System.Reflection;
using VisionStation.Application;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class InspectionRunLifetimeTests
{
    [Fact]
    public async Task BeginShutdownCancelsActiveRunAndDrainWaitsForRunCleanup()
    {
        var lifetime = new InspectionRunLifetime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = lifetime.RunTrackedAsync<int>(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                await allowCleanup.Task;
                throw;
            }
        });

        await started.Task;
        lifetime.BeginShutdown();
        await cancellationObserved.Task;

        var drain = lifetime.DrainAsync();
        Assert.False(drain.IsCompleted);

        allowCleanup.TrySetResult();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(exception.CancellationToken.IsCancellationRequested);
        await drain;
    }

    [Fact]
    public async Task BeginShutdownClosesAdmissionBeforeASecondRunCanStart()
    {
        var lifetime = new InspectionRunLifetime();
        lifetime.BeginShutdown();
        var invoked = false;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifetime.RunTrackedAsync(
                _ =>
                {
                    invoked = true;
                    return Task.FromResult(42);
                }));

        Assert.False(invoked);
        Assert.True(lifetime.IsShutdownRequested);
        Assert.True(exception.CancellationToken.IsCancellationRequested);
        await lifetime.DrainAsync();
    }

    [Fact]
    public async Task DrainCompletesOnlyAfterEveryAcceptedRunHasExited()
    {
        var lifetime = new InspectionRunLifetime();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecondExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = StartBlockingRun(firstStarted, firstCancelled, allowFirstExit);
        var second = StartBlockingRun(secondStarted, secondCancelled, allowSecondExit);
        await Task.WhenAll(firstStarted.Task, secondStarted.Task);

        lifetime.BeginShutdown();
        await Task.WhenAll(firstCancelled.Task, secondCancelled.Task);
        var drain = lifetime.DrainAsync();

        allowFirstExit.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(drain.IsCompleted);

        allowSecondExit.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await drain;

        Task<int> StartBlockingRun(
            TaskCompletionSource started,
            TaskCompletionSource cancelled,
            TaskCompletionSource allowExit)
        {
            return lifetime.RunTrackedAsync<int>(async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 1;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    await allowExit.Task;
                    throw;
                }
            });
        }
    }

    [Fact]
    public async Task CallerCancellationDoesNotCloseAdmissionForLaterRuns()
    {
        var lifetime = new InspectionRunLifetime();
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifetime.RunTrackedAsync(_ => Task.FromResult(1), callerCancellation.Token));

        var result = await lifetime.RunTrackedAsync(_ => Task.FromResult(2));

        Assert.Equal(2, result);
        Assert.False(lifetime.IsShutdownRequested);
    }

    [Fact]
    public async Task ProductionCoordinatorTracksTheCompleteSingleRunIncludingCleanup()
    {
        var lifetime = new InspectionRunLifetime();
        var plc = new CleanupBlockingPlcClient();
        var coordinator = CreateCoordinator(lifetime, plc);

        var run = coordinator.RunSingleAsync();
        await plc.CleanupEntered.Task;

        lifetime.BeginShutdown();
        var drain = lifetime.DrainAsync();
        Assert.False(drain.IsCompleted);

        plc.AllowCleanup.TrySetResult();
        InspectionRunResult result = await run;
        await drain;

        Assert.Equal(InspectionOutcome.Ok, result.Result.Outcome);
        Assert.True(plc.BusyWasSet);
        Assert.True(plc.BusyWasCleared);
    }

    [Fact]
    public void ShutdownRejectsALifetimeThatDiffersFromTheCoordinatorLifetime()
    {
        var coordinatorLifetime = new InspectionRunLifetime();
        var shutdownLifetime = new InspectionRunLifetime();
        var coordinator = CreateCoordinator(coordinatorLifetime, new CleanupBlockingPlcClient());

        var exception = Assert.Throws<ArgumentException>(() =>
            new ApplicationShutdownService(coordinator, shutdownLifetime, null!, null!));

        Assert.Equal("inspectionRunLifetime", exception.ParamName);
    }

    [Fact]
    public async Task ApplicationShutdownDoesNotDisconnectSharedProductionPolicyBeforeDirectRunDrains()
    {
        var lifetime = new InspectionRunLifetime();
        var plc = new CleanupBlockingPlcClient();
        var communication = new StubCommunicationChannelRuntime();
        var coordinator = CreateCoordinator(lifetime, plc, communication);
        var matching = new StubTemplateMatchingService();
        var run = coordinator.RunSingleAsync();
        await plc.CleanupEntered.Task;

        var shutdownService = new ApplicationShutdownService(
            coordinator,
            lifetime,
            matching,
            communication);
        var shutdown = shutdownService.ShutdownAsync();

        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, communication.DisconnectCount);
        Assert.Equal(0, matching.DisposeCount);
        Assert.Equal(0, communication.DisposeCount);

        plc.AllowCleanup.TrySetResult();
        await run;
        await shutdown;

        Assert.Equal(0, communication.DisconnectCount);
        Assert.Equal(1, matching.DisposeCount);
        Assert.Equal(1, communication.DisposeCount);
    }

    [Fact]
    public async Task PublicStopStillDisconnectsProductionPolicyOutsideApplicationShutdown()
    {
        var communication = new StubCommunicationChannelRuntime();
        var coordinator = CreateCoordinator(
            new InspectionRunLifetime(),
            new CleanupBlockingPlcClient(),
            communication);

        await coordinator.StopAsync();

        Assert.Equal(1, communication.DisconnectCount);
    }

    [Fact]
    public async Task OverlappingPublicStopsShareOneTransitionAndDisconnect()
    {
        var communication = new StubCommunicationChannelRuntime { BlockDisconnect = true };
        var coordinator = CreateCoordinator(
            new InspectionRunLifetime(),
            new CleanupBlockingPlcClient(),
            communication);

        var firstStop = coordinator.StopAsync();
        await communication.DisconnectEntered.Task;
        var secondStop = coordinator.StopAsync();

        Assert.Same(firstStop, secondStop);
        Assert.Equal(1, communication.DisconnectCount);
        communication.AllowDisconnect.TrySetResult();
        await Task.WhenAll(firstStop, secondStop);
        Assert.Equal(1, communication.DisconnectCount);
    }

    [Fact]
    public async Task PublicStopRacingApplicationShutdownDoesNotDisconnectBeforeTrackedRunDrains()
    {
        var lifetime = new InspectionRunLifetime();
        var plc = new CleanupBlockingPlcClient();
        var communication = new StubCommunicationChannelRuntime();
        var coordinator = CreateCoordinator(lifetime, plc, communication);
        var matching = new StubTemplateMatchingService();
        var run = coordinator.RunSingleAsync();
        await plc.CleanupEntered.Task;

        var shutdownService = new ApplicationShutdownService(
            coordinator,
            lifetime,
            matching,
            communication);
        var shutdown = shutdownService.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);

        await coordinator.StopAsync();

        Assert.Equal(0, communication.DisconnectCount);
        Assert.Equal(0, communication.DisposeCount);

        plc.AllowCleanup.TrySetResult();
        await run;
        await shutdown;

        Assert.Equal(0, communication.DisconnectCount);
        Assert.Equal(1, communication.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentStartsDoNotEnterProductionConnectionTwiceBeforePublication()
    {
        var lifetime = new InspectionRunLifetime();
        var communication = new StubCommunicationChannelRuntime { BlockConnect = true };
        var plc = new CleanupBlockingPlcClient();
        var coordinator = CreateCoordinator(
            lifetime,
            plc,
            communication);
        Task? secondStart = null;
        var firstStart = coordinator.StartAsync();
        await communication.ConnectEntered.Task;

        try
        {
            secondStart = coordinator.StartAsync();

            Assert.Equal(1, communication.ConnectCount);
        }
        finally
        {
            communication.AllowConnect.TrySetResult();
            if (secondStart is not null)
            {
                await Task.WhenAll(firstStart, secondStart).WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                await firstStart.WaitAsync(TimeSpan.FromSeconds(5));
            }

            lifetime.BeginShutdown();
            plc.AllowCleanup.TrySetResult();
            await coordinator.StopForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await lifetime.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StopDuringProductionConnectionPreventsLateLoopPublication()
    {
        var lifetime = new InspectionRunLifetime();
        var communication = new StubCommunicationChannelRuntime
        {
            BlockConnect = true,
            HonorConnectCancellationWhileBlocked = true
        };
        var plc = new CleanupBlockingPlcClient();
        var coordinator = CreateCoordinator(
            lifetime,
            plc,
            communication);
        var start = coordinator.StartAsync();
        await communication.ConnectEntered.Task;

        await coordinator.StopAsync();
        communication.AllowConnect.TrySetResult();

        try
        {
            await start.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ProductionState.Stopped, coordinator.Snapshot.State);
            var loopTask = (Task?)typeof(ProductionCoordinator)
                .GetField("_loopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator);
            Assert.True(loopTask is null || loopTask.IsCompleted);
        }
        finally
        {
            lifetime.BeginShutdown();
            plc.AllowCleanup.TrySetResult();
            await coordinator.StopForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await lifetime.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StopInvalidatesASecondStartAlreadyWaitingForStartupSerialization()
    {
        var lifetime = new InspectionRunLifetime();
        var communication = new StubCommunicationChannelRuntime
        {
            BlockConnect = true,
            HonorConnectCancellationWhileBlocked = true
        };
        var coordinator = CreateCoordinator(
            lifetime,
            new CleanupBlockingPlcClient(),
            communication);
        var firstStart = coordinator.StartAsync();
        await communication.ConnectEntered.Task;
        var queuedStart = coordinator.StartAsync();

        await coordinator.StopAsync();
        await Task.WhenAll(firstStart, queuedStart).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, communication.ConnectCount);
        Assert.Equal(ProductionState.Stopped, coordinator.Snapshot.State);
        Assert.Null(
            typeof(ProductionCoordinator)
                .GetField("_loopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupCallbackFailureBeforeLoopPublicationRollsBackProductionStateAndResources(
        bool failFromLogCallback)
    {
        var lifetime = new InspectionRunLifetime();
        var communication = new StubCommunicationChannelRuntime();
        var log = new CallbackAppLogService();
        var coordinator = CreateCoordinator(
            lifetime,
            new CleanupBlockingPlcClient(),
            communication,
            log);
        const string failureMessage = "startup callback failed";

        if (failFromLogCallback)
        {
            log.InfoCallback = (source, message) =>
            {
                if (string.Equals(source, "Production", StringComparison.Ordinal) &&
                    string.Equals(message, "连续生产启动", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(failureMessage);
                }
            };
        }
        else
        {
            coordinator.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.State == ProductionState.Running)
                {
                    throw new InvalidOperationException(failureMessage);
                }
            };
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync());

        Assert.Equal(failureMessage, exception.Message);
        Assert.Equal(ProductionState.Stopped, coordinator.Snapshot.State);
        Assert.Equal(1, communication.ConnectCount);
        Assert.Equal(1, communication.DisconnectCount);
        Assert.Null(
            typeof(ProductionCoordinator)
                .GetField("_loopCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator));
        Assert.Null(
            typeof(ProductionCoordinator)
                .GetField("_loopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator));
    }

    [Fact]
    public async Task CallerCancellationDuringProductionConnectRollsBackAttemptedPolicy()
    {
        var lifetime = new InspectionRunLifetime();
        var communication = new StubCommunicationChannelRuntime { BlockConnect = true };
        var coordinator = CreateCoordinator(
            lifetime,
            new CleanupBlockingPlcClient(),
            communication);
        using var callerCancellation = new CancellationTokenSource();
        var start = coordinator.StartAsync(callerCancellation.Token);
        await communication.ConnectEntered.Task;

        callerCancellation.Cancel();
        communication.AllowConnect.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, communication.ConnectCount);
        Assert.Equal(1, communication.DisconnectCount);
        Assert.Equal(ProductionState.Stopped, coordinator.Snapshot.State);
        Assert.Null(
            typeof(ProductionCoordinator)
                .GetField("_loopCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator));
        Assert.Null(
            typeof(ProductionCoordinator)
                .GetField("_loopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator));
    }

    [Fact]
    public async Task StartAfterShutdownIsRejectedBeforeProductionConnectOrLoopCreation()
    {
        var lifetime = new InspectionRunLifetime();
        var communication = new StubCommunicationChannelRuntime();
        var coordinator = CreateCoordinator(
            lifetime,
            new CleanupBlockingPlcClient(),
            communication);
        lifetime.BeginShutdown();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.StartAsync());

        Assert.True(exception.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, communication.ConnectCount);
        Assert.Equal(ProductionState.Stopped, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task ShutdownRacingBlockedStartupDrainsStartupBeforeDisposingCommunication()
    {
        var lifetime = new InspectionRunLifetime();
        var communication = new StubCommunicationChannelRuntime { BlockConnect = true };
        var coordinator = CreateCoordinator(
            lifetime,
            new CleanupBlockingPlcClient(),
            communication);
        var matching = new StubTemplateMatchingService();
        var start = coordinator.StartAsync();
        await communication.ConnectEntered.Task;

        var shutdownService = new ApplicationShutdownService(
            coordinator,
            lifetime,
            matching,
            communication);
        var shutdown = shutdownService.ShutdownAsync();

        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, matching.DisposeCount);
        Assert.Equal(0, communication.DisposeCount);
        Assert.Equal(0, communication.DisconnectCount);

        communication.AllowConnect.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await shutdown;

        Assert.Equal(ProductionState.Stopped, coordinator.Snapshot.State);
        Assert.Equal(1, communication.ConnectCount);
        Assert.Equal(0, communication.DisconnectCount);
        Assert.Equal(1, matching.DisposeCount);
        Assert.Equal(1, communication.DisposeCount);
    }

    [Fact]
    public async Task ShutdownBeforeLoopPublicationPreventsLoopPublication()
    {
        var lifetime = new InspectionRunLifetime();
        var log = new CallbackAppLogService();
        var coordinator = CreateCoordinator(
            lifetime,
            new CleanupBlockingPlcClient(),
            log: log);
        log.InfoCallback = (source, message) =>
        {
            if (!string.Equals(source, "Production", StringComparison.Ordinal) ||
                !string.Equals(message, "连续生产启动", StringComparison.Ordinal))
            {
                return;
            }

            lifetime.BeginShutdown();
            coordinator.StopForShutdownAsync().GetAwaiter().GetResult();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StartAsync());

        Assert.Null(
            typeof(ProductionCoordinator)
                .GetField("_loopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator));
        Assert.Equal(ProductionState.Stopped, coordinator.Snapshot.State);
    }

    private static ProductionCoordinator CreateCoordinator(
        IInspectionRunLifetime lifetime,
        IPlcClient plc,
        ICommunicationChannelRuntime? communication = null,
        IAppLogService? log = null)
    {
        return new ProductionCoordinator(
            new SuccessfulInspectionRunner(),
            lifetime,
            new SimulatedCameraDevice(),
            plc,
            new SimulatedAxisController(),
            log ?? new NullAppLogService(),
            new NullAlarmService(),
            communication ?? new StubCommunicationChannelRuntime(),
            new DeviceConfiguration());
    }

    private sealed class SuccessfulInspectionRunner : IInspectionRunner
    {
        private static readonly ImageFrame Frame = new(
            "frame",
            1,
            1,
            1,
            PixelFormatKind.Gray8,
            [0],
            DateTimeOffset.UnixEpoch,
            "test");

        public event EventHandler<InspectionRunResult>? RunCompleted;

        public Task<InspectionRunResult> RunAsync(
            InspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new InspectionRunResult(
                new InspectionResult { Outcome = InspectionOutcome.Ok },
                Frame,
                Frame,
                new Recipe());
            RunCompleted?.Invoke(this, result);
            return Task.FromResult(result);
        }
    }

    private sealed class CleanupBlockingPlcClient : IPlcClient
    {
        public event EventHandler<DeviceSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public DeviceSnapshot Snapshot { get; } = new(
            "test-plc",
            DeviceConnectionState.Connected,
            "ready",
            DateTimeOffset.UnixEpoch);

        public TaskCompletionSource CleanupEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowCleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BusyWasSet { get; private set; }

        public bool BusyWasCleared { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task SetInspectionBusyAsync(
            bool busy,
            CancellationToken cancellationToken = default)
        {
            if (busy)
            {
                BusyWasSet = true;
                return;
            }

            BusyWasCleared = true;
            CleanupEntered.TrySetResult();
            await AllowCleanup.Task;
        }

        public Task<string> ReadAddressAsync(
            string address,
            CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

        public Task WriteAddressAsync(
            string address,
            string value,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteInspectionResultAsync(
            InspectionResult result,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetAlarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubCommunicationChannelRuntime : ICommunicationChannelRuntime
    {
        public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived
        {
            add { }
            remove { }
        }

        public int DisconnectCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int ConnectCount { get; private set; }

        public bool BlockConnect { get; init; }

        public bool HonorConnectCancellationWhileBlocked { get; init; }

        public bool BlockDisconnect { get; init; }

        public TaskCompletionSource ConnectEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowConnect { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisconnectEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowDisconnect { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ConnectAsync(
            string connectionPolicy,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            ConnectEntered.TrySetResult();
            if (BlockConnect)
            {
                if (HonorConnectCancellationWhileBlocked)
                {
                    await AllowConnect.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await AllowConnect.Task;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task DisconnectAsync(
            string connectionPolicy,
            CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            DisconnectEntered.TrySetResult();
            if (BlockDisconnect)
            {
                await AllowDisconnect.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(
            TcpCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => Task.FromResult(CreateSnapshot("Tcp", channel.Key));

        public Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(
            SerialCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => Task.FromResult(CreateSnapshot("Serial", channel.Key));

        public Task<CommunicationChannelRuntimeSnapshot> ReconnectTcpAsync(
            TcpCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => Task.FromResult(CreateSnapshot("Tcp", channel.Key));

        public Task<CommunicationChannelRuntimeSnapshot> ReconnectSerialAsync(
            SerialCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => Task.FromResult(CreateSnapshot("Serial", channel.Key));

        public Task<byte[]?> TryExchangeTcpAsync(
            TcpCommunicationChannelSettings channel,
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);

        public Task<bool> TrySendTcpAsync(
            TcpCommunicationChannelSettings channel,
            byte[] payload,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<byte[]?> TryExchangeSerialAsync(
            SerialCommunicationChannelSettings channel,
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);

        public Task<bool> TrySendSerialAsync(
            SerialCommunicationChannelSettings channel,
            byte[] payload,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public void Dispose()
        {
            DisposeCount++;
        }

        private static CommunicationChannelRuntimeSnapshot CreateSnapshot(string kind, string key)
        {
            return new CommunicationChannelRuntimeSnapshot(
                kind,
                key,
                CommunicationChannelConnectionPolicies.OnDemand,
                false,
                false,
                false,
                string.Empty,
                "test");
        }
    }

    private sealed class StubTemplateMatchingService : ITemplateMatchingService
    {
        public int DisposeCount { get; private set; }

        public Task<TemplateLearningResult> LearnAsync(
            TemplateLearningRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateMatchBatchResult> MatchAsync(
            TemplateMatchingRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullAppLogService : IAppLogService
    {
        public event EventHandler<AppLogEntry>? LogWritten
        {
            add { }
            remove { }
        }

        public void Info(string source, string message)
        {
        }

        public void Warning(string source, string message)
        {
        }

        public void Error(string source, string message)
        {
        }

        public void Critical(string source, string message)
        {
        }

        public IReadOnlyList<AppLogEntry> Recent(int count) => [];
    }

    private sealed class CallbackAppLogService : IAppLogService
    {
        public event EventHandler<AppLogEntry>? LogWritten
        {
            add { }
            remove { }
        }

        public Action<string, string>? InfoCallback { get; set; }

        public void Info(string source, string message)
        {
            InfoCallback?.Invoke(source, message);
        }

        public void Warning(string source, string message)
        {
        }

        public void Error(string source, string message)
        {
        }

        public void Critical(string source, string message)
        {
        }

        public IReadOnlyList<AppLogEntry> Recent(int count) => [];
    }

    private sealed class NullAlarmService : IAlarmService
    {
        public event EventHandler<AlarmEvent>? AlarmRaised
        {
            add { }
            remove { }
        }

        public event EventHandler<AlarmEvent>? AlarmChanged
        {
            add { }
            remove { }
        }

        public AlarmEvent Raise(
            AlarmSeverity severity,
            string source,
            string message,
            string details = "",
            string? alarmId = null) => new(
            alarmId ?? Guid.NewGuid().ToString("N"),
            severity,
            source,
            message,
            DateTimeOffset.Now,
            Details: details);

        public void Acknowledge(string alarmId)
        {
        }

        public void Clear(string alarmId)
        {
        }

        public IReadOnlyList<AlarmEvent> Active() => [];

        public IReadOnlyList<AlarmEvent> Recent(int count) => [];
    }
}
