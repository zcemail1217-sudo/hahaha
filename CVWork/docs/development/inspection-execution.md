# 检测执行二次开发指南

本指南面向需要新增单次检测、循环检测、配方预览、标定或回放入口的开发者。目标是让扩展代码只依赖稳定的业务接口，不需要了解全局准入、执行权限和释放校验的内部实现。

## 唯一业务入口与组合根

业务代码对“检测执行能力”只注入 `IInspectionExecution`；状态呈现、日志等调用方职责仍按模块需要单独注入：

```csharp
using VisionStation.Application;

namespace InspectionExtensionExample;

public sealed class CalibrationInspectionService
{
    private readonly IInspectionExecution _execution;

    public CalibrationInspectionService(IInspectionExecution execution)
    {
        _execution = execution;
    }
}
```

这只是构造注入骨架；后文“新增单次入口”给出同名类型的完整版本。二次开发时用完整版本替换骨架，不要把两段同名类型同时粘贴到同一项目。

`InspectionExecution` 的具体实现依赖多个设备、配置、视觉、存储和运行控制服务。这些依赖只由应用组合根/DI 装配；扩展模块不直接构造具体实现，更不复制当前的十四参数具体构造函数，也不复制内部执行器、全局锁或 Session 状态。当前组合根将该接口注册为单例；测试和二次开发对检测执行能力仍只依赖这个接口。所有业务入口必须解析到这一个共享 singleton；该保护是进程内边界，不是跨进程或跨工控机分布式锁。

这条边界有两个直接收益：

- 新运行模式只增加自己的模式值、编排代码和行为测试，不修改核心执行实现。
- 所有入口共用同一个全进程准入真相，不会因某个页面自建锁而出现绕过。

## 核心不变量

### 准入与零业务副作用拒绝

- 在所有入口共享组合根的同一 `IInspectionExecution` singleton 时，当前进程任意时刻最多有一个活动 `IInspectionSession`。
- `TryBegin` 是立即、不排队的准入操作：成功返回 `RunAdmission.Acquired`，已被占用则返回 `RunAdmission.Rejected`。不会在旧 Session 结束后暗中启动这次请求。
- “立即”表示准入判断不等待当前 Session，不代表硬实时上限。Acquired 后的 `Changed` 在返回前同步发布，因此缓慢订阅者仍可以延长 `TryBegin` 返回时间；订阅者不应在回调中做阻塞工作。
- 非法的 Mode 或空白 `EntryPoint` 是编程错误，`TryBegin` 会抛出 `ArgumentException`；占用冲突是正常控制结果，不用异常表达。
- 必须先处理 `Rejected`，再保存配方、切换 current、连接设备、设置 PLC 或写检测记录。因此被拒绝的请求对这些业务对象必须保持零副作用。

标准顺序是：

```text
构造 Intent → TryBegin → 立即处理 Rejected → 准备请求/连接设备 → ExecuteAsync → DisposeAsync
```

### Session 的执行与释放

- 一个 Session 可以顺序执行多次，用于连续生产或 Reset 重跑。
- 同一 Session 不能并发调用 `ExecuteAsync`。第二个并发调用会在配方、设备和持久化副作用之前抛出 `InvalidOperationException`。
- 已请求释放或已释放的 Session 不再允许执行，并在副作用之前抛出 `ObjectDisposedException`。
- Session 必须异步释放；执行还在进行时，释放会等待该执行离开后再交还全局准入。没有额外 UI owner 状态时优先使用 `await using`。
- `DisposeAsync` 成功完成才是“这个 owner 已释放”的边界。如果释放失败且 `Current` 仍是自己的 Session，调用方必须 fail closed：继续报告占用，不解冻编辑，不伪造空闲。
- 不要把内置实现的重复释放行为当成所有第三方 `IInspectionSession` 的公共承诺。释放失败后不能盲目再调用一次；应等待实现特定恢复或外部释放确认。

### 事件语义

- `Changed` 在执行权取得或释放后发布新的 `Current`。
- `RunCompleted` 只在一次已准入执行成功完成后发布，事件值与 `ExecuteAsync` 返回的 `InspectionRunResult` 相同。取消或执行异常不发布该事件。
- `RunCompleted` 发布时 Session 仍由调用方持有，而且事件参数不携带 SessionId。需要关联 owner 的 UI/诊断代码应使用自己保存的 SessionId 和当前 `Current`，不从 Mode 或结果猜测。
- 执行模块隔离每个 `Changed` 和 `RunCompleted` 订阅者的异常；一个订阅者失败不阻止其它订阅者，也不改变运行结果和 Session 生命周期。
- 订阅者异常会被隔离，但缓慢的同步订阅者仍会延长发布路径：`Changed` 可延长准入/释放返回，`RunCompleted` 可延长 `ExecuteAsync` 返回。回调应快速投影或转交工作。
- 事件不承诺在 UI 线程上回调。UI 订阅者必须自行切换 dispatcher。

