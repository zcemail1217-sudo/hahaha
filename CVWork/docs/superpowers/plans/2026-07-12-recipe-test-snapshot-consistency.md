# Recipe Test Snapshot Consistency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让配方试运行直接执行准入时冻结的业务配方快照，并关闭导航刷新与并行 Resume 异常造成的状态真相缺口。

**Architecture:** `InspectionRequest` 增加兼容的可选 `RecipeSnapshot`；`InspectionRunner` 在快照存在时先校验身份并绕过配方仓库，否则保留按 ID/current 解析。Recipe Management 只在完整试运行生命周期开始时保存基础配方一次，Reset 仅派生下一 attempt 的默认值快照；Session owner 的 `finally` 独占运行状态复位，离页不再淘汰已经承诺的 deferred refresh。

**Tech Stack:** .NET 8、C# records、WPF/Prism `AsyncDelegateCommand`、xUnit、`IInspectionExecution`/`IInspectionSession`、JSON 配方仓库。

---

## 实施边界与文件职责

- Modify: `VisionStation.Domain/Models.cs`
  - 只定义 `InspectionRequest.RecipeSnapshot` 公开契约和 XML 文档。
- Modify: `VisionStation.Application/InspectionServices.cs`
  - 只负责 Runner 的快照优先解析、身份校验和原有仓库 fallback。
- Modify: `VisionStation.Application.Tests/InspectionRunnerContractTests.cs`
  - 锁定可选快照属性的公开形状与默认值。
- Create: `VisionStation.Application.Tests/InspectionRunnerRecipeResolutionTests.cs`
  - 通过真实 `InspectionRunner` 锁定快照优先、仓库绕过、ID 校验和旧 fallback。
- Modify: `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs`
  - 调整基础保存/attempt 生命周期、请求快照、Catch owner 边界和导航 generation。
- Modify: `VisionStation.Vision.UI.Tests/RecipeManagementInspectionExecutionTests.cs`
  - 锁定 Reset、外部 writer、导航交错和 Resume 异常。
- Modify: `VisionStation.Vision.UI.Tests/RecipeManagementTestHarness.cs`
  - 只增加 Resume 抛错和 dispatcher 观测接缝。

不要修改 `InspectionExecutionContracts.cs`、`InspectionExecution.cs`、DI 注册、Variable Center 或 Vision Debug 的生产代码；它们不需要知道快照解析实现。

## Task 1: 增加可选 RecipeSnapshot 公共契约

**Files:**

- Modify: `VisionStation.Application.Tests/InspectionRunnerContractTests.cs`
- Modify: `VisionStation.Domain/Models.cs:536-550`

- [ ] **Step 1: 写公开契约失败测试**

在 `InspectionExecutionContractTests` 增加：

```csharp
[Fact]
public void InspectionRequest_exposes_optional_recipe_snapshot()
{
    var property = typeof(InspectionRequest).GetProperty("RecipeSnapshot");

    Assert.NotNull(property);
    Assert.Equal(typeof(Recipe), property.PropertyType);
    Assert.True(property.CanRead);
    Assert.NotNull(property.SetMethod);
    Assert.Null(property.GetValue(new InspectionRequest()));
}
```

- [ ] **Step 2: 运行测试并确认 RED**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionRequest_exposes_optional_recipe_snapshot"
```

Expected: FAIL，`GetProperty("RecipeSnapshot")` 返回 `null`。

- [ ] **Step 3: 增加最小公开属性与 XML 文档**

在 `InspectionRequest` 的 `RecipeId` 后增加：

```csharp
/// <summary>
/// Gets the logical immutable recipe snapshot for this inspection.
/// When null, the runner resolves <see cref="RecipeId" /> from the recipe repository.
/// </summary>
/// <remarks>
/// After calling ExecuteAsync, callers must not mutate collections reachable from this snapshot.
/// When both values are supplied, <see cref="RecipeId" /> must match <see cref="Recipe.Id" />.
/// </remarks>
public Recipe? RecipeSnapshot { get; init; }
```

- [ ] **Step 4: 运行契约测试与 Domain/Application 编译**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionExecutionContractTests"
dotnet build .\VisionStation.Application\VisionStation.Application.csproj -c Debug --nologo
```

Expected: contract tests 全绿，Application 0 error。

- [ ] **Step 5: 提交并推送**

```powershell
git add VisionStation.Domain/Models.cs VisionStation.Application.Tests/InspectionRunnerContractTests.cs
git commit -m "feat: add inspection recipe snapshot contract"
git push
```

