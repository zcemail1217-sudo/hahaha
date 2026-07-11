# 生产运行唯一准入与状态机设计

**状态：** 已批准，已按二次开发要求深化 interface

**日期：** 2026-07-11

**适用方案：** `CVWork.sln`

## 1. 背景

当前 `ProductionCoordinator` 对单次检测和连续生产没有统一的并发准入控制：

- 两个并发 `StartAsync` 可能同时通过 `_loopTask` 检查并创建两条生产循环。
- `RunSingleAsync` 可以与连续生产或另一次单次检测重叠。
- `StopAsync` 只能取消连续循环，不能取消生产看板发起的单次检测。
- 配方管理页直接调用 `IInspectionRunner`，绕过 `ProductionCoordinator`。
- 正常取消可能先被记录成生产故障，状态、报警与实际运行生命周期不一致。

最初的“Gate + 裸 Runner + Lease 参数”方案可以阻止当前绕过，但二次开发者必须同时理解 Gate、Lease、Runner、验证顺序和释放规则，interface 偏浅。最终设计将准入、执行、防绕过、事件和租约释放深化到一个 `IInspectionExecution` module 中；外部调用者只通过一个 seam 开始会话，并通过该会话执行检测。

## 2. 目标

1. 任意时刻最多只有一个检测会话进入设备连接、配方切换或检测执行阶段。
2. 冲突请求立即拒绝，不排队，不在已有任务结束后自动执行。
3. 单次检测、连续生产、配方试运行以及未来新增检测模式彼此互斥。
4. `StopAsync` 可以取消生产看板发起的单次检测或连续生产，并等待同一活动任务完成清理。
5. 正常取消不进入 `Faulted`、不产生生产故障报警。
6. 状态快照准确表达启动、运行、停止中、停止和故障。
7. 预期控制结果通过返回值表达，不进入全局 UI 异常处理器。
8. 外部只有一个检测执行 seam，调用者不能直接取得或绕过裸 Runner。
9. 新增运行模式或 UI 入口时，不复制锁、验证、状态和清理逻辑，不修改核心 module 的 implementation。
10. 公开 interface 带 XML 文档和最小二次开发示例，自动化测试通过同一 seam 验证行为。

## 3. 非目标

本阶段不实现以下能力：

- 轴减速停止、轴组补偿、硬件急停或 IO 安全态。
- 设备调试页上的手动轴、IO、相机操作与生产会话互锁。
- 跨进程或多工控机分布式锁。
- 完整重构 `InspectionRunControl` 的 Pause/Resume/Reset 所有权模型。
- Error/NG 结果语义、图像保留和跨存储一致性修复。
- 在只有一个准入实现时预先引入策略插件、规则集合或第三方扩展框架。

这些属于后续独立 P0/P1 改造。软件运行取消不能替代独立硬件急停回路。

## 4. Interface 方案比较

### 4.1 仅修补 Coordinator 和按钮状态

给 `ProductionCoordinator` 增加 `SemaphoreSlim`，并在生产看板禁用按钮。改动最少，但配方试运行仍可绕过，未来调用者也可能直接调用 Runner，不能满足全局唯一准入。

### 4.2 Gate + Runner 双 module

单例 Gate 负责发 Lease，`IInspectionRunner.RunAsync` 增加 Lease 参数。interface 较小，但调用者必须知道“先申请、再验证、执行、最后释放”的正确顺序。删除 Gate 后，复杂度会重新散落到所有调用者，说明这个组合仍不够深。

### 4.3 单一检测执行 module（采用）

外部只依赖 `IInspectionExecution`。`TryBegin(intent)` 返回 `IInspectionSession` 或明确拒绝；Session 是唯一执行能力。锁、SessionId、authority、Runner、事件隔离和释放规则全部藏在 implementation 内。

运行模式使用可验证的值对象，不使用封闭枚举。新增模式只定义稳定 Key、显示名和入口名，不需要修改 Gate 或多处 `switch`。这个方案同时提供最高的 depth、调用者 leverage 和维护 locality。

### 4.4 串行命令 Actor

把单次、连续、试运行、暂停、复位和停止全部改造成串行命令 Actor。interface 可以很小，但天然倾向排队，并需要同时重构多个 ViewModel 和运行控制模块，首轮风险过高。

## 5. Module 与 seam

### 5.1 外部 seam

`IInspectionExecution` 是业务调用者和测试共同使用的唯一外部 seam：

