# 配方试运行不可变快照一致性设计

**状态：** 已批准，待书面复核

**日期：** 2026-07-12

**适用方案：** `CVWork.sln`

**关联设计：** `2026-07-11-production-run-admission-design.md`

## 1. 背景

配方试运行已经通过 `IInspectionExecution` 获得全进程唯一 Session，但当前实现仍存在一个跨模块一致性缺口：

1. `RecipeManagementViewModel` 在准入后构建配方快照并保存到仓库。
2. `InspectionRunner` 收到的只有 `RecipeId`，执行前会再次从仓库读取配方。
3. `VariableCenterViewModel`、`VisionDebugViewModel` 等模块仍可保存同一个配方。

因此，UI 中被冻结的配方不一定是 Runner 实际执行的配方。外部保存如果发生在“试运行保存”和“Runner 读取”之间，Runner 会执行外部版本；Reset 如果再次保存旧快照，又可能覆盖已经成功保存的外部更新。

最终质量复审还发现两个独立的状态真相问题：

- deferred refresh 已启动后再离开页面，会被离页时推进的 generation 淘汰，但 pending 已经清空。
- 暂停状态下并行进入 Resume 分支时，异常 Catch 会把仍持有 Session 的 owner 错标为空闲并解冻编辑区。

## 2. 目标

1. 配方试运行实际执行准入时冻结的不可变配方对象，不在执行前按 ID 二次读取可变仓库。
2. `InspectionResult.RecipeId`、记录和追溯仍使用真实业务配方 ID，不创建临时配方身份。
3. Reset 只派生下一次 attempt 的运行值，不重复保存基础配方，不覆盖外部模块的更新。
4. 现有只传 `RecipeId` 的生产、单次检测和二次开发调用保持兼容。
5. 请求携带快照时，快照身份与 `RecipeId` 不允许含糊或不一致。
6. 只有持有 Session 的 owner 生命周期可以复位 `IsTestRunning` 和 `IsTestRunPaused`。
7. 已知的 deferred refresh 在离开页面后仍能完成；配方切换、完整加载和清空仍能淘汰真正过期的结果。
8. 所有新不变量都由行为测试锁定，并保持接口便于二次开发。

## 3. 非目标

- 本次不实现通用的配方版本库、乐观并发控制或跨进程事务。
- 本次不禁止操作员在试运行期间从其它页面编辑配方；这些更新允许保存，但不能改变当前 Session 的不可变运行快照。
- 本次不改变连续生产每个周期按当前配方 ID 读取仓库的既有语义。
- 本次不把 `Recipe` 序列化进日志、数据库或进程间消息。
- 本次不重构所有配方写入者或引入全局 repository lock。

## 4. 方案比较

### 4.1 在运行期间拒绝所有配方写入

让 Variable Center、Vision Debug、Calibration 和未来所有 writer 注入 `IInspectionExecution`，活动 Session 存在时禁止保存。它会把执行互斥扩散到每个写入入口，第三方扩展仍可能绕过，而且用户无法在运行期间准备下一版配方。

### 4.2 保存唯一临时配方并按临时 ID 执行

该方案可以隔离仓库覆盖，但临时 ID 会进入配方列表、检测记录和图像追溯；异常清理还会留下垃圾配方。为了恢复业务 ID，还需要额外的 ID 映射协议。

### 4.3 请求携带可选不可变快照（采用）

在 `InspectionRequest` 增加可选 `RecipeSnapshot`。快照存在时 Runner 直接使用它；快照不存在时保留现有 `RecipeId -> repository` 行为。

该方案把“一次执行究竟使用哪个配方”放回执行请求本身，既不扩散 writer 锁，也不制造临时身份。调用者可以明确选择动态 ID 读取或不可变快照，二次开发语义最清晰。

## 5. 公共契约

`InspectionRequest` 增加：

```csharp
/// <summary>
/// 获取本次检测使用的不可变配方快照；null 时按 RecipeId 从仓库解析。
/// </summary>
public Recipe? RecipeSnapshot { get; init; }
```

契约不变量：

- `RecipeSnapshot is null`：保持现有行为。`RecipeId` 非空时按 ID 查询，空时回退到当前配方。
- `RecipeSnapshot is not null`：Runner 不调用配方仓库，直接规范化并执行该快照。
- 快照存在时，`RecipeSnapshot.Id` 必须为非空白业务 ID；否则在任何下游读取或执行副作用前抛出 `ArgumentException`。
- 快照存在且 `RecipeId` 为空：逻辑 ID 取 `RecipeSnapshot.Id`。
- 快照存在且 `RecipeId` 非空：必须与 `RecipeSnapshot.Id` 按忽略大小写相等；否则在任何设备、通信或记录副作用前抛出 `ArgumentException`。
- 结果、记录和追溯始终使用最终快照自身的业务配方 ID。
- Runner 只能从快照派生新的 record/集合，不得修改请求持有的对象图。
- `RecipeSnapshot` 是逻辑不可变契约：调用者在提交 `ExecuteAsync` 后不得修改其集合；Recipe Management 必须用脱离 UI 行对象的新数组构建快照。

