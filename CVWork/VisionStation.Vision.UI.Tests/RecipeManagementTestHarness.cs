using System.IO;
using Prism.Events;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Client.ViewModels;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision.UI.Services;

namespace VisionStation.Vision.UI.Tests;

internal sealed class RecipeManagementTestHarness : IAsyncDisposable
{
    private readonly string _root;

    private RecipeManagementTestHarness(
        string root,
        RecipeManagementViewModel viewModel,
        RecordingRecipeRepository recipes,
        FakeInspectionExecution execution,
        RecordingInspectionSession session,
        RecordingCommunicationChannels channels,
        RecordingRunControl runControl)
    {
        _root = root;
        ViewModel = viewModel;
        Recipes = recipes;
        Execution = execution;
        Session = session;
        Channels = channels;
        RunControl = runControl;
    }

    public RecipeManagementViewModel ViewModel { get; }
    public RecordingRecipeRepository Recipes { get; }
    public FakeInspectionExecution Execution { get; }
    public RecordingInspectionSession Session { get; }
    public RecordingCommunicationChannels Channels { get; }
    public RecordingRunControl RunControl { get; }

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
        var viewModel = new RecipeManagementViewModel(
            recipes,
            paths,
            new NullFlowEditorDialogService(),
            new NullAppLogService(),
            new EventAggregator(),
            new FakeDeviceConfigurationRepository(),
            execution,
            channels,
            runControl,
            new ImmediateUiDispatcher(),
            new UnsavedChangesService());

        await WaitUntilAsync(() => viewModel.SelectedRecipe is not null && !viewModel.IsBusy);
        return new RecipeManagementTestHarness(
            root,
            viewModel,
            recipes,
            execution,
            session,
            channels,
            runControl);
    }

    public static ActiveInspectionRun Active(InspectionRunMode mode, string entryPoint) =>
        new(Guid.NewGuid(), new InspectionRunIntent(mode, entryPoint), DateTimeOffset.UtcNow);

    public static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private static async Task WaitUntilAsync(Func<bool> condition)
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
    private Recipe _recipe;

    public RecordingRecipeRepository(Recipe recipe) => _recipe = recipe;

    public int SaveCount { get; private set; }
    public int SetCurrentCount { get; private set; }
    public Func<Recipe, CancellationToken, Task> SaveHandler { get; set; } =
        static (_, _) => Task.CompletedTask;

    public Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_recipe);

    public Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_recipe.Id);

    public Task SetCurrentRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        SetCurrentCount++;
        return Task.CompletedTask;
    }

    public Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Recipe?>(_recipe.Id == recipeId ? _recipe : null);

    public Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>([_recipe]);

    public async Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        await SaveHandler(recipe, cancellationToken);
        _recipe = recipe;
    }

    public Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class RecordingRunControl : IInspectionRunControl
{
    public bool IsPaused { get; private set; }
    public int BeginCount { get; private set; }
    public void BeginRun()
    {
        BeginCount++;
        IsPaused = false;
    }

    public void EndRun() => IsPaused = false;
    public void Pause() => IsPaused = true;
    public void Resume() => IsPaused = false;
    public void RequestReset() { }
    public Task WaitIfPausedOrResetAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();
}

internal sealed class NullFlowEditorDialogService : IFlowEditorDialogService
{
    public Task ShowEditorAsync(
        string? recipeId = null,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
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

internal sealed class NullAppLogService : IAppLogService
{
    public event EventHandler<AppLogEntry>? LogWritten
    {
        add { }
        remove { }
    }

    public void Info(string source, string message) { }
    public void Warning(string source, string message) { }
    public void Error(string source, string message) { }
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
        return Task.CompletedTask;
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