```csharp
public interface IInspectionExecution
{
    ActiveInspectionRun? Current { get; }

    event EventHandler<InspectionExecutionChangedEventArgs>? Changed;

    event EventHandler<InspectionRunResult>? RunCompleted;

    RunAdmission TryBegin(InspectionRunIntent intent);
}
```

调用者不再注入或引用 `IInspectionRunner`、Gate 或内部 authority。

### 5.2 运行模式和意图

```csharp
public readonly record struct InspectionRunMode
{
    public InspectionRunMode(string key, string displayName)
    {
        var normalizedKey = key?.Trim() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                normalizedKey,
                "^[a-z0-9]+(?:[._-][a-z0-9]+)*$"))
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

    public string Key { get; }

    public string DisplayName { get; }
}

public sealed record InspectionRunIntent(
    InspectionRunMode Mode,
    string EntryPoint);
```

约束：

- `Key` 使用稳定的小写标识，如 `production.manual-single`、`production.continuous`、`recipe.test`。
- Key 只允许小写字母、数字以及分隔符 `.`, `_`, `-`，且不能为空。
- `DisplayName` 和 `EntryPoint` 不能为空；前者供 UI 显示，后者供日志和诊断定位调用入口。
- `TryBegin` 会再次验证 Mode 和 EntryPoint，因此 `default(InspectionRunMode)` 不能绕过构造校验。
- 核心 module 不根据 Mode 做 `switch`，也不维护允许模式注册表。
- 现有模式集中放在 `InspectionRunModes` 静态类；二次开发功能也可以在自己的功能目录定义模式常量。

### 5.3 活动快照

```csharp
public sealed record ActiveInspectionRun(
    Guid SessionId,
    InspectionRunIntent Intent,
    DateTimeOffset StartedAt);

public sealed class InspectionExecutionChangedEventArgs : EventArgs
{
    public InspectionExecutionChangedEventArgs(ActiveInspectionRun? current) =>
        Current = current;

    public ActiveInspectionRun? Current { get; }
}
```

活动快照只暴露诊断和 UI 所需的不可变信息，不暴露取消源、内部任务、authority 或锁对象。

### 5.4 准入结果

```csharp
public enum RunRejectionReason
{
    Busy,
    AlreadyRunning,
    NotOwner
}

public sealed record RunRejection(
    RunRejectionReason Reason,
    ActiveInspectionRun Active);

public abstract record RunAdmission
{
    public sealed record Acquired(IInspectionSession Session) : RunAdmission;

    public sealed record Rejected(RunRejection Rejection) : RunAdmission;
}
```

准入冲突是正常控制结果，不使用异常表达。Module 自身在已有会话时返回 `Busy`；Coordinator 可根据自己持有的活动会话把重复 Start 映射为 `AlreadyRunning`，Stop 遇到非自身会话时映射为 `NotOwner`。

### 5.5 Session interface

```csharp
public interface IInspectionSession : IAsyncDisposable
{
    ActiveInspectionRun Run { get; }

    Task<InspectionRunResult> ExecuteAsync(
        InspectionRequest request,
        CancellationToken cancellationToken = default);
}
```

Session interface 的不变量：

- Session 只能由 `IInspectionExecution.TryBegin` 创建，调用者不能伪造。
- 同一个 Session 可以顺序执行多个请求，以支持连续生产和配方 Reset 重跑。
- 同一个 Session 的并发 `ExecuteAsync` 在任何配方、设备或持久化副作用前抛出 `InvalidOperationException`。
- `DisposeAsync` 幂等；如果执行仍在进行，则等待该执行结束后再释放全局准入。
- 已释放 Session 再执行时，在任何副作用前抛出 `ObjectDisposedException`。
- 旧 Session 的迟到释放不能释放后来创建的新会话。

## 6. Implementation 隐藏内容

`InspectionExecution` implementation 负责：

- 以短时 `lock` 原子检查并写入当前 Session。
- 生成唯一 SessionId 和不可伪造 authority。
- 在 Session 执行前验证对象身份、SessionId、authority 和释放状态。
- 将执行转交给现有 `InspectionRunner` 逻辑。
- 隔离 `Changed` 和 `RunCompleted` 的各个订阅者异常。
- 在 Session 最终释放后清空 Current 并发布事件。

`TryBegin` 为 O(1) 进程内操作，绝不等待、排队或启动后台任务。状态先在锁内提交，再在锁外发布事件。

