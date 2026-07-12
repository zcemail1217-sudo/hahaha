# Production Run Admission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立不可绕过、立即拒绝冲突的单一检测执行 module，并让生产单次、连续生产和配方试运行共享可信的状态、取消和清理语义。

**Architecture:** 对外只暴露根命名空间下的 `IInspectionExecution` seam；调用者通过 `TryBegin` 获得 `IInspectionSession`，所有准入、authority、顺序执行和释放校验都留在 implementation 内。`ProductionCoordinator` 持有自有 Session、CTS 和 Completion，UI 使用 `ProductionSnapshot.ActiveSessionId` 与全局 Current 精确判断所有权；配方试运行在任何保存或切换前取得同一 Session。

**Tech Stack:** .NET 8、C# 12、WPF、Prism 9 `AsyncDelegateCommand`、xUnit 2.9、现有设备/通信 adapter。

---

## 规范与成功证据

- 设计规范：`docs/superpowers/specs/2026-07-11-production-run-admission-design.md`
- 每一个行为变更严格执行 Red → Green → Refactor。
- 每个任务结束运行指定测试、检查 `git status`、提交并推送当前分支。
- 最终证据必须包括 Release 全量构建、196 个现有测试加全部新增测试通过、`git diff --check` 为空。

## 文件结构

- `VisionStation.Application/Inspection/Execution/InspectionExecutionContracts.cs`：唯一公开 seam、Session、Mode、Intent、Admission。
- `VisionStation.Application/Inspection/Execution/InspectionExecution.cs`：进程内准入、Session 生命周期、事件隔离和内部 executor seam。
- `VisionStation.Application/ProductionCommandModels.cs`：生产命令的 Completed/Canceled/Rejected/NoOp 结果。
- `VisionStation.Application/ProductionCoordinator.cs`：生产所有权、状态、取消和一次清理。
- `VisionStation.Client/Presentation/ProductionRunUiState.cs`：Shell 与 Dashboard 共用的纯 UI 投影。
- `VisionStation.Vision.UI/Services/IFlowEditorDialogService.cs`：由传入巨大 ViewModel 的浅 interface 深化为 `ShowEditorAsync(recipeId)`。
- `docs/development/inspection-execution.md`：二次开发指南。

## Task 0: 创建隔离工作区并验证基线

**Files:** 无修改。

- [ ] **Step 1: 使用 `using-git-worktrees` 建立隔离工作区**

分支名使用：

```text
codex/production-run-admission
```

- [ ] **Step 2: 验证工作区和基线**

Run:

```powershell
git status --short --branch
dotnet test .\CVWork.sln -c Release --nologo
```

Expected:

```text
工作区无未提交修改
VisionStation.Application.Tests: 112 passed
VisionStation.Vision.Tests: 21 passed
VisionStation.Vision.UI.Tests: 63 passed
Total: 196 passed, 0 failed
```

如基线失败，停止实施并报告，不把既有失败混入本改造。

## Task 1: 发布可扩展的检测执行 contracts

**Files:**

- Create: `VisionStation.Application/Inspection/Execution/InspectionExecutionContracts.cs`
- Modify: `VisionStation.Application.Tests/InspectionRunnerContractTests.cs`

- [ ] **Step 1: 写 contract 存在性的失败测试**

将 `InspectionRunnerContractTests.cs` 替换为：

```csharp
using VisionStation.Application;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class InspectionExecutionContractTests
{
    [Fact]
    public void Application_exposes_inspection_execution_contracts()
    {
        var assembly = typeof(InspectionRunResult).Assembly;

        Assert.NotNull(assembly.GetType("VisionStation.Application.IInspectionExecution"));
        Assert.NotNull(assembly.GetType("VisionStation.Application.IInspectionSession"));
    }
}
```

- [ ] **Step 2: 运行 Red**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionExecutionContractTests"
```

Expected: FAIL，两个 `assembly.GetType` 调用至少一个返回 `null`。

- [ ] **Step 3: 新增最小 contracts**

创建 `InspectionExecutionContracts.cs`：

```csharp
using System.Text.RegularExpressions;
using VisionStation.Domain;

namespace VisionStation.Application;

/// <summary>检测执行的唯一外部 seam。</summary>
public interface IInspectionExecution
{
    /// <summary>当前占用快照；空值表示可以申请新会话。</summary>
    ActiveInspectionRun? Current { get; }

    /// <summary>在占用者发生变化后发布；单个订阅者异常不会阻断其他订阅者。</summary>
    event EventHandler<InspectionExecutionChangedEventArgs>? Changed;

    /// <summary>一次检测成功返回结果后发布；取消或异常不发布结果。</summary>
    event EventHandler<InspectionRunResult>? RunCompleted;

    /// <summary>同步尝试取得全局检测执行权。</summary>
    /// <param name="intent">运行模式和调用入口。</param>
    /// <returns>立即返回取得的 Session 或包含当前占用者的拒绝结果；不会排队。</returns>
    /// <exception cref="ArgumentException">Mode 或 EntryPoint 无效。</exception>
    RunAdmission TryBegin(InspectionRunIntent intent);
}

/// <summary>一次已取得全局准入权的检测会话。</summary>
public interface IInspectionSession : IAsyncDisposable
{
    /// <summary>本会话不可变的身份与运行意图。</summary>
    ActiveInspectionRun Run { get; }

