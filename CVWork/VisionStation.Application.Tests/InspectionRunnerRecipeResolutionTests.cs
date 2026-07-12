using VisionStation.Application;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class InspectionRunnerRecipeResolutionTests
{
    [Fact]
    public async Task RunAsync_uses_matching_snapshot_without_reading_recipe_repository()
    {
        var repositoryRecipe = CreateProcessOnlyRecipe("recipe-1", "Repository Recipe");
        var snapshot = CreateProcessOnlyRecipe("recipe-1", "Frozen Snapshot");
        var recipes = new RecordingRecipeRepository(repositoryRecipe);
        var configuration = new RecordingDeviceConfigurationRepository();
        var runner = CreateRunner(recipes, configuration);

        var result = await runner.RunAsync(new InspectionRequest
        {
            RecipeId = snapshot.Id,
            RecipeSnapshot = snapshot,
            ProcessOnly = true
        });

        Assert.Equal("Frozen Snapshot", result.Recipe.Name);
        Assert.Equal("recipe-1", result.Result.RecipeId);
        Assert.Equal(0, recipes.GetAsyncCount);
        Assert.Equal(0, recipes.GetCurrentAsyncCount);
        Assert.Empty(snapshot.Flows);
        Assert.Single(result.Recipe.Flows);
    }

    [Fact]
    public async Task RunAsync_uses_snapshot_id_when_request_recipe_id_is_empty()
    {
        var snapshot = CreateProcessOnlyRecipe("snapshot-only", "Snapshot Only");
        var recipes = new RecordingRecipeRepository(
            CreateProcessOnlyRecipe("repository", "Repository Recipe"));
        var runner = CreateRunner(recipes, new RecordingDeviceConfigurationRepository());

        var result = await runner.RunAsync(new InspectionRequest
        {
            RecipeSnapshot = snapshot,
            ProcessOnly = true
        });

        Assert.Equal("snapshot-only", result.Result.RecipeId);
        Assert.Equal("snapshot-only", result.Recipe.Id);
        Assert.Equal(0, recipes.GetAsyncCount);
        Assert.Equal(0, recipes.GetCurrentAsyncCount);
    }

    [Fact]
    public async Task RunAsync_rejects_mismatched_snapshot_before_downstream_reads()
    {
        var recipes = new RecordingRecipeRepository(
            CreateProcessOnlyRecipe("recipe-1", "Repository Recipe"));
        var configuration = new RecordingDeviceConfigurationRepository();
        var runner = CreateRunner(recipes, configuration);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(new InspectionRequest
            {
                RecipeId = "recipe-1",
                RecipeSnapshot = CreateProcessOnlyRecipe("recipe-2", "Wrong Snapshot"),
                ProcessOnly = true
            }));

        Assert.Contains("RecipeSnapshot.Id", error.Message);
        Assert.Equal(0, recipes.GetAsyncCount);
        Assert.Equal(0, recipes.GetCurrentAsyncCount);
        Assert.Equal(0, configuration.GetAsyncCount);
    }

    [Fact]
    public async Task RunAsync_without_snapshot_preserves_repository_resolution()
    {
        var stored = CreateProcessOnlyRecipe("recipe-1", "Repository Recipe");
        var recipes = new RecordingRecipeRepository(stored);
        var runner = CreateRunner(recipes, new RecordingDeviceConfigurationRepository());

        var result = await runner.RunAsync(new InspectionRequest
        {
            RecipeId = stored.Id,
            ProcessOnly = true
        });

        Assert.Equal("Repository Recipe", result.Recipe.Name);
        Assert.Equal(1, recipes.GetAsyncCount);
        Assert.Equal(0, recipes.GetCurrentAsyncCount);
    }

    private static Recipe CreateProcessOnlyRecipe(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            ProcessSteps =
            [
                new ProcessStepDefinition
                {
                    StepNo = 1,
                    Name = "Resolve recipe only",
                    StepType = ProcessStepType.Delay,
                    DelayMs = 0
                }
            ]
        };

    private static InspectionRunner CreateRunner(
        IRecipeRepository recipes,
        IDeviceConfigurationRepository configurationRepository) =>
        new(
            new FakeCameraDevice(),
            new StubConfigurableCameraDevice(),
            new FakeAxisController(),
            new FakePlcClient(),
            new DeviceRuntime(),
            new DeviceConfiguration(),
            configurationRepository,
            new UnexpectedVisionPipeline(),
            recipes,
            new NoOpInspectionRecordRepository(),
            new UnexpectedImageTraceStore(),
            new FakeAppLogService(),
            new FakeCommunicationChannels(),
            new InspectionRunControl());

    private sealed class RecordingRecipeRepository : IRecipeRepository
    {
        private readonly Recipe _recipe;

        public RecordingRecipeRepository(Recipe recipe) => _recipe = recipe;

        public int GetAsyncCount { get; private set; }

        public int GetCurrentAsyncCount { get; private set; }

        public Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            GetCurrentAsyncCount++;
            return Task.FromResult(_recipe);
        }

        public Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_recipe.Id);

        public Task SetCurrentRecipeAsync(
            string recipeId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Recipe?> GetAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            GetAsyncCount++;
            return Task.FromResult<Recipe?>(
                string.Equals(recipeId, _recipe.Id, StringComparison.OrdinalIgnoreCase)
                    ? _recipe
                    : null);
        }

        public Task<IReadOnlyList<Recipe>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Recipe>>([_recipe]);

        public Task SaveAsync(
            Recipe recipe,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            string recipeId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingDeviceConfigurationRepository : IDeviceConfigurationRepository
    {
        public event EventHandler<DeviceConfiguration>? ConfigurationSaved;

        public int GetAsyncCount { get; private set; }

        public Task<DeviceConfiguration> GetAsync(CancellationToken cancellationToken = default)
        {
            GetAsyncCount++;
            return Task.FromResult(new DeviceConfiguration());
        }

        public Task SaveAsync(
            DeviceConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            ConfigurationSaved?.Invoke(this, configuration);
            return Task.CompletedTask;
        }
    }

    private sealed class StubConfigurableCameraDevice : IConfigurableCameraDevice
    {
        public string SelectedDeviceId { get; private set; } = string.Empty;

        public Task SelectDeviceAsync(
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            SelectedDeviceId = deviceId;
            return Task.CompletedTask;
        }

        public Task ApplyAcquisitionSettingsAsync(
            CameraAcquisitionSettings settings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UnexpectedVisionPipeline : IVisionPipeline
    {
        public Task<VisionPipelineResult> ExecuteAsync(
            Recipe recipe,
            ImageFrame frame,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Vision pipeline must not run in this test.");
    }

    private sealed class NoOpInspectionRecordRepository : IInspectionRecordRepository
    {
        public Task AddAsync(
            InspectionResult result,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<InspectionResult>> RecentAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InspectionResult>>([]);
    }

    private sealed class UnexpectedImageTraceStore : IImageTraceStore
    {
        public Task<ImageTracePaths> SaveAsync(
            Recipe recipe,
            ImageFrame originalFrame,
            ImageFrame resultFrame,
            InspectionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Trace store must not run in this test.");
    }
}
