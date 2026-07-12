using System.IO;
using Prism.Events;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Client.Events;
using VisionStation.Client.ViewModels;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision.UI.Services;

namespace VisionStation.Vision.UI.Tests;

internal sealed class RecipeManagementTestHarness : IAsyncDisposable
{
    private readonly string _root;
    private readonly IEventAggregator _events;

    private RecipeManagementTestHarness(
        string root,
        IEventAggregator events,
        RecipeManagementViewModel viewModel,
        RecordingRecipeRepository recipes,
        FakeInspectionExecution execution,
        RecordingInspectionSession session,
        RecordingCommunicationChannels channels,
        RecordingRunControl runControl,
        ImmediateUiDispatcher uiDispatcher,
        ConfigurableFlowEditorDialogService flowEditor,
        ConfigurableAppLogService log)
    {
        _root = root;
        _events = events;
        ViewModel = viewModel;
        Recipes = recipes;
        Execution = execution;
        Session = session;
        Channels = channels;
        RunControl = runControl;
        UiDispatcher = uiDispatcher;
        FlowEditor = flowEditor;
        Log = log;
    }

    public RecipeManagementViewModel ViewModel { get; }
    public RecordingRecipeRepository Recipes { get; }
    public FakeInspectionExecution Execution { get; }
    public RecordingInspectionSession Session { get; }
    public RecordingCommunicationChannels Channels { get; }
    public RecordingRunControl RunControl { get; }
    public ImmediateUiDispatcher UiDispatcher { get; }
    public ConfigurableFlowEditorDialogService FlowEditor { get; }
    public ConfigurableAppLogService Log { get; }