## Task 2: Runner 快照优先解析与身份校验

**Files:**

- Create: `VisionStation.Application.Tests/InspectionRunnerRecipeResolutionTests.cs`
- Modify: `VisionStation.Application/InspectionServices.cs:43-110`

- [ ] **Step 1: 创建真实 Runner 行为测试**

创建 `InspectionRunnerRecipeResolutionTests`，先实现下面四个核心测试。复审加固同时覆盖空白快照 ID、空请求 ID 的 current fallback、missing ID 的 current fallback，以及 result/trace/record 的规范快照业务 ID。测试必须实例化真实 `InspectionRunner`；现有 `FakeCameraDevice`、`FakeAxisController`、`FakePlcClient`、`FakeCommunicationChannels` 和 `FakeAppLogService` 可直接复用。

文件顶部使用：

```csharp
using VisionStation.Application;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Application.Tests;
```

```csharp
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
```

测试文件中的配方工厂必须走不触发相机/视觉/追溯的 `ProcessOnly + Delay` 路径：

```csharp
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
```

测试适配器必须完整实现所需 interface：

```csharp
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

    public Task SetCurrentRecipeAsync(string recipeId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default)
    {
        GetAsyncCount++;
        return Task.FromResult<Recipe?>(
            string.Equals(recipeId, _recipe.Id, StringComparison.OrdinalIgnoreCase)
                ? _recipe
                : null);
    }

    public Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>([_recipe]);

    public Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
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

    public Task SelectDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
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
    public Task AddAsync(InspectionResult result, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

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
```

`CreateRunner` 必须使用真实 `InspectionRunControl` 与 `DeviceRuntime`：

```csharp
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
```

- [ ] **Step 2: 运行 Runner 测试并确认 RED**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionRunnerRecipeResolutionTests"
```

Expected: 快照测试返回 `Repository Recipe` 或发生仓库读取；身份不一致测试不抛 `ArgumentException`。

- [ ] **Step 3: 提取私有解析 helper 并接入真实 Runner**

在 `InspectionRunner.RunAsync` 中把原有仓库解析替换为：

```csharp
var stopwatch = Stopwatch.StartNew();
var recipe = await ResolveRecipeAsync(request, cancellationToken);
_configuration = await _configurationRepository.GetAsync(cancellationToken);
```

在 `InspectionRunner` 增加：

```csharp
private async Task<Recipe> ResolveRecipeAsync(
    InspectionRequest request,
    CancellationToken cancellationToken)
{
    if (request.RecipeSnapshot is { } snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Id))
        {
            throw new ArgumentException(
                "RecipeSnapshot.Id is required.",
                nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.RecipeId) &&
            !string.Equals(
                request.RecipeId,
                snapshot.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"RecipeId '{request.RecipeId}' does not match RecipeSnapshot.Id '{snapshot.Id}'.",
                nameof(request));
        }

        return snapshot.WithNormalizedFlows();
    }

    Recipe? recipe = null;
    if (!string.IsNullOrWhiteSpace(request.RecipeId))
    {
        recipe = await _recipes.GetAsync(request.RecipeId, cancellationToken);
    }

    return (recipe ?? await _recipes.GetCurrentAsync(cancellationToken))
        .WithNormalizedFlows();
}
```

不要移动 `Stopwatch.StartNew()`，不要改变配置、设备、记录和 `RunCompleted` 的现有顺序。

- [ ] **Step 4: 运行定向与 Application 全量测试**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionRunnerRecipeResolutionTests|FullyQualifiedName~InspectionExecutionContractTests"
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release
```

Expected: 定向全绿，Application Release 全量全绿。

- [ ] **Step 5: 提交并推送**

```powershell
git add VisionStation.Application/InspectionServices.cs VisionStation.Application.Tests/InspectionRunnerRecipeResolutionTests.cs
git commit -m "feat: execute inspections from recipe snapshots"
git push
```

## Task 3: 试运行只保存一次并按 attempt 传递快照

**Files:**

- Modify: `VisionStation.Vision.UI.Tests/RecipeManagementInspectionExecutionTests.cs`
- Modify: `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs:1108-1322,1658-1667`

- [ ] **Step 1: 把现有 Reset 测试改为快照与单次保存断言**