### SessionId 所有权与 ABA 防护

有 Start/Stop 生命周期的编排器必须保存自己取得的 `session.Run.SessionId`，并用它与 `IInspectionExecution.Current?.SessionId` 比较。不通过 Mode Key、DisplayName 或 EntryPoint 猜测 owner。

这个规则防止 ABA：旧 Session A 结束后，同一模式可能已启动新 Session B。A 的迟到回调不得清除 B 的状态。判断应使用唯一 `SessionId`，并在切换到 UI dispatcher 后重新读取当前 owner，不盲信排队前捕获的旧快照。`ProductionSnapshot.ActiveSessionId` 只是 `ProductionCoordinator` 对这条通用所有权规则的投影。

## 配方解析与不可变快照

调用方必须明确选择“动态读取”还是“稳定执行”：

| 场景 | `InspectionRequest` | 解析语义 |
| --- | --- | --- |
| 连续生产每周期需要跟随仓库最新配方 | 只设置 `RecipeId` | 按 ID 读取；ID 为空或指定 ID 不存在时回退到 current |
| 预览、标定、回放、配方试运行需要稳定复现 | 同时设置 `RecipeId` 和 `RecipeSnapshot` | 直接规范化并执行快照，完全绕过 `IRecipeRepository` 的所有成员 |

快照路径的身份规则：

- `RecipeSnapshot.Id` 必须是非空白的真实业务配方 ID。
- `RecipeId` 非空时，它必须与 `RecipeSnapshot.Id` 按 `StringComparison.OrdinalIgnoreCase` 相等。不一致会在配方仓库、设备、通信、图像追溯和记录副作用前抛出 `ArgumentException`。
- `RecipeId` 为空但快照存在时，最终业务 ID 使用快照的 ID。
- `InspectionRunResult.Recipe.Id`、`InspectionRunResult.Result.RecipeId`、图像追溯和检测记录都使用最终快照的业务 ID，不创建临时配方身份。

需要冻结 UI 当前编辑值时，在 `RunAdmission.Acquired` 之后、第一个异步副作用之前调用 snapshot builder。这样准入被拒绝时不保存、不连接设备，准入成功时又能把当前 UI 值冻结为本次 Session 的输入。如果编排服务需要接收 UI builder，传入 `Func<InspectionRequest>` 并只在 Acquired 分支调用它。

“快照绕过仓库”只指 `IRecipeRepository`。后续检测仍会按现有流程读取设备配置、调用相机/轴/PLC/通信与视觉执行，并在 `ProcessOnly == false` 时写图像追溯和检测记录。因此快照是配方输入一致性，不是整次检测的无副作用模式。

### 最小可复制快照请求

下面的工厂方法为 ROI 点、工具参数、流程列表和运行变量创建全新数组/字典；没有把 UI 可编辑集合放进请求。

```csharp
using System;
using System.Collections.Generic;
using VisionStation.Domain;

namespace InspectionExtensionExample;

public static class SnapshotRequestFactory
{
    public static InspectionRequest CreateCalibrationPreview()
    {
        var roi = new RoiDefinition
        {
            Id = "calibration-roi",
            Name = "标定区域",
            Shape = RoiShapeKind.Polygon,
            Points = new Point2D[]
            {
                new(10, 10),
                new(200, 10),
                new(200, 160),
                new(10, 160)
            }
        };

        var tool = new VisionToolDefinition
        {
            Id = "calibration-locate",
            Name = "标定定位",
            Kind = VisionToolKind.TemplateLocate,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Threshold"] = "0.82"
            }
        };

        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "CalibrationPreview",
            Rois = new RoiDefinition[] { roi },
            Tools = new VisionToolDefinition[] { tool }
        };

        var snapshot = new Recipe
        {
            Id = "calibration-v2",
            Name = "标定配方 V2",
            ProductCode = "CAL-V2",
            CurrentFlowId = flow.Id,
            Flows = new VisionFlowDefinition[] { flow },
            Rois = new RoiDefinition[] { roi },
            Tools = new VisionToolDefinition[] { tool }
        };

        return new InspectionRequest
        {
            RecipeId = snapshot.Id,
            RecipeSnapshot = snapshot,
            BatchId = DateTimeOffset.Now.ToString("yyyyMMdd"),
            OperatorName = Environment.UserName,
            ProcessOnly = true,
            RuntimeVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Calibration.OffsetX"] = "0",
                ["Calibration.OffsetY"] = "0"
            }
        };
    }
}
```