现有 `IInspectionRunner` 不再作为公共业务依赖注册。Runner 逻辑降为 module 的 implementation 细节。为了让 module 测试不依赖相机、轴、PLC 和文件系统，可以在 Application 程序集内部保留 `IInspectionExecutor` seam：

- 生产 adapter 使用现有 `InspectionRunner` implementation。
- Application.Tests 通过 `InternalsVisibleTo` 使用确定性的内存 adapter。
- 内部 seam 不出现在 `IInspectionExecution` interface，也不暴露给 Client。

测试仍从外部 `IInspectionExecution` seam 断言可观察结果，不测试锁、authority 或私有字段。

## 7. 标准调用方式

```csharp
var admission = _inspectionExecution.TryBegin(
    new InspectionRunIntent(
        InspectionRunModes.ManualSingle,
        nameof(ProductionDashboardViewModel)));

if (admission is RunAdmission.Rejected rejected)
{
    // 将 rejection 映射为该入口自己的返回值或用户提示。
    return;
}

await using var session = ((RunAdmission.Acquired)admission).Session;
var result = await session.ExecuteAsync(request, cancellationToken);
```

所有业务入口遵守相同形状：“申请 Session → 处理 Rejected → 通过 Session 执行 → `await using` 释放”。调用者不需要知道锁、Lease 验证、Runner 或释放校验。

## 8. ProductionCoordinator 设计

### 8.1 活动操作

Coordinator 内部维护一个由短时锁保护的活动生产操作：

```csharp
private sealed record ActiveProductionOperation(
    Guid SessionId,
    IInspectionSession Session,
    CancellationTokenSource Cancellation,
    Task Completion);
```

Coordinator 在异步初始化之前创建 `TaskCompletionSource`，先把它的 Task 连同 Session、CTS 写入活动操作，再启动实际工作。初始化期间到达的 Stop 因此能够观察并取消同一个操作。

约束：

- 不在锁内等待设备、Session 或后台任务。
- 后台任务捕获本次运行自己的 Session、CTS 和 Completion，不读取可被后续调用覆盖的共享 CTS 字段。
- 连续生产从启动前到最终清理完成始终持有同一个 Session，每个周期调用 `session.ExecuteAsync`。
- 单次检测持有 Session 直至 PLC Busy、通信通道和任务清理结束。

### 8.2 命令返回值

```csharp
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

公开方法调整为：

```csharp
Task<ProductionCommandResult<InspectionRunResult>> RunSingleAsync(
    CancellationToken cancellationToken = default);

Task<ProductionCommandResult> StartAsync(
    CancellationToken cancellationToken = default);

Task<ProductionCommandResult> StopAsync(
    CancellationToken cancellationToken = default);
```

返回语义：

- 冲突的 Start 或 Single：`Rejected`。
- 重复 Start：`Rejected(AlreadyRunning)`。
- Stopped 状态调用 Stop：`NoOp`。
- Stop 成功结束单次或连续任务：`Completed`。
- 正常取消导致单次任务结束：`Canceled`。
- 真实设备、配置或代码异常仍记录报警、进入 `Faulted` 并向调用者抛出；UI 必须捕获并显示。

### 8.3 状态转换

在 `ProductionState` 末尾追加 `Starting` 和 `Stopping`，保留现有枚举值。

```text
单次成功：
Stopped/Faulted -> Starting -> Running -> Stopping -> Stopped

连续运行：
Stopped/Faulted -> Starting -> Running -> Stopping -> Stopped

启动期间停止：
Starting -> Stopping -> Stopped