    /// <summary>在本会话内执行一次检测。</summary>
    /// <param name="request">检测请求。</param>
    /// <param name="cancellationToken">本次执行的取消令牌。</param>
    /// <returns>检测结果。</returns>
    /// <exception cref="InvalidOperationException">同一 Session 正在执行另一请求。</exception>
    /// <exception cref="ObjectDisposedException">Session 已请求释放或已经释放。</exception>
    Task<InspectionRunResult> ExecuteAsync(
        InspectionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>可由业务扩展的检测运行模式；Key 用于稳定标识，DisplayName 用于界面显示。</summary>
public readonly record struct InspectionRunMode
{
    /// <summary>创建可扩展的检测运行模式。</summary>
    /// <param name="key">稳定的小写模式标识。</param>
    /// <param name="displayName">用户可读显示名。</param>
    /// <exception cref="ArgumentException">Key 或显示名不符合约束。</exception>
    public InspectionRunMode(string key, string displayName)
    {
        Key = key?.Trim() ?? string.Empty;
        DisplayName = displayName?.Trim() ?? string.Empty;
    }

    /// <summary>用于日志和持久化的稳定小写标识。</summary>
    public string Key { get; }

    /// <summary>供操作界面显示的名称。</summary>
    public string DisplayName { get; }
}

/// <summary>系统内置的检测运行模式。</summary>
public static class InspectionRunModes
{
    /// <summary>生产单次检测。</summary>
    public static InspectionRunMode ManualSingle { get; } =
        new("production.manual-single", "生产单次检测");

    /// <summary>连续生产。</summary>
    public static InspectionRunMode Continuous { get; } =
        new("production.continuous", "连续生产");

    /// <summary>配方试运行。</summary>
    public static InspectionRunMode RecipeTest { get; } =
        new("recipe.test", "配方试运行");
}

/// <summary>调用入口申请检测执行权时提交的意图。</summary>
/// <param name="Mode">运行模式。</param>
/// <param name="EntryPoint">调用 module 或 ViewModel 名称。</param>
public sealed record InspectionRunIntent(
    InspectionRunMode Mode,
    string EntryPoint);

/// <summary>当前持有全局检测执行权的会话快照。</summary>
/// <param name="SessionId">module 生成的唯一会话标识。</param>
/// <param name="Intent">申请该会话的运行意图。</param>
/// <param name="StartedAt">取得执行权的时间。</param>
public sealed record ActiveInspectionRun(
    Guid SessionId,
    InspectionRunIntent Intent,
    DateTimeOffset StartedAt);

/// <summary>检测执行占用状态变化事件参数。</summary>
public sealed class InspectionExecutionChangedEventArgs : EventArgs
{
    /// <summary>创建占用变化事件参数。</summary>
    /// <param name="current">变化后的占用快照；null 表示释放。</param>
    public InspectionExecutionChangedEventArgs(ActiveInspectionRun? current)
    {
        Current = current;
    }

    /// <summary>变化后的占用快照；空值表示已释放。</summary>
    public ActiveInspectionRun? Current { get; }
}

/// <summary>检测执行准入被拒绝的原因。</summary>
public enum RunRejectionReason
{
    /// <summary>检测执行正被其他会话占用。</summary>
    Busy,

    /// <summary>同一编排器的相同循环已经启动。</summary>
    AlreadyRunning,

    /// <summary>调用者没有当前会话的停止所有权。</summary>
    NotOwner
}

/// <summary>包含拒绝原因和当前占用者的准入拒绝结果。</summary>
/// <param name="Reason">拒绝原因。</param>
/// <param name="Active">拒绝发生时的当前占用快照。</param>
public sealed record RunRejection(
    RunRejectionReason Reason,
    ActiveInspectionRun Active);

/// <summary>检测执行准入结果。</summary>
public abstract record RunAdmission
{
    /// <summary>已取得检测执行会话。</summary>
    /// <param name="Session">唯一可执行能力，必须异步释放。</param>
    public sealed record Acquired(IInspectionSession Session) : RunAdmission;

    /// <summary>未取得检测执行会话。</summary>
    /// <param name="Rejection">包含原因和占用者的拒绝信息。</param>
    public sealed record Rejected(RunRejection Rejection) : RunAdmission;
}
```

- [ ] **Step 4: 运行最小 Green**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionExecutionContractTests"
```

Expected: PASS。

- [ ] **Step 5: 先添加 Mode 校验测试**

在测试类中增加：

```csharp
[Theory]
[InlineData("")]
[InlineData("Production.Manual")]
[InlineData("production manual")]
[InlineData("production/manual")]
public void InspectionRunMode_rejects_invalid_key(string key)
{
    Assert.Throws<ArgumentException>(() => new InspectionRunMode(key, "测试"));
}

[Fact]
public void InspectionRunMode_rejects_empty_display_name()
{
    Assert.Throws<ArgumentException>(
        () => new InspectionRunMode("custom.test", " "));
}

[Fact]
public void InspectionRunMode_accepts_extension_mode_without_registration()
{
    var mode = new InspectionRunMode("calibration.test", "标定试运行");

    Assert.Equal("calibration.test", mode.Key);
    Assert.Equal("标定试运行", mode.DisplayName);
}
```

- [ ] **Step 6: 运行校验 Red**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionExecutionContractTests"
```

Expected: invalid key/display name 测试失败，因为最小构造器尚未拒绝输入。

- [ ] **Step 7: 实现 Mode 校验**

在 `InspectionRunMode` 中加入字段，并用下列构造器替换最小构造器：

```csharp
private static readonly Regex KeyPattern = new(
    "^[a-z0-9]+(?:[._-][a-z0-9]+)*$",
    RegexOptions.CultureInvariant);

public InspectionRunMode(string key, string displayName)
{
    var normalizedKey = key?.Trim() ?? string.Empty;
    if (!KeyPattern.IsMatch(normalizedKey))
    {
        throw new ArgumentException("Run mode key is invalid.", nameof(key));
    }

    if (string.IsNullOrWhiteSpace(displayName))
    {
        throw new ArgumentException("Display name is required.", nameof(displayName));
    }

    Key = normalizedKey;
    DisplayName = displayName.Trim();
}
```

- [ ] **Step 8: 验证并提交**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionExecutionContractTests"
git diff --check
git status --short
git add VisionStation.Application VisionStation.Application.Tests
git commit -m "feat: add inspection execution contracts"
git push -u origin HEAD
```

## Task 2: 实现深 InspectionExecution module

**Files:**

- Create: `VisionStation.Application/Inspection/Execution/InspectionExecution.cs`
- Create: `VisionStation.Application.Tests/InspectionExecutionTests.cs`
- Existing: `VisionStation.Application/Properties/AssemblyInfo.cs` 已包含 `InternalsVisibleTo("VisionStation.Application.Tests")`

- [ ] **Step 1: 写 Session 行为测试**

创建 `InspectionExecutionTests.cs`：

```csharp
using VisionStation.Application;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class InspectionExecutionTests
{
    private static readonly InspectionRunIntent Intent = new(
        new InspectionRunMode("custom.test", "自定义检测"),
        nameof(InspectionExecutionTests));

    [Fact]
    public async Task TryBegin_rejects_second_session_until_first_is_disposed()
    {
        var execution = CreateExecution();

        var acquired = Assert.IsType<RunAdmission.Acquired>(execution.TryBegin(Intent));
        var rejected = Assert.IsType<RunAdmission.Rejected>(execution.TryBegin(Intent));

        Assert.Equal(RunRejectionReason.Busy, rejected.Rejection.Reason);
        Assert.Equal(acquired.Session.Run, rejected.Rejection.Active);
        Assert.Equal(acquired.Session.Run, execution.Current);

        await acquired.Session.DisposeAsync();

        Assert.Null(execution.Current);
        var next = Assert.IsType<RunAdmission.Acquired>(execution.TryBegin(Intent));
        await next.Session.DisposeAsync();
    }

    [Fact]
    public async Task TryBegin_with_100_concurrent_callers_acquires_exactly_one()
    {
        var execution = CreateExecution();

        var admissions = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(_ => Task.Run(() => execution.TryBegin(Intent))));

        var acquired = Assert.Single(admissions.OfType<RunAdmission.Acquired>());
        Assert.Equal(99, admissions.OfType<RunAdmission.Rejected>().Count());
        await acquired.Session.DisposeAsync();
    }

    [Fact]
    public async Task Session_allows_sequential_execution_and_publishes_results()
    {
        var executor = new RecordingExecutor();
        var execution = new InspectionExecution(executor);
        var completed = 0;
        execution.RunCompleted += (_, _) => completed++;
        await using var session = Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(Intent)).Session;

        await session.ExecuteAsync(new InspectionRequest());
        await session.ExecuteAsync(new InspectionRequest());

        Assert.Equal(2, executor.CallCount);
        Assert.Equal(2, completed);
    }

    [Fact]
    public async Task Session_rejects_concurrent_execution_before_executor_side_effects()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var executor = new RecordingExecutor
        {
            Handler = async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return TestRunResults.Ok();
            }
        };
        var execution = new InspectionExecution(executor);
        await using var session = Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(Intent)).Session;

        var first = session.ExecuteAsync(new InspectionRequest());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ExecuteAsync(new InspectionRequest()));
        Assert.Equal(1, executor.CallCount);

        release.TrySetResult(true);
        await first;
    }

    [Fact]
    public async Task Dispose_during_execution_keeps_global_admission_until_execution_finishes()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var executor = new RecordingExecutor
        {
            Handler = async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return TestRunResults.Ok();
            }
        };
        var execution = new InspectionExecution(executor);
        var session = Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(Intent)).Session;
        var run = session.ExecuteAsync(new InspectionRequest());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = session.DisposeAsync().AsTask();

        Assert.False(dispose.IsCompleted);
        Assert.IsType<RunAdmission.Rejected>(execution.TryBegin(Intent));

        release.TrySetResult(true);
        await run;
        await dispose;
        Assert.Null(execution.Current);
    }

    [Fact]
    public async Task Disposed_session_fails_before_executor_is_called()
    {
        var executor = new RecordingExecutor();
        var execution = new InspectionExecution(executor);
        var session = Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(Intent)).Session;
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.ExecuteAsync(new InspectionRequest()));
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_stale_session_cannot_release_next_session()
    {
        var execution = CreateExecution();
        var observed = new List<ActiveInspectionRun?>();
        execution.Changed += (_, args) => observed.Add(args.Current);
        var first = Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(Intent)).Session;

        await Task.WhenAll(
            first.DisposeAsync().AsTask(),
            first.DisposeAsync().AsTask());
        var second = Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(Intent)).Session;

        await first.DisposeAsync();

        Assert.Equal(second.Run, execution.Current);
        Assert.Equal(second.Run, observed[^1]);
        await second.DisposeAsync();
        Assert.Null(execution.Current);
    }

    [Fact]
    public async Task Reentrant_reacquire_does_not_publish_stale_null_to_later_subscribers()
    {
        var execution = CreateExecution();
        RunAdmission.Acquired? next = null;
        var observed = new List<ActiveInspectionRun?>();
        var reacquire = true;
        execution.Changed += (_, args) =>
        {
            if (args.Current is null && reacquire)
            {
                reacquire = false;
                next = Assert.IsType<RunAdmission.Acquired>(
                    execution.TryBegin(Intent));
            }
        };
        execution.Changed += (_, args) => observed.Add(args.Current);
        var first = Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(Intent));

        await first.Session.DisposeAsync();

        Assert.NotNull(next);
        Assert.Equal(next.Session.Run, execution.Current);
        Assert.Equal(next.Session.Run, observed[^1]);
        await next.Session.DisposeAsync();
    }

    [Fact]
    public async Task Subscriber_failures_do_not_break_result_or_release()
    {
        var execution = CreateExecution();
        var changedCalls = 0;
        var completedCalls = 0;
        execution.Changed += (_, _) => throw new InvalidOperationException("bad changed subscriber");
        execution.Changed += (_, _) => changedCalls++;
        execution.RunCompleted += (_, _) => throw new InvalidOperationException("bad completed subscriber");
        execution.RunCompleted += (_, _) => completedCalls++;

        await using (var session = Assert.IsType<RunAdmission.Acquired>(
                         execution.TryBegin(Intent)).Session)
        {
            await session.ExecuteAsync(new InspectionRequest());
        }

        Assert.Equal(2, changedCalls);
        Assert.Equal(1, completedCalls);
        Assert.Null(execution.Current);
    }

    [Fact]
    public void TryBegin_rejects_default_mode_before_creating_session()
    {
        var execution = CreateExecution();

        Assert.Throws<ArgumentException>(() => execution.TryBegin(
            new InspectionRunIntent(default, nameof(InspectionExecutionTests))));
        Assert.Null(execution.Current);
    }

    private static InspectionExecution CreateExecution() =>
        new(new RecordingExecutor());

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingExecutor : IInspectionExecutor
    {
        private int _callCount;

        public Func<InspectionRequest, CancellationToken, Task<InspectionRunResult>> Handler { get; set; } =
            static (_, _) => Task.FromResult(TestRunResults.Ok());

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<InspectionRunResult> ExecuteAsync(
            InspectionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Handler(request, cancellationToken);
        }
    }
}

internal static class TestRunResults
{
    public static InspectionRunResult Ok()
    {
        var frame = new ImageFrame(
            "test-frame",
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
            new Recipe());
    }
}
```

- [ ] **Step 2: 运行 module Red**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionExecutionTests"
```

Expected: FAIL/compile failure，缺少 `InspectionExecution` 和 `IInspectionExecutor`。

- [ ] **Step 3: 实现 module**

创建 `InspectionExecution.cs`：

```csharp
using VisionStation.Domain;

namespace VisionStation.Application;

internal interface IInspectionExecutor
{
    Task<InspectionRunResult> ExecuteAsync(
        InspectionRequest request,
        CancellationToken cancellationToken);
}

public sealed class InspectionExecution : IInspectionExecution
{
    private readonly object _syncRoot = new();
    private readonly object _changedQueueRoot = new();
    private readonly Queue<(ActiveInspectionRun? Current, long Revision)> _changedQueue = new();
    private readonly IInspectionExecutor _executor;
    private readonly IAppLogService? _log;
    private SessionState? _current;
    private long _changeRevision;
    private bool _publishingChanged;

    internal InspectionExecution(
        IInspectionExecutor executor,
        IAppLogService? log = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _log = log;
    }

    public ActiveInspectionRun? Current
    {
        get
        {
            lock (_syncRoot)
            {
                return _current?.Run;
            }
        }
    }

    public event EventHandler<InspectionExecutionChangedEventArgs>? Changed;

    public event EventHandler<InspectionRunResult>? RunCompleted;

    public RunAdmission TryBegin(InspectionRunIntent intent)
    {
        ValidateIntent(intent);
        SessionState state;
        long revision;
        lock (_syncRoot)
        {
            if (_current is not null)
            {
                return new RunAdmission.Rejected(
                    new RunRejection(RunRejectionReason.Busy, _current.Run));
            }

            state = new SessionState(new ActiveInspectionRun(
                Guid.NewGuid(),
                intent,
                DateTimeOffset.UtcNow));
            _current = state;
            revision = ++_changeRevision;
        }

        PublishChanged(state.Run, revision);
        return new RunAdmission.Acquired(new InspectionSession(this, state));
    }

    private async Task<InspectionRunResult> ExecuteAsync(
        SessionState state,
        InspectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        TaskCompletionSource<bool> executionCompletion;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(state.DisposeRequested || state.Released, state);
            EnsureCurrent(state);
            if (state.IsExecuting)
            {
                throw new InvalidOperationException("The inspection session is already executing.");
            }

            state.IsExecuting = true;
            executionCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            state.ExecutionCompletion = executionCompletion;
        }

        try
        {
            var result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            PublishRunCompleted(result);
            return result;
        }
        finally
        {
            lock (_syncRoot)
            {
                state.IsExecuting = false;
                state.ExecutionCompletion = null;
            }

            executionCompletion.TrySetResult(true);
        }
    }

    private ValueTask DisposeAsync(SessionState state)
    {
        var startDisposal = false;
        Task disposal;
        lock (_syncRoot)
        {
            state.DisposeRequested = true;
            if (!state.DisposeStarted)
            {
                state.DisposeStarted = true;
                startDisposal = true;
            }

            disposal = state.DisposeCompletion.Task;
        }

        if (startDisposal)
        {
            _ = DisposeCoreAsync(state);
        }

        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync(SessionState state)
    {
        try
        {
            Task? execution;
            lock (_syncRoot)
            {
                execution = state.ExecutionCompletion?.Task;
            }

            if (execution is not null)
            {
                await execution.ConfigureAwait(false);
            }

            var changed = false;
            var revision = 0L;
            lock (_syncRoot)
            {
                if (!state.Released)
                {
                    state.Released = true;
                    if (ReferenceEquals(_current, state))
                    {
                        _current = null;
                        revision = ++_changeRevision;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                PublishChanged(null, revision);
            }

            state.DisposeCompletion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            state.DisposeCompletion.TrySetException(ex);
        }
    }

    private void EnsureCurrent(SessionState state)
    {
        if (!ReferenceEquals(_current, state))
        {
            throw new InvalidOperationException("The inspection session is not current.");
        }
    }

    private static void ValidateIntent(InspectionRunIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(intent.Mode.Key) ||
            string.IsNullOrWhiteSpace(intent.Mode.DisplayName))
        {
            throw new ArgumentException("Run mode is required.", nameof(intent));
        }

        if (string.IsNullOrWhiteSpace(intent.EntryPoint))
        {
            throw new ArgumentException("Entry point is required.", nameof(intent));
        }
    }

    private void PublishChanged(ActiveInspectionRun? current, long revision)
    {
        lock (_changedQueueRoot)
        {
            _changedQueue.Enqueue((current, revision));
            if (_publishingChanged)
            {
                return;
            }

            _publishingChanged = true;
        }

        while (true)
        {
            (ActiveInspectionRun? Current, long Revision) change;
            lock (_changedQueueRoot)
            {
                if (_changedQueue.Count == 0)
                {
                    _publishingChanged = false;
                    return;
                }

                change = _changedQueue.Dequeue();
            }

            PublishChangedItem(change.Current, change.Revision);
        }
    }

    private void PublishChangedItem(
        ActiveInspectionRun? current,
        long revision)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList()
                     .Cast<EventHandler<InspectionExecutionChangedEventArgs>>())
        {
            lock (_syncRoot)
            {
                if (revision != _changeRevision)
                {
                    return;
                }
            }

            try
            {
                handler(this, new InspectionExecutionChangedEventArgs(current));
            }
            catch (Exception ex)
            {
                try
                {
                    _log?.Warning(
                        "InspectionExecution",
                        $"Changed subscriber failed: {ex.Message}");
                }
                catch
                {
                }
            }
        }
    }

    private void PublishRunCompleted(InspectionRunResult result)
    {
        PublishSafely(RunCompleted, result, "RunCompleted");
    }

    private void PublishSafely<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs args,
        string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<TEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                try
                {
                    _log?.Warning("InspectionExecution", $"{eventName} subscriber failed: {ex.Message}");
                }
                catch
                {
                    // Logging must not break admission cleanup.
                }
            }
        }
    }

    private sealed class InspectionSession : IInspectionSession
    {
        private readonly InspectionExecution _owner;
        private readonly SessionState _state;

        public InspectionSession(InspectionExecution owner, SessionState state)
        {
            _owner = owner;
            _state = state;
        }

        public ActiveInspectionRun Run => _state.Run;

        public Task<InspectionRunResult> ExecuteAsync(
            InspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _owner.ExecuteAsync(_state, request, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return _owner.DisposeAsync(_state);
        }
    }

    private sealed class SessionState
    {
        public SessionState(ActiveInspectionRun run)
        {
            Run = run;
        }

        public ActiveInspectionRun Run { get; }

        public bool IsExecuting { get; set; }

        public bool DisposeRequested { get; set; }

        public bool DisposeStarted { get; set; }

        public bool Released { get; set; }

        public TaskCompletionSource<bool>? ExecutionCompletion { get; set; }

        public TaskCompletionSource<bool> DisposeCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
```

- [ ] **Step 4: 运行 module Green 和 Application 全测**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionExecutionTests"
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug
```

Expected: 全部 PASS；无测试卡死超过 2 秒。

- [ ] **Step 5: 检查并提交**

Run:

```powershell
git diff --check
git status --short
git add VisionStation.Application VisionStation.Application.Tests
git commit -m "feat: add exclusive inspection execution module"
git push -u origin HEAD
```

## Task 3: 增加 Stop 等待超时配置

**Files:**

- Modify: `VisionStation.Domain/DeviceConfigurationModels.cs:253-262`
- Modify: `VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs:601-609`
- Modify: `VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs:905-911`
- Modify: `VisionStation.Application.Tests/ProductionSettingsConfigurationTests.cs:18-46`

- [ ] **Step 1: 写配置归一化 Red**

在现有无效 Production 设置中加入：

```csharp
StopWaitTimeoutMs = 0,
```

在读取后的断言中加入：

```csharp
Assert.Equal(10000, configuration.SystemSettings.Production.StopWaitTimeoutMs);
```

- [ ] **Step 2: 运行 Red**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionSettingsConfigurationTests"
```

Expected: compile failure，`ProductionSettingsConfiguration` 尚无 `StopWaitTimeoutMs`。

- [ ] **Step 3: 实现默认值和归一化**

在 Domain record 末尾增加：

```csharp
public int StopWaitTimeoutMs { get; init; } = 10000;
```

在 Production normalizer 的 `with` 表达式增加：

```csharp
StopWaitTimeoutMs = production.StopWaitTimeoutMs <= 0
    ? defaults.Production.StopWaitTimeoutMs
    : production.StopWaitTimeoutMs
```

默认配置对象明确写入：

```csharp
StopWaitTimeoutMs = 10000
```

- [ ] **Step 4: 验证、提交、推送**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionSettingsConfigurationTests"
git diff --check
git add VisionStation.Domain VisionStation.Infrastructure VisionStation.Application.Tests
git commit -m "feat: configure bounded production stop wait"
git push -u origin HEAD
```

Expected: 配置测试 PASS。

## Task 4: 重构 ProductionCoordinator 的准入与状态

**Depends on:** Task 1–3。

**Files:**

- Create: `VisionStation.Application/ProductionCommandModels.cs`
- Create: `VisionStation.Application.Tests/ProductionCoordinatorTestDoubles.cs`
- Create: `VisionStation.Application.Tests/ProductionCoordinatorTests.cs`
- Modify: `VisionStation.Application/ProductionCoordinator.cs:6-255`
- Modify: `VisionStation.Domain/Models.cs:19-25`
- Modify: `VisionStation.Client/App.xaml.cs:102-103`
- Modify: `VisionStation.Application/Inspection/Execution/InspectionExecution.cs`

- [ ] **Step 1: 先写成功、冲突和取消测试**

按本计划“Appendix A”创建测试 doubles，然后创建 `ProductionCoordinatorTests.cs`：

```csharp
using VisionStation.Application;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class ProductionCoordinatorTests
{
    [Fact]
    public async Task RunSingleAsync_when_successful_publishes_exact_state_sequence()
    {
        var harness = CoordinatorHarness.Create();
        var states = new DistinctStateRecorder(harness.Coordinator);

        var result = await harness.Coordinator.RunSingleAsync();

        Assert.Equal(ProductionCommandDisposition.Completed, result.Disposition);
        Assert.NotNull(result.Value);
        Assert.Equal(
            [
                ProductionState.Starting,
                ProductionState.Running,
                ProductionState.Stopping,
                ProductionState.Stopped
            ],
            states.States);
        Assert.Null(harness.Execution.Current);
    }

    [Fact]
    public async Task RunSingleAsync_while_continuous_returns_busy_immediately()
    {
        var entered = CoordinatorHarness.NewSignal();
        var harness = CoordinatorHarness.Create();
        harness.Executor.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestRunResults.Ok();
        };

        var started = await harness.Coordinator.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var conflictTask = harness.Coordinator.RunSingleAsync();

        Assert.Equal(ProductionCommandDisposition.Completed, started.Disposition);
        Assert.True(conflictTask.IsCompleted);
        var conflict = await conflictTask;
        Assert.Equal(ProductionCommandDisposition.Rejected, conflict.Disposition);
        Assert.Equal(RunRejectionReason.Busy, conflict.Rejection?.Reason);

        await harness.Coordinator.StopAsync();
    }

    [Fact]
    public async Task StartAsync_while_single_is_running_returns_busy_immediately()
    {
        var entered = CoordinatorHarness.NewSignal();
        var harness = CoordinatorHarness.Create();
        harness.Executor.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestRunResults.Ok();
        };

        var single = harness.Coordinator.RunSingleAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var conflict = await harness.Coordinator.StartAsync();

        Assert.Equal(ProductionCommandDisposition.Rejected, conflict.Disposition);
        Assert.Equal(RunRejectionReason.Busy, conflict.Rejection?.Reason);

        await harness.Coordinator.StopAsync();
        Assert.Equal(ProductionCommandDisposition.Canceled, (await single).Disposition);
    }

    [Fact]
    public async Task RunSingleAsync_when_caller_cancels_returns_canceled_without_fault_alarm()
    {
        var entered = CoordinatorHarness.NewSignal();
        var harness = CoordinatorHarness.Create();
        var states = new DistinctStateRecorder(harness.Coordinator);
        harness.Executor.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestRunResults.Ok();
        };
        using var cancellation = new CancellationTokenSource();

        var run = harness.Coordinator.RunSingleAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await run;

        Assert.Equal(ProductionCommandDisposition.Canceled, result.Disposition);
        Assert.DoesNotContain(
            harness.Alarms.Raised,
            alarm => alarm.Severity is AlarmSeverity.Error or AlarmSeverity.Critical);
        Assert.Equal(ProductionState.Stopped, harness.Coordinator.Snapshot.State);
        Assert.Null(harness.Execution.Current);
        Assert.Contains(ProductionState.Stopping, states.States);
    }

    [Fact]
    public async Task Stop_from_admission_changed_cancels_pending_production_owner()
    {
        var harness = CoordinatorHarness.Create();
        Task<ProductionCommandResult>? stop = null;
        Task<ProductionCommandResult>? duplicate = null;
        harness.Execution.Changed += (_, args) =>
        {
            if (args.Current is not null)
            {
                duplicate = harness.Coordinator.StartAsync();
                stop = harness.Coordinator.StopAsync();
            }
        };

        var start = await harness.Coordinator.StartAsync();
        var duplicateResult = await Assert.IsType<Task<ProductionCommandResult>>(
            duplicate);
        var stopResult = await Assert.IsType<Task<ProductionCommandResult>>(stop);

        Assert.Equal(ProductionCommandDisposition.Canceled, start.Disposition);
        Assert.Equal(
            RunRejectionReason.AlreadyRunning,
            duplicateResult.Rejection?.Reason);
        Assert.Equal(ProductionCommandDisposition.Completed, stopResult.Disposition);
        Assert.Null(harness.Execution.Current);
        Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
    }

    [Fact]
    public async Task Concurrent_start_calls_initialize_only_one_operation()
    {
        var connectEntered = CoordinatorHarness.NewSignal();
        var allowConnect = CoordinatorHarness.NewSignal();
        var harness = CoordinatorHarness.Create();
        harness.Camera.ConnectHandler = async token =>
        {
            connectEntered.TrySetResult(true);
            await allowConnect.Task.WaitAsync(token);
        };

        var first = harness.Coordinator.StartAsync();
        await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicateTask = harness.Coordinator.StartAsync();

        Assert.True(duplicateTask.IsCompleted);
        var duplicate = await duplicateTask;
        Assert.Equal(ProductionCommandDisposition.Rejected, duplicate.Disposition);
        Assert.Equal(RunRejectionReason.AlreadyRunning, duplicate.Rejection?.Reason);
        Assert.Equal(1, harness.Camera.ConnectCount);

        allowConnect.TrySetResult(true);
        Assert.Equal(
            ProductionCommandDisposition.Completed,
            (await first).Disposition);

        await harness.Coordinator.StopAsync();
    }
}
```

- [ ] **Step 2: 运行 Coordinator Red**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionCoordinatorTests"
```

Expected: compile failure，缺少命令结果、Starting/Stopping 和新构造契约。

- [ ] **Step 3: 增加命令结果和状态值**

创建 `ProductionCommandModels.cs`：

```csharp
namespace VisionStation.Application;

public enum ProductionCommandDisposition
{
    Completed,
    Canceled,
    Rejected,
    NoOp
}

public sealed record ProductionCommandResult(
    ProductionCommandDisposition Disposition,
    RunRejection? Rejection = null);

public sealed record ProductionCommandResult<T>(
    ProductionCommandDisposition Disposition,
    T? Value = default,
    RunRejection? Rejection = null);
```

用下面的完整定义替换 `ProductionState`，在末尾追加新值以保留旧枚举数值：

```csharp
public enum ProductionState
{
    Stopped,
    Running,
    Paused,
    Faulted,
    Starting,
    Stopping
}
```

把 `ProductionSnapshot` 的最后两行精确改为：

```csharp
DateTimeOffset UpdatedAt,
Guid? ActiveSessionId = null);
```

- [ ] **Step 4: 给 InspectionExecution 增加迁移期构造器**

在 `InspectionExecution.cs` 加入下列迁移期 public 构造器和 private adapter。它复用容器中同一个旧 Runner，避免迁移中间态创建两个 Runner；adapter 只存在到 Task 8：

```csharp
public InspectionExecution(
    IInspectionRunner runner,
    IAppLogService log)
    : this(new LegacyInspectionRunnerExecutor(runner), log)
{
}

private sealed class LegacyInspectionRunnerExecutor : IInspectionExecutor
{
    private readonly IInspectionRunner _runner;

    public LegacyInspectionRunnerExecutor(IInspectionRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<InspectionRunResult> ExecuteAsync(
        InspectionRequest request,
        CancellationToken cancellationToken) =>
        _runner.RunAsync(request, cancellationToken);
}
```

`InspectionRunner` 本身暂时不改 class 声明、方法名或 event；这样 public class 不会暴露 internal base interface，也让尚未迁移的配方页与 VariableCenter 保持可编译。

Module 自己发布 `RunCompleted`；本任务不要订阅旧 Runner event。

最终生产构造器在 Task 8 移除旧 seam 时一次性换入，形状为：

```csharp
public InspectionExecution(
    ICameraDevice camera,
    IConfigurableCameraDevice configurableCamera,
    IAxisController axis,
    IPlcClient plc,
    IDeviceRuntime devices,
    DeviceConfiguration configuration,
    IDeviceConfigurationRepository configurationRepository,
    IVisionPipeline pipeline,
    IRecipeRepository recipes,
    IInspectionRecordRepository records,
    IImageTraceStore traceStore,
    IAppLogService log,
    ICommunicationChannelRuntime communicationChannels,
    IInspectionRunControl runControl)
    : this(
        new InspectionRunner(
            camera,
            configurableCamera,
            axis,
            plc,
            devices,
            configuration,
            configurationRepository,
            pipeline,
            recipes,
            records,
            traceStore,
            log,
            communicationChannels,
            runControl),
        log)
{
}
```

- [ ] **Step 5: 用已批准状态机重写 Coordinator 核心**

采用以下字段和活动操作类型，删除 `_runner`、`_loopCancellation`、`_loopTask`。本地 reservation 在调用全局 module 之前登记，所以 Stop、重复 Start 和清理都能通过同一个 operation 身份协调：

```csharp
private readonly IInspectionExecution _inspectionExecution;
private readonly object _snapshotQueueRoot = new();
private readonly SortedDictionary<long, SnapshotUpdate> _snapshotQueue = new();
private ActiveProductionOperation? _activeOperation;
private long _snapshotRevision;
private long _nextSnapshotRevisionToPublish = 1;
private bool _publishingSnapshots;

private readonly record struct SnapshotUpdate(
    ProductionSnapshot Snapshot,
    long Revision);

private sealed class ActiveProductionOperation
{
    public ActiveProductionOperation(
        InspectionRunIntent intent,
        CancellationTokenSource cancellation)
    {
        Intent = intent;
        ReservationRun = new ActiveInspectionRun(
            Guid.NewGuid(),
            intent,
            DateTimeOffset.UtcNow);
        _cancellation = cancellation;
        Token = cancellation.Token;
    }

    private readonly object _cancellationRoot = new();
    private readonly CancellationTokenSource _cancellation;
    private int _completionStarted;
    private bool _cancellationDisposed;

    public InspectionRunIntent Intent { get; }

    public ActiveInspectionRun ReservationRun { get; }

    public IInspectionSession? Session { get; private set; }

    public ActiveInspectionRun ActiveRun => Session?.Run ?? ReservationRun;

    public CancellationToken Token { get; }

    public bool StopTimedOut { get; set; }

    public void AttachSession(IInspectionSession session)
    {
        Session = session;
    }

    public TaskCompletionSource<bool> CompletionSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => CompletionSource.Task;

    public bool TryBeginCompletion() =>
        Interlocked.CompareExchange(ref _completionStarted, 1, 0) == 0;

    public void RequestCancellation()
    {
        lock (_cancellationRoot)
        {
            if (_cancellationDisposed)
            {
                return;
            }

            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException)
            {
            }
        }
    }

    public void DisposeCancellation()
    {
        lock (_cancellationRoot)
        {
            if (_cancellationDisposed)
            {
                return;
            }

            _cancellationDisposed = true;
            _cancellation.Dispose();
        }
    }
}
```

`ReservationRun` 只在 Session 尚未附加的极短窗口为重复生产命令提供 Mode、EntryPoint 和 `AlreadyRunning/Busy` 诊断；它的 SessionId 不写入 `ProductionSnapshot.ActiveSessionId`，也不作为 module authority。附加后所有权一律使用真实 `Session.Run`。

构造函数完整替换为下面的签名和赋值；设备事件订阅保持现有三行：

```csharp
public ProductionCoordinator(
    IInspectionExecution inspectionExecution,
    ICameraDevice camera,
    IPlcClient plc,
    IAxisController axis,
    IAppLogService log,
    IAlarmService alarms,
    ICommunicationChannelRuntime communicationChannels,
    DeviceConfiguration configuration)
{
    _inspectionExecution = inspectionExecution;
    _camera = camera;
    _plc = plc;
    _axis = axis;
    _log = log;
    _alarms = alarms;
    _communicationChannels = communicationChannels;
    _productionSettings = configuration.SystemSettings.Production;

    _camera.StateChanged += OnDeviceStateChanged;
    _plc.StateChanged += OnDeviceStateChanged;
    _axis.StateChanged += OnDeviceStateChanged;
}
```

公开方法采用下面的精确流程：

```csharp
public async Task<ProductionCommandResult<InspectionRunResult>> RunSingleAsync(
    CancellationToken cancellationToken = default)
{
    var intent = new InspectionRunIntent(
        InspectionRunModes.ManualSingle,
        nameof(ProductionCoordinator));
    var operation = TryReserveOperation(intent, cancellationToken, out var localRejection);
    if (operation is null)
    {
        return new ProductionCommandResult<InspectionRunResult>(
            ProductionCommandDisposition.Rejected,
            Rejection: localRejection);
    }

    var admission = await AcquireSessionAsync(operation);
    if (admission is RunAdmission.Rejected rejected)
    {
        return new ProductionCommandResult<InspectionRunResult>(
            ProductionCommandDisposition.Rejected,
            Rejection: rejected.Rejection);
    }

    var session = operation.Session!;
    var faulted = false;
    try
    {
        await InitializeProductionAsync(operation.Token);
        if (!TryTransitionToRunning(operation))
        {
            throw new OperationCanceledException(operation.Token);
        }

        var result = await RunSingleCoreAsync(session, operation.Token);
        return new ProductionCommandResult<InspectionRunResult>(
            ProductionCommandDisposition.Completed,
            result);
    }
    catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
    {
        return new ProductionCommandResult<InspectionRunResult>(
            ProductionCommandDisposition.Canceled);
    }
    catch (Exception ex)
    {
        faulted = true;
        RaiseSingleRunFailure(ex);
        throw;
    }
    finally
    {
        await CompleteOperationAsync(operation, faulted);
    }
}

public async Task<ProductionCommandResult> StartAsync(
    CancellationToken cancellationToken = default)
{
    var intent = new InspectionRunIntent(
        InspectionRunModes.Continuous,
        nameof(ProductionCoordinator));
    var operation = TryReserveOperation(intent, cancellationToken, out var localRejection);
    if (operation is null)
    {
        return new ProductionCommandResult(
            ProductionCommandDisposition.Rejected,
            localRejection);
    }

    var admission = await AcquireSessionAsync(operation);
    if (admission is RunAdmission.Rejected rejected)
    {
        return new ProductionCommandResult(
            ProductionCommandDisposition.Rejected,
            MapStartRejection(rejected.Rejection));
    }

    try
    {
        await InitializeProductionAsync(operation.Token);
        if (!TryTransitionToRunning(operation))
        {
            throw new OperationCanceledException(operation.Token);
        }

        _ = Task.Run(
            () => RunContinuousAsync(operation),
            CancellationToken.None);
        return new ProductionCommandResult(ProductionCommandDisposition.Completed);
    }
    catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
    {
        await CompleteOperationAsync(operation, faulted: false);
        return new ProductionCommandResult(ProductionCommandDisposition.Canceled);
    }
    catch (Exception ex)
    {
        RaiseContinuousFailure(ex);
        await CompleteOperationAsync(operation, faulted: true);
        throw;
    }
}

public async Task<ProductionCommandResult> StopAsync(
    CancellationToken cancellationToken = default)
{
    ActiveProductionOperation? operation;
    lock (_syncRoot)
    {
        operation = _activeOperation;
    }

    if (operation is null)
    {
        var external = _inspectionExecution.Current;
        return external is null
            ? new ProductionCommandResult(ProductionCommandDisposition.NoOp)
            : new ProductionCommandResult(
                ProductionCommandDisposition.Rejected,
                new RunRejection(RunRejectionReason.NotOwner, external));
    }

    PublishSnapshot(TryCommitStopping(operation));
    operation.RequestCancellation();
    await operation.Completion.WaitAsync(cancellationToken);
    return new ProductionCommandResult(ProductionCommandDisposition.Completed);
}
```

增加私有 helper；这里是唯一允许的执行与清理顺序：

```csharp
private ActiveProductionOperation? TryReserveOperation(
    InspectionRunIntent intent,
    CancellationToken cancellationToken,
    out RunRejection? rejection)
{
    lock (_syncRoot)
    {
        if (_activeOperation is not null)
        {
            var active = _activeOperation.ActiveRun;
            rejection = new RunRejection(
                IsCoordinatorContinuous(intent) &&
                IsCoordinatorContinuous(active.Intent)
                    ? RunRejectionReason.AlreadyRunning
                    : RunRejectionReason.Busy,
                active);
            return null;
        }

        var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operation = new ActiveProductionOperation(intent, linkedCancellation);
        _activeOperation = operation;
        rejection = null;
        return operation;
    }
}

private async Task<RunAdmission> AcquireSessionAsync(
    ActiveProductionOperation operation)
{
    var admission = _inspectionExecution.TryBegin(operation.Intent);
    if (admission is RunAdmission.Rejected)
    {
        CompleteRejectedReservation(operation);
        return admission;
    }

    var acquired = (RunAdmission.Acquired)admission;
    SnapshotUpdate? update = null;
    var attached = false;
    lock (_syncRoot)
    {
        if (ReferenceEquals(_activeOperation, operation))
        {
            operation.AttachSession(acquired.Session);
            update = CommitStateLocked(
                operation.StopTimedOut
                    ? ProductionState.Faulted
                    : ProductionState.Starting,
                acquired.Session.Run.SessionId);
            attached = true;
        }
    }

    if (!attached)
    {
        await acquired.Session.DisposeAsync();
        CompleteRejectedReservation(operation);
        throw new InvalidOperationException(
            "Production reservation lost before session attachment.");
    }

    PublishSnapshot(update);
    return admission;
}

private void CompleteRejectedReservation(
    ActiveProductionOperation operation)
{
    SnapshotUpdate? update = null;
    lock (_syncRoot)
    {
        if (ReferenceEquals(_activeOperation, operation))
        {
            _activeOperation = null;
            if (operation.StopTimedOut)
            {
                update = CommitStateLocked(ProductionState.Faulted, null);
            }
        }
    }

    PublishSnapshot(update);
    operation.DisposeCancellation();
    operation.CompletionSource.TrySetResult(true);
}

private async Task InitializeProductionAsync(CancellationToken cancellationToken)
{
    await _camera.ConnectAsync(cancellationToken);
    await _plc.ConnectAsync(cancellationToken);
    await _axis.ConnectAsync(cancellationToken);
    await _communicationChannels.ConnectAsync(
        CommunicationChannelConnectionPolicies.Production,
        cancellationToken);
}

private async Task<InspectionRunResult> RunSingleCoreAsync(
    IInspectionSession session,
    CancellationToken cancellationToken)
{
    await _plc.SetInspectionBusyAsync(true, cancellationToken);
    try
    {
        var result = await session.ExecuteAsync(new InspectionRequest
        {
            RecipeId = string.Empty,
            BatchId = DateTimeOffset.Now.ToString("yyyyMMdd"),
            OperatorName = Environment.UserName,
            TriggeredByPlc = false
        }, cancellationToken);
        await _plc.WriteInspectionResultAsync(result.Result, cancellationToken);
        ApplyResult(result.Result);
        PublishInspectionCompleted(result);
        return result;
    }
    finally
    {
        await ClearInspectionBusyAsync();
    }
}

private async Task RunContinuousAsync(ActiveProductionOperation operation)
{
    var faulted = false;
    var consecutiveFailures = 0;
    try
    {
        while (!operation.Token.IsCancellationRequested)
        {
            try
            {
                await RunSingleCoreAsync(operation.Session!, operation.Token);
                consecutiveFailures = 0;
                await Task.Delay(
                    TimeSpan.FromMilliseconds(_productionSettings.CycleDelayMs),
                    operation.Token);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                RaiseContinuousFailure(ex);
                if (_productionSettings.AutoStopOnAlarm ||
                    consecutiveFailures >= _productionSettings.MaxConsecutiveFailures)
                {
                    faulted = true;
                    break;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(_productionSettings.CycleDelayMs),
                    operation.Token);
            }
        }
    }
    catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        faulted = true;
        RaiseContinuousFailure(ex);
    }
    finally
    {
        await CompleteOperationAsync(operation, faulted);
    }
}