把 `Reset_attempt_uses_frozen_recipe_with_variable_values_reset_to_defaults` 重命名为 `Reset_attempt_uses_frozen_recipe_snapshot_with_defaults_without_resaving`，用以下核心断言替换两次仓库保存断言：

```csharp
var saved = Assert.Single(harness.Recipes.SavedRecipes);
Assert.Equal(
    "current-before-run",
    Assert.Single(saved.Variables).CurrentValue);

Assert.Equal(2, harness.Session.Requests.Count);
var firstSnapshot = Assert.IsType<Recipe>(harness.Session.Requests[0].RecipeSnapshot);
var secondSnapshot = Assert.IsType<Recipe>(harness.Session.Requests[1].RecipeSnapshot);
Assert.Equal(
    "current-before-run",
    Assert.Single(firstSnapshot.Variables).CurrentValue);
Assert.Equal(
    "default-after-reset",
    Assert.Single(secondSnapshot.Variables).CurrentValue);
Assert.All(harness.Session.Requests, request =>
    Assert.Equal(request.RecipeId, request.RecipeSnapshot!.Id, ignoreCase: true));
Assert.Equal(1, harness.Recipes.SaveCount);
Assert.Equal(1, harness.Recipes.SetCurrentCount);
```

同步调整 `Running_recipe_is_frozen_across_reset_attempts`：仓库只保存一次；两个 request 都有同一业务 ID/名称的非空快照。

- [ ] **Step 2: 重写“保存期间 Reset”测试锁定锁存语义**

把测试重命名为 `Reset_during_initial_persist_is_latched_without_cancelling_or_resaving`，用显式门控制保存：

```csharp
harness.PublishRecipeChanged(RecipeWithVariable(
    "LatchedReset",
    "current-before-reset",
    "default-after-reset"));
await RecipeManagementTestHarness.WaitUntilAsync(() =>
    harness.ViewModel.RecipeVariables.Any(variable =>
        variable.Key == "LatchedReset"));

var saveEntered = RecipeManagementTestHarness.NewSignal();
var allowSave = RecipeManagementTestHarness.NewSignal();
CancellationToken saveToken = default;
harness.Recipes.SaveHandler = async (_, token) =>
{
    saveToken = token;
    saveEntered.TrySetResult(true);
    await allowSave.Task;
    token.ThrowIfCancellationRequested();
};

var running = harness.ViewModel.TestRunRecipeCommand.Execute();
await saveEntered.Task.WaitAsync(CommandTimeout);
harness.ViewModel.ResetTestRunCommand.Execute();

Assert.False(saveToken.IsCancellationRequested);
allowSave.TrySetResult(true);
await running.WaitAsync(CommandTimeout);

Assert.Equal(1, harness.Recipes.SaveCount);
Assert.Equal(1, harness.Recipes.SetCurrentCount);
Assert.Single(harness.Session.Requests);
Assert.Equal(1, harness.RunControl.BeginCount);
var snapshot = Assert.IsType<Recipe>(
    Assert.Single(harness.Session.Requests).RecipeSnapshot);
Assert.All(snapshot.Variables, variable =>
    Assert.Equal(variable.DefaultValue, variable.CurrentValue));
```

测试开始前用现有 `RecipeWithVariable` 发布 `CurrentValue != DefaultValue` 的配方，避免空集合让断言假绿。

- [ ] **Step 3: 新增外部 writer + Reset 不覆盖测试**

新增 `Reset_does_not_overwrite_external_recipe_saved_after_initial_persist`：

```csharp
var firstAttemptEntered = RecipeManagementTestHarness.NewSignal();
var attempt = 0;
harness.Session.Handler = async (_, token) =>
{
    if (Interlocked.Increment(ref attempt) == 1)
    {
        firstAttemptEntered.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }

    return RecipeRunResults.Ok();
};

var running = harness.ViewModel.TestRunRecipeCommand.Execute();
await firstAttemptEntered.Task.WaitAsync(CommandTimeout);

var external = RecipeWithVariable("ExternalAfterSave", "external");
harness.PublishRecipeChanged(external);
harness.ViewModel.ResetTestRunCommand.Execute();
await running.WaitAsync(CommandTimeout);

var stored = await harness.Recipes.GetAsync("recipe-1");
Assert.NotNull(stored);
Assert.Contains(stored!.Variables, variable =>
    variable.Key == "ExternalAfterSave" && variable.CurrentValue == "external");
await RecipeManagementTestHarness.WaitUntilAsync(() =>
    harness.ViewModel.RecipeVariables.Any(variable =>
        variable.Key == "ExternalAfterSave" && variable.CurrentValue == "external"));
Assert.Equal(1, harness.Recipes.SaveCount);
Assert.Equal(2, harness.Session.Requests.Count);
```

