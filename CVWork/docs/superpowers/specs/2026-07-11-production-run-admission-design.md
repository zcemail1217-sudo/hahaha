# 生产运行唯一准入与状态机设计

**状态：** 已批准

**日期：** 2026-07-11

**适用方案：** `CVWork.sln`

## 1. 背景

当前 `ProductionCoordinator` 对单次检测和连续生产没有统一的并发准入控制：

- 两个并发 `StartAsync` 可能同时通过 `_loopTask` 检查并创建两条生产循环。
- `RunSingleAsync` 可以与连续生产或另一次单次检测重叠。
- `StopAsync` 只能取消连续循环，不能取消生产看板发起的单次检测。
- 配方管理页直接调用 `IInspectionRunner`，绕过 `ProductionCoordinator`，所以只修补生产看板或 Coordinator 不能形成全局互斥。
- 正常取消可能先被记录成生产故障，状态、报警与实际运行生命周期不一致。

本设计建立一个进程内唯一的检测运行准入模块，并让生产单次、连续生产和配方试运行共用同一个租约协议。它同时收紧 `ProductionCoordinator` 的状态转换和停止语义。

## 2. 目标

1. 任意时刻最多只有一个检测会话可以进入设备连接、配方切换或检测执行阶段。
2. 冲突请求立即拒绝，不排队，不在已有任务结束后自动执行。
3. 单次检测、连续生产、配方试运行三种模式彼此互斥。
4. `StopAsync` 可以取消生产看板发起的单次检测或连续生产，并等待同一活动任务完成清理。
5. 正常取消不进入 `Faulted`、不产生生产故障报警。
6. 状态快照能够准确表达启动、运行、停止中、停止和故障。
7. 预期控制结果通过返回值表达，不进入全局 UI 异常处理器。
8. 通过自动化测试证明准入、状态、取消和清理的并发行为。

## 3. 非目标

本阶段不实现以下能力：

- 轴减速停止、轴组补偿、硬件急停或 IO 安全态。
- 设备调试页上的手动轴、IO、相机操作与生产会话互锁。
- 跨进程或多工控机分布式锁。
- 完整重构 `InspectionRunControl` 的 Pause/Resume/Reset 所有权模型。
- Error/NG 结果语义、图像保留和跨存储一致性修复。

这些属于后续独立 P0/P1 改造。尤其是软件运行取消不能替代独立硬件急停回路。

## 4. 方案比较

### 4.1 仅修补 Coordinator 和按钮状态

给 `ProductionCoordinator` 增加 `SemaphoreSlim`，并在生产看板禁用按钮。改动最小，但配方试运行仍可绕过，未来调用者也可能直接调用 Runner。不能满足全局唯一准入。

### 4.2 不排队的全局运行租约（采用）

新增单例 `InspectionRunGate`，使用短时 `lock` 保护当前租约。所有检测入口先调用 `TryAcquire`；失败时立即返回当前占用信息。Runner 必须接收有效的不透明租约，防止绕过准入。

这个方案能覆盖现有两个执行入口，改造范围可控，并提供清晰、可测试的模块边界。

### 4.3 统一检测命令 Actor

把单次、连续、试运行、暂停、复位和停止全部改造成串行命令 Actor。边界最彻底，但天然倾向排队，且需要同时重构多个 ViewModel 和运行控制组件，首轮风险过高。

## 5. 核心模型

### 5.1 运行类型和活动快照

```csharp
public enum InspectionRunKind
{
    ManualSingle,
    Continuous,
    RecipeTest
}

public sealed record ActiveInspectionRun(
    Guid SessionId,
    InspectionRunKind Kind,
    DateTimeOffset StartedAt);

public sealed class InspectionRunGateChangedEventArgs : EventArgs
{
    public InspectionRunGateChangedEventArgs(ActiveInspectionRun? active) => Active = active;

    public ActiveInspectionRun? Active { get; }
}
```

`ActiveInspectionRun` 只暴露诊断和 UI 所需的只读信息，不暴露取消源或内部任务。

### 5.2 准入结果

```csharp
public enum RunRejectionReason
{
    Busy,
    AlreadyRunning,
    NotOwner
}

public abstract record RunAdmission
{
    public sealed record Acquired(InspectionRunLease Lease) : RunAdmission;

    public sealed record Rejected(
        RunRejectionReason Reason,
        ActiveInspectionRun Active) : RunAdmission;
}
```

准入冲突是正常控制结果，不使用异常表达。

### 5.3 租约和 Gate

`InspectionRunGate` 注册为应用级单例，提供：

```csharp
public sealed class InspectionRunGate
{
    public ActiveInspectionRun? Active { get; }

    public event EventHandler<InspectionRunGateChangedEventArgs>? ActiveChanged;

    public RunAdmission TryAcquire(InspectionRunKind kind);

    public void Validate(InspectionRunLease lease);
}
```