private async Task CompleteOperationAsync(
    ActiveProductionOperation operation,
    bool faulted)
{
    if (!operation.TryBeginCompletion())
    {
        await operation.Completion;
        return;
    }

    var finalFaulted = faulted;
    try
    {
        PublishSnapshot(TryCommitStopping(operation));
        await DisconnectProductionSafelyAsync();
        if (operation.Session is not null)
        {
            await operation.Session.DisposeAsync();
        }
    }
    catch (Exception ex)
    {
        finalFaulted = true;
        ReportCleanupFailureSafely(ex);
    }
    finally
    {
        SnapshotUpdate? finalUpdate = null;
        lock (_syncRoot)
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                var finalState = operation.StopTimedOut || finalFaulted
                    ? ProductionState.Faulted
                    : ProductionState.Stopped;
                _activeOperation = null;
                finalUpdate = CommitStateLocked(finalState, null);
            }
        }

        try
        {
            PublishSnapshot(finalUpdate);
        }
        finally
        {
            operation.DisposeCancellation();
            operation.CompletionSource.TrySetResult(true);
        }
    }
}

private static bool IsCoordinatorContinuous(InspectionRunIntent intent) =>
    intent.Mode.Key == InspectionRunModes.Continuous.Key &&
    string.Equals(
        intent.EntryPoint,
        nameof(ProductionCoordinator),
        StringComparison.Ordinal);

private static RunRejection MapStartRejection(RunRejection rejection)
{
    return IsCoordinatorContinuous(rejection.Active.Intent)
        ? rejection with { Reason = RunRejectionReason.AlreadyRunning }
        : rejection;
}
```

补齐清理、报警和安全事件 helper：

```csharp
private async Task DisconnectProductionSafelyAsync()
{
    try
    {
        using var cleanup = CreateCleanupCancellation();
        await _communicationChannels.DisconnectAsync(
            CommunicationChannelConnectionPolicies.Production,
            cleanup.Token);
    }
    catch (Exception ex)
    {
        _log.Warning("Production", $"Failed to disconnect production channels: {ex.Message}");
    }
}

private void RaiseSingleRunFailure(Exception ex)
{
    _log.Error("Production", ex.Message);
    _alarms.Raise(
        AlarmSeverity.Error,
        "Production",
        $"Single inspection failed: {ex.Message}",
        ex.ToString(),
        "production:single-run");
}

private void RaiseContinuousFailure(Exception ex)
{
    _log.Error("Production", $"Continuous production failed: {ex.Message}");
    _alarms.Raise(
        AlarmSeverity.Critical,
        "Production",
        $"Continuous production stopped: {ex.Message}",
        ex.ToString(),
        "production:continuous-run");
}

private void ReportCleanupFailureSafely(Exception ex)
{
    try
    {
        _log.Error("Production", $"Production cleanup failed: {ex.Message}");
        _alarms.Raise(
            AlarmSeverity.Critical,
            "Production",
            $"Production cleanup failed: {ex.Message}",
            ex.ToString(),
            "production:cleanup");
    }
    catch
    {
    }
}

