using System.IO;
using System.Runtime.CompilerServices;
using Prism.Events;
using VisionStation.Application;
using VisionStation.Application.Presentation;
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
        IEventAggregator events)
    {
        return new RecipeManagementViewModel(
            repository,
            new RuntimePaths(_runtimeDirectory),
            CreateUnusedVisionDebugViewModel(),
            new NullFlowEditorDialogService(),
            new NullAppLogService(),
            events,
            new InMemoryDeviceConfigurationRepository(),
            new UnusedInspectionRunner(),
            new UnusedCommunicationChannelRuntime(),
            new InspectionRunControl(),
            new ImmediateUiDispatcher(),
            new UnsavedChangesService());
    }

    private static Recipe CreateRecipe(
        string storageRevision,
        string name,
        string description,
        string variableValue)
    {
        return new Recipe
        {
            Id = "recipe-1",
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

    private static VisionDebugViewModel CreateUnusedVisionDebugViewModel()
    {
        return (VisionDebugViewModel)RuntimeHelpers.GetUninitializedObject(typeof(VisionDebugViewModel));
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

    private sealed class UnusedCommunicationChannelRuntime : ICommunicationChannelRuntime
    {
        public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