- [ ] **Step 4: 运行测试并确认 RED**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~Reset_attempt_uses_frozen_recipe_snapshot_with_defaults_without_resaving|FullyQualifiedName~Reset_during_initial_persist_is_latched_without_cancelling_or_resaving|FullyQualifiedName~Reset_does_not_overwrite_external_recipe_saved_after_initial_persist|FullyQualifiedName~Running_recipe_is_frozen_across_reset_attempts"
```

Expected: 当前代码两次保存、请求 `RecipeSnapshot` 为 null，且 Reset 覆盖 external recipe。

- [ ] **Step 5: 把基础保存移出 attempt 循环**

在取得 Session、构建 `runRecipeSnapshot`、设置 `IsTestRunning` 后，用 lifetime token 只保存一次：

```csharp
_log.Info("Recipe", $"试运行：正在保存配方 {recipeName} 并启动流程");
var persistedRecipe = await PersistSelectedRecipeAsync(
    setCurrentRecipe: true,
    refreshList: false,
    lifetime.Token,
    runRecipeSnapshot);
if (persistedRecipe is null)
{
    StatusText = "试运行取消：当前配方保存失败";
    LogWarningSafely(StatusText);
    return;
}

runRecipeSnapshot = persistedRecipe;
recipeName = persistedRecipe.Name;
var attemptRecipeSnapshot = _testRunResetRequested
    ? PrepareResetAttempt(runRecipeSnapshot)
    : runRecipeSnapshot;
```

基础保存期间不创建 `_testRunAttemptCancellation`；Reset 只锁存 `_testRunResetRequested`，导航仍通过 `lifetime.Token` 取消保存。

- [ ] **Step 6: 循环内删除仓库保存并传递快照**

每个 attempt 保留 linked CTS、`BeginRun`、连接和 Session 执行，但删除循环内 `PersistSelectedRecipeAsync`。请求使用：

```csharp
var run = await session.ExecuteAsync(new InspectionRequest
{
    RecipeId = attemptRecipeSnapshot.Id,
    RecipeSnapshot = attemptRecipeSnapshot,
    BatchId = DateTimeOffset.Now.ToString("yyyyMMdd"),
    OperatorName = Environment.UserName,
    TriggeredByPlc = false,
    ProcessOnly = true
}, attempt.Token);
```

把 Reset 的公共状态变化抽成：

```csharp
private Recipe PrepareResetAttempt(Recipe runRecipeSnapshot)
{
    var attemptRecipeSnapshot = WithVariableValuesResetToDefaults(
        runRecipeSnapshot);
    IsTestRunPaused = false;
    ResetRecipeVariablesToDefaults();
    ResetProcessStepRuntimeStates(prepareForRun: true);
    StatusText = "流程已复位，正在从第一步重新开始";
    TestRunStateText = StatusText;
    AddTestRunLog("INFO", "Recipe", StatusText);
    _log.Info("Recipe", StatusText);
    return attemptRecipeSnapshot;
}
```

执行期间捕获 Reset 后也调用这个 helper；循环开始时先读取已锁存值，再把 `_testRunResetRequested` 清为 false，不能提前丢失基础保存期间的 Reset。

- [ ] **Step 7: 运行 Task 3 定向与完整 Recipe 测试**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~RecipeManagementInspectionExecutionTests"
```

Expected: 新旧 Reset、拒绝零副作用、Session dispose 与 deferred replay 测试全部通过。

- [ ] **Step 8: 提交并推送**

```powershell
git add VisionStation.Client/ViewModels/RecipeManagementViewModel.cs VisionStation.Vision.UI.Tests/RecipeManagementInspectionExecutionTests.cs
git commit -m "fix: execute recipe tests from frozen snapshots"
git push
```

## Task 4: 修复导航 refresh 与 Resume Catch owner 真相

**Files:**

- Modify: `VisionStation.Vision.UI.Tests/RecipeManagementInspectionExecutionTests.cs`
- Modify: `VisionStation.Vision.UI.Tests/RecipeManagementTestHarness.cs`
- Modify: `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs:1357-1380,2657-2670`

- [ ] **Step 1: 增加可抛 Resume 与 dispatcher 观测接缝**

`RecordingRunControl` 增加：