private bool TryTransitionToRunning(ActiveProductionOperation operation)
{
    SnapshotUpdate? update = null;
    lock (_syncRoot)
    {
        if (ReferenceEquals(_activeOperation, operation) &&
            operation.Session is not null &&
            !operation.Token.IsCancellationRequested &&
            _snapshot.State == ProductionState.Starting)
        {
            update = CommitStateLocked(
                ProductionState.Running,
                operation.Session.Run.SessionId);
        }
    }

    PublishSnapshot(update);
    return update is not null;
}

private SnapshotUpdate? TryCommitStopping(
    ActiveProductionOperation operation)
{
    lock (_syncRoot)
    {
        if (!ReferenceEquals(_activeOperation, operation) ||
            operation.StopTimedOut ||
            _snapshot.State is not (
                ProductionState.Starting or
                ProductionState.Running or
                ProductionState.Paused))
        {
            return null;
        }

        return CommitStateLocked(
            ProductionState.Stopping,
            operation.Session?.Run.SessionId ?? _snapshot.ActiveSessionId);
    }
}

private SnapshotUpdate CommitStateLocked(
    ProductionState state,
    Guid? activeSessionId)
{
    _snapshot = _snapshot with
    {
        State = state,
        ActiveSessionId = activeSessionId,
        UpdatedAt = DateTimeOffset.Now
    };
    return new SnapshotUpdate(_snapshot, ++_snapshotRevision);
}

private void PublishSnapshot(SnapshotUpdate? update)
{
    if (update is null)
    {
        return;
    }

    lock (_snapshotQueueRoot)
    {
        _snapshotQueue.Add(update.Value.Revision, update.Value);
        if (_publishingSnapshots)
        {
            return;
        }

        _publishingSnapshots = true;
    }

    while (true)
    {
        SnapshotUpdate next;
        lock (_snapshotQueueRoot)
        {
            if (!_snapshotQueue.Remove(
                    _nextSnapshotRevisionToPublish,
                    out next))
            {
                _publishingSnapshots = false;
                return;
            }

            _nextSnapshotRevisionToPublish++;
        }

        PublishSafely(
            SnapshotChanged,
            next.Snapshot,
            nameof(SnapshotChanged));
    }
}

private void PublishInspectionCompleted(InspectionRunResult result)
{
    PublishSafely(InspectionCompleted, result, nameof(InspectionCompleted));
}

private void PublishSafely<TEventArgs>(
    EventHandler<TEventArgs>? handlers,
    TEventArgs args,
    string eventName)
{
    if (handlers is null)
    {
        return;
    }

    foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<TEventArgs>>())
    {
        try
        {
            handler(this, args);
        }
        catch (Exception ex)
        {
            try
            {
                _log.Warning("Production", $"{eventName} subscriber failed: {ex.Message}");
            }
            catch
            {
            }
        }
    }
}
```

把 `ApplyResult` 完整替换为：

```csharp
private void ApplyResult(InspectionResult result)
{
    SnapshotUpdate update;
    lock (_syncRoot)
    {
        var total = _snapshot.TotalCount + 1;
        var ok = _snapshot.OkCount +
                 (result.Outcome == InspectionOutcome.Ok ? 1 : 0);
        var ng = _snapshot.NgCount +
                 (result.Outcome == InspectionOutcome.Ng ? 1 : 0);
        _snapshot = _snapshot with
        {
            TotalCount = total,
            OkCount = ok,
            NgCount = ng,
            YieldRate = total == 0 ? 100 : ok * 100.0 / total,
            LastCycleTime = result.CycleTime,
            UpdatedAt = DateTimeOffset.Now
        };
        update = new SnapshotUpdate(_snapshot, ++_snapshotRevision);
    }

    PublishSnapshot(update);
}
```

`OnDeviceStateChanged` 的报警 switch 保持不变，只把最后一行直接 invoke 替换为：

```csharp
PublishSafely(DeviceStateChanged, snapshot, nameof(DeviceStateChanged));
```

现有 `ClearInspectionBusyAsync` 和 `CreateCleanupCancellation` 原样保留；Stop 不直接调用它们。

- [ ] **Step 6: 注册 module，并保留旧 Runner 直到配方迁移**

在 `App.xaml.cs` 临时使用：

```csharp
containerRegistry.RegisterSingleton<IInspectionRunner, InspectionRunner>();
containerRegistry.RegisterSingleton<IInspectionExecution, InspectionExecution>();
containerRegistry.RegisterSingleton<ProductionCoordinator>();
```

- [ ] **Step 7: 运行 Green、全量 Application 测试和 Client build**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionCoordinatorTests"
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug
dotnet build .\VisionStation.Client\VisionStation.Client.csproj -c Debug --nologo
```

Expected: tests PASS，Client build 成功。

- [ ] **Step 8: 提交并推送**

Run:

```powershell
git diff --check
git status --short
git add VisionStation.Application VisionStation.Application.Tests VisionStation.Domain VisionStation.Client
git commit -m "feat: serialize production execution state"
git push -u origin HEAD
```

## Task 5: 完成 Stop 并发、单一清理和超时语义

**Files:**

- Modify: `VisionStation.Application.Tests/ProductionCoordinatorTests.cs`
- Modify: `VisionStation.Application.Tests/ProductionCoordinatorTestDoubles.cs`
- Modify: `VisionStation.Application/ProductionCoordinator.cs`

- [ ] **Step 1: 写初始化取消、并发 Stop 和 timeout Red**

在 `ProductionCoordinatorTests` 增加：

```csharp
[Fact]
public async Task Stop_during_initialization_cancels_registered_operation()
{
    var harness = CoordinatorHarness.Create();
    var states = new DistinctStateRecorder(harness.Coordinator);
    harness.Camera.ConnectHandler =
        token => Task.Delay(Timeout.InfiniteTimeSpan, token);

    var start = harness.Coordinator.StartAsync();
    await harness.Camera.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var stop = await harness.Coordinator.StopAsync();
    var startResult = await start;

    Assert.Equal(ProductionCommandDisposition.Completed, stop.Disposition);
    Assert.Equal(ProductionCommandDisposition.Canceled, startResult.Disposition);
    Assert.Equal(
        [ProductionState.Starting, ProductionState.Stopping, ProductionState.Stopped],
        states.States);
    Assert.Equal(1, harness.Channels.DisconnectCount);
    Assert.Null(harness.Execution.Current);
}

[Fact]
public async Task Stop_during_last_initialization_return_never_transitions_back_to_running()
{
    var connectEntered = CoordinatorHarness.NewSignal();
    var allowConnectReturn = CoordinatorHarness.NewSignal();
    var harness = CoordinatorHarness.Create();
    var states = new DistinctStateRecorder(harness.Coordinator);
    harness.Camera.ConnectHandler = async _ =>
    {
        connectEntered.TrySetResult(true);
        await allowConnectReturn.Task;
    };

    var start = harness.Coordinator.StartAsync();
    await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var stop = harness.Coordinator.StopAsync();
    allowConnectReturn.TrySetResult(true);

    Assert.Equal(ProductionCommandDisposition.Canceled, (await start).Disposition);
    Assert.Equal(ProductionCommandDisposition.Completed, (await stop).Disposition);
    Assert.Equal(
        [ProductionState.Starting, ProductionState.Stopping, ProductionState.Stopped],
        states.States);
}

[Fact]
public async Task Reentrant_stop_publishes_snapshots_in_revision_order()
{
    var harness = CoordinatorHarness.Create();
    Task<ProductionCommandResult>? stop = null;
    var observed = new List<ProductionState>();
    harness.Coordinator.SnapshotChanged += (_, snapshot) =>
    {
        if (snapshot.State == ProductionState.Starting && stop is null)
        {
            stop = harness.Coordinator.StopAsync();
        }
    };
    harness.Coordinator.SnapshotChanged += (_, snapshot) =>
    {
        if (observed.Count == 0 || observed[^1] != snapshot.State)
        {
            observed.Add(snapshot.State);
        }
    };

    var start = await harness.Coordinator.StartAsync();
    var stopResult = await Assert.IsType<Task<ProductionCommandResult>>(stop);

    Assert.Equal(ProductionCommandDisposition.Canceled, start.Disposition);
    Assert.Equal(ProductionCommandDisposition.Completed, stopResult.Disposition);
    Assert.Equal(
        [ProductionState.Starting, ProductionState.Stopping, ProductionState.Stopped],
        observed);
}

[Fact]
public async Task Concurrent_stop_calls_wait_same_completion_and_clean_once()
{
    var entered = CoordinatorHarness.NewSignal();
    var cancellationObserved = CoordinatorHarness.NewSignal();
    var allowExit = CoordinatorHarness.NewSignal();
    var harness = CoordinatorHarness.Create(stopWaitTimeoutMs: 5000);
    harness.Executor.Handler = async (_, token) =>
    {
        entered.TrySetResult(true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            cancellationObserved.TrySetResult(true);
            await allowExit.Task;
            throw;
        }

        return TestRunResults.Ok();
    };

    await harness.Coordinator.StartAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var stops = Enumerable.Range(0, 8)
        .Select(_ => harness.Coordinator.StopAsync())
        .ToArray();
    await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    allowExit.TrySetResult(true);
    var results = await Task.WhenAll(stops);

    Assert.All(results, result =>
        Assert.Equal(ProductionCommandDisposition.Completed, result.Disposition));
    Assert.Equal(1, harness.Channels.DisconnectCount);
    Assert.Equal(1, harness.Plc.BusyWrites.Count(value => !value));
    Assert.Null(harness.Execution.Current);
    Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
}

[Fact]
public async Task Stop_timeout_faults_and_holds_session_until_late_cleanup()
{
    var entered = CoordinatorHarness.NewSignal();
    var allowExit = CoordinatorHarness.NewSignal();
    var harness = CoordinatorHarness.Create(stopWaitTimeoutMs: 200);
    harness.Executor.Handler = async (_, _) =>
    {
        entered.TrySetResult(true);
        await allowExit.Task;
        return TestRunResults.Ok();
    };

    await harness.Coordinator.StartAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

    var firstStop = harness.Coordinator.StopAsync();
    Assert.Same(
        firstStop,
        await Task.WhenAny(firstStop, Task.Delay(TimeSpan.FromSeconds(2))));
    await Assert.ThrowsAsync<TimeoutException>(() => firstStop);
    Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
    Assert.NotNull(harness.Coordinator.Snapshot.ActiveSessionId);
    Assert.NotNull(harness.Execution.Current);
    Assert.Contains(harness.Alarms.Raised, alarm =>
        alarm.Severity == AlarmSeverity.Critical);

    var blocked = harness.Execution.TryBegin(new InspectionRunIntent(
        InspectionRunModes.RecipeTest,
        nameof(ProductionCoordinatorTests)));
    Assert.IsType<RunAdmission.Rejected>(blocked);

    await Assert.ThrowsAsync<TimeoutException>(() => harness.Coordinator.StopAsync());
    Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
    Assert.Single(harness.Alarms.Raised.Where(alarm =>
        alarm.Id == "production:stop-timeout"));

    var finalStop = harness.Coordinator.StopAsync();
    allowExit.TrySetResult(true);
    Assert.Equal(
        ProductionCommandDisposition.Completed,
        (await finalStop).Disposition);
    await CoordinatorHarness.WaitUntilAsync(() =>
        harness.Execution.Current is null &&
        harness.Coordinator.Snapshot.ActiveSessionId is null &&
        harness.Channels.DisconnectCount == 1);
    Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
    Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
}

[Fact]
public async Task Timeout_before_session_attachment_never_regresses_faulted_to_starting()
{
    var changedEntered = CoordinatorHarness.NewSignal();
    var releaseChanged = CoordinatorHarness.NewSignal();
    var harness = CoordinatorHarness.Create(stopWaitTimeoutMs: 200);
    var states = new DistinctStateRecorder(harness.Coordinator);
    harness.Execution.Changed += (_, args) =>
    {
        if (args.Current is not null)
        {
            changedEntered.TrySetResult(true);
            releaseChanged.Task.GetAwaiter().GetResult();
        }
    };

    var start = Task.Run(() => harness.Coordinator.StartAsync());
    await changedEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var stop = harness.Coordinator.StopAsync();
    Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(2))));
    await Assert.ThrowsAsync<TimeoutException>(() => stop);

    releaseChanged.TrySetResult(true);
    Assert.Equal(
        ProductionCommandDisposition.Canceled,
        (await start).Disposition);
    Assert.DoesNotContain(ProductionState.Starting, states.States);
    Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
    Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
}

[Fact]
public async Task Pending_reservation_timeout_never_claims_external_session_ownership()
{
    var external = new ActiveInspectionRun(
        Guid.NewGuid(),
        new InspectionRunIntent(
            InspectionRunModes.RecipeTest,
            "RecipeManagementViewModel"),
        DateTimeOffset.UtcNow);
    var execution = new BlockingRejectedInspectionExecution(external);
    var harness = CoordinatorHarness.Create(
        stopWaitTimeoutMs: 200,
        inspectionExecution: execution);

    var start = Task.Run(() => harness.Coordinator.StartAsync());
    await execution.TryBeginEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var stop = harness.Coordinator.StopAsync();
    Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(2))));
    await Assert.ThrowsAsync<TimeoutException>(() => stop);

    Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
    Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
    Assert.Equal(external, execution.Current);

    execution.AllowTryBeginReturn.TrySetResult(true);
    var startResult = await start.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(ProductionCommandDisposition.Rejected, startResult.Disposition);
    Assert.Equal(RunRejectionReason.Busy, startResult.Rejection?.Reason);
    Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
}

[Fact]
public async Task Stop_when_idle_is_noop_and_external_session_is_not_owner()
{
    var harness = CoordinatorHarness.Create();

    var idle = await harness.Coordinator.StopAsync();
    var external = Assert.IsType<RunAdmission.Acquired>(
        harness.Execution.TryBegin(new InspectionRunIntent(
            InspectionRunModes.RecipeTest,
            nameof(ProductionCoordinatorTests))));
    var notOwner = await harness.Coordinator.StopAsync();

    Assert.Equal(ProductionCommandDisposition.NoOp, idle.Disposition);
    Assert.Equal(ProductionCommandDisposition.Rejected, notOwner.Disposition);
    Assert.Equal(RunRejectionReason.NotOwner, notOwner.Rejection?.Reason);
    Assert.Equal(0, harness.Channels.DisconnectCount);
    await external.Session.DisposeAsync();
}

[Fact]
public async Task RunSingle_fault_publishes_faulted_sequence_and_releases_session()
{
    var harness = CoordinatorHarness.Create();
    var states = new DistinctStateRecorder(harness.Coordinator);
    harness.Executor.Handler = static (_, _) =>
        Task.FromException<InspectionRunResult>(
            new InvalidOperationException("boom"));

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => harness.Coordinator.RunSingleAsync());

    Assert.Equal(
        [
            ProductionState.Starting,
            ProductionState.Running,
            ProductionState.Stopping,
            ProductionState.Faulted
        ],
        states.States);
    Assert.Null(harness.Execution.Current);
    Assert.Single(harness.Alarms.Raised.Where(alarm =>
        alarm.Severity == AlarmSeverity.Error));
}
```

- [ ] **Step 2: 运行 Stop Red**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionCoordinatorTests"
```

Expected: timeout 测试失败或挂起保护触发；不得通过增加测试等待时间解决。

- [ ] **Step 3: 实现有界 Stop 和迟到清理**

把 `StopAsync` 末尾的无界等待和返回两行精确替换为：

```csharp
try
{
    await operation.Completion.WaitAsync(
        TimeSpan.FromMilliseconds(_productionSettings.StopWaitTimeoutMs),
        cancellationToken);
    return new ProductionCommandResult(ProductionCommandDisposition.Completed);
}
catch (TimeoutException)
{
    var timeoutUpdate = TryMarkStopTimedOut(operation, out var firstTimeout);
    PublishSnapshot(timeoutUpdate);
    if (firstTimeout)
    {
        _alarms.Raise(
            AlarmSeverity.Critical,
            "Production",
            "Production stop timed out; the production operation has not completed.",
            alarmId: "production:stop-timeout");
    }

    throw;
}
```

新增 helper；`StopTimedOut` 的读取、首次写入和 Faulted 快照在同一个 Coordinator 锁协议内完成：

```csharp
private SnapshotUpdate? TryMarkStopTimedOut(
    ActiveProductionOperation operation,
    out bool firstTimeout)
{
    lock (_syncRoot)
    {
        firstTimeout = ReferenceEquals(_activeOperation, operation) &&
                       !operation.Completion.IsCompleted &&
                       !operation.StopTimedOut;
        if (!firstTimeout)
        {
            return null;
        }

        operation.StopTimedOut = true;
        return CommitStateLocked(
            ProductionState.Faulted,
            operation.Session?.Run.SessionId);
    }
}
```

`TryCommitStopping` 已在 timeout 标记存在时返回 null，二次 Stop 只再次取消/有界等待，绝不把 Faulted 改回 Stopping。timeout 只能投影 `operation.Session` 的真实 SessionId；Session 尚未附加时保持 null，绝不能借用全局 `Current`，因为它可能属于配方等外部入口。`AcquireSessionAsync` 后续附加成功时再原子写入真实生产 SessionId。`CompleteOperationAsync` 在同一锁内读取标记并最终提交 `Faulted + ActiveSessionId=null`；Stop 本身不清 Busy、不 Disconnect、不 Dispose Session。

- [ ] **Step 4: 验证清理次数和全量 Application 测试**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionCoordinatorTests"
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug
```