该属性是可选扩展，因此现有对象初始化器和第三方调用者无需修改。

## 6. Runner 解析规则

`InspectionRunner` 将配方解析集中到一个小型 helper：

1. 如果请求带快照，先验证 ID 一致性，再返回 `RecipeSnapshot.WithNormalizedFlows()`。
2. 否则保留现有仓库查询和 current fallback。
3. 配方解析完成后，现有配置读取、变量绑定、流程执行、结果记录逻辑不变。

身份不一致属于调用者编程错误，不应静默选择其中一个 ID，也不应回退仓库。

## 7. Recipe Management 数据流

### 7.1 准入与基础保存

试运行顺序保持“先准入、后副作用”：

1. `TryBegin(RecipeTest)`。
2. Rejected 立即返回，不保存、不切换、不连接、不启动 RunControl。
3. Acquired 后在首个 await 前构建一次基础结构快照 `runRecipeSnapshot`。
4. 仅在完整运行生命周期开始时，把基础快照保存并设为当前配方一次。

这一次保存代表用户明确点击试运行时对本页编辑内容的提交。之后的 Reset attempt 不再保存同一业务配方。

基础保存使用 lifetime token，而不是 attempt token。若用户在基础保存尚未完成时按 Reset，Reset 被记录为待处理请求，但不取消或重复这次基础保存；保存完成后，首个真正执行的 attempt 直接使用默认变量值快照。导航离开仍可通过 lifetime token 取消基础保存。这样保留“Reset 后从默认值开始”的用户语义，同时不制造第二次业务配方写入。

基础保存阶段尚未进入可控制 attempt：Pause/Resume 必须禁用且方法内拒绝；Reset 只锁存 ViewModel 本地请求，不调用全局 `IInspectionRunControl`。只有 `BeginRun()` 成功后，Pause/Resume/Reset 才能触碰共享 RunControl 和 attempt cancellation。这样保存失败或离页取消不会把 pause/reset 状态泄漏给下一次生产。

### 7.2 attempt 执行

- 基础保存期间未收到 Reset 时，第一次 attempt 使用 `runRecipeSnapshot`。
- Reset（包括基础保存期间锁存的 Reset）从同一个基础结构快照派生 `CurrentValue = DefaultValue` 的 `attemptRecipeSnapshot`。
- 每次 `Session.ExecuteAsync` 同时传入真实 `RecipeId` 和对应的 `RecipeSnapshot`。
- Session、业务配方 ID、流程结构和配方中保存的设备引用在整个试运行生命周期内保持一致；设备配置仓库本身仍按既有 Runner 语义读取。
- 外部 writer 后续保存同 ID 的配方不会影响已提交给 Session 的快照。

### 7.3 外部更新

运行期间收到的 `RecipeChangedEvent` 继续合并为一个 deferred refresh。由于 Reset 不再重复保存基础配方，外部更新不会被旧快照覆盖；owner 清理后读取仓库中的最新版本并投影变量。

本设计不解决两个 writer 同时首次保存时的通用 last-writer-wins 问题；它只保证一次已准入执行不再受后续仓库变化影响，也不使用 Reset 回写覆盖后续变化。

## 8. 状态与导航真相

### 8.1 Catch 只报告，不迁移 owner 状态

`TestRunRecipeCommand.Catch` 只负责：

- 通过 `IUiDispatcher` 更新错误状态文本。
- 安全记录错误；日志订阅者异常不能继续传播。

它不得修改 `IsTestRunning`、`IsTestRunPaused`、CTS、Session 引用或命令所有权。只有取得 Session 的主调用在 `finally` 中复位这些状态。

因此，暂停后的 Resume 即使 `Resume()` 或日志失败，页面仍保持运行中和编辑冻结；用户仍可通过导航取消，由 owner 完成清理。

Recipe Management 的 TestRun 与 Flow Editor 两个 Prism async command Catch 都必须通过 `IUiDispatcher` 更新绑定状态；Prism Catch 所在线程不视为 UI 线程。dispatcher 或日志失败只被安全记录，不能反向改变 Session owner 真相。

### 8.2 三个显式控制阶段

配方试运行区分三个不互相替代的状态：