`Recipe` 是 record，但 `with` 是浅复制。真实 UI 的 snapshot builder 必须为所有可达列表、数组和字典创建新对象，包括流程中的 ROI、Points、Tools 和 `VisionToolDefinition.Parameters`。提交 `Session.ExecuteAsync` 后，调用方不得再修改快照可达的集合或字典。该约束是逻辑不可变契约，不是由 `IReadOnlyList<T>` 自动提供的深度冻结。

快照保证本次执行输入稳定，不等价于持久化事务。当前 `JsonRecipeRepository` 先写同目录临时文件，写入成功且未取消时再原子移动覆盖目标，因而对取消和序列化失败安全。它没有承诺突然断电后“最新一次写入”已耐久到存储介质，也不是“配方文件 + current 指针文件”的双文件事务，不提供并发 writer 的版本校验或乐观并发控制。

## 新增单次入口

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
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

这个入口没有额外 owner 状态，所以 `await using` 是最小且正确的释放方式。如果页面必须在 Session 释放成功后才解冻编辑，请使用后文的显式释放模式。

## 循环执行

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using VisionStation.Application;
using VisionStation.Domain;

namespace InspectionExtensionExample;

public static class BatchInspectionService
{
    private static readonly InspectionRunMode BatchPreview =
        new("batch.preview", "批量预检");

    public static async Task RunAsync(
        IInspectionExecution execution,
        Func<InspectionRequest> requestFactory,
        Action<InspectionRunResult> onCompleted,
        Action<string> showStatus,
        CancellationToken cancellationToken)
    {
        var admission = execution.TryBegin(new InspectionRunIntent(
            BatchPreview,
            nameof(BatchInspectionService)));

        if (admission is RunAdmission.Rejected rejected)
        {
            var active = rejected.Rejection.Active;
            showStatus(
                $"{active.Intent.Mode.DisplayName}（{active.Intent.EntryPoint}）正在占用检测执行");
            return;
        }

        await using var session = ((RunAdmission.Acquired)admission).Session;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await session.ExecuteAsync(
                requestFactory(),
                cancellationToken);
            onCompleted(result);
        }
    }
}
```

循环中每次只 `await` 一个 `ExecuteAsync`，因此同一 Session 上不会出现并发执行。`requestFactory` 可以每周期创建只含 `RecipeId` 的动态请求，也可以为需要稳定重现的批处理创建独立快照请求。

## Reset 重跑

Reset 需要同时解决三个竞态：Reset 可能发生在 attempt 发布取消源之前；执行器可能忽略已取消 token 并迟到返回成功；Reset 还可能发生在执行返回与 attempt 清理之间。下面示例用单调递增的 reset version 做最终仲裁，并在释放 attempt `CancellationTokenSource` 之前等待 `CancelAsync` 完成。`RequestReset()` 返回 `true` 表示请求已进入本次运行的正常完成仲裁；返回 `false` 表示完成门已经关闭。生命周期取消或非 Reset 的真实异常优先级更高，可以终止本次运行，调用方不得把 `true` 理解成对致命故障的重试承诺。

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using VisionStation.Application;
using VisionStation.Domain;

namespace InspectionExtensionExample;

public sealed class RecipePreviewService
{
    private readonly object _attemptGate = new();
    private readonly IInspectionExecution _execution;
    private CancellationTokenSource? _attemptCancellation;
    private Task _attemptCancellationCompletion = Task.CompletedTask;
    private long _resetVersion;
    private bool _acceptsReset;

    public RecipePreviewService(IInspectionExecution execution)
    {
        _execution = execution;
    }

    public bool RequestReset()
    {
        lock (_attemptGate)
        {
            if (!_acceptsReset)
            {
                return false;
            }

            _resetVersion++;
            if (_attemptCancellation is null)
            {
                return true;
            }

            var currentCancellation = _attemptCancellation.CancelAsync();
            _attemptCancellationCompletion = Task.WhenAll(
                _attemptCancellationCompletion,
                currentCancellation);
            return true;
        }
    }

    public async Task<InspectionRunResult?> RunAsync(
        Func<bool, InspectionRequest> requestFactory,
        Action<string> showStatus,
        CancellationToken lifetimeToken)
    {
        var admission = _execution.TryBegin(new InspectionRunIntent(
            InspectionRunModes.RecipeTest,
            nameof(RecipePreviewService)));

        if (admission is RunAdmission.Rejected rejected)
        {
            var active = rejected.Rejection.Active;
            showStatus(
                $"{active.Intent.Mode.DisplayName}（{active.Intent.EntryPoint}）正在占用检测执行");
            return null;
        }

        var session = ((RunAdmission.Acquired)admission).Session;
        long initialResetVersion;
        lock (_attemptGate)
        {
            initialResetVersion = _resetVersion;
            _acceptsReset = true;
        }

        InspectionRunResult? finalResult;
        try
        {
            finalResult = await RunAttemptsAsync(
                session,
                requestFactory,
                showStatus,
                initialResetVersion,
                lifetimeToken);
        }
        finally
        {
            lock (_attemptGate)
            {
                _acceptsReset = false;
                _attemptCancellation = null;
            }

            await session.DisposeAsync();
        }

        return finalResult;
    }

    private async Task<InspectionRunResult> RunAttemptsAsync(
        IInspectionSession session,
        Func<bool, InspectionRequest> requestFactory,
        Action<string> showStatus,
        long initialResetVersion,
        CancellationToken lifetimeToken)
    {
        while (true)
        {
            lifetimeToken.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken);

            long observedResetVersion;
            lock (_attemptGate)
            {
                observedResetVersion = _resetVersion;
                _attemptCancellation = attempt;
                _attemptCancellationCompletion = Task.CompletedTask;
            }

            InspectionRunResult? attemptResult = null;
            try
            {
                attemptResult = await session.ExecuteAsync(
                    requestFactory(observedResetVersion != initialResetVersion),
                    attempt.Token);

                // 执行器可能忽略取消并迟到返回成功。
                attempt.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (
                WasResetAfter(observedResetVersion) &&
                !lifetimeToken.IsCancellationRequested)
            {
                // Reset 是控制流；清理 attempt 后由最终版本仲裁决定重跑。
            }
            finally
            {
                Task cancellationCompletion;
                lock (_attemptGate)
                {
                    if (ReferenceEquals(_attemptCancellation, attempt))
                    {
                        _attemptCancellation = null;
                    }

                    cancellationCompletion = _attemptCancellationCompletion;
                    _attemptCancellationCompletion = Task.CompletedTask;
                }

                await cancellationCompletion;
            }

            lifetimeToken.ThrowIfCancellationRequested();

            bool restartRequested;
            lock (_attemptGate)
            {
                restartRequested = _resetVersion != observedResetVersion;
                if (!restartRequested)
                {
                    // 与 RequestReset 使用同一边界：之后到达的 Reset 明确被拒绝，不会丢失。
                    _acceptsReset = false;
                }
            }

            if (restartRequested)
            {
                showStatus("Reset 已接受，使用默认值创建下一次快照");
                continue;
            }

            return attemptResult!;
        }
    }

    private bool WasResetAfter(long observedResetVersion)
    {
        lock (_attemptGate)
        {
            return _resetVersion != observedResetVersion;
        }
    }
}
```