Expected: PASS；测试总耗时不因 timeout case 增加超过 2 秒。

- [ ] **Step 5: 提交并推送**

Run:

```powershell
git diff --check
git add VisionStation.Application VisionStation.Application.Tests
git commit -m "fix: make production stop bounded and idempotent"
git push -u origin HEAD
```

Expected: tests PASS，diff check 无输出，提交和推送成功。

## Task 6: 建立可复用 UI 投影并迁移生产界面

**Files:**

- Create: `VisionStation.Client/Presentation/ProductionRunUiState.cs`
- Create: `VisionStation.Vision.UI.Tests/ProductionRunUiStateTests.cs`
- Create: `VisionStation.Vision.UI.Tests/ProductionViewModelContractTests.cs`
- Create: `VisionStation.Vision.UI.Tests/ProductionRunXamlTests.cs`
- Modify: `VisionStation.Client/AssemblyInfo.cs`
- Modify: `VisionStation.Client/ViewModels/ProductionDashboardViewModel.cs:16-215`
- Modify: `VisionStation.Client/ViewModels/ShellWindowViewModel.cs:17-247`
- Modify: `VisionStation.Client/Views/ProductionDashboardView.xaml:83-105`

- [ ] **Step 1: 写 ownership 和状态映射 Red**

创建 `ProductionRunUiStateTests.cs`：

```csharp
using VisionStation.Application;
using VisionStation.Client.Presentation;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ProductionRunUiStateTests
{
    [Theory]
    [InlineData(ProductionState.Starting, "启动中", "#FFFFC95A")]
    [InlineData(ProductionState.Running, "运行", "#FF5CE08A")]
    [InlineData(ProductionState.Stopping, "停止中", "#FFFFC95A")]
    [InlineData(ProductionState.Faulted, "故障", "#FFFF667A")]
    [InlineData(ProductionState.Stopped, "停止", "#FFA9B7C2")]
    public void Create_maps_state_text_and_brush(
        ProductionState state,
        string text,
        string brush)
    {
        var result = ProductionRunUiState.Create(state, null, null, commandBusy: false);

        Assert.Equal(text, result.StateText);
        Assert.Equal(brush, result.StateBrush);
    }

    [Fact]
    public void Create_with_external_session_disables_all_production_commands()
    {
        var active = Active("recipe.test", "配方试运行", "RecipeManagementViewModel");

        var result = ProductionRunUiState.Create(
            ProductionState.Stopped,
            active,
            productionSessionId: null,
            commandBusy: false);

        Assert.False(result.CanRunSingle);
        Assert.False(result.CanStart);
        Assert.False(result.CanStop);
        Assert.True(result.IsExternallyOccupied);
        Assert.Contains("配方试运行", result.OccupancyText);
        Assert.Contains("RecipeManagementViewModel", result.OccupancyText);
        Assert.Contains("RecipeManagementViewModel", result.StateText);
    }

    [Fact]
    public void Create_with_owned_faulted_session_allows_stop_retry_but_not_start()
    {
        var active = Active("production.continuous", "连续生产", "ProductionCoordinator");

        var result = ProductionRunUiState.Create(
            ProductionState.Faulted,
            active,
            active.SessionId,
            commandBusy: false);

        Assert.False(result.CanRunSingle);
        Assert.False(result.CanStart);
        Assert.True(result.CanStop);
    }

    [Fact]
    public void Create_with_owned_starting_session_keeps_stop_available_during_start_command()
    {
        var active = Active("production.continuous", "连续生产", "ProductionCoordinator");

        var result = ProductionRunUiState.Create(
            ProductionState.Starting,
            active,
            active.SessionId,
            commandBusy: true);

        Assert.True(result.CanStop);
    }

    [Fact]
    public void Create_when_faulted_and_unoccupied_allows_explicit_restart()
    {
        var result = ProductionRunUiState.Create(
            ProductionState.Faulted,
            current: null,
            productionSessionId: null,
            commandBusy: false);

        Assert.True(result.CanRunSingle);
        Assert.True(result.CanStart);
        Assert.False(result.CanStop);
    }

    [Fact]
    public void Create_with_owned_stopping_session_disables_duplicate_stop()
    {
        var active = Active("production.continuous", "连续生产", "ProductionCoordinator");

        var result = ProductionRunUiState.Create(
            ProductionState.Stopping,
            active,
            active.SessionId,
            commandBusy: true);

        Assert.False(result.CanStop);
    }

    private static ActiveInspectionRun Active(
        string key,
        string displayName,
        string entryPoint) =>
        new(
            Guid.NewGuid(),
            new InspectionRunIntent(new InspectionRunMode(key, displayName), entryPoint),
            DateTimeOffset.UtcNow);
}
```

- [ ] **Step 2: 运行映射 Red**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionRunUiStateTests"
```

Expected: compile failure，缺少 `ProductionRunUiState`。

- [ ] **Step 3: 实现纯 UI 投影**

在 `Client/AssemblyInfo.cs` 中把 `System.Runtime.CompilerServices` using 放到现有 `using System.Windows;` 旁边，并把 friend assembly attribute 放在所有 using 之后；不要把 using 追加到现有 assembly attribute 下方：

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("VisionStation.Vision.UI.Tests")]
```

创建 `ProductionRunUiState.cs`：

```csharp
using VisionStation.Application;
using VisionStation.Domain;

namespace VisionStation.Client.Presentation;

internal sealed record ProductionRunUiState(
    string StateText,
    string StateBrush,
    bool CanRunSingle,
    bool CanStart,
    bool CanStop,
    bool IsExternallyOccupied,
    string OccupancyText)
{
    public static ProductionRunUiState Create(
        ProductionState state,
        ActiveInspectionRun? current,
        Guid? productionSessionId,
        bool commandBusy)
    {
        var ownsCurrent = current is not null &&
                          productionSessionId == current.SessionId;
        var externalCurrent = current is not null && !ownsCurrent;
        var canStart = current is null &&
                       !commandBusy &&
                       state is ProductionState.Stopped or ProductionState.Faulted;
        var canStop = ownsCurrent &&
                      state is not ProductionState.Stopping;
        var occupancy = current is null
            ? string.Empty
            : $"{current.Intent.Mode.DisplayName}（{current.Intent.EntryPoint}）正在占用检测执行";

        var stateText = externalCurrent
            ? $"占用：{current!.Intent.Mode.DisplayName}（{current.Intent.EntryPoint}）"
            : state switch
            {
                ProductionState.Starting => "启动中",
                ProductionState.Running => "运行",
                ProductionState.Stopping => "停止中",
                ProductionState.Paused => "暂停",
                ProductionState.Faulted => "故障",
                _ => "停止"
            };
        var stateBrush = externalCurrent
            ? "#FFFFC95A"
            : state switch
            {
                ProductionState.Running => "#FF5CE08A",
                ProductionState.Starting or ProductionState.Stopping or ProductionState.Paused => "#FFFFC95A",
                ProductionState.Faulted => "#FFFF667A",
                _ => "#FFA9B7C2"
            };

        return new ProductionRunUiState(
            stateText,
            stateBrush,
            canStart,
            canStart,
            canStop,
            externalCurrent,
            occupancy);
    }

    public static string FormatRejection(RunRejection rejection) =>
        $"{rejection.Active.Intent.Mode.DisplayName}（{rejection.Active.Intent.EntryPoint}）正在占用检测执行";
}
```

- [ ] **Step 4: 运行映射 Green**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionRunUiStateTests"
```

Expected: PASS。

- [ ] **Step 5: 写 ViewModel contract Red**

创建 `ProductionViewModelContractTests.cs`：

```csharp
using Prism.Commands;
using VisionStation.Application;
using VisionStation.Client.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ProductionViewModelContractTests
{
    [Fact]
    public void Dashboard_uses_async_commands_and_global_execution_seam()
    {
        var constructor = Assert.Single(typeof(ProductionDashboardViewModel).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IInspectionExecution));
        Assert.All(
            new[]
            {
                nameof(ProductionDashboardViewModel.RunSingleCommand),
                nameof(ProductionDashboardViewModel.StartCommand),
                nameof(ProductionDashboardViewModel.StopCommand)
            },
            propertyName => Assert.Equal(
                typeof(AsyncDelegateCommand),
                typeof(ProductionDashboardViewModel)
                    .GetProperty(propertyName)!
                    .PropertyType));
    }

    [Fact]
    public void Shell_observes_global_execution_seam()
    {
        var constructor = Assert.Single(typeof(ShellWindowViewModel).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IInspectionExecution));
    }
}
```

创建 `ProductionRunXamlTests.cs`，先锁定 Stop 不被加载遮罩覆盖：

```csharp
using System.Runtime.CompilerServices;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ProductionRunXamlTests
{
    [Fact]
    public void Dashboard_keeps_stop_header_outside_loading_overlay()
    {
        var xaml = File.ReadAllText(GetDashboardPath());
        var overlayStart = xaml.IndexOf(
            "<controls:SimpleLoadingOverlay",
            StringComparison.Ordinal);
        Assert.True(overlayStart >= 0);
        var overlayEnd = xaml.IndexOf("/>", overlayStart, StringComparison.Ordinal);
        Assert.True(overlayEnd > overlayStart);
        var overlay = xaml[overlayStart..(overlayEnd + 2)];

        Assert.Contains("ToolTip=\"停止当前生产检测\"", xaml);
        Assert.Contains("Grid.Column=\"0\"", overlay);
        Assert.Contains("Margin=\"0,52,10,0\"", overlay);
        Assert.DoesNotContain("Grid.ColumnSpan", overlay);
    }

    private static string GetDashboardPath(
        [CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "VisionStation.Client",
            "Views",
            "ProductionDashboardView.xaml"));
}
```

- [ ] **Step 6: 运行 ViewModel contract Red**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionViewModelContractTests|FullyQualifiedName~ProductionRunXamlTests"
```

Expected: FAIL，Dashboard 仍使用 `DelegateCommand`、两个 VM 未注入 seam，Stop tooltip/overlay 范围也未迁移。

- [ ] **Step 7: 迁移 Dashboard**

在文件顶部增加 `using VisionStation.Client.Presentation;`。新增字段；快照不使用硬编码默认值，必须来自 Coordinator：

```csharp
private readonly IInspectionExecution _inspectionExecution;
private ProductionSnapshot _productionSnapshot;
private int _activeCommandCount;
private bool _showingExecutionOccupancy;
```

把构造函数签名替换为：

```csharp
public ProductionDashboardViewModel(
    ProductionCoordinator coordinator,
    IInspectionExecution inspectionExecution,
    IInspectionRecordRepository records,
    IRecipeRepository recipes,
    IAppLogService log,
    IUiDispatcher uiDispatcher,
    IVisionOverlayBuilder overlayBuilder,
    ProductionDashboardLayoutService layoutService)
```

在现有构造函数赋值区加入以下两行；其他依赖赋值保持原顺序：

```csharp
_inspectionExecution = inspectionExecution;
_productionSnapshot = coordinator.Snapshot;
```

命令改为：

```csharp
RunSingleCommand = new AsyncDelegateCommand(RunSingleAsync, CanRunSingle);
StartCommand = new AsyncDelegateCommand(StartAsync, CanStart);
StopCommand = new AsyncDelegateCommand(StopAsync, CanStop);
```

属性类型改为：

```csharp
public AsyncDelegateCommand RunSingleCommand { get; }
public AsyncDelegateCommand StartCommand { get; }
public AsyncDelegateCommand StopCommand { get; }
```

用下面代码替换现有 Coordinator Snapshot 订阅，并增加 Current 订阅；完成所有订阅后立即应用一次构造时取得的快照：

```csharp
_coordinator.SnapshotChanged += (_, snapshot) => _uiDispatcher.Invoke(() =>
{
    _productionSnapshot = snapshot;
    ApplySnapshot(snapshot);
    RefreshExecutionPresentation();
});
_inspectionExecution.Changed += (_, _) =>
    _uiDispatcher.Invoke(RefreshExecutionPresentation);

ApplySnapshot(_productionSnapshot);
RefreshExecutionPresentation();
```

三个 handler 使用下列统一形状，分别调用对应 Coordinator 方法：

```csharp
private async Task RunSingleAsync()
{
    await ExecuteProductionCommandAsync(async () =>
    {
        var result = await _coordinator.RunSingleAsync();
        ApplyCommandResult(result.Disposition, result.Rejection);
    });
}

private Task StartAsync() =>
    ExecuteProductionCommandAsync(async () =>
    {
        var result = await _coordinator.StartAsync();
        ApplyCommandResult(result.Disposition, result.Rejection);
    });

private Task StopAsync() =>
    ExecuteProductionCommandAsync(async () =>
    {
        var result = await _coordinator.StopAsync();
        ApplyCommandResult(result.Disposition, result.Rejection);
    });

private async Task ExecuteProductionCommandAsync(Func<Task> action)
{
    var activeCommands = Interlocked.Increment(ref _activeCommandCount);
    IsBusy = activeCommands > 0;
    RefreshProductionCommands();
    try
    {
        await action();
    }
    catch (Exception ex)
    {
        LastMessage = $"生产命令失败：{ex.Message}";
        _log.Error("Production", LastMessage);
    }
    finally
    {
        activeCommands = Interlocked.Decrement(ref _activeCommandCount);
        IsBusy = activeCommands > 0;
        RefreshProductionCommands();
    }
}

private void ApplyCommandResult(
    ProductionCommandDisposition disposition,
    RunRejection? rejection)
{
    if (disposition == ProductionCommandDisposition.Rejected)
    {
        LastMessage = rejection is null
            ? "生产检测正在申请执行权"
            : ProductionRunUiState.FormatRejection(rejection);
    }
    else if (disposition == ProductionCommandDisposition.Canceled)
    {
        LastMessage = "生产检测已取消";
    }
}

private ProductionRunUiState CurrentUiState() =>
    ProductionRunUiState.Create(
        _productionSnapshot.State,
        _inspectionExecution.Current,
        _productionSnapshot.ActiveSessionId,
        Volatile.Read(ref _activeCommandCount) > 0);

private bool CanRunSingle() => CurrentUiState().CanRunSingle;
private bool CanStart() => CurrentUiState().CanStart;
private bool CanStop() => CurrentUiState().CanStop;

private void RefreshExecutionPresentation()
{
    var state = CurrentUiState();
    if (state.IsExternallyOccupied)
    {
        LastMessage = state.OccupancyText;
        _showingExecutionOccupancy = true;
    }
    else if (_showingExecutionOccupancy)
    {
        var current = _inspectionExecution.Current;
        LastMessage = current is null
            ? "检测执行占用已释放"
            : $"{current.Intent.Mode.DisplayName}已取得检测执行权";
        _showingExecutionOccupancy = false;
    }

    RefreshProductionCommands();
}

private void RefreshProductionCommands()
{
    RunSingleCommand.RaiseCanExecuteChanged();
    StartCommand.RaiseCanExecuteChanged();
    StopCommand.RaiseCanExecuteChanged();
}
```

删除旧 `IsBusy` 入口判断。计数器避免 RunSingle 与 Stop 两个 wrapper 重叠时，先完成的命令错误清除 Busy；Async command 和全局 seam 共同负责互斥。

- [ ] **Step 8: 迁移 Shell 和 XAML**

`ShellWindowViewModel` 增加 `using VisionStation.Client.Presentation;` 和字段：

```csharp
private readonly IInspectionExecution _inspectionExecution;
private ProductionSnapshot _productionSnapshot;
```

把构造函数签名替换为：

```csharp
public ShellWindowViewModel(
    IRegionManager regionManager,
    ProductionCoordinator coordinator,
    IInspectionExecution inspectionExecution,
    IUiDispatcher uiDispatcher,
    ICameraDevice camera,
    IPlcClient plc,
    IAxisController axis,
    IAlarmService alarms,
    IUnsavedChangesService unsavedChanges)
```

在赋值区增加：

```csharp
_inspectionExecution = inspectionExecution;
_productionSnapshot = coordinator.Snapshot;
```

用下面代码替换现有 `coordinator.SnapshotChanged` 订阅，增加 execution 订阅，并在构造函数设备快照初始化之后调用一次刷新：

```csharp
coordinator.SnapshotChanged += (_, snapshot) => _uiDispatcher.Invoke(() =>
{
    _productionSnapshot = snapshot;
    RefreshProductionPresentation();
});
_inspectionExecution.Changed += (_, _) =>
    _uiDispatcher.Invoke(RefreshProductionPresentation);

RefreshProductionPresentation();
```

Snapshot/Changed 任一变化都调用：

