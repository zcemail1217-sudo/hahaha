using System.IO;
using System.Runtime.CompilerServices;
using Prism.Events;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Application.Recipes;
using VisionStation.Client.Events;
using VisionStation.Client.ViewModels;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision.UI.Services;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class RecipeManagementViewModelTests : IDisposable
{
    private readonly string _runtimeDirectory = Path.Combine(
        Path.GetTempPath(),
        "VisionStation.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConstructorUsesRecipeTemplateLifecycleService()
    {
        var parameterTypes = typeof(RecipeManagementViewModel)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IRecipeTemplateLifecycleService), parameterTypes);
    }

    [Fact]
    public async Task DuplicateRecipeUsesLifecycleWithoutPreapplyingCopySuffixes()
    {
        var source = CreateRecipe("revision-r1", "source-name", "source-description", "source-value");
        var repository = new MutableRecipeRepository([source], source.Id);
        var lifecycle = new RecordingRecipeTemplateLifecycleService(repository);
        var viewModel = CreateViewModel(repository, new EventAggregator(), lifecycle);
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.RecipeId == source.Id);

        viewModel.DuplicateRecipeCommand.Execute();

        await WaitUntilAsync(() => lifecycle.DuplicateCallCount == 1 && viewModel.RecipeId != source.Id);
        Recipe duplicateSource = Assert.IsType<Recipe>(lifecycle.LastDuplicateSource);
        Assert.Equal(source.Name, duplicateSource.Name);
        Assert.Equal(source.ProductCode, duplicateSource.ProductCode);
        Assert.DoesNotContain("-副本", duplicateSource.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("-COPY", duplicateSource.ProductCode, StringComparison.Ordinal);
        Assert.NotEqual(source.Id, lifecycle.LastDuplicateRecipeId);
        Assert.Equal(0, repository.CreateCallCount);
    }

    [Fact]
    public async Task DuplicateRecipeDisablesReentryWhileLifecycleCopyIsPending()
    {
        var source = CreateRecipe("revision-r1", "source-name", "source-description", "source-value");
        var repository = new MutableRecipeRepository([source], source.Id);
        var lifecycle = new RecordingRecipeTemplateLifecycleService(repository)
        {
            BlockDuplicate = true
        };
        var viewModel = CreateViewModel(repository, new EventAggregator(), lifecycle);
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.RecipeId == source.Id);

        viewModel.DuplicateRecipeCommand.Execute();
        await lifecycle.DuplicateStarted;

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.DuplicateRecipeCommand.CanExecute());
        lifecycle.AllowDuplicateToComplete();
        await WaitUntilAsync(() => lifecycle.DuplicateCallCount == 1 && !viewModel.IsBusy);
    }

    [Fact]
    public async Task DeleteRecipeCapturesSelectionReloadsAuthoritativeRevisionAndOnlyCallsLifecycle()
    {
        var target = CreateRecipe("revision-r1", "target", "target-description", "target-value");
        var fallback = CreateRecipe(
            "revision-fallback",
            "fallback",
            "fallback-description",
            "fallback-value",
            "recipe-2");
        var repository = new MutableRecipeRepository([target, fallback], target.Id);
        var lifecycle = new RecordingRecipeTemplateLifecycleService(repository);
        var viewModel = CreateViewModel(repository, new EventAggregator(), lifecycle);
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.Recipes.Count == 2 && viewModel.RecipeId == target.Id);

        viewModel.ConfirmDeleteRecipe = _ =>
        {
            repository.Replace(target with { StorageRevision = "revision-r2" });
            viewModel.SelectedRecipe = Assert.Single(viewModel.Recipes, item => item.Id == fallback.Id);
            return true;
        };
        viewModel.DeleteRecipeCommand.Execute();

        await WaitUntilAsync(() => lifecycle.DeleteCallCount == 1 && !viewModel.IsBusy);
        Recipe deletedRecipe = Assert.IsType<Recipe>(lifecycle.LastDeletedRecipe);
        Assert.Equal(target.Id, deletedRecipe.Id);
        Assert.Equal("revision-r2", deletedRecipe.StorageRevision);
        Assert.Equal(0, repository.DeleteCallCount);
        Assert.Equal(0, repository.SetCurrentCallCount);
    }

    [Fact]
    public async Task DeleteLifecycleFailureKeepsSelectionAndReportsUiError()
    {
        var target = CreateRecipe("revision-r1", "target", "target-description", "target-value");
        var fallback = CreateRecipe(
            "revision-fallback",
            "fallback",
            "fallback-description",
            "fallback-value",
            "recipe-2");
        var repository = new MutableRecipeRepository([target, fallback], target.Id);
        var lifecycle = new RecordingRecipeTemplateLifecycleService(repository)
        {
            DeleteFailure = new InvalidOperationException("revision conflict")
        };
        var log = new RecordingAppLogService();
        var viewModel = CreateViewModel(
            repository,
            new EventAggregator(),
            lifecycle,
            log: log);
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.RecipeId == target.Id);
        viewModel.ConfirmDeleteRecipe = _ => true;

        viewModel.DeleteRecipeCommand.Execute();

        await WaitUntilAsync(() => lifecycle.DeleteCallCount == 1 && !viewModel.IsBusy);
        Assert.Equal(target.Id, viewModel.RecipeId);
        Assert.Equal(target.Id, viewModel.SelectedRecipe?.Id);
        Assert.Contains("删除配方失败", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("revision conflict", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains(log.Errors, message => message.Contains("revision conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestRunTracksSaveConnectionAndInspectionAsOneLifetimeOperation()
    {
        var recipe = CreateRecipe("revision-r1", "recipe", "description", "value");
        var lifetime = new RecordingInspectionRunLifetime();
        var repository = new MutableRecipeRepository([recipe], recipe.Id)
        {
            IsInsideLifetime = () => lifetime.IsInsideOperation
        };
        var events = new EventAggregator();
        var recipeChangedAfterCurrentSelection = false;
        events.GetEvent<RecipeChangedEvent>().Subscribe(
            _ => recipeChangedAfterCurrentSelection = repository.SetCurrentCallCount == 1);
        var communication = new UnusedCommunicationChannelRuntime(() => lifetime.IsInsideOperation);
        var runner = new SuccessfulInspectionRunner(() => lifetime.IsInsideOperation);
        var viewModel = CreateViewModel(
            repository,
            events,
            inspectionRunner: runner,
            communicationChannels: communication,
            inspectionRunLifetime: lifetime);
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.RecipeId == recipe.Id);

        viewModel.TestRunRecipeCommand.Execute();

        try
        {
            await WaitUntilAsync(() => runner.RunCallCount == 1 && !viewModel.IsTestRunning);
        }
        catch (TaskCanceledException)
        {
            Assert.Fail(
                $"Timed out waiting for the test run. Calls={runner.RunCallCount}, " +
                $"Running={viewModel.IsTestRunning}, Busy={viewModel.IsBusy}, " +
                $"LifetimeCalls={lifetime.RunTrackedCallCount}, Status={viewModel.StatusText}");
        }
        Assert.Equal(1, lifetime.RunTrackedCallCount);
        Assert.True(repository.SaveObservedInsideLifetime);
        Assert.True(repository.SetCurrentObservedInsideLifetime);
        Assert.True(communication.ConnectObservedInsideLifetime);
        Assert.True(runner.RunObservedInsideLifetime);
        Assert.True(recipeChangedAfterCurrentSelection);
    }

    [Fact]
    public async Task ShutdownCancellationWinsOverConcurrentResetAndDoesNotRestartTestRun()
    {
        var recipe = CreateRecipe("revision-r1", "recipe", "description", "value");
        var lifetime = new InspectionRunLifetime();
        var runner = new CoordinatedCancellationInspectionRunner();
        var viewModel = CreateViewModel(
            new MutableRecipeRepository([recipe], recipe.Id),
            new EventAggregator(),
            inspectionRunner: runner,
            communicationChannels: new UnusedCommunicationChannelRuntime(() => true),
            inspectionRunLifetime: lifetime);
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.RecipeId == recipe.Id);

        viewModel.TestRunRecipeCommand.Execute();
        await runner.Started;
        viewModel.ResetTestRunCommand.Execute();
        await runner.CancellationObserved;
        lifetime.BeginShutdown();
        runner.AllowCancellationToPropagate();

        await WaitUntilAsync(() => !viewModel.IsTestRunning);
        await lifetime.DrainAsync();
        Assert.Equal(1, runner.RunCallCount);
        Assert.False(viewModel.IsTestRunPaused);
        Assert.Contains("应用正在关闭", viewModel.TestRunStateText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalVariableSync_UsesAuthoritativeRevisionOnNextSave_AndKeepsLocalNonVariableEdits()
    {
        var revisionOne = "revision-r1";
        var revisionTwo = "revision-r2";
        var initialRecipe = CreateRecipe(revisionOne, "stored-name", "stored-description", "old-value");
        var authoritativeRecipe = CreateRecipe(revisionTwo, "external-name", "external-description", "new-value");
        var repository = new RevisionTrackingRecipeRepository(initialRecipe);
        EventAggregator events;
        RecipeManagementViewModel viewModel;
        var previousSynchronizationContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());
            events = new EventAggregator();
            viewModel = CreateViewModel(repository, events);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }

        viewModel.RecipeName = "local-name";
        viewModel.Description = "local-description";
        Assert.True(viewModel.HasUnsavedChanges);

        repository.AuthoritativeRecipe = authoritativeRecipe;
        events.GetEvent<RecipeChangedEvent>().Publish(initialRecipe.Id);

        Assert.Equal("new-value", Assert.Single(viewModel.RecipeVariables).CurrentValue);
        Assert.Equal("local-name", viewModel.RecipeName);
        Assert.Equal("local-description", viewModel.Description);
        Assert.True(viewModel.HasUnsavedChanges);

        viewModel.SaveCommand.Execute();

        var savedRecipe = Assert.IsType<Recipe>(repository.LastSavedRecipe);
        Assert.Equal(revisionTwo, savedRecipe.StorageRevision);
        Assert.Equal("local-name", savedRecipe.Name);
        Assert.Equal("local-description", savedRecipe.Description);
        Assert.Equal("new-value", Assert.Single(savedRecipe.Variables).CurrentValue);
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeDirectory))
        {
            Directory.Delete(_runtimeDirectory, recursive: true);
        }
    }

    private RecipeManagementViewModel CreateViewModel(
        IRecipeRepository repository,
        IEventAggregator events,
        IRecipeTemplateLifecycleService? recipeTemplateLifecycle = null,
        IInspectionRunner? inspectionRunner = null,
        ICommunicationChannelRuntime? communicationChannels = null,
        IInspectionRunLifetime? inspectionRunLifetime = null,
        IAppLogService? log = null)
    {
        return new RecipeManagementViewModel(
            repository,
            new RuntimePaths(_runtimeDirectory),
            CreateUnusedVisionDebugViewModel(),
            new NullFlowEditorDialogService(),
            log ?? new NullAppLogService(),
            events,
            new InMemoryDeviceConfigurationRepository(),
            inspectionRunner ?? new UnusedInspectionRunner(),
            communicationChannels ?? new UnusedCommunicationChannelRuntime(),
            new InspectionRunControl(),
            new ImmediateUiDispatcher(),
            new UnsavedChangesService(),
            recipeTemplateLifecycle ?? new RecordingRecipeTemplateLifecycleService(),
            inspectionRunLifetime ?? new RecordingInspectionRunLifetime());
    }

    private static Recipe CreateRecipe(
        string storageRevision,
        string name,
        string description,
        string variableValue,
        string id = "recipe-1")
    {
        return new Recipe
        {
            Id = id,
            StorageRevision = storageRevision,
            Name = name,
            ProductCode = "PRODUCT-1",
            Description = description,
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Id = "variable-1",
                    Key = "ProductCode",
                    Name = "Product Code",
                    CurrentValue = variableValue
                }
            ]
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private static VisionDebugViewModel CreateUnusedVisionDebugViewModel()
    {
        return (VisionDebugViewModel)RuntimeHelpers.GetUninitializedObject(typeof(VisionDebugViewModel));
    }

    private sealed class RecordingRecipeTemplateLifecycleService : IRecipeTemplateLifecycleService
    {
        private readonly MutableRecipeRepository? _repository;
        private readonly TaskCompletionSource _duplicateStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowDuplicate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingRecipeTemplateLifecycleService(MutableRecipeRepository? repository = null)
        {
            _repository = repository;
        }

        public int DuplicateCallCount { get; private set; }

        public bool BlockDuplicate { get; init; }

        public Task DuplicateStarted => _duplicateStarted.Task;

        public Recipe? LastDuplicateSource { get; private set; }

        public string? LastDuplicateRecipeId { get; private set; }

        public int DeleteCallCount { get; private set; }

        public Recipe? LastDeletedRecipe { get; private set; }

        public Exception? DeleteFailure { get; init; }

        public async Task<Recipe> DuplicateAsync(
            Recipe source,
            string newRecipeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DuplicateCallCount++;
            LastDuplicateSource = source;
            LastDuplicateRecipeId = newRecipeId;
            _duplicateStarted.TrySetResult();
            if (BlockDuplicate)
            {
                await _allowDuplicate.Task.WaitAsync(cancellationToken);
            }

            var copy = source with
            {
                Id = newRecipeId,
                Name = $"{source.Name}-副本",
                ProductCode = $"{source.ProductCode}-COPY",
                StorageRevision = "revision-copy"
            };
            _repository?.Replace(copy);
            return copy;
        }

        public void AllowDuplicateToComplete()
        {
            _allowDuplicate.TrySetResult();
        }

        public Task DeleteAsync(Recipe recipe, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCallCount++;
            LastDeletedRecipe = recipe;
            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }

            _repository?.Remove(recipe.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingInspectionRunLifetime : IInspectionRunLifetime
    {
        private readonly CancellationTokenSource _shutdownCancellation = new();
        private int _insideOperation;

        public bool IsShutdownRequested => _shutdownCancellation.IsCancellationRequested;

        public bool IsInsideOperation => Volatile.Read(ref _insideOperation) > 0;

        public int RunTrackedCallCount { get; private set; }

        public async Task<T> RunTrackedAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            RunTrackedCallCount++;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdownCancellation.Token);
            Interlocked.Increment(ref _insideOperation);
            try
            {
                return await operation(linkedCancellation.Token);
            }
            finally
            {
                Interlocked.Decrement(ref _insideOperation);
            }
        }

        public void BeginShutdown()
        {
            _shutdownCancellation.Cancel();
        }

        public Task DrainAsync()
        {
            return Task.CompletedTask;
        }
    }

    private sealed class MutableRecipeRepository(
        IEnumerable<Recipe> recipes,
        string currentRecipeId) : IRecipeRepository
    {
        private readonly List<Recipe> _recipes = [.. recipes];
        private string _currentRecipeId = currentRecipeId;

        public int CreateCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public int SetCurrentCallCount { get; private set; }

        public Func<bool>? IsInsideLifetime { get; init; }

        public bool SaveObservedInsideLifetime { get; private set; }

        public bool SetCurrentObservedInsideLifetime { get; private set; }

        public Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                _recipes.First(recipe => string.Equals(recipe.Id, _currentRecipeId, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_currentRecipeId);
        }

        public Task SetCurrentRecipeAsync(string recipeId, CancellationToken cancellationToken = default)
        {
            SetCurrentCallCount++;
            SetCurrentObservedInsideLifetime = IsInsideLifetime?.Invoke() == true;
            _currentRecipeId = recipeId;
            return Task.CompletedTask;
        }

        public Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_recipes.FirstOrDefault(recipe =>
                string.Equals(recipe.Id, recipeId, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Recipe>>([.. _recipes]);
        }

        public Task<Recipe> CreateAsync(Recipe recipe, CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            throw new InvalidOperationException("RecipeManagementViewModel must duplicate through the lifecycle service.");
        }

        public Task<Recipe> SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
        {
            SaveObservedInsideLifetime = IsInsideLifetime?.Invoke() == true;
            Replace(recipe);
            return Task.FromResult(recipe);
        }

        public Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            throw new InvalidOperationException("RecipeManagementViewModel must delete through the lifecycle service.");
        }

        public Task<RecipeMutationSession> BeginMutationAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Replace(Recipe recipe)
        {
            var index = _recipes.FindIndex(item =>
                string.Equals(item.Id, recipe.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _recipes[index] = recipe;
            }
            else
            {
                _recipes.Add(recipe);
            }
        }

        public void Remove(string recipeId)
        {
            _recipes.RemoveAll(recipe =>
                string.Equals(recipe.Id, recipeId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class RevisionTrackingRecipeRepository(Recipe authoritativeRecipe) : IRecipeRepository
    {
        public Recipe AuthoritativeRecipe { get; set; } = authoritativeRecipe;

        public Recipe? LastSavedRecipe { get; private set; }

        public Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AuthoritativeRecipe);
        }

        public Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AuthoritativeRecipe.Id);
        }

        public Task SetCurrentRecipeAsync(string recipeId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default)
        {
            Recipe? recipe = string.Equals(recipeId, AuthoritativeRecipe.Id, StringComparison.OrdinalIgnoreCase)
                ? AuthoritativeRecipe
                : null;
            return Task.FromResult(recipe);
        }

        public Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<Recipe> recipes = [AuthoritativeRecipe];
            return Task.FromResult(recipes);
        }

        public Task<Recipe> CreateAsync(Recipe recipe, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Recipe> SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
        {
            LastSavedRecipe = recipe;
            AuthoritativeRecipe = recipe with { StorageRevision = "revision-r3" };
            return Task.FromResult(AuthoritativeRecipe);
        }

        public Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RecipeMutationSession> BeginMutationAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class InMemoryDeviceConfigurationRepository : IDeviceConfigurationRepository
    {
        public event EventHandler<DeviceConfiguration>? ConfigurationSaved
        {
            add { }
            remove { }
        }

        public Task<DeviceConfiguration> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeviceConfiguration());
        }

        public Task SaveAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NullFlowEditorDialogService : IFlowEditorDialogService
    {
        public void ShowEditor(VisionDebugViewModel viewModel)
        {
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

        public IReadOnlyList<AppLogEntry> Recent(int count)
        {
            return Array.Empty<AppLogEntry>();
        }
    }

    private sealed class RecordingAppLogService : IAppLogService
    {
        public event EventHandler<AppLogEntry>? LogWritten
        {
            add { }
            remove { }
        }

        public List<string> Errors { get; } = [];

        public void Info(string source, string message)
        {
        }

        public void Warning(string source, string message)
        {
        }

        public void Error(string source, string message)
        {
            Errors.Add(message);
        }

        public void Critical(string source, string message)
        {
        }

        public IReadOnlyList<AppLogEntry> Recent(int count)
        {
            return Array.Empty<AppLogEntry>();
        }
    }

    private sealed class UnusedInspectionRunner : IInspectionRunner
    {
        public event EventHandler<InspectionRunResult>? RunCompleted
        {
            add { }
            remove { }
        }

        public Task<InspectionRunResult> RunAsync(
            InspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class SuccessfulInspectionRunner(Func<bool> isInsideLifetime) : IInspectionRunner
    {
        public event EventHandler<InspectionRunResult>? RunCompleted
        {
            add { }
            remove { }
        }

        public int RunCallCount { get; private set; }

        public bool RunObservedInsideLifetime { get; private set; }

        public Task<InspectionRunResult> RunAsync(
            InspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCallCount++;
            RunObservedInsideLifetime = isInsideLifetime();
            var frame = new ImageFrame(
                "frame",
                1,
                1,
                1,
                PixelFormatKind.Gray8,
                [0],
                DateTimeOffset.Now,
                "test");
            return Task.FromResult(new InspectionRunResult(
                new InspectionResult
                {
                    RecipeId = request.RecipeId,
                    Outcome = InspectionOutcome.Ok,
                    CycleTime = TimeSpan.FromMilliseconds(1)
                },
                frame,
                frame,
                new Recipe { Id = request.RecipeId }));
        }
    }

    private sealed class CoordinatedCancellationInspectionRunner : IInspectionRunner
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<InspectionRunResult>? RunCompleted
        {
            add { }
            remove { }
        }

        public int RunCallCount { get; private set; }

        public Task Started => _started.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public async Task<InspectionRunResult> RunAsync(
            InspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            RunCallCount++;
            _started.TrySetResult();
            using var registration = cancellationToken.Register(
                () => _cancellationObserved.TrySetResult());
            await _cancellationObserved.Task;
            await _allowCancellation.Task;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was expected.");
        }

        public void AllowCancellationToPropagate()
        {
            _allowCancellation.TrySetResult();
        }
    }

    private sealed class UnusedCommunicationChannelRuntime(Func<bool>? isInsideLifetime = null)
        : ICommunicationChannelRuntime
    {
        public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived
        {
            add { }
            remove { }
        }

        public bool ConnectObservedInsideLifetime { get; private set; }

        public Task ConnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
        {
            if (isInsideLifetime is null)
            {
                throw new NotSupportedException();
            }

            cancellationToken.ThrowIfCancellationRequested();
            ConnectObservedInsideLifetime = isInsideLifetime();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(
            TcpCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(
            SerialCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CommunicationChannelRuntimeSnapshot> ReconnectTcpAsync(
            TcpCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CommunicationChannelRuntimeSnapshot> ReconnectSerialAsync(
            SerialCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<byte[]?> TryExchangeTcpAsync(
            TcpCommunicationChannelSettings channel,
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TrySendTcpAsync(
            TcpCommunicationChannelSettings channel,
            byte[] payload,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<byte[]?> TryExchangeSerialAsync(
            SerialCommunicationChannelSettings channel,
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TrySendSerialAsync(
            SerialCommunicationChannelSettings channel,
            byte[] payload,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action)
        {
            action();
        }
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            callback(state);
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            callback(state);
        }
    }
}