`requestFactory(bool useResetDefaults)` 每次必须创建新 `InspectionRequest`。`useResetDefaults == true` 时，它应从本次会话的基础结构快照派生 `CurrentValue = DefaultValue` 的新快照，而不是把旧基础快照再保存到仓库。上例的 `_attemptGate` 只保护这个服务自己的 Reset/attempt 交接，不是第二套全局准入。

正常完成路径上，`RequestReset` 与最终 return 使用同一把 `_attemptGate`：Reset 先取得 gate 就触发下一次 attempt，最终仲裁先关闭 gate 就使后续 Reset 明确返回 `false`。若生命周期取消或真实异常正在终止运行，它们优先于 Reset；UI 应同时关闭 Reset 命令，并按取消/异常通道呈现结果。

除了 acceptance gate，下面两个执行细节也不能删除：

1. `ExecuteAsync` 返回后立即执行 `attempt.Token.ThrowIfCancellationRequested()`。
2. `finally` 清除 attempt 并等待 `CancelAsync` 完成后，再与 `RequestReset` 在同一同步边界内读取 reset version 并决定返回或重跑。

前者处理“已取消却迟到成功”，后者处理“成功返回与 attempt 清理之间又收到 Reset”。仅依赖 `OperationCanceledException` 会丢失这两类竞态。

## 取消、异常、Stop 与 owner 真相

### 正常取消和真实异常

只有已知生命周期 token 被取消时，`OperationCanceledException` 才是正常控制结果。其它异常需要记录原始信息、呈现给操作员，并保留异常语义。下面是调用方方法体内的处理片段：

```csharp
try
{
    await service.RunAsync(request, lifetimeToken);
}
catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
{
    showStatus("检测已取消");
}
catch (Exception exception)
{
    showStatus($"检测失败：{exception.Message}");
    throw;
}
```

不要把所有 `OperationCanceledException` 都吞掉，也不要在普通 Catch 中提前清除 owner。最终释放仍由取得 Session 的编排路径负责。

### Stop UI 超时不等于释放