    public static async Task<RecipeManagementTestHarness> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisionStationTests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePaths(root);
        var recipes = new RecordingRecipeRepository(new Recipe
        {
            Id = "recipe-1",
            Name = "Recipe 1",
            ProductCode = "P-1"
        });
        var session = new RecordingInspectionSession(Active(
            InspectionRunModes.RecipeTest,
            nameof(RecipeManagementViewModel)));
        var execution = new FakeInspectionExecution(session);
        var channels = new RecordingCommunicationChannels();
        var runControl = new RecordingRunControl();
        var uiDispatcher = new ImmediateUiDispatcher();
        var flowEditor = new ConfigurableFlowEditorDialogService();
        var log = new ConfigurableAppLogService();
        EventAggregator events;
        RecipeManagementViewModel viewModel;
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                new ImmediateSynchronizationContext());
            events = new EventAggregator();
            viewModel = new RecipeManagementViewModel(
                recipes,
                paths,
                flowEditor,
                log,
                events,
                new FakeDeviceConfigurationRepository(),
                execution,
                channels,
                runControl,
                uiDispatcher,
                new UnsavedChangesService());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await WaitUntilAsync(() => viewModel.SelectedRecipe is not null && !viewModel.IsBusy);
        return new RecipeManagementTestHarness(
            root,
            events,
            viewModel,
            recipes,
            execution,
            session,
            channels,
            runControl,
            uiDispatcher,
            flowEditor,
            log);
    }

    public static ActiveInspectionRun Active(InspectionRunMode mode, string entryPoint) =>
        new(Guid.NewGuid(), new InspectionRunIntent(mode, entryPoint), DateTimeOffset.UtcNow);

    public static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void PublishRecipeChanged(Recipe recipe)
    {
        Recipes.ReplaceCurrent(recipe);
        _events.GetEvent<RecipeChangedEvent>().Publish(recipe.Id);
    }

    public async ValueTask DisposeAsync()
    {
        ViewModel.OnNavigatedFrom(null!);
        Execution.ClearExternalCurrentForTeardown();
        await WaitUntilAsync(() => Execution.Current is null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

internal sealed class FakeInspectionExecution : IInspectionExecution
{
    private readonly RecordingInspectionSession _session;
    private EventHandler<InspectionExecutionChangedEventArgs>? _changed;

    public FakeInspectionExecution(RecordingInspectionSession session)
    {
        _session = session;
        _session.Released = () => PublishCurrent(null);
        TryBeginHandler = intent =>
        {
            PublishCurrent(_session.Run);
            return new RunAdmission.Acquired(_session);
        };
    }

    public ActiveInspectionRun? Current { get; private set; }
    public int ChangedSubscriberCount { get; private set; }
    public event EventHandler<InspectionExecutionChangedEventArgs>? Changed
    {
        add
        {
            _changed += value;
            ChangedSubscriberCount++;
        }
        remove
        {
            _changed -= value;
            ChangedSubscriberCount--;
        }
    }

    public event EventHandler<InspectionRunResult>? RunCompleted;
    public Func<InspectionRunIntent, RunAdmission> TryBeginHandler { get; set; }
    public int TryBeginCount { get; private set; }
    public InspectionRunIntent? LastIntent { get; private set; }

    public RunAdmission TryBegin(InspectionRunIntent intent)
    {
        TryBeginCount++;
        LastIntent = intent;
        return TryBeginHandler(intent);
    }

    public void PublishCurrent(ActiveInspectionRun? current)
    {
        Current = current;
        _changed?.Invoke(this, new InspectionExecutionChangedEventArgs(current));
    }

    public void ClearExternalCurrentForTeardown()
    {
        if (Current is not null && Current.SessionId != _session.Run.SessionId)
        {
            PublishCurrent(null);
        }
    }

    public void PublishCompleted(InspectionRunResult result) =>
        RunCompleted?.Invoke(this, result);
}

internal sealed class RecordingInspectionSession : IInspectionSession
{
    public RecordingInspectionSession(ActiveInspectionRun run)
    {
        Run = run;
    }

    public ActiveInspectionRun Run { get; }
    public List<InspectionRequest> Requests { get; } = [];
    public Func<InspectionRequest, CancellationToken, Task<InspectionRunResult>> Handler { get; set; } =
        static (_, _) => Task.FromResult(RecipeRunResults.Ok());
    public Action? Released { get; set; }
    public int DisposeCount { get; private set; }

    public Task<InspectionRunResult> ExecuteAsync(
        InspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Handler(request, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        Released?.Invoke();
        return ValueTask.CompletedTask;
    }
}

internal static class RecipeRunResults
{
    public static InspectionRunResult Ok()
    {
        var frame = new ImageFrame(
            "recipe-test-frame",
            1,
            1,
            1,
            PixelFormatKind.Gray8,
            [0],
            DateTimeOffset.UtcNow,
            "test");
        return new InspectionRunResult(
            new InspectionResult
            {
                Outcome = InspectionOutcome.Ok,
                CycleTime = TimeSpan.FromMilliseconds(5)
            },
            frame,
            frame,
            new Recipe { Id = "recipe-1", Name = "Recipe 1" });
    }
}

internal sealed class RecordingRecipeRepository : IRecipeRepository
{
    private readonly object _savedSyncRoot = new();
    private readonly List<Recipe> _savedRecipes = [];
    private Recipe _recipe;
    private int _getAsyncCount;

    public RecordingRecipeRepository(Recipe recipe) => _recipe = recipe;

    public int SaveCount { get; private set; }
    public int SetCurrentCount { get; private set; }
    public int GetAsyncCount => Volatile.Read(ref _getAsyncCount);
    public IReadOnlyList<Recipe> SavedRecipes
    {
        get
        {
            lock (_savedSyncRoot)
            {
                return _savedRecipes.ToArray();
            }
        }
    }

    public Func<Recipe, CancellationToken, Task> SaveHandler { get; set; } =
        static (_, _) => Task.CompletedTask;
    public Func<string, Recipe?, CancellationToken, Task<Recipe?>>? GetAsyncHandler { get; set; }

    public Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Volatile.Read(ref _recipe));

    public Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Volatile.Read(ref _recipe).Id);

    public Task SetCurrentRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        SetCurrentCount++;
        return Task.CompletedTask;
    }

    public Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _getAsyncCount);
        var current = Volatile.Read(ref _recipe);
        var handler = GetAsyncHandler;
        return handler is null
            ? Task.FromResult<Recipe?>(current.Id == recipeId ? current : null)
            : handler(recipeId, current, cancellationToken);
    }

    public void ReplaceCurrent(Recipe recipe) => Volatile.Write(ref _recipe, recipe);

    public Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>([Volatile.Read(ref _recipe)]);

    public async Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        await SaveHandler(recipe, cancellationToken);
        lock (_savedSyncRoot)
        {
            _savedRecipes.Add(recipe);
        }

        Volatile.Write(ref _recipe, recipe);
    }

    public Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class RecordingRunControl : IInspectionRunControl
{
    public bool IsPaused { get; private set; }
    public int BeginCount { get; private set; }
    public int EndCount { get; private set; }
    public int PauseCount { get; private set; }
    public int ResumeCount { get; private set; }
    public int RequestResetCount { get; private set; }
    public Action EndRunHandler { get; set; } = static () => { };
    public Action ResumeHandler { get; set; } = static () => { };
    public Action RequestResetHandler { get; set; } = static () => { };
    public void BeginRun()
    {
        BeginCount++;
        IsPaused = false;
    }

    public void EndRun()
    {
        EndCount++;
        IsPaused = false;
        EndRunHandler();
    }

    public void Pause()
    {
        PauseCount++;
        IsPaused = true;
    }

    public void Resume()
    {
        ResumeCount++;
        ResumeHandler();
        IsPaused = false;
    }

    public void RequestReset()
    {
        RequestResetCount++;
        RequestResetHandler();
    }
    public Task WaitIfPausedOrResetAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    private int _invokeDepth;

    public bool IsInvoking => Volatile.Read(ref _invokeDepth) > 0;

    public void Invoke(Action action)
    {
        Interlocked.Increment(ref _invokeDepth);
        try
        {
            action();
        }
        finally
        {
            Interlocked.Decrement(ref _invokeDepth);
        }
    }
}