```csharp
public Action ResumeHandler { get; set; } = static () => { };

public void Resume()
{
    ResumeHandler();
    IsPaused = false;
}
```

把 `ImmediateUiDispatcher` 扩展为：

```csharp
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
```

`RecipeManagementTestHarness` 构造器、属性和 `CreateAsync` 必须保存同一个 dispatcher 实例为 `UiDispatcher`，不能在构造 VM 时临时 `new` 后丢失。

- [ ] **Step 2: 写 Resume 异常 owner 真相测试**

新增 `Resume_failure_reports_on_dispatcher_without_releasing_owner`：

```csharp
var entered = RecipeManagementTestHarness.NewSignal();
harness.Session.Handler = async (_, token) =>
{
    entered.TrySetResult(true);
    await Task.Delay(Timeout.InfiniteTimeSpan, token);
    return RecipeRunResults.Ok();
};

var owner = harness.ViewModel.TestRunRecipeCommand.Execute();
await entered.Task.WaitAsync(CommandTimeout);
harness.ViewModel.PauseTestRunCommand.Execute();

var reportedInsideDispatcher = false;
harness.ViewModel.PropertyChanged += (_, args) =>
{
    if (args.PropertyName == nameof(harness.ViewModel.StatusText) &&
        harness.ViewModel.StatusText.Contains("resume-failure"))
    {
        reportedInsideDispatcher = harness.UiDispatcher.IsInvoking;
    }
};
harness.RunControl.ResumeHandler = () =>
    throw new InvalidOperationException("resume-failure");

var failure = await Record.ExceptionAsync(() =>
    harness.ViewModel.TestRunRecipeCommand.Execute().WaitAsync(CommandTimeout));

Assert.Null(failure);
Assert.True(reportedInsideDispatcher);
Assert.True(harness.ViewModel.IsTestRunning);
Assert.True(harness.ViewModel.IsTestRunPaused);
Assert.False(harness.ViewModel.IsRecipeEditingEnabled);
Assert.Equal(0, harness.Session.DisposeCount);

harness.ViewModel.OnNavigatedFrom(null!);
await owner.WaitAsync(CommandTimeout);
Assert.False(harness.ViewModel.IsTestRunning);
Assert.False(harness.ViewModel.IsTestRunPaused);
Assert.Equal(1, harness.Session.DisposeCount);
```

测试前配置 `Session.Handler` 使用 owner token 无限等待并通过 `entered` 发信号。

- [ ] **Step 3: 写“replay 先启动、随后离页”测试**

新增 `Cleanup_replay_started_before_navigation_still_applies`：

```csharp
var runEntered = RecipeManagementTestHarness.NewSignal();
var allowRun = new TaskCompletionSource<InspectionRunResult>(
    TaskCreationOptions.RunContinuationsAsynchronously);
harness.Session.Handler = (_, _) =>
{
    runEntered.TrySetResult(true);
    return allowRun.Task;
};

var running = harness.ViewModel.TestRunRecipeCommand.Execute();
await runEntered.Task.WaitAsync(CommandTimeout);
var external = RecipeWithVariable("ReplayAfterNavigation", "latest");
harness.PublishRecipeChanged(external);

var readStarted = RecipeManagementTestHarness.NewSignal();
var completeRead = new TaskCompletionSource<Recipe?>(
    TaskCreationOptions.RunContinuationsAsynchronously);
harness.Recipes.GetAsyncHandler = (_, _, _) =>
{
    readStarted.TrySetResult(true);
    return completeRead.Task;
};

allowRun.TrySetResult(RecipeRunResults.Ok());
await running.WaitAsync(CommandTimeout);
await readStarted.Task.WaitAsync(CommandTimeout);
harness.ViewModel.OnNavigatedFrom(null!);
completeRead.TrySetResult(external);

await RecipeManagementTestHarness.WaitUntilAsync(() =>
    harness.ViewModel.RecipeVariables.Any(variable =>
        variable.Key == "ReplayAfterNavigation"));
```

测试中的 `Session.Handler` 用 `allowRun` 控制完成，确保 cleanup 已取走 pending 并开始读取后才导航。