```csharp
private void RefreshProductionPresentation()
{
    var state = ProductionRunUiState.Create(
        _productionSnapshot.State,
        _inspectionExecution.Current,
        _productionSnapshot.ActiveSessionId,
        commandBusy: false);
    ProductionStateText = state.StateText;
    ProductionStateBrush = state.StateBrush;
}
```

外部占用时 `ProductionRunUiState.StateText` 已包含 Mode 和 EntryPoint，所以 Shell 现有 `ProductionStateText` 绑定无需新增模式分支。

把 Stop 按钮 tooltip 精确改为：

```xml
ToolTip="停止当前生产检测"
```

把文件末尾的加载遮罩从整个页面限制到左侧检测内容区，确保 52px 高的命令栏始终可以点击：

```xml
<controls:SimpleLoadingOverlay Grid.Column="0"
                               Margin="0,52,10,0"
                               Panel.ZIndex="20"
                               IsActive="{Binding IsBusy}" />
```

- [ ] **Step 9: 验证、提交、推送**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~ProductionRunUiStateTests|FullyQualifiedName~ProductionViewModelContractTests|FullyQualifiedName~ProductionRunXamlTests"
dotnet build .\VisionStation.Client\VisionStation.Client.csproj -c Debug --nologo
git diff --check
git add VisionStation.Client VisionStation.Vision.UI.Tests
git commit -m "feat: expose production ownership in the UI"
git push -u origin HEAD
```

## Task 7: 迁移配方试运行并保证零副作用拒绝

**Files:**

- Create: `VisionStation.Vision.UI.Tests/RecipeManagementInspectionExecutionTests.cs`
- Create: `VisionStation.Vision.UI.Tests/RecipeManagementTestHarness.cs`
- Create: `VisionStation.Vision.UI.Tests/FlowEditorDialogContractTests.cs`
- Modify: `VisionStation.Vision.UI/Services/IFlowEditorDialogService.cs`
- Modify: `VisionStation.Vision.UI/Services/WpfFlowEditorDialogService.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/VisionDebugViewModel.cs:95-1387`
- Modify: `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs:21-128,958-1068,1183-1212,1795-1848`

- [ ] **Step 1: 写拒绝、Reset 和页面离开测试**

按 “Appendix B” 创建 `RecipeManagementTestHarness.cs`，然后创建测试：

```csharp
using VisionStation.Application;
using VisionStation.Client.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class RecipeManagementInspectionExecutionTests
{
    [Fact]
    public async Task Known_external_session_disables_test_run_until_released()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var active = RecipeManagementTestHarness.Active(
            InspectionRunModes.Continuous,
            nameof(ProductionCoordinator));

        harness.Execution.PublishCurrent(active);
        Assert.False(harness.ViewModel.TestRunRecipeCommand.CanExecute());

        harness.Execution.PublishCurrent(null);
        Assert.True(harness.ViewModel.TestRunRecipeCommand.CanExecute());
    }

    [Fact]
    public async Task Navigation_return_refreshes_occupancy_that_changed_while_away()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        harness.ViewModel.OnNavigatedFrom(null!);
        harness.Execution.PublishCurrent(RecipeManagementTestHarness.Active(
            InspectionRunModes.Continuous,
            nameof(ProductionCoordinator)));

        harness.ViewModel.OnNavigatedTo(null!);

        Assert.False(harness.ViewModel.TestRunRecipeCommand.CanExecute());
        harness.Execution.PublishCurrent(null);
    }

    [Fact]
    public async Task Rejected_run_does_not_save_switch_connect_or_begin_run_control()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var active = RecipeManagementTestHarness.Active(
            InspectionRunModes.Continuous,
            nameof(ProductionCoordinator));
        harness.Execution.TryBeginHandler = _ => new RunAdmission.Rejected(
            new RunRejection(RunRejectionReason.Busy, active));

        await harness.ViewModel.TestRunRecipeCommand.Execute();

        Assert.Equal(1, harness.Execution.TryBeginCount);
        Assert.Equal(0, harness.Recipes.SaveCount);
        Assert.Equal(0, harness.Recipes.SetCurrentCount);
        Assert.Equal(0, harness.Channels.ConnectCount);
        Assert.Equal(0, harness.RunControl.BeginCount);
        Assert.Contains("连续生产", harness.ViewModel.StatusText);
        Assert.Equal(InspectionRunModes.RecipeTest, harness.Execution.LastIntent?.Mode);
        Assert.Equal(
            nameof(RecipeManagementViewModel),
            harness.Execution.LastIntent?.EntryPoint);
    }

    [Fact]
    public async Task Paused_run_can_reenter_async_command_only_to_resume()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var entered = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.ViewModel.PauseTestRunCommand.Execute();

        Assert.True(harness.ViewModel.IsTestRunPaused);
        Assert.True(harness.ViewModel.TestRunRecipeCommand.CanExecute());
        await harness.ViewModel.TestRunRecipeCommand.Execute();
        Assert.False(harness.ViewModel.IsTestRunPaused);
        Assert.False(harness.RunControl.IsPaused);

        harness.ViewModel.OnNavigatedFrom(null!);
        await running.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Reset_during_persist_reuses_session_and_restarts_attempt()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var saveEntered = RecipeManagementTestHarness.NewSignal();
        var saveAttempt = 0;
        harness.Recipes.SaveHandler = async (_, token) =>
        {
            if (Interlocked.Increment(ref saveAttempt) == 1)
            {
                saveEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.ViewModel.ResetTestRunCommand.Execute();
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, harness.Execution.TryBeginCount);
        Assert.Equal(2, harness.Recipes.SaveCount);
        Assert.Single(harness.Session.Requests);
        Assert.Equal(1, harness.Session.DisposeCount);
    }

    [Fact]
    public async Task Reset_reuses_one_session_for_two_attempts_and_disposes_once()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var firstEntered = RecipeManagementTestHarness.NewSignal();
        var attempt = 0;
        harness.Session.Handler = async (_, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                firstEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.ViewModel.ResetTestRunCommand.Execute();
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, harness.Execution.TryBeginCount);
        Assert.Equal(2, harness.Session.Requests.Count);
        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.Equal(1, harness.Channels.DisconnectCount);
    }

    [Fact]
    public async Task Navigation_away_cancels_lifetime_and_releases_session_once()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var entered = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.ViewModel.OnNavigatedFrom(null!);
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.Equal(1, harness.Channels.DisconnectCount);
        Assert.Contains("已取消", harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Navigation_cancel_wins_over_late_reset_exception()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var entered = RecipeManagementTestHarness.NewSignal();
        var cancellationObserved = RecipeManagementTestHarness.NewSignal();
        var allowResetException = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
                await allowResetException.Task;
                throw new InspectionRunResetException();
            }

            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.ViewModel.ResetTestRunCommand.Execute();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.ViewModel.OnNavigatedFrom(null!);
        allowResetException.TrySetResult(true);
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(harness.Session.Requests);
        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.Contains("已取消", harness.ViewModel.StatusText);
    }
}
```

创建 `FlowEditorDialogContractTests.cs`，锁定不向配方页泄漏巨大 ViewModel，并锁定延迟解析构造环：

```csharp
using VisionStation.Client.ViewModels;
using VisionStation.Vision.UI.Services;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class FlowEditorDialogContractTests
{
    [Fact]
    public void Dialog_service_uses_lazy_workspace_and_recipe_vm_has_no_debug_vm_dependency()
    {
        var dialogConstructor = Assert.Single(
            typeof(WpfFlowEditorDialogService).GetConstructors());
        Assert.Equal(
            typeof(Lazy<VisionDebugViewModel>),
            Assert.Single(dialogConstructor.GetParameters()).ParameterType);

        var throwingLazy = new Lazy<VisionDebugViewModel>(
            () => throw new InvalidOperationException("must stay lazy"));
        _ = new WpfFlowEditorDialogService(throwingLazy);
        Assert.False(throwingLazy.IsValueCreated);

        var show = typeof(IFlowEditorDialogService).GetMethod("ShowEditorAsync")!;
        var recipeId = show.GetParameters()[0];
        Assert.True(recipeId.HasDefaultValue);
        Assert.Null(recipeId.DefaultValue);
        Assert.NotNull(typeof(VisionDebugViewModel).GetMethod(
            nameof(VisionDebugViewModel.EnsureInitializedAsync)));

        var recipeConstructor = Assert.Single(
            typeof(RecipeManagementViewModel).GetConstructors());
        Assert.DoesNotContain(
            recipeConstructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(VisionDebugViewModel));
    }
}
```

- [ ] **Step 2: 运行配方 Red**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~RecipeManagementInspectionExecutionTests|FullyQualifiedName~FlowEditorDialogContractTests"
```

Expected: compile/behavior failure，命令仍为 `DelegateCommand`、VM 仍依赖裸 Runner，且无导航取消。

- [ ] **Step 3: 深化 Flow Editor interface，移除巨大 VM 构造依赖**

把 `IFlowEditorDialogService` 改为：

```csharp
namespace VisionStation.Vision.UI.Services;

/// <summary>按配方打开共享视觉流程编辑工作区。</summary>
public interface IFlowEditorDialogService
{
    /// <summary>打开共享流程编辑器；指定配方时先串行加载，null 时保留当前编辑状态。</summary>
    Task ShowEditorAsync(
        string? recipeId = null,
        CancellationToken cancellationToken = default);
}
```

`WpfFlowEditorDialogService` 注入 `Lazy<VisionDebugViewModel>`，延迟解析用来打破 `VisionDebugViewModel -> IFlowEditorDialogService -> VisionDebugViewModel` 的构造环；用下面的字段、构造器和方法替换现有实现，现有 `GetOwner()` 原样保留：

```csharp
private readonly Lazy<VisionDebugViewModel> _viewModel;

public WpfFlowEditorDialogService(Lazy<VisionDebugViewModel> viewModel)
{
    _viewModel = viewModel;
}

public async Task ShowEditorAsync(
    string? recipeId = null,
    CancellationToken cancellationToken = default)
{
    var viewModel = _viewModel.Value;
    if (string.IsNullOrWhiteSpace(recipeId))
    {
        await viewModel.EnsureInitializedAsync(cancellationToken);
    }
    else
    {
        await viewModel.LoadRecipeAsync(recipeId, cancellationToken);
    }

    var existing = System.Windows.Application.Current.Windows
        .OfType<FlowEditorWindow>()
        .FirstOrDefault();
    if (existing is not null)
    {
        if (existing.WindowState == WindowState.Minimized)
        {
            existing.WindowState = WindowState.Normal;
        }

        existing.Activate();
        return;
    }

    var window = new FlowEditorWindow(viewModel)
    {
        Owner = GetOwner()
    };
    window.Show();
}
```

在 `VisionDebugViewModel` 增加字段：

```csharp
private readonly SemaphoreSlim _recipeLoadGate = new(1, 1);
private readonly Task _initialization;
```

把构造函数末尾的 `_ = LoadRecipeAsync();` 替换为 `_initialization = InitializeRecipeAsync();`。把现有 `LoadRecipeAsync` 重命名为 `LoadRecipeCoreAsync`、改为 private 并增加 token 参数：

```csharp
private async Task LoadRecipeCoreAsync(
    string? recipeId,
    CancellationToken cancellationToken)
```

在该 core 方法 opening brace 后插入：

```csharp
await _recipeLoadGate.WaitAsync(cancellationToken);
try
{
```

把原有配方查询表达式替换为：

```csharp
var recipe = string.IsNullOrWhiteSpace(recipeId)
    ? await _recipes.GetCurrentAsync(cancellationToken)
    : await _recipes.GetAsync(recipeId, cancellationToken)
        ?? await _recipes.GetCurrentAsync(cancellationToken);
```

原方法从下一句 `recipe = recipe.WithNormalizedFlows();` 到原 closing brace 的每条语句只增加一级缩进、不改内容；在原 closing brace 位置改为：

```csharp
}
finally
{
    _recipeLoadGate.Release();
}
```

随后新增完整的安全初始化和 public wrapper：

```csharp
private async Task InitializeRecipeAsync()
{
    try
    {
        await LoadRecipeCoreAsync(null, CancellationToken.None);
    }
    catch (Exception ex)
    {
        _log.Error("VisionDebug", $"Initial recipe load failed: {ex.Message}");
    }
}

/// <summary>等待首次配方加载完成，不重新投影当前编辑状态。</summary>
public Task EnsureInitializedAsync(
    CancellationToken cancellationToken = default) =>
    _initialization.WaitAsync(cancellationToken);

public async Task LoadRecipeAsync(
    string? recipeId = null,
    CancellationToken cancellationToken = default)
{
    await EnsureInitializedAsync(cancellationToken);
    await LoadRecipeCoreAsync(recipeId, cancellationToken);
}
```

这样首次 Lazy 解析只启动一个可观察的初始化 Task；指定 recipe 的调用等待它后串行加载。`LoadRecipeAsync(null)` 仍保留“重新加载当前配方”的旧语义；只有 dialog 的 null 分支调用 `EnsureInitializedAsync`，因此从 VisionDebug 自身打开窗口不会重置当前 `SelectedVisionFlow`。

同时将 `VisionDebugViewModel.OpenFlowEditorCommand` 的类型改为 `AsyncDelegateCommand<object>`，两个创建点分别改为 `new AsyncDelegateCommand<object>(OpenFlowEditorAsync)` 和 `new AsyncDelegateCommand(async () => await OpenFlowEditorAsync(flow))`，方法改为：

```csharp
private async Task OpenFlowEditorAsync(object? item)
{
    if (item is VisionFlowItem flow &&
        !string.Equals(flow.Id, _activeFlowId, StringComparison.OrdinalIgnoreCase))
    {
        SelectedVisionFlow = flow;
    }

    if (_currentRecipe is not null)
    {
        await _flowEditorDialog.ShowEditorAsync();
    }
}
```

Recipe VM 删除 `VisionDebugViewModel` 字段和构造参数；打开编辑器改为：

```csharp
await _flowEditorDialog.ShowEditorAsync(recipe.Id);
```

- [ ] **Step 4: 将试运行命令改为可等待的 async command**

增加 `using Prism.Navigation.Regions;` 和 `using VisionStation.Client.Presentation;`，类声明增加 Prism 导航 interface：

```csharp
public sealed class RecipeManagementViewModel : BindableBase, INavigationAware
```

字段替换为：

```csharp
private readonly IInspectionExecution _inspectionExecution;
private CancellationTokenSource? _testRunLifetimeCancellation;
private CancellationTokenSource? _testRunAttemptCancellation;
private bool _inspectionExecutionSubscribed;
```

把构造参数 `IInspectionRunner inspectionRunner` 精确替换为 `IInspectionExecution inspectionExecution`，把赋值 `_inspectionRunner = inspectionRunner;` 替换为 `_inspectionExecution = inspectionExecution;`。命令改为：

```csharp
TestRunRecipeCommand = new AsyncDelegateCommand(
    TestRunRecipeAsync,
    CanTestRun)
    .EnableParallelExecution();
```

属性类型同步改为 `AsyncDelegateCommand`。`EnableParallelExecution` 只允许暂停状态再次进入同一命令的 Resume 分支；初始运行仍由下面的谓词和全局 Session 双重保护：

```csharp
private bool CanTestRun() =>
    !IsBusy &&
    (IsTestRunPaused ||
     (!IsTestRunning && _inspectionExecution.Current is null));
```

构造函数末尾调用 `SubscribeInspectionExecution()`，保证已知外部占用时按钮立即禁用，同时允许导航离开时解除 singleton event 引用：

```csharp
private void SubscribeInspectionExecution()
{
    if (_inspectionExecutionSubscribed)
    {
        return;
    }

    _inspectionExecution.Changed += OnInspectionExecutionChanged;
    _inspectionExecutionSubscribed = true;
    RaiseCommandStates();
}

private void UnsubscribeInspectionExecution()
{
    if (!_inspectionExecutionSubscribed)
    {
        return;
    }

    _inspectionExecution.Changed -= OnInspectionExecutionChanged;
    _inspectionExecutionSubscribed = false;
}

private void OnInspectionExecutionChanged(
    object? sender,
    InspectionExecutionChangedEventArgs args)
{
    _uiDispatcher.Invoke(RaiseCommandStates);
}
```

- [ ] **Step 5: 用一个 Session 覆盖完整试运行生命周期**

用下列方法替换 `TestRunRecipeAsync`；保留现有状态日志和结果快照 helper：

