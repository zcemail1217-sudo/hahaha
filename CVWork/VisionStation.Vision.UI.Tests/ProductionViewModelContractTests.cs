using System.IO;
using Prism.Commands;
using VisionStation.Application;
using VisionStation.Client.Services;
using VisionStation.Client.ViewModels;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.Services;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ProductionViewModelContractTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void ProductionDashboard_HasSingleConstructorWithInspectionExecution()
    {
        var constructor = Assert.Single(typeof(ProductionDashboardViewModel).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IInspectionExecution));
    }

    [Theory]
    [InlineData(nameof(ProductionDashboardViewModel.RunSingleCommand))]
    [InlineData(nameof(ProductionDashboardViewModel.StartCommand))]
    [InlineData(nameof(ProductionDashboardViewModel.StopCommand))]
    public void ProductionCommands_AreAwaitableAsyncCommands(string propertyName)
    {
        var property = typeof(ProductionDashboardViewModel).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(typeof(AsyncDelegateCommand), property.PropertyType);
    }

    [Fact]
    public void Shell_HasSingleConstructorWithInspectionExecution()
    {
        var constructor = Assert.Single(typeof(ShellWindowViewModel).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IInspectionExecution));
    }

    [Fact]
    public async Task RunSingle_ThrowingDispatcherDoesNotFaultOrStrandCommandAdmission()
    {
        using var harness = ProductionDashboardHarness.Create();
        harness.Execution.TryBeginHandler = _ => throw new InvalidOperationException("unexpected-execution");
        harness.Dispatcher.ThrowOnNextInvoke();

        var exception = await Record.ExceptionAsync(
            () => harness.ViewModel.RunSingleCommand.Execute().WaitAsync(CommandTimeout));

        Assert.True(
            exception is null &&
            !harness.ViewModel.IsBusy &&
            harness.ViewModel.RunSingleCommand.CanExecute(),
            $"Exception={exception?.GetType().Name ?? "none"}; " +
            $"IsBusy={harness.ViewModel.IsBusy}; " +
            $"CanExecute={harness.ViewModel.RunSingleCommand.CanExecute()}");
    }

    [Fact]
    public async Task RunSingle_ThrowingLogSubscriberDoesNotFaultAndRestoresCommandState()
    {
        using var harness = ProductionDashboardHarness.Create();
        harness.Execution.TryBeginHandler = _ => throw new InvalidOperationException("execution-failure");
        harness.Log.LogWritten += static (_, _) => throw new InvalidOperationException("log-subscriber-failure");

        var exception = await Record.ExceptionAsync(
            () => harness.ViewModel.RunSingleCommand.Execute().WaitAsync(CommandTimeout));

        Assert.True(
            exception is null &&
            !harness.ViewModel.IsBusy &&
            harness.ViewModel.RunSingleCommand.CanExecute(),
            $"Exception={exception?.GetType().Name ?? "none"}; " +
            $"IsBusy={harness.ViewModel.IsBusy}; " +
            $"CanExecute={harness.ViewModel.RunSingleCommand.CanExecute()}");
        Assert.Equal("生产命令失败：execution-failure", harness.ViewModel.LastMessage);
    }

    [Fact]
    public async Task RunSingle_ExternalOwnershipDuringFailureDoesNotOverwriteFailureMessage()
    {
        using var harness = ProductionDashboardHarness.Create();
        harness.Execution.TryBeginHandler = _ =>
        {
            harness.Execution.PublishExternal("配方试运行", "配方管理");
            throw new InvalidOperationException("interleaved-failure");
        };

        var exception = await Record.ExceptionAsync(
            () => harness.ViewModel.RunSingleCommand.Execute().WaitAsync(CommandTimeout));

        Assert.Null(exception);
        Assert.Equal("生产命令失败：interleaved-failure", harness.ViewModel.LastMessage);
        Assert.False(harness.ViewModel.IsBusy);
    }

    private sealed class ProductionDashboardHarness : IDisposable
    {
        private readonly string _runtimeDirectory;

        private ProductionDashboardHarness(
            string runtimeDirectory,
            ProductionDashboardViewModel viewModel,
            ControllableInspectionExecution execution,
            ThrowingUiDispatcher dispatcher,
            PublishingLogService log)
        {
            _runtimeDirectory = runtimeDirectory;
            ViewModel = viewModel;
            Execution = execution;
            Dispatcher = dispatcher;
            Log = log;
        }

        public ProductionDashboardViewModel ViewModel { get; }

        public ControllableInspectionExecution Execution { get; }

        public ThrowingUiDispatcher Dispatcher { get; }

        public PublishingLogService Log { get; }

        public static ProductionDashboardHarness Create()
        {
            var runtimeDirectory = Path.Combine(
                Path.GetTempPath(),
                $"VisionStation.ProductionDashboardTests-{Guid.NewGuid():N}");
            try
            {
                var execution = new ControllableInspectionExecution();
                var dispatcher = new ThrowingUiDispatcher();
                var log = new PublishingLogService();
                var configuration = new DeviceConfiguration();
                var coordinator = new ProductionCoordinator(
                    execution,
                    new SimulatedCameraDevice(),
                    new SimulatedPlcClient(),
                    new SimulatedAxisController(configuration),
                    log,
                    new NoOpAlarmService(),
                    new NoOpCommunicationChannels(),
                    configuration);
                var viewModel = new ProductionDashboardViewModel(
                    coordinator,
                    execution,
                    new EmptyInspectionRecordRepository(),
                    new InMemoryRecipeRepository(),
                    log,
                    dispatcher,
                    new EmptyOverlayBuilder(),
                    new ProductionDashboardLayoutService(new RuntimePaths(runtimeDirectory)));

                return new ProductionDashboardHarness(
                    runtimeDirectory,
                    viewModel,
                    execution,
                    dispatcher,
                    log);
            }
            catch
            {
                if (Directory.Exists(runtimeDirectory))
                {
                    Directory.Delete(runtimeDirectory, recursive: true);
                }

                throw;
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_runtimeDirectory))
            {
                Directory.Delete(_runtimeDirectory, recursive: true);
            }
        }
    }

    private sealed class ControllableInspectionExecution : IInspectionExecution
    {
        private ActiveInspectionRun? _current;

        public ActiveInspectionRun? Current => Volatile.Read(ref _current);

        public Func<InspectionRunIntent, RunAdmission> TryBeginHandler { get; set; } =
            _ => throw new InvalidOperationException("Inspection execution was not configured.");

        public event EventHandler<InspectionExecutionChangedEventArgs>? Changed;

        public event EventHandler<InspectionRunResult>? RunCompleted
        {
            add { }
            remove { }
        }

        public RunAdmission TryBegin(InspectionRunIntent intent) => TryBeginHandler(intent);

        public void PublishExternal(string displayName, string entryPoint)
        {
            var current = new ActiveInspectionRun(
                Guid.NewGuid(),
                new InspectionRunIntent(new InspectionRunMode("test.external", displayName), entryPoint),
                DateTimeOffset.UtcNow);
            Volatile.Write(ref _current, current);
            Changed?.Invoke(this, new InspectionExecutionChangedEventArgs(current));
        }
    }

    private sealed class ThrowingUiDispatcher : IUiDispatcher
    {
        private int _throwNext;

        public void ThrowOnNextInvoke() => Interlocked.Exchange(ref _throwNext, 1);

        public void Invoke(Action action)
        {
            if (Interlocked.Exchange(ref _throwNext, 0) == 1)
            {
                throw new InvalidOperationException("dispatcher-failure");
            }

            action();
        }
    }

    private sealed class PublishingLogService : IAppLogService
    {
        private readonly List<AppLogEntry> _entries = [];

        public event EventHandler<AppLogEntry>? LogWritten;

        public void Info(string source, string message) => Write("Info", source, message);

        public void Warning(string source, string message) => Write("Warning", source, message);

        public void Error(string source, string message) => Write("Error", source, message);

        public void Critical(string source, string message) => Write("Critical", source, message);

        public IReadOnlyList<AppLogEntry> Recent(int count)
        {
            lock (_entries)
            {
                return _entries
                    .TakeLast(Math.Max(0, count))
                    .Reverse()
                    .ToArray();
            }
        }

        private void Write(string level, string source, string message)
        {
            var entry = new AppLogEntry(DateTimeOffset.UtcNow, level, source, message);
            lock (_entries)
            {
                _entries.Add(entry);
            }

            LogWritten?.Invoke(this, entry);
        }
    }

    private sealed class InMemoryRecipeRepository : IRecipeRepository
    {
        private Recipe _recipe = new();

        public Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_recipe);

        public Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_recipe.Id);

        public Task SetCurrentRecipeAsync(string recipeId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Recipe?>(string.Equals(recipeId, _recipe.Id, StringComparison.OrdinalIgnoreCase)
                ? _recipe
                : null);

        public Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Recipe>>([_recipe]);

        public Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
        {
            _recipe = recipe;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyInspectionRecordRepository : IInspectionRecordRepository
    {
        public Task AddAsync(InspectionResult result, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<InspectionResult>> RecentAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InspectionResult>>([]);
    }

    private sealed class EmptyOverlayBuilder : IVisionOverlayBuilder
    {
        public IReadOnlyList<VisionOverlayItem> Build(
            Recipe recipe,
            ImageFrame frame,
            IReadOnlyList<ToolResult> toolResults,
            InspectionOutcome outcome) => [];
    }

    private sealed class NoOpAlarmService : IAlarmService
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
            string? alarmId = null) =>
            new(
                alarmId ?? Guid.NewGuid().ToString("N"),
                severity,
                source,
                message,
                DateTimeOffset.UtcNow,
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

    private sealed class NoOpCommunicationChannels : ICommunicationChannelRuntime
    {
        public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(string connectionPolicy, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DisconnectAsync(string connectionPolicy, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(
            TcpCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => Snapshot("tcp", channel.Key);

        public Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(
            SerialCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => Snapshot("serial", channel.Key);

        public Task<CommunicationChannelRuntimeSnapshot> ReconnectTcpAsync(
            TcpCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => Snapshot("tcp", channel.Key);

        public Task<CommunicationChannelRuntimeSnapshot> ReconnectSerialAsync(
            SerialCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => Snapshot("serial", channel.Key);

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
        }

        private static Task<CommunicationChannelRuntimeSnapshot> Snapshot(string kind, string key) =>
            Task.FromResult(new CommunicationChannelRuntimeSnapshot(
                kind,
                key,
                CommunicationChannelConnectionPolicies.Production,
                IsRuntimeManaged: true,
                IsConnected: true,
                IsListening: false,
                PeerText: string.Empty,
                StatusText: "Connected"));
    }
}