`ProductionCoordinator.StopAsync` 的等待时间可以有上限，目的是避免 UI 无限等待。`ProductionSettingsConfiguration.StopWaitTimeoutMs` 默认为 10000 ms，配置归一化会把非正值恢复为默认值。`StopAsync` 的调用方 token 只能结束该调用方的等待；正在运行的统一后台清理仍必须继续到完成边界。内部等待超时后：

- 本地 production operation/reservation 仍然有效，新的生产命令继续被它拒绝；“reservation 活跃”不等于 Session 已经附加。
- 如果 Session 已经附加，它仍是 production owner，`ProductionSnapshot.ActiveSessionId` 保存自己的 SessionId；UI 必须显示占用，直到统一清理确认释放。
- 如果 timeout 发生在 Session 附加前，`ActiveSessionId` 保持 `null`，绝不能借用全局 `Current`，因为全局 owner 可能是配方试运行等外部 Session B。
- 只有活动任务进入统一清理，并对已附加 Session 完成释放确认后，才能清除对应 owner 投影；没有附加 Session 的 reservation 也必须走同一 Completion 边界结束。

这个超时是软件等待边界，不证明设备运动已安全停止。软件取消不能代替独立的硬件急停、安全回路或设备制动策略。

生产清理还有一个必须保留的释放确认边界：如果 Session 释放失败且无法确认 `Current` 已离开原 SessionId（包括读取 `Current` 失败），或释放方法成功返回但 `Current` 仍是原 SessionId，`ProductionCoordinator` 将该 operation 标记为等待释放确认，保持 `Faulted + ActiveSessionId`，并拒绝新的生产运行。它不重试未知 Session 实现的 `DisposeAsync`，也不重复产生 cleanup 报警。若释放虽然返回异常，但实时 `Current` 已确认不再是原 SessionId，则 owner 已释放，清理失败仍进入 `Faulted`，但不保留旧 owner 投影。

后续 `IInspectionExecution.Changed` 只作为释放可能已发生的触发信号。Handler 先在 Coordinator 锁内快照当前 operation/Session，释放锁后安全读取实时 `Current`，再按保存的 SessionId 判断。只有 `Current` 已不再是原 SessionId 时，才为同一 operation 单调记录 release-observed；后来读到相同 SessionId 或读取失败都不反向清除已确认的观测，也不能凭空创建观测。

旧 operation 的 cleanup outcome、报警和 Completion 仍先完成提交。只有 release-confirmation-pending、`operation.Completion.IsCompleted` 和单调 release-observed 三者同时成立，Coordinator 才在再次校验 operation/Session 身份后清除旧 production operation 和本地 `ActiveSessionId` 投影。Completion 之后的最终协调只消费内部 flag，不再读取外部 `Current`。事件参数本身不是 owner 真相，旧的迟到 `Changed(null)` 不能释放当前 owner。

上述协调保留 `Faulted` 状态与已产生的 cleanup 报警。如果此时全局 `Current` 为空，下一次显式生产命令可重新准入；如果已有外部 Session B，生产准入返回指向 B 的 `Busy` 拒绝，不把 B 写入生产 `ActiveSessionId`，也不用旧 owner A 覆盖它。

正常执行且清理/释放全部成功时，仍按正常路径进入 `Stopped`；迟到的释放事件不能再把已成功结束的 operation 改成 `Faulted`。

### 需要编辑冻结时显式释放

如果调用方有 `IsRunning`/“编辑已冻结”等额外投影，不使用隐式 `await using` 收尾。在显式 `finally` 中等待一次 `DisposeAsync`，然后按自己保存的 SessionId 确认释放：

```csharp
using System;
using System.Threading.Tasks;
using VisionStation.Application;

namespace InspectionExtensionExample;

public sealed record InspectionSessionReleaseOutcome(
    bool OwnershipReleased,
    Exception? CleanupFailure);

public static class InspectionSessionRelease
{
    public static async Task<InspectionSessionReleaseOutcome> ReleaseOwnerAsync(
        IInspectionExecution execution,
        IInspectionSession session,
        Action<string> showStatus)
    {
        var ownedSessionId = session.Run.SessionId;
        Exception? disposeFailure = null;

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception exception)
        {
            disposeFailure = exception;
        }

        ActiveInspectionRun? current;
        try
        {
            current = execution.Current;
        }
        catch (Exception exception)
        {
            var confirmationFailure = new InvalidOperationException(
                "无法读取 Current 以确认会话释放。",
                exception);
            showStatus($"无法确认会话释放，仍保持占用：{exception.Message}");
            return new InspectionSessionReleaseOutcome(
                false,
                CombineFailures(disposeFailure, confirmationFailure));
        }

        var stillOwnsExecution = current?.SessionId == ownedSessionId;
        if (stillOwnsExecution)
        {
            var releaseFailure = disposeFailure ?? new InvalidOperationException(
                "DisposeAsync 已返回，但 Current 仍是本会话。");
            showStatus(
                $"会话释放未确认，仍保持占用：{releaseFailure.Message}");
            return new InspectionSessionReleaseOutcome(false, releaseFailure);
        }

        if (disposeFailure is not null)
        {
            showStatus(
                $"释放返回异常，但 owner 已不再是本会话：{disposeFailure.Message}");
        }

        return new InspectionSessionReleaseOutcome(true, disposeFailure);
    }

    private static Exception CombineFailures(Exception? first, Exception second) =>
        first is null
            ? second
            : new AggregateException(first, second);
}
```