实现约束：

- `TryAcquire` 只使用普通 `lock`，绝不等待。
- 第一个请求获得带有唯一 `SessionId` 的 `InspectionRunLease`；后续请求立即收到 `Rejected`。
- `InspectionRunLease.Dispose()` 幂等。
- 释放时同时校验 Gate 实例和 `SessionId`；旧租约或重复 Dispose 不能释放新会话。
- `ActiveChanged` 在内部锁之外发布。
- `InspectionRunGateChangedEventArgs` 始终携带当前可空的 `ActiveInspectionRun` 快照。
- Gate 注入 `IAppLogService`；订阅者异常被逐个隔离并记录，不能阻止租约释放。

## 6. Runner 强制准入

`IInspectionRunner.RunAsync` 调整为必须携带租约：

```csharp
Task<InspectionRunResult> RunAsync(
    InspectionRunLease lease,
    InspectionRequest request,
    CancellationToken cancellationToken = default);
```

Runner 在读取配方、连接设备或执行步骤之前调用 Gate 验证租约。空租约、已释放租约、过期租约和非当前租约均被拒绝。

当前需要更新的执行调用点只有：

- `ProductionCoordinator`
- `RecipeManagementViewModel` 的配方试运行

仅订阅 `RunCompleted` 的模块不受签名变化影响。

## 7. ProductionCoordinator 设计

### 7.1 活动操作

Coordinator 内部维护一个由短时锁保护的活动操作记录：

```csharp
private sealed record ActiveProductionOperation(
    Guid SessionId,
    InspectionRunKind Kind,
    InspectionRunLease Lease,
    CancellationTokenSource Cancellation,
    Task Completion);
```

Coordinator 在启动异步初始化之前创建 `TaskCompletionSource`，先把它的 Task 连同 Lease、CTS 写入活动操作，再启动实际工作；活动任务只在自己的最终 `finally` 中完成该 Task。这样，初始化期间到达的 Stop 也能观察并取消同一个活动操作。

约束：

- 不在锁内等待设备、Runner 或后台任务。
- 每个后台任务捕获本次运行自己的 Lease、CTS 和 Completion，不读取可能被后续调用覆盖的共享 CTS 字段。
- 连续生产从启动前到最终清理完成始终持有同一个租约，每个检测周期调用私有 `RunSingleCoreAsync`，不重复申请租约。
- 单次检测持有租约直至 PLC Busy、通信通道和任务清理结束。

### 7.2 命令返回值

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
    RunAdmission.Rejected? Rejection = null);

public sealed record ProductionCommandResult<T>(
    ProductionCommandDisposition Disposition,
    T? Value = default,
    RunAdmission.Rejected? Rejection = null);
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

### 7.3 状态转换

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

本阶段不产生新的 `Paused` 状态，因为生产 Coordinator 还没有真实 Pause API。

状态提交在内部锁内完成，`SnapshotChanged` 在锁外发布。事件订阅者异常不能回滚已经提交的状态，也不能破坏运行清理。

## 8. Stop 语义

`StopAsync` 不申请新租约，也不与活动任务争抢运行 Gate。

1. 在短时锁内取得当前活动生产操作并切换到 `Stopping`。
2. 在锁外取消该操作的 CTS。
3. 等待该操作自身的 Completion；多个并发 Stop 等待同一个任务。
4. 只有活动操作的 `finally` 执行一次 PLC Busy 清理、Production 通信断开和租约释放。
5. Stop 不重复执行 Disconnect。

模式规则：

- `ManualSingle`：取消当前单次检测并等待清理。
- `Continuous`：取消当前周期和后续循环并等待清理。
- `RecipeTest`：生产 Coordinator 返回 `Rejected(NotOwner)`；配方页使用自己的 Reset/取消入口。

增加 `ProductionSettingsConfiguration.StopWaitTimeoutMs`，默认 10000 ms，并由配置 Normalizer 修复非正值。Stop 超时后的规则：

- 状态进入 `Faulted` 并产生 Critical 报警。
- 活动任务和租约保持占用，禁止任何新检测开始。
- 后台任务最终结束时才执行清理和释放租约。
- 超时故障标记不会被后台迟到的完成自动改成 Stopped；下一次显式启动可从 Faulted 进入 Starting。

该超时只防止 UI 无限等待，不代表设备运动已经安全停止。

## 9. 配方试运行集成

配方管理页必须在以下动作之前申请 `RecipeTest` 租约：

- 保存测试配方；
- 切换当前配方；
- 初始化 `InspectionRunControl`；
- 连接设备或调用 Runner。

准入被拒绝时，不允许先保存、切换配方或连接设备。租约覆盖一次测试运行以及同一测试会话中的 Reset 重跑，直到正常完成、取消、异常或页面退出。