```csharp
private async Task TestRunRecipeAsync()
{
    if (IsTestRunPaused)
    {
        ResumeTestRun();
        return;
    }

    if (IsTestRunning || _loadedRecipe is null)
    {
        if (_loadedRecipe is null)
        {
            StatusText = "请先选择一个配方，再试运行流程";
        }
        return;
    }

    var admission = _inspectionExecution.TryBegin(new InspectionRunIntent(
        InspectionRunModes.RecipeTest,
        nameof(RecipeManagementViewModel)));
    if (admission is RunAdmission.Rejected rejected)
    {
        StatusText = ProductionRunUiState.FormatRejection(rejected.Rejection);
        TestRunStateText = StatusText;
        RaiseCommandStates();
        return;
    }

    await using var session = ((RunAdmission.Acquired)admission).Session;
    using var lifetime = new CancellationTokenSource();
    _testRunLifetimeCancellation = lifetime;
    var recipeName = RecipeName;
    var runControlStarted = false;
    IsTestRunning = true;
    IsTestRunPaused = false;
    RaiseCommandStates();
    try
    {
        var restartRequested = false;
        do
        {
            restartRequested = false;
            _testRunResetRequested = false;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.Token);
            _testRunAttemptCancellation = attempt;
            _inspectionRunControl.BeginRun();
            runControlStarted = true;
            try
            {
                _log.Info("Recipe", $"试运行：正在保存配方 {recipeName} 并启动流程");
                var recipe = await PersistSelectedRecipeAsync(
                    setCurrentRecipe: true,
                    refreshList: false,
                    attempt.Token);
                if (recipe is null)
                {
                    StatusText = "试运行取消：当前配方保存失败";
                    _log.Warning("Recipe", StatusText);
                    return;
                }

                recipeName = recipe.Name;
                ResetProcessStepRuntimeStates(prepareForRun: true);
                StatusText = $"正在试运行 {recipe.Name}";
                TestRunStateText = StatusText;
                _log.Info("Recipe", $"Test run started for recipe {recipe.Name}");
                await _communicationChannels.ConnectAsync(
                    CommunicationChannelConnectionPolicies.Production,
                    attempt.Token);
                var run = await session.ExecuteAsync(new InspectionRequest
                {
                    RecipeId = recipe.Id,
                    BatchId = DateTimeOffset.Now.ToString("yyyyMMdd"),
                    OperatorName = Environment.UserName,
                    TriggeredByPlc = false,
                    ProcessOnly = true
                }, attempt.Token);
                StatusText = $"试运行完成：{run.Result.Outcome}，耗时 {run.Result.CycleTime.TotalMilliseconds:0} ms";
                TestRunStateText = StatusText;
                AddTestRunResultSnapshot(run);
                _log.Info("Recipe", $"Test run completed for recipe {recipe.Name}: {run.Result.Outcome}");
            }
            catch (InspectionRunResetException) when (
                !lifetime.IsCancellationRequested)
            {
                restartRequested = true;
            }
            catch (OperationCanceledException) when (
                _testRunResetRequested && !lifetime.IsCancellationRequested)
            {
                restartRequested = true;
            }
            finally
            {
                _testRunAttemptCancellation = null;
            }

            if (restartRequested)
            {
                IsTestRunPaused = false;
                ResetRecipeVariablesToDefaults();
                ResetProcessStepRuntimeStates(prepareForRun: true);
                StatusText = "流程已复位，正在从第一步重新开始";
                TestRunStateText = StatusText;
                AddTestRunLog("INFO", "Recipe", StatusText);
                _log.Info("Recipe", StatusText);
            }
        }
        while (restartRequested);
    }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
    {
        StatusText = "配方试运行已取消";
        TestRunStateText = StatusText;
    }
    catch (Exception ex)
    {
        StatusText = $"试运行失败：{ex.Message}";
        TestRunStateText = StatusText;
        MarkActiveRuntimeStepFailed(ex.Message);
        _log.Error("Recipe", $"Test run failed for recipe {recipeName}: {ex.Message}");
    }
    finally
    {
        if (runControlStarted)
        {
            _inspectionRunControl.EndRun();
        }

        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _communicationChannels.DisconnectAsync(
                CommunicationChannelConnectionPolicies.Production,
                cleanup.Token);
        }
        catch (Exception ex)
        {
            _log.Warning("Recipe", $"试运行通信清理失败：{ex.Message}");
        }

        _testRunAttemptCancellation = null;
        _testRunLifetimeCancellation = null;
        _testRunResetRequested = false;
        IsTestRunPaused = false;
        IsTestRunning = false;
        RaiseCommandStates();
    }
}
```

修改 Persist helper：

```csharp
private async Task<Recipe?> PersistSelectedRecipeAsync(
    bool setCurrentRecipe,
    bool refreshList,
    CancellationToken cancellationToken = default)
```

在 helper 中做三处精确替换：

```csharp
await _recipes.SaveAsync(recipe, cancellationToken);
await _recipes.SetCurrentRecipeAsync(recipe.Id, cancellationToken);
var currentRecipeId = setCurrentRecipe
    ? recipe.Id
    : await _recipes.GetCurrentRecipeIdAsync(cancellationToken);
```

第三段替换原有单行条件表达式；`refreshList: false` 的试运行路径因此从保存到切换都响应 Attempt/Lifetime 取消。

- [ ] **Step 6: Reset 只取消 attempt，导航离开取消 lifetime**

Reset 运行分支替换旧 CTS：

```csharp
_testRunResetRequested = true;
_inspectionRunControl.RequestReset();
_testRunAttemptCancellation?.Cancel();
```

实现导航 interface：

```csharp
public void OnNavigatedTo(NavigationContext navigationContext)
{
    SubscribeInspectionExecution();
}

public bool IsNavigationTarget(NavigationContext navigationContext) => true;

public void OnNavigatedFrom(NavigationContext navigationContext)
{
    _testRunLifetimeCancellation?.Cancel();
    UnsubscribeInspectionExecution();
}
```

- [ ] **Step 7: 运行 Green、Client build、提交推送**