internal sealed class ImmediateSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) =>
        callback(state);

    public override void Send(SendOrPostCallback callback, object? state) =>
        callback(state);
}

internal sealed class ConfigurableFlowEditorDialogService : IFlowEditorDialogService
{
    public Func<string?, CancellationToken, Task> Handler { get; set; } =
        static (_, _) => Task.CompletedTask;

    public Task ShowEditorAsync(
        string? recipeId = null,
        CancellationToken cancellationToken = default) =>
        Handler(recipeId, cancellationToken);
}

internal sealed class FakeDeviceConfigurationRepository : IDeviceConfigurationRepository
{
    public event EventHandler<DeviceConfiguration>? ConfigurationSaved;

    public Task<DeviceConfiguration> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceConfiguration());

    public Task SaveAsync(
        DeviceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ConfigurationSaved?.Invoke(this, configuration);
        return Task.CompletedTask;
    }
}

internal sealed class ConfigurableAppLogService : IAppLogService
{
    public bool ThrowOnWarning { get; set; }
    public bool ThrowOnError { get; set; }

    public event EventHandler<AppLogEntry>? LogWritten
    {
        add { }
        remove { }
    }

    public void Info(string source, string message) { }
    public void Warning(string source, string message)
    {
        if (ThrowOnWarning)
        {
            throw new InvalidOperationException("warning-log-failure");
        }
    }

    public void Error(string source, string message)
    {
        if (ThrowOnError)
        {
            throw new InvalidOperationException("error-log-failure");
        }
    }
    public void Critical(string source, string message) { }
    public IReadOnlyList<AppLogEntry> Recent(int count) => [];
}

internal sealed class RecordingCommunicationChannels : ICommunicationChannelRuntime
{
    public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived
    {
        add { }
        remove { }
    }

    public int ConnectCount { get; private set; }
    public int DisconnectCount { get; private set; }
    public Func<string, CancellationToken, Task> DisconnectHandler { get; set; } =
        static (_, _) => Task.CompletedTask;

    public Task ConnectAsync(
        string connectionPolicy,
        CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(
        string connectionPolicy,
        CancellationToken cancellationToken = default)
    {
        DisconnectCount++;
        return DisconnectHandler(connectionPolicy, cancellationToken);
    }

    public Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(
        TcpCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommunicationChannelRuntimeSnapshot(
            "TCP",
            channel.Key,
            channel.ConnectionPolicy,
            false,
            false,
            false,
            string.Empty,
            string.Empty));

    public Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(
        SerialCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommunicationChannelRuntimeSnapshot(
            "Serial",
            channel.Key,
            channel.ConnectionPolicy,
            false,
            false,
            false,
            string.Empty,
            string.Empty));

    public Task<CommunicationChannelRuntimeSnapshot> ReconnectTcpAsync(
        TcpCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) =>
        GetTcpSnapshotAsync(channel, cancellationToken);

    public Task<CommunicationChannelRuntimeSnapshot> ReconnectSerialAsync(
        SerialCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) =>
        GetSerialSnapshotAsync(channel, cancellationToken);

    public Task<byte[]?> TryExchangeTcpAsync(
        TcpCommunicationChannelSettings channel,
        byte[] payload,
        int timeoutMs,
        bool waitResponse,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    public Task<bool> TrySendTcpAsync(
        TcpCommunicationChannelSettings channel,
        byte[] payload,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<byte[]?> TryExchangeSerialAsync(
        SerialCommunicationChannelSettings channel,
        byte[] payload,
        int timeoutMs,
        bool waitResponse,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    public Task<bool> TrySendSerialAsync(
        SerialCommunicationChannelSettings channel,
        byte[] payload,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public void Dispose() { }
}