调用方只在 `OwnershipReleased == true` 后解冻本功能的编辑区；为 `false` 时保持 fail closed，不再次调用未知实现的 `DisposeAsync`。`CleanupFailure` 非空时，无论 owner 是否已经释放，都必须把这个原始异常对象交给结构化日志/报警或上层聚合，不能只保留 `Message` 文本。`OwnershipReleased == true` 只表示旧 owner 已离开，不等于 cleanup 成功。如果 `Current` 已是另一个 SessionId，说明本功能的 owner 已结束，但全局 UI 仍应呈现新 owner，而不是宣布整个执行模块空闲。

## UI 订阅、dispatcher 与迟到投影

UI 对 `Changed` 和 `RunCompleted` 的接入至少有三层保护：

1. 回调先切换到该 UI 框架自己的 dispatcher，再修改绑定属性和集合。
2. owner 专属页面在 dispatcher 中重新读取 `IInspectionExecution.Current`，并用保存的 SessionId 做 owner 判断；全局监视页若要呈现所有运行结果，应把“不筛 owner”写成显式设计并单独测试。
3. 页面开始释放后关闭 projection gate、取消订阅，并拒绝已经排队的迟到回调。

最小回调形状如下；这是订阅者类中的成员方法片段，需与该页面自己的字段、订阅/退订和生命周期代码配合：

```csharp
private void OnExecutionChanged(
    object? sender,
    InspectionExecutionChangedEventArgs args)
{
    if (Volatile.Read(ref _projectionClosed) != 0)
    {
        return;
    }

    _uiDispatcher.Invoke(() =>
    {
        if (Volatile.Read(ref _projectionClosed) != 0)
        {
            return;
        }

        ApplyCurrentOwner(_inspectionExecution.Current);
    });
}

private void OnRunCompleted(object? sender, InspectionRunResult result)
{
    if (Volatile.Read(ref _projectionClosed) != 0)
    {
        return;
    }

    _uiDispatcher.Invoke(() =>
    {
        if (Volatile.Read(ref _projectionClosed) != 0)
        {
            return;
        }

        if (_inspectionExecution.Current?.SessionId != _ownedSessionId)
        {
            return;
        }

        ApplyResult(result);
    });
}
```

片段中的 `_ownedSessionId` 是页面在成功准入时保存的 `session.Run.SessionId`。两次 `Volatile` 检查能拒绝关门前后排队的迟到回调，但本身不是“等待已进入回调全部退出”的 drain 协议；如果 teardown 不能与 `Apply...` 并发，关闭投影和投影写入必须共用同一同步 gate，或显式等待在途回调排空。完整页面还需要在释放时取消两个订阅，并观察页面启动的轮询/后台任务。`VariableCenterViewModel` 当前的 `IDisposable` + `IAsyncDisposable` 生命周期、projection gate 和事件取消顺序可作为工程参考。它是一个 ViewModel 的私有实现模式，不是 `IInspectionExecution` 或 `IInspectionSession` 的公共契约；二次开发页面应根据自己的 UI 框架实现等价生命周期。

## `Action<string> showStatus` 的作用

示例中的 `Action<string> showStatus` 是调用方注入的 UI/日志呈现函数，不属于 `IInspectionExecution`。WPF 页面可以在该函数中通过 dispatcher 更新状态，后台服务可以改为结构化日志，测试可以传入记录消息的 delegate。不要为了呈现文本而把 UI 依赖放进执行模块。

## 新模式接入检查清单