Run:

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~RecipeManagementInspectionExecutionTests|FullyQualifiedName~FlowEditorDialogContractTests"
dotnet build .\VisionStation.Client\VisionStation.Client.csproj -c Debug --nologo
git diff --check
git add VisionStation.Client VisionStation.Vision.UI VisionStation.Vision.UI.Tests
git commit -m "feat: guard recipe test runs with inspection sessions"
git push -u origin HEAD
```

## Task 8: 移除裸 Runner seam 并完成事件迁移

**Files:**

- Modify: `VisionStation.Application.Tests/InspectionRunnerContractTests.cs`
- Modify: `VisionStation.Application.Tests/InspectionRunnerRecipeResolutionTests.cs`
- Modify: `VisionStation.Application/InspectionServices.cs:17-22,41,99-101,170`
- Modify: `VisionStation.Application/Inspection/Execution/InspectionExecution.cs`
- Modify: `VisionStation.Client/App.xaml.cs:102-104`
- Modify: `VisionStation.Client/ViewModels/VariableCenterViewModel.cs:24-76,730,1237`
- Create: `VisionStation.Vision.UI.Tests/VariableCenterInspectionExecutionTests.cs`

- [ ] **Step 1: 写封装 Red**

在 Application contract 测试增加：

```csharp
[Fact]
public void Raw_inspection_runner_is_not_a_public_contract()
{
    var assembly = typeof(IInspectionExecution).Assembly;
    var runner = assembly.GetType(
        "VisionStation.Application.InspectionRunner",
        throwOnError: true)!;

    Assert.Null(assembly.GetType("VisionStation.Application.IInspectionRunner"));
    Assert.False(runner.IsPublic);
}
```

在 `VariableCenterInspectionExecutionTests.cs` 增加：

```csharp
using System.Runtime.CompilerServices;
using VisionStation.Application;
using VisionStation.Client.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VariableCenterInspectionExecutionTests
{
    [Fact]
    public void VariableCenter_observes_the_execution_module_not_raw_runner()
    {
        var constructor = Assert.Single(typeof(VariableCenterViewModel).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Contains(parameters, parameter =>
            parameter.ParameterType == typeof(IInspectionExecution));
        Assert.DoesNotContain(parameters, parameter =>
            parameter.ParameterType.Name == "IInspectionRunner");
    }

    [Fact]
    public void VariableCenter_subscribes_and_unsubscribes_module_result_event()
    {
        var source = File.ReadAllText(GetViewModelPath());

        Assert.Contains(
            "_inspectionExecution.RunCompleted += OnInspectionCompleted;",
            source);
        Assert.Contains(
            "_inspectionExecution.RunCompleted -= OnInspectionCompleted;",
            source);
    }

    private static string GetViewModelPath(
        [CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "VisionStation.Client",
            "ViewModels",
            "VariableCenterViewModel.cs"));
}
```

- [ ] **Step 2: 运行封装 Red**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~InspectionExecutionContractTests"
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~VariableCenterInspectionExecutionTests"
```

Expected: FAIL；旧 interface 和 public Runner 仍存在，VariableCenter 仍注入旧类型。

- [ ] **Step 3: 将 Runner 降为内部 executor**

在 `InspectionServices.cs` 做以下四个精确机械修改：

1. 删除从 `public interface IInspectionRunner` 开始到该 interface 结束的整个定义。
2. 将 `public sealed class InspectionRunner : IInspectionRunner` 替换为 `internal sealed class InspectionRunner : IInspectionExecutor`。
3. 将方法签名 `public async Task<InspectionRunResult> RunAsync(` 替换为 `public async Task<InspectionRunResult> ExecuteAsync(`；参数列表和方法体其余语句不变。
4. 删除 `public event EventHandler<InspectionRunResult>? RunCompleted;` 和方法末尾的 `RunCompleted?.Invoke(this, runResult);`，仍直接 `return runResult;`。
5. 把 `VisionStation.Application.Tests/InspectionRunnerRecipeResolutionTests.cs` 中对真实 Runner 的所有 `RunAsync` 调用同步改为 `ExecuteAsync`；该测试程序集已有 `InternalsVisibleTo`，不需要反射或兼容 adapter。

结果事件只由 `InspectionExecution` 安全发布。

在 `InspectionExecution` 删除整个迁移期 `InspectionExecution(IInspectionRunner, IAppLogService)` 构造器和 `LegacyInspectionRunnerExecutor` nested class，换成最终生产构造器：

在文件顶部与 `using VisionStation.Domain;` 并列增加：

```csharp
using VisionStation.Devices;
using VisionStation.Vision;
```

```csharp
public InspectionExecution(
    ICameraDevice camera,
    IConfigurableCameraDevice configurableCamera,
    IAxisController axis,
    IPlcClient plc,
    IDeviceRuntime devices,
    DeviceConfiguration configuration,
    IDeviceConfigurationRepository configurationRepository,
    IVisionPipeline pipeline,
    IRecipeRepository recipes,
    IInspectionRecordRepository records,
    IImageTraceStore traceStore,
    IAppLogService log,
    ICommunicationChannelRuntime communicationChannels,
    IInspectionRunControl runControl)
    : this(
        new InspectionRunner(
            camera,
            configurableCamera,
            axis,
            plc,
            devices,
            configuration,
            configurationRepository,
            pipeline,
            recipes,
            records,
            traceStore,
            log,
            communicationChannels,
            runControl),
        log)
{
}
```

`InspectionRunner` 此时已直接满足内部 `IInspectionExecutor`，不再需要任何兼容 adapter。

- [ ] **Step 4: 迁移 VariableCenter 和最终 DI**

VariableCenter 做三处精确替换：字段 `_inspectionRunner` 改名为 `_inspectionExecution` 且类型改为 `IInspectionExecution`；构造参数 `IInspectionRunner inspectionRunner` 改为 `IInspectionExecution inspectionExecution`；赋值改为 `_inspectionExecution = inspectionExecution;`。最终字段为：

```csharp
private readonly IInspectionExecution _inspectionExecution;
```

订阅/退订改为：

```csharp
_inspectionExecution.RunCompleted += OnInspectionCompleted;
_inspectionExecution.RunCompleted -= OnInspectionCompleted;
```

App 组合根删除旧注册，只保留：

```csharp
containerRegistry.RegisterSingleton<IInspectionExecution, InspectionExecution>();
containerRegistry.RegisterSingleton<ProductionCoordinator>();
```

全仓验证没有业务代码引用旧 interface：

```powershell
rg -n "IInspectionRunner|\.RunAsync\(new InspectionRequest" .\VisionStation.Application .\VisionStation.Client .\VisionStation.Vision.UI -g "*.cs"
```

Expected: `IInspectionRunner` 无结果；检测执行只通过 `session.ExecuteAsync`。

- [ ] **Step 5: 运行 Green 和完整编译**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Debug
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Debug --filter "FullyQualifiedName~VariableCenterInspectionExecutionTests"
dotnet build .\CVWork.sln -c Debug --nologo
```

Expected: PASS；解决方案无 compile error。

- [ ] **Step 6: 提交并推送**

Run:

```powershell
git diff --check
git add VisionStation.Application VisionStation.Application.Tests VisionStation.Client VisionStation.Vision.UI.Tests
git commit -m "refactor: hide raw inspection runner"
git push -u origin HEAD
```

## Task 9: 编写二次开发指南并完成全量验证

**Files:**

- Create: `docs/development/inspection-execution.md`
- Modify: public contracts in `InspectionExecutionContracts.cs` only if XML docs self-review finds missing invariant

- [ ] **Step 1: 创建二次开发指南**

文件必须包含下面的完整结构和可复制示例：

````markdown
# 检测执行二次开发指南

## 唯一入口

业务代码只注入 `IInspectionExecution`。不要注入、创建或复制内部 Runner、锁或 Session 状态。

## 核心不变量

- 全进程最多一个 `IInspectionSession`。
- `TryBegin` 立即返回 Acquired/Rejected，永不排队。
- 同一 Session 可以顺序执行多次，但不能并发执行。
- Session 必须使用 `await using`；Dispose 幂等并等待当前执行结束。
- 正常取消不是故障；真实异常由业务编排 module 记录和呈现。

## 新增单次入口

```csharp
using VisionStation.Application;
using VisionStation.Domain;

namespace InspectionExtensionExample;

public sealed class CalibrationInspectionService
{
    private static readonly InspectionRunMode CalibrationTest =
        new("calibration.test", "标定试运行");
    private readonly IInspectionExecution _execution;
    private readonly Action<string> _showStatus;

    public CalibrationInspectionService(
        IInspectionExecution execution,
        Action<string> showStatus)
    {
        _execution = execution;
        _showStatus = showStatus;
    }

    public async Task<InspectionRunResult?> RunAsync(
        InspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var admission = _execution.TryBegin(new InspectionRunIntent(
            CalibrationTest,
            nameof(CalibrationInspectionService)));
        if (admission is RunAdmission.Rejected rejected)
        {
            var active = rejected.Rejection.Active;
            _showStatus(
                $"{active.Intent.Mode.DisplayName}（{active.Intent.EntryPoint}）正在占用检测执行");
            return null;
        }

        await using var session = ((RunAdmission.Acquired)admission).Session;
        return await session.ExecuteAsync(request, cancellationToken);
    }
}
```

## 循环执行

```csharp
using VisionStation.Application;
using VisionStation.Domain;

namespace InspectionExtensionExample;

public static class BatchInspectionService
{
    public static async Task RunAsync(
        IInspectionExecution execution,
        Func<InspectionRequest> requestFactory,
        Action<string> showStatus,
        CancellationToken cancellationToken)
    {
        var mode = new InspectionRunMode("batch.preview", "批量预检");
        var admission = execution.TryBegin(new InspectionRunIntent(
            mode,
            nameof(BatchInspectionService)));
        if (admission is RunAdmission.Rejected rejected)
        {
            var active = rejected.Rejection.Active;
            showStatus(
                $"{active.Intent.Mode.DisplayName}（{active.Intent.EntryPoint}）正在占用检测执行");
            return;
        }

        await using var session = ((RunAdmission.Acquired)admission).Session;
        while (!cancellationToken.IsCancellationRequested)
        {
            await session.ExecuteAsync(requestFactory(), cancellationToken);
        }
    }
}
```

## Reset 重跑

```csharp
using VisionStation.Application;
using VisionStation.Domain;

namespace InspectionExtensionExample;

public sealed class RecipePreviewService
{
    private readonly IInspectionExecution _execution;
    private CancellationTokenSource? _attemptCancellation;
    private long _resetVersion;

    public RecipePreviewService(IInspectionExecution execution)
    {
        _execution = execution;
    }

    public void RequestReset()
    {
        Interlocked.Increment(ref _resetVersion);
        try
        {
            Volatile.Read(ref _attemptCancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task<InspectionRunResult?> RunAsync(
        Func<InspectionRequest> requestFactory,
        CancellationToken lifetimeToken)
    {
        var admission = _execution.TryBegin(new InspectionRunIntent(
            InspectionRunModes.RecipeTest,
            nameof(RecipePreviewService)));
        if (admission is RunAdmission.Rejected)
        {
            return null;
        }

        await using var session = ((RunAdmission.Acquired)admission).Session;
        while (true)
        {
            var observedReset = Volatile.Read(ref _resetVersion);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken);
            Volatile.Write(ref _attemptCancellation, attempt);
            if (Volatile.Read(ref _resetVersion) != observedReset)
            {
                attempt.Cancel();
            }

            try
            {
                return await session.ExecuteAsync(
                    requestFactory(),
                    attempt.Token);
            }
            catch (OperationCanceledException) when (
                Volatile.Read(ref _resetVersion) != observedReset &&
                !lifetimeToken.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _attemptCancellation,
                    null,
                    attempt);
            }
        }
    }
}
```

## UI 接入

订阅 `IInspectionExecution.Changed`，切回 UI dispatcher 后刷新命令。每个有 Stop 生命周期的编排器都要保存自己取得的 `session.Run.SessionId` 并与 `Current?.SessionId` 比较；`ProductionSnapshot.ActiveSessionId` 只是 ProductionCoordinator 对该通用所有权规则的投影，不通过 Mode Key 猜测。

## 新模式检查清单

1. Key 使用稳定小写标识。
2. DisplayName 是用户可读文本。
3. EntryPoint 使用调用 module/ViewModel 名称。
4. Rejected 在任何保存、设备连接或副作用之前处理。
5. 添加 seam 行为测试和取消/释放测试。

## 禁止事项

- 不直接调用裸 Runner。
- 不复制全局锁。
- 不等待 Busy Session。
- 不在核心 implementation 中按 Mode 写 switch。
- 不用软件取消替代硬件急停。
````

在示例后明确说明：`Action<string> showStatus` 是调用方注入的 UI/日志呈现函数，不属于 `IInspectionExecution`；二次开发者可按自身界面框架替换它。

- [ ] **Step 2: 自审 interface 与指南一致性**

Run:

```powershell
rg -n "placeholder|NotImplementedException|IInspectionRunner|SemaphoreSlim" .\docs\development\inspection-execution.md .\VisionStation.Application\Inspection\Execution
rg -n "public (interface|sealed record|enum|readonly record struct)" .\VisionStation.Application\Inspection\Execution\InspectionExecutionContracts.cs
```

Expected: 无占位文本、未实现异常或旧 Runner；公开类型均有 XML summary，指南使用的类型名与源码一致。

- [ ] **Step 3: 运行针对性测试**

Run:

```powershell
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~InspectionExecution|FullyQualifiedName~ProductionCoordinator|FullyQualifiedName~ProductionSettingsConfiguration"
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ProductionRun|FullyQualifiedName~ProductionViewModel|FullyQualifiedName~RecipeManagementInspectionExecution|FullyQualifiedName~FlowEditorDialogContract|FullyQualifiedName~VariableCenterInspectionExecution"
```

Expected: 所有新增测试 PASS，0 failed。

- [ ] **Step 4: 运行完整 Release 验证**

Run:

```powershell
dotnet test .\CVWork.sln -c Release --nologo
dotnet build .\CVWork.sln -c Release --no-restore --nologo
git diff --check
git status --short --branch
```

Expected:

```text
所有测试通过，0 failed，0 skipped
Build succeeded，0 warnings，0 errors
git diff --check 无输出
只有本任务预期文件待提交
```

- [ ] **Step 5: 使用 requesting-code-review 检查规格覆盖**

审阅者必须逐项核对：全局唯一、立即拒绝、Session 防 ABA、取消不报警、Stop timeout 保持占用、配方零副作用拒绝、二次开发不改核心 implementation。

- [ ] **Step 6: 修复审阅发现后重新执行 Step 3–4**

任何代码修正都先补失败测试，再实现；不能仅凭 review 描述直接修改生产代码。

- [ ] **Step 7: 最终提交并推送**

Run:

```powershell
git add .
git commit -m "docs: add inspection execution extension guide"
git push -u origin HEAD
git status --short --branch
```

Expected: 工作区干净，当前分支与远端一致。

## Appendix A: ProductionCoordinator 测试 doubles

`VisionStation.Application.Tests/ProductionCoordinatorTestDoubles.cs` 使用以下完整结构；未参与断言的设备方法仍必须实现 interface，不能使用动态 mock：

```csharp
using System.Collections.Concurrent;
using VisionStation.Application;
using VisionStation.Devices;
using VisionStation.Domain;

namespace VisionStation.Application.Tests;

internal sealed class CoordinatorHarness
{
    private CoordinatorHarness(
        ProductionCoordinator coordinator,
        IInspectionExecution execution,
        TestInspectionExecutor executor,
        FakeCameraDevice camera,
        FakePlcClient plc,
        FakeAxisController axis,
        FakeCommunicationChannels channels,
        FakeAlarmService alarms)
    {
        Coordinator = coordinator;
        Execution = execution;
        Executor = executor;
        Camera = camera;
        Plc = plc;
        Axis = axis;
        Channels = channels;
        Alarms = alarms;
    }

    public ProductionCoordinator Coordinator { get; }
    public IInspectionExecution Execution { get; }
    public TestInspectionExecutor Executor { get; }
    public FakeCameraDevice Camera { get; }
    public FakePlcClient Plc { get; }
    public FakeAxisController Axis { get; }
    public FakeCommunicationChannels Channels { get; }
    public FakeAlarmService Alarms { get; }

    public static CoordinatorHarness Create(
        int stopWaitTimeoutMs = 1000,
        IInspectionExecution? inspectionExecution = null)
    {
        var log = new FakeAppLogService();
        var executor = new TestInspectionExecutor();
        var execution = inspectionExecution ?? new InspectionExecution(executor, log);
        var camera = new FakeCameraDevice();
        var plc = new FakePlcClient();
        var axis = new FakeAxisController();
        var channels = new FakeCommunicationChannels();
        var alarms = new FakeAlarmService();
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
            channels,
            configuration);
        return new CoordinatorHarness(
            coordinator,
            execution,
            executor,
            camera,
            plc,
            axis,
            channels,
            alarms);
    }

    public static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

internal sealed class TestInspectionExecutor : IInspectionExecutor
{
    private int _callCount;

    public Func<InspectionRequest, CancellationToken, Task<InspectionRunResult>> Handler { get; set; } =
        static (_, _) => Task.FromResult(TestRunResults.Ok());

    public int CallCount => Volatile.Read(ref _callCount);

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

    public ActiveInspectionRun? Current { get; }

    public TaskCompletionSource<bool> TryBeginEntered { get; } =
        CoordinatorHarness.NewSignal();

    public TaskCompletionSource<bool> AllowTryBeginReturn { get; } =
        CoordinatorHarness.NewSignal();

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
        TryBeginEntered.TrySetResult(true);
        AllowTryBeginReturn.Task.GetAwaiter().GetResult();
        return new RunAdmission.Rejected(
            new RunRejection(RunRejectionReason.Busy, Current!));
    }
}

internal sealed class DistinctStateRecorder
{
    private readonly object _syncRoot = new();
    private readonly List<ProductionState> _states = [];

    public DistinctStateRecorder(ProductionCoordinator coordinator)
    {
        coordinator.SnapshotChanged += (_, snapshot) =>
        {
            lock (_syncRoot)
            {
                if (_states.Count == 0 || _states[^1] != snapshot.State)
                {
                    _states.Add(snapshot.State);
                }
            }
        };
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
}

internal sealed class FakeCameraDevice : ICameraDevice
{
    private int _connectCount;

    public event EventHandler<DeviceSnapshot>? StateChanged
    {
        add { }
        remove { }
    }

    public string DeviceId => "test-camera";

    public DeviceSnapshot Snapshot { get; } =
        new("Camera", DeviceConnectionState.Disconnected, "test", DateTimeOffset.UtcNow);

    public TaskCompletionSource<bool> ConnectEntered { get; } =
        CoordinatorHarness.NewSignal();

    public Func<CancellationToken, Task> ConnectHandler { get; set; } =
        static _ => Task.CompletedTask;

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCount);
        ConnectEntered.TrySetResult(true);
        return ConnectHandler(cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<ImageFrame> GrabAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TestRunResults.Ok().OriginalFrame);
}

internal sealed class FakePlcClient : IPlcClient
{
    private int _connectCount;

    public event EventHandler<DeviceSnapshot>? StateChanged
    {
        add { }
        remove { }
    }

    public DeviceSnapshot Snapshot { get; } =
        new("PLC", DeviceConnectionState.Disconnected, "test", DateTimeOffset.UtcNow);

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public ConcurrentQueue<bool> BusyWrites { get; } = new();

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCount);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetInspectionBusyAsync(bool busy, CancellationToken cancellationToken = default)
    {
        BusyWrites.Enqueue(busy);
        return Task.CompletedTask;
    }

    public Task<string> ReadAddressAsync(string address, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public Task WriteAddressAsync(string address, string value, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task WriteInspectionResultAsync(InspectionResult result, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ResetAlarmAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class FakeAxisController : IAxisController
{
    private int _connectCount;

    public event EventHandler<DeviceSnapshot>? StateChanged
    {
        add { }
        remove { }
    }

    public DeviceSnapshot Snapshot { get; } =
        new("Axis", DeviceConnectionState.Disconnected, "test", DateTimeOffset.UtcNow);

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCount);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ServoOnAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ServoOffAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearAlarmAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ZeroPositionAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HomeAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HomeAsync(AxisHomeCommand command, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MoveAbsoluteAsync(AxisMoveCommand command, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MoveLinearInterpolationAsync(AxisLinearInterpolationCommand command, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StartJogAsync(AxisJogCommand command, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopJogAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(string axisKey = AxisDefaults.PrimaryAxisKey, AxisStopMode stopMode = AxisStopMode.Smooth, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task EmergencyStopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<AxisStatus> GetAxisStatusAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default) => Task.FromResult(new AxisStatus { AxisKey = axisKey });
    public Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeCommunicationChannels : ICommunicationChannelRuntime
{
    private int _connectCount;
    private int _disconnectCount;

    public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived
    {
        add { }
        remove { }
    }

    public int ConnectCount => Volatile.Read(ref _connectCount);
    public int DisconnectCount => Volatile.Read(ref _disconnectCount);

    public Task ConnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCount);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _disconnectCount);
        return Task.CompletedTask;
    }

    public Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(TcpCommunicationChannelSettings channel, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommunicationChannelRuntimeSnapshot("TCP", channel.Key, channel.ConnectionPolicy, false, false, false, string.Empty, string.Empty));

    public Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(SerialCommunicationChannelSettings channel, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommunicationChannelRuntimeSnapshot("Serial", channel.Key, channel.ConnectionPolicy, false, false, false, string.Empty, string.Empty));

    public Task<CommunicationChannelRuntimeSnapshot> ReconnectTcpAsync(TcpCommunicationChannelSettings channel, CancellationToken cancellationToken = default) =>
        GetTcpSnapshotAsync(channel, cancellationToken);

    public Task<CommunicationChannelRuntimeSnapshot> ReconnectSerialAsync(SerialCommunicationChannelSettings channel, CancellationToken cancellationToken = default) =>
        GetSerialSnapshotAsync(channel, cancellationToken);

    public Task<byte[]?> TryExchangeTcpAsync(TcpCommunicationChannelSettings channel, byte[] payload, int timeoutMs, bool waitResponse, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    public Task<bool> TrySendTcpAsync(TcpCommunicationChannelSettings channel, byte[] payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<byte[]?> TryExchangeSerialAsync(SerialCommunicationChannelSettings channel, byte[] payload, int timeoutMs, bool waitResponse, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    public Task<bool> TrySendSerialAsync(SerialCommunicationChannelSettings channel, byte[] payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public void Dispose()
    {
    }
}

internal sealed class FakeAppLogService : IAppLogService
{
    private readonly object _syncRoot = new();
    private readonly List<AppLogEntry> _entries = [];

    public event EventHandler<AppLogEntry>? LogWritten;

    public void Info(string source, string message) => Write("Info", source, message);
    public void Warning(string source, string message) => Write("Warning", source, message);
    public void Error(string source, string message) => Write("Error", source, message);
    public void Critical(string source, string message) => Write("Critical", source, message);
    public IReadOnlyList<AppLogEntry> Recent(int count)
    {
        lock (_syncRoot)
        {
            return _entries.TakeLast(count).ToArray();
        }
    }

    private void Write(string level, string source, string message)
    {
        var entry = new AppLogEntry(DateTimeOffset.UtcNow, level, source, message);
        lock (_syncRoot)
        {
            _entries.Add(entry);
        }

        LogWritten?.Invoke(this, entry);
    }
}

internal sealed class FakeAlarmService : IAlarmService
{
    private readonly object _syncRoot = new();
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

    public AlarmEvent Raise(AlarmSeverity severity, string source, string message, string details = "", string? alarmId = null)
    {
        var alarm = new AlarmEvent(
            alarmId ?? Guid.NewGuid().ToString("N"),
            severity,
            source,
            message,
            DateTimeOffset.UtcNow,
            Details: details);
        lock (_syncRoot)
        {
            _raised.Add(alarm);
        }

        AlarmRaised?.Invoke(this, alarm);
        return alarm;
    }

    public void Acknowledge(string alarmId)
    {
        AlarmEvent? changed = null;
        lock (_syncRoot)
        {
            var index = _raised.FindIndex(item => item.Id == alarmId);
            if (index >= 0)
            {
                _raised[index] = _raised[index] with
                {
                    Acknowledged = true,
                    AcknowledgedAt = DateTimeOffset.UtcNow
                };
                changed = _raised[index];
            }
        }

        if (changed is not null) AlarmChanged?.Invoke(this, changed);
    }

    public void Clear(string alarmId)
    {
        AlarmEvent? changed = null;
        lock (_syncRoot)
        {
            var index = _raised.FindIndex(item => item.Id == alarmId);
            if (index >= 0)
            {
                _raised[index] = _raised[index] with
                {
                    ClearedAt = DateTimeOffset.UtcNow
                };
                changed = _raised[index];
            }
        }

        if (changed is not null) AlarmChanged?.Invoke(this, changed);
    }

    public IReadOnlyList<AlarmEvent> Active() =>
        Raised.Where(item => item.IsActive).ToArray();

    public IReadOnlyList<AlarmEvent> Recent(int count) =>
        Raised.TakeLast(count).ToArray();
}
```

## Appendix B: RecipeManagement 测试 harness

`RecipeManagementTestHarness.cs` 必须使用以下确定性实现；所有 interface 方法都返回明确的 CompletedTask、空集合或固定结果，不能抛 `NotImplementedException`：

```csharp
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
        var session = new RecordingInspectionSession(Active(InspectionRunModes.RecipeTest, nameof(RecipeManagementViewModel)));
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
        return new RecipeManagementTestHarness(root, viewModel, recipes, execution, session, channels, runControl);
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
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

internal sealed class FakeInspectionExecution : IInspectionExecution
{
    private readonly RecordingInspectionSession _session;

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
    public event EventHandler<InspectionExecutionChangedEventArgs>? Changed;
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
        Changed?.Invoke(this, new InspectionExecutionChangedEventArgs(current));
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

    public Task<InspectionRunResult> ExecuteAsync(InspectionRequest request, CancellationToken cancellationToken = default)
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

    public Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(_recipe);
    public Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(_recipe.Id);
    public Task SetCurrentRecipeAsync(string recipeId, CancellationToken cancellationToken = default) { SetCurrentCount++; return Task.CompletedTask; }
    public Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default) => Task.FromResult<Recipe?>(_recipe.Id == recipeId ? _recipe : null);
    public Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Recipe>>([_recipe]);
    public async Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        await SaveHandler(recipe, cancellationToken);
        _recipe = recipe;
    }
    public Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class RecordingRunControl : IInspectionRunControl
{
    public bool IsPaused { get; private set; }
    public int BeginCount { get; private set; }
    public void BeginRun() { BeginCount++; IsPaused = false; }
    public void EndRun() => IsPaused = false;
    public void Pause() => IsPaused = true;
    public void Resume() => IsPaused = false;
    public void RequestReset() { }
    public Task WaitIfPausedOrResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();
}

internal sealed class NullFlowEditorDialogService : IFlowEditorDialogService
{
    public Task ShowEditorAsync(string? recipeId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeDeviceConfigurationRepository : IDeviceConfigurationRepository
{
    public event EventHandler<DeviceConfiguration>? ConfigurationSaved;
    public Task<DeviceConfiguration> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DeviceConfiguration());
    public Task SaveAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default) { ConfigurationSaved?.Invoke(this, configuration); return Task.CompletedTask; }
}

internal sealed class NullAppLogService : IAppLogService
{
    public event EventHandler<AppLogEntry>? LogWritten { add { } remove { } }
    public void Info(string source, string message) { }
    public void Warning(string source, string message) { }
    public void Error(string source, string message) { }
    public void Critical(string source, string message) { }
    public IReadOnlyList<AppLogEntry> Recent(int count) => [];
}
```

```csharp
internal sealed class RecordingCommunicationChannels : ICommunicationChannelRuntime
{
    public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived
    {
        add { }
        remove { }
    }

    public int ConnectCount { get; private set; }
    public int DisconnectCount { get; private set; }

    public Task ConnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
    {
        DisconnectCount++;
        return Task.CompletedTask;
    }

    public Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(
        TcpCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommunicationChannelRuntimeSnapshot(
            "TCP", channel.Key, channel.ConnectionPolicy, false, false, false, string.Empty, string.Empty));

    public Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(
        SerialCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommunicationChannelRuntimeSnapshot(
            "Serial", channel.Key, channel.ConnectionPolicy, false, false, false, string.Empty, string.Empty));

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

    public void Dispose()
    {
    }
}
```