- `IsTestRunning`：Session owner 生命周期，控制配方编辑冻结和整体状态展示。
- `isTestRunAttemptActive`：`BeginRun()` 成功到 attempt 离开的窗口，控制 Pause/Resume 以及 Reset 对共享 RunControl 的调用。
- `acceptsTestRunReset`：从基础保存开始到进入最终 cleanup 前的窗口；最终 Disconnect cleanup 期间 Reset 命令禁用，方法内也拒绝。

attempt phase 和 Reset 接受 phase 的命令通知异常必须被隔离；内部真相先更新，`CanExecuteChanged` 订阅者异常不得打断取消完成等待、CTS 释放、下一 attempt 或 owner cleanup。

### 8.3 离页不淘汰已知刷新

`OnNavigatedFrom` 不再推进 recipe refresh generation。已启动的有限刷新可以在页面离开后安全完成。

以下权威状态变化仍推进 generation：

- 选择其它配方；
- 完整 `ApplyRecipe`；
- `ClearEditor`；
- 新的外部刷新请求。

因此 A→B→A 的旧 A 结果仍会被淘汰，而单纯离开页面不会丢失已经承诺的同步。

## 9. 错误处理

- 快照 ID 不一致：`ArgumentException`，发生在配方仓库、设备和记录副作用之前。
- 基础配方首次保存失败：试运行失败并由 owner 清理 Session；不连接、不执行。
- deferred refresh 瞬时失败：同 generation 固定最多两次读取；新 generation 可以随时淘汰旧尝试。
- Resume/Catch 报错：只报告，绝不伪造空闲状态。
- `CanExecuteChanged` 订阅者报错：安全记录，不能传播进 attempt/finally 生命周期。
- EndRun、Disconnect、日志和 Session Dispose 继续保持分段隔离及既有有界清理语义。

## 10. 测试设计

### 10.1 Application

- 请求带快照时，Runner 不访问 `IRecipeRepository.GetAsync/GetCurrentAsync`，实际结果来自快照。
- 快照 ID 为空白时，在配方仓库、配置、设备、追溯和记录访问前失败。
- `RecipeId` 与快照 ID 不一致时，在任何执行副作用前失败。
- 请求 ID 与快照 ID 仅大小写不同时允许执行，结果、追溯和记录统一使用快照中的业务 ID。
- 不带快照的现有请求仍按 ID/current 读取仓库。

### 10.2 Recipe Management

- 首次 attempt 使用冻结的 CurrentValue；Reset 后第二次使用 DefaultValue。
- 整个生命周期只保存业务配方一次，Reset 不再覆盖外部 writer 的新版本。
- Reset 发生在基础保存期间时仍只保存一次；保存完成后首个执行 attempt 直接使用 DefaultValue。
- 外部 writer 在基础保存后更新仓库，即使发生 Reset，最终 deferred replay 仍读取并显示外部版本。
- 请求同时携带相同的 RecipeId 和 RecipeSnapshot；两个 attempt 共用一个 Session。
- cleanup replay 已启动后再离页，读取完成仍应用；配方切换仍能淘汰旧结果。
- 暂停后的 Resume 抛错时，主运行仍为 running、编辑区仍冻结、Session 未提前释放；导航取消后由 owner 复位。
- 基础保存期间 Pause 不可执行，Reset 只锁存且不调用共享 RunControl；离页取消不残留控制状态。
- attempt phase 通知订阅者抛错时，Reset 仍能完成第二 attempt 和唯一 cleanup。
- 最终 Disconnect cleanup 期间 Reset 的 CanExecute 与方法内执行均被拒绝。
- TestRun 与 Flow Editor 的后台异步失败都通过同一个 UI dispatcher 投影状态。

### 10.3 回归矩阵

- Task 7 定向测试与并发压力循环。
- Vision UI 全量测试。
- Application Debug/Release 全量测试。
- Client Debug build。
- `git diff --check`、跟踪构建产物检查和工作树检查。

## 11. 二次开发约束

- 需要稳定复现配方的单次、预览、标定或回放入口，应在准入后构建快照并通过 `RecipeSnapshot` 传入。
- 需要每周期跟随当前配方变化的连续生产入口，继续只传 `RecipeId` 或使用 current fallback。
- 不要为了快照执行创建临时业务 RecipeId。
- 不要在命令 Catch 中修改 Session owner 状态。
- 新 repository 实现必须遵守 CancellationToken；deferred refresh 仍有固定次数上限和 generation 防迟到投影。

## 12. 完成标准

1. 三个最终质量复审 Important 都有确定性 RED 和 GREEN 证据。
2. Application 与 Recipe Management 行为测试锁定快照优先、身份校验、Reset 不覆写、owner 状态真相和导航交错。
3. 原 Task 7 规格复审和质量复审均为 Ready。
4. 全量验证通过，工作区干净，提交已推送。