非取消异常：
Starting/Running -> Stopping -> Faulted
```

本阶段不产生新的 `Paused` 状态，因为 ProductionCoordinator 还没有真实 Pause interface。

状态提交在内部锁内完成，`SnapshotChanged` 在锁外发布。事件订阅者异常不能回滚已经提交的状态，也不能破坏 Session 清理。

## 9. Stop 语义

`StopAsync` 不申请新 Session，也不与活动任务争抢 `IInspectionExecution`。

1. 在短时锁内取得当前活动生产操作并切换到 `Stopping`。
2. 在锁外取消该操作的 CTS。
3. 等待该操作自己的 Completion；多个并发 Stop 等待同一个任务。
4. 只有活动操作的 `finally` 执行一次 PLC Busy 清理、Production 通信断开和 Session 释放。
5. Stop 不重复执行 Disconnect。

所有权规则：

- Coordinator 持有 ManualSingle 或 Continuous Session 时，Stop 取消并等待它。
- Coordinator 没有活动操作、但 `IInspectionExecution.Current` 非空时，当前会话属于其他入口，Stop 返回 `Rejected(NotOwner)`。
- Stopped 且全局无活动 Session 时，Stop 返回 `NoOp`。

增加 `ProductionSettingsConfiguration.StopWaitTimeoutMs`，默认 10000 ms，并由配置 Normalizer 修复非正值。Stop 超时后：

- 状态进入 `Faulted` 并产生 Critical 报警。
- 活动任务和 Session 保持占用，禁止任何新检测开始。
- 后台任务最终结束时才执行清理并释放 Session。
- 超时故障标记不会被后台迟到完成自动改成 Stopped；下一次显式启动可以从 Faulted 进入 Starting。

该超时只防止 UI 无限等待，不代表设备运动已经安全停止。

## 10. 配方试运行集成

配方管理页必须在以下动作之前申请 `recipe.test` Session：

- 保存测试配方。
- 切换当前配方。
- 初始化 `InspectionRunControl`。
- 连接设备或执行检测。

准入被拒绝时，不允许先保存、切换配方或连接设备。Session 覆盖一次测试会话中的首次运行和 Reset 重跑，直到正常完成、取消、异常或页面退出。

Pause、Resume 和 Reset 仍只作用于配方试运行。生产看板 Stop 不跨所有者取消该 Session。

## 11. UI 行为

生产看板和 Shell 需要处理：

- `Starting` 显示“启动中”。
- `Stopping` 显示“停止中”。
- 当前有生产单次或连续任务时，Run/Start 不可用，Stop 可用。
- 当前由其他入口占用时，生产 Run/Start/Stop 均不可用，并直接显示 `Current.Intent.Mode.DisplayName`。
- `Rejected` 显示明确提示，不写故障报警。
- 异步命令入口捕获真实异常，避免落入全局 UI 异常处理器。
- Stop 按钮提示改为“停止当前生产检测”。

配方管理页在其他 Session 占用时禁用试运行，并在竞态导致的 `Rejected` 返回时显示占用入口和模式。

`VariableCenterViewModel` 从 `IInspectionRunner.RunCompleted` 改为订阅 `IInspectionExecution.RunCompleted`，不再依赖裸 Runner。

## 12. 清理与异常处理

- PLC Busy 在每个检测周期的 `finally` 中使用独立、有限时清理令牌清除。
- Production 策略通信通道由活动会话所有者在最终 `finally` 中断开一次。
- 用户 Stop、调用方取消和应用退出取消不记录为单次检测失败报警。
- 非取消异常记录原始异常、进入 `Faulted`，并确保最终释放 Session。
- 准入拒绝、重复 Stop 和 Stopped 状态 Stop 不写报警。
- Module 和 Coordinator 的事件订阅者异常只写日志，不改变运行结果或 Session 生命周期。

## 13. 二次开发指南

新增一个检测入口只需定义模式并使用外部 seam：

```csharp
private static readonly InspectionRunMode CalibrationTest =
    new("calibration.test", "标定试运行");

var admission = _inspectionExecution.TryBegin(
    new InspectionRunIntent(CalibrationTest, nameof(CalibrationViewModel)));

if (admission is RunAdmission.Rejected rejected)
{
    ShowOccupied(rejected.Rejection.Active);
    return;
}