- [ ] **Step 4: 运行两个测试并确认 RED**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~Resume_failure_reports_on_dispatcher_without_releasing_owner|FullyQualifiedName~Cleanup_replay_started_before_navigation_still_applies"
```

Expected: Resume 测试观察到 `IsTestRunning == false` 或非 dispatcher 更新；导航测试等待变量超时。

- [ ] **Step 5: Catch 只经 dispatcher 报告错误**

用下面的方法替换 `ReportTestRunFailureSafely`；不要改 owner 状态或 CTS：

```csharp
private void ReportTestRunFailureSafely(Exception exception)
{
    var message = $"试运行失败：{exception.Message}";
    try
    {
        _uiDispatcher.Invoke(() =>
        {
            StatusText = message;
            TestRunStateText = message;
        });
    }
    catch
    {
    }

    LogErrorSafely(message);
}
```

`IsTestRunning`、`IsTestRunPaused` 和 `RaiseCommandStates` 继续只由 Session owner 的现有 `finally` 处理。

- [ ] **Step 6: 离页不推进 recipe generation**

`OnNavigatedFrom` 删除且只删除这一句：

```csharp
AdvanceRecipePageEpoch();
```

保留 lifetime cancel 与 inspection execution 退订。选择配方、`ApplyRecipe`、`ClearEditor` 和新 refresh 请求仍推进 generation，A→B→A 测试必须继续通过。

- [ ] **Step 7: 运行 Task 4 定向、Task 7 全量和压力测试**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~RecipeManagementInspectionExecutionTests|FullyQualifiedName~FlowEditorDialogContractTests"
```

随后先构建一次，再用 `--no-build` 压力复跑：

```powershell
$failedRuns = 0
1..20 | ForEach-Object {
    dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --no-build --filter "FullyQualifiedName~RecipeManagementInspectionExecutionTests|FullyQualifiedName~FlowEditorDialogContractTests" | Out-Host
    if ($LASTEXITCODE -ne 0) { $failedRuns++ }
}
if ($failedRuns -ne 0) { throw "FAILED_RUNS=$failedRuns" }
```

Expected: 定向全绿；`FAILED_RUNS=0`。

- [ ] **Step 8: 提交并推送**

```powershell
git add VisionStation.Client/ViewModels/RecipeManagementViewModel.cs VisionStation.Vision.UI.Tests/RecipeManagementInspectionExecutionTests.cs VisionStation.Vision.UI.Tests/RecipeManagementTestHarness.cs
git commit -m "fix: preserve recipe test owner truth"
git push
```

## Task 5: 独立复审与全量验证

**Files:**

- No production edits unless a reviewer reports a verified issue.

- [ ] **Step 1: 独立规格复审**

让未参与实现的 reviewer 对照：

- `docs/superpowers/specs/2026-07-12-recipe-test-snapshot-consistency-design.md`
- Task 7 原计划 `docs/superpowers/plans/2026-07-11-production-run-admission.md:3133-3850`

必须确认：拒绝零副作用、单 Session、Reset attempt、不可变快照、真实 RecipeId、导航取消和 deferred replay 均无回归。Critical/Important 非 0 时回到对应 Task 先加 RED 再修复。

- [ ] **Step 2: 独立质量复审**

重点检查：

- snapshot ID mismatch 是否在配置/设备/记录副作用前失败；
- 空白 snapshot ID 是否在所有下游读取前失败；
- Runner 快照路径是否完全绕过 recipe repository；
- OrdinalIgnoreCase、空 ID/current fallback、missing ID/current fallback 是否有 mutation 证据；
- 结果、追溯与记录是否统一采用 snapshot 的规范业务 ID；
- Reset 是否只有一次业务配方保存；
- 外部 writer 更新是否不再被 Reset 覆盖；
- Catch 是否不迁移 owner 状态且只经 dispatcher 更新 UI；
- 离页后旧 refresh 仍受配方切换/Apply generation 门约束；
- 测试是否使用确定性交错而非时间猜测。

Critical/Important 非 0 时回到对应 Task TDD 修复并重新复审。

- [ ] **Step 3: 运行新鲜完整验证矩阵**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug
dotnet build .\VisionStation.Client\VisionStation.Client.csproj -c Debug --nologo
git diff --check
git status --short
git rev-list --left-right --count HEAD...origin/codex/production-run-admission
```

Expected:

- Application 全量 0 failed；
- Vision UI 全量 0 failed；
- Client 0 warning / 0 error；
- `git diff --check` 无输出；
- 工作树干净；
- upstream count 为 `0 0`。

- [ ] **Step 4: 更新主计划状态**

Task 7 只有在规格 reviewer 和质量 reviewer 都给出 Ready、完整矩阵通过且远端同步后才标记 completed；随后进入主计划 Task 8。