1. `InspectionRunMode.Key` 使用稳定小写标识，只含小写字母、数字和 `.`/`_`/`-` 分隔符。
2. `DisplayName` 是操作员可读文本；`EntryPoint` 是实际调用 module/ViewModel 名称。业务上比较模式身份时比较 `Mode.Key`，不比较整个 `InspectionRunMode` record，因为 `DisplayName` 也参与 record equality 且可能本地化或调整。
3. 新功能对检测执行能力只注入 `IInspectionExecution`，具体实现仍由应用组合根装配；状态呈现、日志等调用方职责使用各自的抽象。
4. 在任何保存、current 切换、设备连接和记录写入前调用 `TryBegin`，并立即处理 `Rejected`。
5. 明确选择动态 `RecipeId` 或逻辑不可变 `RecipeSnapshot`；不混淆两者的一致性语义。
6. 多次执行时只在同一 Session 上顺序 `await`，不并发发起。
7. 有 Stop 生命周期时，编排器保存 Session、自己的 cancellation/completion 和 SessionId；UI 不持有全局锁或内部执行器。
8. 正常取消与真实异常分支处理；只有释放确认后才清除 owner 投影。
9. UI 事件订阅者切换 dispatcher，并实现取消订阅和迟到投影门。
10. 添加准入拒绝零副作用、顺序执行、取消、释放和 UI dispatcher 行为测试。

## 禁止事项

- 不绕过 `IInspectionExecution` 直接调用内部执行实现。
- 不在扩展功能中直接构造 `InspectionExecution`。
- 不复制第二套全局准入锁，不轮询或等待 Busy Session。
- 不在核心执行实现中按 Mode 写分支；新模式不应迫使旧模式修改。
- 不在 `Rejected` 前保存配方、连接设备或产生其它业务副作用。
- 不在同一 Session 上并发执行。
- 不在提交后修改 `RecipeSnapshot` 可达集合，不用浅 `with` 充当深快照。
- 不在 Stop 超时或 `DisposeAsync` 失败后伪造空闲，不盲目重试未知 Session 实现的释放。
- 不用软件取消代替硬件急停或设备安全停止。

## 测试清单

新入口至少覆盖以下行为，测试通过公共 seam 观察结果，不依赖私有锁或字段：

- 已有 Session 时新准入立即 `Rejected`，并且保存、配置、设备、通信、追溯和记录计数均为零。
- 释放后新请求可以取得；旧 Session 的迟到清理不能释放新 Session。
- 同一 Session 顺序执行多次成功；并发第二次执行在副作用前失败。
- `Changed` 和 `RunCompleted` 中一个订阅者抛异常时，其它订阅者、结果和释放仍正确。
- 调用方 token 取消不被记录为业务故障；非取消异常保留原始语义且仍释放。
- Stop 超时后 SessionId 和 owner 投影仍保留，活动任务结束前新准入继续被拒绝。
- `DisposeAsync` 未完成时 UI 仍显示占用；释放失败且 `Current.SessionId` 仍匹配时保持 fail closed。
- 生产释放待确认时，后续 `Changed` 触发且实时 `Current` 确认为 null 或外部 B 后，只清理旧 owner 投影，保留 `Faulted`/原 cleanup 报警，不重试 Dispose；B 不得被投影为生产 owner。
- 快照执行不访问 `IRecipeRepository` 任何成员，结果/追溯/记录的 RecipeId 都来自快照。
- 空白快照 ID 和身份不匹配在任何下游副作用前失败；仅大小写不同时允许。
- 动态 `RecipeId` 在有 ID 和空 ID/current fallback 两种情况下均保持兼容。
- 在没有被生命周期取消或真实异常终止时，Reset 发生在基础准备、正在执行、执行迟到成功以及 attempt 清理交错，已接受 Reset 都不丢失，且不重复保存基础配方；完成门关闭后的 Reset 明确返回拒绝。
- UI 事件通过 dispatcher 投影，页面释放后排队的旧回调不再修改绑定状态。

## 常见错误与排障

| 现象 | 优先检查 | 修正方向 |
| --- | --- | --- |
| 显示占用拒绝，但配方或设备状态已改变 | `TryBegin` 是否晚于保存/连接 | 把准入移到第一个业务副作用之前 |
| 第二个 `ExecuteAsync` 报正在执行 | 是否在同一 Session 上启动了多个未等待任务 | 改为顺序 `await`，不用并发集合启动周期 |
| UI 显示空闲，但 `Current` 仍是原 SessionId | 是否在 `DisposeAsync` 完成前清除了 owner | 恢复 fail-closed，只在释放确认后解冻 |
| Stop 超时后新运行仍被拒绝 | 原活动任务、SessionId 和设备清理是否完成 | 这是预期保护；定位不响应取消的设备/通信路径，不手工伪造释放 |
| Reset 已点击，旧 attempt 的成功结果却结束了会话 | 是否缺少 post-return token check、`CancelAsync` 完成等待或清理后 version 仲裁 | 恢复 Reset 示例的完整交接顺序 |
| 运行中快照随 UI 编辑变化 | snapshot builder 是否沿用 UI 集合或只用浅 `with` | 创建全新数组/字典，提交后不再修改 |
| 传了快照却仍访问配方仓库 | 请求的 `RecipeSnapshot` 是否为 null，执行代码是否绕过公共 seam | 从请求中传入有效快照并只通过 Session 执行 |
| 快照请求在执行前抛 `ArgumentException` | `RecipeId`、`RecipeSnapshot.Id` 是否空白或业务身份不一致 | 使用真实 ID；仅大小写可不同，不用临时 ID |
| UI 出现跨线程异常或离页后仍被刷新 | 订阅者是否切 dispatcher，释放时是否关闭投影门并取消订阅 | 在 dispatcher 内再检查生命周期和当前 owner |
| 成功回调没有触发 | 本次执行是否取消或抛异常 | `RunCompleted` 只表示成功完成；取消/异常走调用方的控制和错误通道 |