await using var session = ((RunAdmission.Acquired)admission).Session;
await session.ExecuteAsync(request, cancellationToken);
```

新增入口通常只修改：

1. 新功能自己的模式常量。
2. 新功能自己的调用代码。
3. 通过 `IInspectionExecution` seam 的行为测试。

不需要修改 `InspectionExecution` implementation、Runner、锁、状态验证、DI 注册或现有 UI 的模式 `switch`。

如果新功能需要 Start/Stop 生命周期，应在自己的编排 module 内持有 Session、CTS 和 Completion，沿用 `ProductionCoordinator` 的所有权模式；不向 UI 暴露锁或内部 executor。

实施同时新增 `docs/development/inspection-execution.md`，包含：

- interface 和不变量。
- 标准单次、循环和 Reset 重跑示例。
- Rejected、取消、异常和 Dispose 处理。
- 新增运行模式与调用入口检查清单。

当前不增加 `IInspectionAdmissionRule` 等策略 seam。只有出现第二个真实准入策略时，才在 module implementation 内增加可替换 adapter；任何策略都只能追加拒绝，不能放宽“全局最多一个 Session”的核心不变量。

## 14. 测试策略

实施使用测试驱动开发：先观察每个新测试因缺少行为而失败，再写最小实现。

### 14.1 InspectionExecution module 测试

1. 100 个并发 `TryBegin` 恰好一个成功，其余立即拒绝。
2. Rejected 包含同一个活动 Session，不产生延迟执行。
3. Session 释放后新请求可立即取得。
4. 重复释放不改变当前会话。
5. 旧 Session 不能释放后来创建的新会话。
6. 已释放 Session 执行时在任何 adapter 调用前失败。
7. 同一 Session 并发执行时只有一个进入内部 executor。
8. 同一 Session 顺序执行多个请求成功。
9. Changed 和 RunCompleted 订阅者异常不泄漏 Session。
10. 新的自定义 `InspectionRunMode` 无需修改 module 即可运行。

### 14.2 Coordinator 测试

1. 两个并发 Start 只执行一次设备初始化和循环创建。
2. Start 初始化期间 Stop 能取消初始化并最终进入 Stopped。
3. 单次检测期间 Stop 取消执行，且不产生 Faulted 报警。
4. 连续期间 Single、Single 期间 Start 均立即拒绝。
5. 多个并发 Stop 等待同一 Completion，通信清理只执行一次。
6. 用户取消不报警，非取消异常进入 Faulted。
7. 故障或正常退出均最终释放 Session。
8. Stop 超时进入 Faulted，且活动任务结束前不能取得新 Session。
9. 精确断言 Starting、Running、Stopping、Stopped/Faulted 的顺序。

### 14.3 配方试运行测试

1. 其他 Session 占用时，试运行不得保存或切换配方，也不得执行检测。
2. 试运行占用时，生产 Start 和 Single 立即拒绝。
3. Reset 重跑期间持有同一 Session。
4. 正常结束、取消、异常和页面退出均释放 Session。

### 14.4 UI 测试

1. 新增状态映射为正确文本和颜色。
2. Current 变化后正确更新命令可用状态。
3. Stop 在单次和连续生产期间可用。
4. Rejected 显示模式和入口，不进入全局异常处理。

## 15. 预计修改范围

主要文件：

- `VisionStation.Application/Inspection/Execution/InspectionExecutionContracts.cs`（新增）
- `VisionStation.Application/Inspection/Execution/InspectionExecution.cs`（新增）
- `VisionStation.Application/InspectionServices.cs`
- `VisionStation.Application/ProductionCoordinator.cs`
- `VisionStation.Domain/Models.cs`
- `VisionStation.Domain/DeviceConfigurationModels.cs`
- `VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs`
- `VisionStation.Client/App.xaml.cs`
- `VisionStation.Client/ViewModels/ProductionDashboardViewModel.cs`
- `VisionStation.Client/ViewModels/ShellWindowViewModel.cs`
- `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs`
- `VisionStation.Client/ViewModels/VariableCenterViewModel.cs`
- `VisionStation.Client/Views/ProductionDashboardView.xaml`
- `VisionStation.Application.Tests/InspectionExecutionTests.cs`（新增）
- `VisionStation.Application.Tests/ProductionCoordinatorTests.cs`（新增）
- `VisionStation.Vision.UI.Tests` 中对应的 ViewModel/状态测试
- `docs/development/inspection-execution.md`（新增）

不进行无关格式化、命名重写或大规模 ViewModel 拆分。

## 16. 完成标准

1. 所有检测执行入口都通过 `IInspectionExecution`，Client 不再注入裸 Runner。
2. 并发冲突立即返回 Rejected，永不排队。
3. 单次、连续、配方试运行和自定义模式之间不能重叠。
4. Stop 正常取消单次或连续任务，不产生 Faulted 报警。
5. Stop 超时不会错误释放 Session 或显示 Stopped。
6. 每个 Session 的通信和准入清理恰好执行一次。
7. UI 能显示 Starting、Stopping、占用模式和入口。
8. 新模式示例不修改核心 module 即可通过测试。
9. 公开 interface 有完整 XML 文档，二次开发指南包含可复制示例和约束。
10. 新增测试经过明确的 Red-Green 循环。
11. Release 构建无错误，完整测试套件通过。
