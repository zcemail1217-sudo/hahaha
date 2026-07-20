using VisionStation.Application;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class InspectionRunnerRecipeResolutionTests
{
    [Fact]
    public async Task ExecuteAsync_uses_matching_snapshot_without_reading_recipe_repository()
    {
        var repositoryRecipe = CreateProcessOnlyRecipe("recipe-1", "Repository Recipe");
        var snapshot = CreateProcessOnlyRecipe("recipe-1", "Frozen Snapshot");
        var recipes = new RecordingRecipeRepository(repositoryRecipe);
        var configuration = new RecordingDeviceConfigurationRepository();
        var runner = CreateRunner(recipes, configuration);

        var result = await runner.ExecuteAsync(new InspectionRequest
        {
            RecipeId = "RECIPE-1",
            RecipeSnapshot = snapshot,
            ProcessOnly = true
        });

        Assert.Equal("Frozen Snapshot", result.Recipe.Name);
        Assert.Equal("recipe-1", result.Result.RecipeId);
        Assert.Equal("recipe-1", result.Recipe.Id);
        Assert.Equal(0, recipes.TotalCalls);
        Assert.Equal(0, recipes.GetAsyncCount);
        Assert.Equal(0, recipes.GetCurrentAsyncCount);
        Assert.Empty(snapshot.Flows);
        Assert.Single(result.Recipe.Flows);
    }

    [Fact]
    public async Task ExecuteAsync_uses_snapshot_id_when_request_recipe_id_is_empty()
    {
        var snapshot = CreateProcessOnlyRecipe("snapshot-only", "Snapshot Only");
        var recipes = new RecordingRecipeRepository(
            CreateProcessOnlyRecipe("repository", "Repository Recipe"));
        var runner = CreateRunner(recipes, new RecordingDeviceConfigurationRepository());

        var result = await runner.ExecuteAsync(new InspectionRequest
        {
            RecipeSnapshot = snapshot,
            ProcessOnly = true
        });

        Assert.Equal("snapshot-only", result.Result.RecipeId);
        Assert.Equal("snapshot-only", result.Recipe.Id);
        Assert.Equal(0, recipes.TotalCalls);
        Assert.Equal(0, recipes.GetAsyncCount);
        Assert.Equal(0, recipes.GetCurrentAsyncCount);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_blank_snapshot_id_before_downstream_reads()
    {
        var recipes = new RecordingRecipeRepository(
            CreateProcessOnlyRecipe("recipe-1", "Repository Recipe"));
        var configuration = new RecordingDeviceConfigurationRepository();
        var runner = CreateRunner(recipes, configuration);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ExecuteAsync(new InspectionRequest
            {
                RecipeId = "recipe-1",
                RecipeSnapshot = CreateProcessOnlyRecipe(" ", "Invalid Snapshot"),
                ProcessOnly = true
            }));

        Assert.StartsWith("RecipeSnapshot.Id is required.", error.Message);
        Assert.Equal("request", error.ParamName);
        Assert.Equal(0, recipes.TotalCalls);
        Assert.Equal(0, recipes.GetAsyncCount);
        Assert.Equal(0, recipes.GetCurrentAsyncCount);
        Assert.Equal(0, configuration.GetAsyncCount);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_mismatched_snapshot_before_downstream_reads()
    {
        var recipes = new RecordingRecipeRepository(
            CreateProcessOnlyRecipe("recipe-1", "Repository Recipe"));
        var configuration = new RecordingDeviceConfigurationRepository();
        var runner = CreateRunner(recipes, configuration);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.ExecuteAsync(new InspectionRequest
            {
                RecipeId = "recipe-1",
                RecipeSnapshot = CreateProcessOnlyRecipe("recipe-2", "Wrong Snapshot"),
                ProcessOnly = true
            }));

        Assert.Contains("RecipeSnapshot.Id", error.Message);
        Assert.Contains("recipe-1", error.Message);
        Assert.Contains("recipe-2", error.Message);
        Assert.Equal("request", error.ParamName);
        Assert.Equal(0, recipes.TotalCalls);
        Assert.Equal(0, recipes.GetAsyncCount);
        Assert.Equal(0, recipes.GetCurrentAsyncCount);
        Assert.Equal(0, configuration.GetAsyncCount);
    }

    [Fact]
    public async Task ExecuteAsync_without_snapshot_preserves_repository_resolution()
    {
        var stored = CreateProcessOnlyRecipe("recipe-1", "Repository Recipe");
        var recipes = new RecordingRecipeRepository(stored);
        var runner = CreateRunner(recipes, new RecordingDeviceConfigurationRepository());

        var result = await runner.ExecuteAsync(new InspectionRequest
        {
            RecipeId = stored.Id,
            ProcessOnly = true
        });

        Assert.Equal("Repository Recipe", result.Recipe.Name);
        Assert.Equal(1, recipes.TotalCalls);
        Assert.Equal(1, recipes.GetAsyncCount);
        Assert.Equal(0, recipes.GetCurrentAsyncCount);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("missing", 1)]
    public async Task ExecuteAsync_without_snapshot_falls_back_to_current_recipe(
        string recipeId,
        int expectedGetAsyncCount)
    {
        var current = CreateProcessOnlyRecipe("current-recipe", "Current Recipe");
        var recipes = new RecordingRecipeRepository(current);
        var runner = CreateRunner(recipes, new RecordingDeviceConfigurationRepository());

        var result = await runner.ExecuteAsync(new InspectionRequest
        {
            RecipeId = recipeId,
            RecipeSnapshot = null,
            ProcessOnly = true
        });

        Assert.Equal("Current Recipe", result.Recipe.Name);
        Assert.Equal("current-recipe", result.Recipe.Id);
        Assert.Equal("current-recipe", result.Result.RecipeId);
        Assert.Equal(expectedGetAsyncCount + 1, recipes.TotalCalls);
        Assert.Equal(expectedGetAsyncCount, recipes.GetAsyncCount);
        Assert.Equal(1, recipes.GetCurrentAsyncCount);
    }

    [Fact]
    public async Task ExecuteAsync_non_process_only_uses_snapshot_business_id_for_result_trace_and_record()
    {
        var snapshot = new Recipe
        {
            Id = "recipe-1",
            Name = "Frozen Snapshot"
        };
        var recipes = new RecordingRecipeRepository(
            CreateProcessOnlyRecipe("repository", "Repository Recipe"));
        var configuration = new RecordingDeviceConfigurationRepository();
        var records = new RecordingInspectionRecordRepository();
        var traceStore = new RecordingImageTraceStore();
        var runner = CreateRunner(
            recipes,
            configuration,
            new PassThroughVisionPipeline(),
            records,
            traceStore);

        var result = await runner.ExecuteAsync(new InspectionRequest
        {
            RecipeId = "RECIPE-1",
            RecipeSnapshot = snapshot,
            ProcessOnly = false
        });

        Assert.Equal("recipe-1", result.Result.RecipeId);
        Assert.Equal("recipe-1", result.Recipe.Id);
        Assert.Equal("recipe-1", Assert.IsType<Recipe>(traceStore.SavedRecipe).Id);
        Assert.Equal("recipe-1", Assert.IsType<InspectionResult>(traceStore.SavedResult).RecipeId);
        Assert.Equal("recipe-1", Assert.IsType<InspectionResult>(records.AddedResult).RecipeId);
        Assert.Equal(1, traceStore.SaveCount);
        Assert.Equal(1, records.AddCount);
        Assert.Equal(0, recipes.TotalCalls);
        Assert.Equal(0, recipes.GetAsyncCount);
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
        CreateRunner(
            recipes,
            configurationRepository,
            new UnexpectedVisionPipeline(),
            new NoOpInspectionRecordRepository(),
            new UnexpectedImageTraceStore());

    private static InspectionRunner CreateRunner(
        IRecipeRepository recipes,
        IDeviceConfigurationRepository configurationRepository,
        IVisionPipeline pipeline,
        IInspectionRecordRepository records,
        IImageTraceStore traceStore) =>
        new(
            new FakeCameraDevice(),
            new StubConfigurableCameraDevice(),
            new FakeAxisController(),
            new FakePlcClient(),
            new DeviceRuntime(),
            new DeviceConfiguration(),
            configurationRepository,
            pipeline,
            recipes,
            records,
            traceStore,
            new FakeAppLogService(),
            new FakeCommunicationChannels(),
            new InspectionRunControl());

    private sealed class RecordingRecipeRepository : IRecipeRepository
    {
        private readonly Recipe _recipe;

        public RecordingRecipeRepository(Recipe recipe) => _recipe = recipe;

        public int TotalCalls { get; private set; }

        public int GetAsyncCount { get; private set; }

        public int GetCurrentAsyncCount { get; private set; }

        public Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            GetCurrentAsyncCount++;
            return Task.FromResult(_recipe);
        }

        public Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.FromResult(_recipe.Id);
        }

        public Task SetCurrentRecipeAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }

        public Task<Recipe?> GetAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            GetAsyncCount++;
            return Task.FromResult<Recipe?>(
                string.Equals(recipeId, _recipe.Id, StringComparison.OrdinalIgnoreCase)
                    ? _recipe
                    : null);
        }

        public Task<IReadOnlyList<Recipe>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.FromResult<IReadOnlyList<Recipe>>([_recipe]);
        }

        public Task SaveAsync(
            Recipe recipe,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }
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

    private sealed class PassThroughVisionPipeline : IVisionPipeline
    {
        public Task<VisionPipelineResult> ExecuteAsync(
            Recipe recipe,
            ImageFrame frame,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new VisionPipelineResult(
                frame,
                [],
                InspectionOutcome.Ok,
                string.Empty,
                "Inspection passed."));
        }
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

    private sealed class RecordingInspectionRecordRepository : IInspectionRecordRepository
    {
        public int AddCount { get; private set; }

        public InspectionResult? AddedResult { get; private set; }

        public Task AddAsync(
            InspectionResult result,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount++;
            AddedResult = result;
            return Task.CompletedTask;
        }

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

    private sealed class RecordingImageTraceStore : IImageTraceStore
    {
        public int SaveCount { get; private set; }

        public Recipe? SavedRecipe { get; private set; }

        public InspectionResult? SavedResult { get; private set; }

        public Task<ImageTracePaths> SaveAsync(
            Recipe recipe,
            ImageFrame originalFrame,
            ImageFrame resultFrame,
            InspectionResult result,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            SavedRecipe = recipe;
            SavedResult = result;
            return Task.FromResult(new ImageTracePaths(
                "original.png",
                "result.png",
                "metadata.json"));
        }
    }
}