## 源码导航

| 职责 | 路径 |
| --- | --- |
| 公共准入、Session、Mode、Intent、拒绝和事件契约 | [`VisionStation.Application/Inspection/Execution/InspectionExecutionContracts.cs`](../../VisionStation.Application/Inspection/Execution/InspectionExecutionContracts.cs) |
| 全进程唯一准入、顺序执行与事件隔离实现 | [`VisionStation.Application/Inspection/Execution/InspectionExecution.cs`](../../VisionStation.Application/Inspection/Execution/InspectionExecution.cs) |
| 配方动态解析、快照身份校验和检测执行流程 | [`VisionStation.Application/InspectionServices.cs`](../../VisionStation.Application/InspectionServices.cs) |
| `InspectionRequest`、`Recipe`、流程/ROI/工具和结果领域类型 | [`VisionStation.Domain/Models.cs`](../../VisionStation.Domain/Models.cs) |
| `ProductionSettingsConfiguration` 与 Stop 等待默认值 | [`VisionStation.Domain/DeviceConfigurationModels.cs`](../../VisionStation.Domain/DeviceConfigurationModels.cs) |
| 具体实现的单例 DI 装配 | [`VisionStation.Client/App.xaml.cs`](../../VisionStation.Client/App.xaml.cs) |
| 生产单次/连续运行、Stop 超时与 `ActiveSessionId` 投影 | [`VisionStation.Application/ProductionCoordinator.cs`](../../VisionStation.Application/ProductionCoordinator.cs) |
| 配方试运行的快照、Reset attempt 和显式 owner 清理参考 | [`VisionStation.Client/ViewModels/RecipeManagementViewModel.cs`](../../VisionStation.Client/ViewModels/RecipeManagementViewModel.cs) |
| UI 事件取消、迟到投影门和 `IAsyncDisposable` 参考 | [`VisionStation.Client/ViewModels/VariableCenterViewModel.cs`](../../VisionStation.Client/ViewModels/VariableCenterViewModel.cs) |
| 生产 UI 所有权和拒绝文本投影 | [`VisionStation.Client/Presentation/ProductionRunUiState.cs`](../../VisionStation.Client/Presentation/ProductionRunUiState.cs) |
| JSON 配方同目录临时写入与原子覆盖 | [`VisionStation.Infrastructure/JsonRecipeRepository.cs`](../../VisionStation.Infrastructure/JsonRecipeRepository.cs) |
| Stop 等待配置归一化 | [`VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs`](../../VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs) |
| 公共 seam 的准入、并发、事件和释放行为测试 | [`VisionStation.Application.Tests/InspectionExecutionTests.cs`](../../VisionStation.Application.Tests/InspectionExecutionTests.cs) |
| 快照/动态 ID 解析及零副作用测试 | [`VisionStation.Application.Tests/InspectionRunnerRecipeResolutionTests.cs`](../../VisionStation.Application.Tests/InspectionRunnerRecipeResolutionTests.cs) |
| 生产编排、Stop、SessionId 和清理行为测试 | [`VisionStation.Application.Tests/ProductionCoordinatorTests.cs`](../../VisionStation.Application.Tests/ProductionCoordinatorTests.cs) |
| 配方试运行、Reset、Dispose 和 dispatcher 行为测试 | [`VisionStation.Vision.UI.Tests/RecipeManagementInspectionExecutionTests.cs`](../../VisionStation.Vision.UI.Tests/RecipeManagementInspectionExecutionTests.cs) |
| Variable Center 事件投影和释放行为测试 | [`VisionStation.Vision.UI.Tests/VariableCenterInspectionExecutionTests.cs`](../../VisionStation.Vision.UI.Tests/VariableCenterInspectionExecutionTests.cs) |
| JSON 取消/序列化失败安全测试 | [`VisionStation.Application.Tests/JsonRecipeRepositoryTests.cs`](../../VisionStation.Application.Tests/JsonRecipeRepositoryTests.cs) |