Pause、Resume 和 Reset 仍只作用于配方试运行。生产看板 Stop 不跨所有者取消这类会话。

## 10. UI 行为

生产看板和 Shell 需要处理：

- `Starting` 显示“启动中”。
- `Stopping` 显示“停止中”。
- 当前有生产单次或连续任务时，Run/Start 不可用，Stop 可用。
- 当前由配方试运行占用时，生产 Run/Start/Stop 均不可用，并显示占用模式。
- `Rejected` 显示明确提示，不写故障报警。
- 异步命令入口必须捕获真实异常，避免落入全局 UI 异常处理器。
- Stop 按钮提示由“停止连续检测循环”改为“停止当前生产检测”。

配方管理页在生产任务占用时禁用试运行，并在竞态导致的 `Rejected` 返回时显示占用信息。

## 11. 清理与异常处理

- PLC Busy 在每个检测周期的 `finally` 中使用独立、有限时清理令牌清除。
- Production 策略通信通道由活动会话所有者在最终 `finally` 中断开一次。
- 用户 Stop、调用方取消和应用退出取消不记录为单次检测失败报警。
- 非取消异常记录原始异常、进入 `Faulted`，并确保最终释放租约。
- Gate 拒绝、重复 Stop 和 Stopped 状态 Stop 不写报警。
- 事件订阅者异常只写日志，不改变运行结果或租约生命周期。

## 12. 测试策略

实施使用测试驱动开发，先观察每个新测试因缺少行为而失败，再写最小实现。

### 12.1 Gate 单元测试

1. 100 个并发 `TryAcquire` 恰好一个成功，其余立即拒绝。
2. 拒绝结果包含同一个活动 Session，不产生延迟执行。
3. Lease 释放后新请求可立即取得。
4. Lease 重复释放不改变当前会话。
5. 旧 Lease 不能释放后来创建的新会话。
6. 空、过期、已释放或非当前 Lease 调 Runner 时被拒绝。

### 12.2 Coordinator 测试

1. 两个并发 Start 只执行一次设备初始化和循环创建。
2. Start 初始化期间 Stop 能取消初始化并最终进入 Stopped。
3. 单次检测期间 Stop 取消 Runner，且不产生 Faulted 报警。
4. 连续期间 Single、Single 期间 Start 均立即拒绝。
5. 多个并发 Stop 等待同一 Completion，通信清理只执行一次。
6. 用户取消不报警，非取消异常进入 Faulted。
7. 故障或正常退出均最终释放租约。
8. Stop 超时进入 Faulted，且活动任务结束前不能取得新租约。
9. 精确断言 Starting、Running、Stopping、Stopped/Faulted 的顺序。

### 12.3 配方试运行测试

1. 生产占用时，试运行不得保存或切换配方，也不得调用 Runner。
2. 试运行占用时，生产 Start 和 Single 立即拒绝。
3. Reset 重跑期间持有同一租约。
4. 正常结束、取消、异常和页面退出均释放租约。

### 12.4 UI 测试

1. 新增状态映射为正确文本和颜色。
2. ActiveRun 变化后正确更新命令可用状态。
3. Stop 在单次和连续生产期间可用。
4. Rejected 显示占用模式，不进入全局异常处理。

## 13. 预计修改范围

主要文件：

- `VisionStation.Application/InspectionRunGate.cs`（新增）
- `VisionStation.Application/ProductionCoordinator.cs`
- `VisionStation.Application/InspectionServices.cs` 或其 `IInspectionRunner` 定义文件
- `VisionStation.Domain/Models.cs`
- `VisionStation.Domain/DeviceConfigurationModels.cs`
- `VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs`
- `VisionStation.Client/App.xaml.cs`
- `VisionStation.Client/ViewModels/ProductionDashboardViewModel.cs`
- `VisionStation.Client/ViewModels/ShellWindowViewModel.cs`
- `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs`
- `VisionStation.Client/Views/ProductionDashboardView.xaml`
- `VisionStation.Application.Tests/InspectionRunGateTests.cs`（新增）
- `VisionStation.Application.Tests/ProductionCoordinatorTests.cs`（新增）
- `VisionStation.Vision.UI.Tests` 中对应的 ViewModel/状态测试

不进行无关的格式化、命名重写或大规模 ViewModel 拆分。

## 14. 完成标准

1. 所有检测执行入口都必须持有当前有效租约。
2. 并发冲突立即返回 Rejected，永不排队。
3. 单次、连续、配方试运行之间不能重叠。
4. Stop 正常取消单次或连续任务，不产生 Faulted 报警。
5. Stop 超时不会错误释放租约或显示 Stopped。
6. 每个会话的通信和租约清理恰好执行一次。
7. UI 能显示 Starting、Stopping 和占用拒绝原因。
8. 新增测试经过明确的 Red-Green 循环。
9. Release 构建无错误，完整测试套件通过。
