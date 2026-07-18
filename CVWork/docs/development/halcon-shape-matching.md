# HALCON 比例缩放形状匹配二次开发指南

本文面向继续开发 `TemplateLocate` 和 `MultiTargetMatch` 的人。对上层业务暴露的边界是 `ITemplateMatchingService`、`ITemplateModelStore` 和 `ITemplateModelResourceManager`；上层不应引用 `HalconDotNet` 类型，也不应在 Tool、ViewModel 或配方层复制匹配算法。

## 1. 从公开入口开始

生产组合根通过 `HalconTemplateMatchingFactory.Create` 一次创建三个 backend、HALCON runtime probe、专用 worker、model cache 和资源管理器。下面是一个完整的公开 service 调用例子；其中 `RuntimePaths`、图像和 ROI 均由现有应用提供。

```csharp
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;

public sealed record TemplateExampleResult(
    TemplateLearningResult Learning,
    TemplateMatchBatchResult? Matching);

public static class TemplateExample
{
    public static async Task<TemplateExampleResult> LearnAndMatchAsync(
        RuntimePaths runtimePaths,
        HalconRuntimeConfiguration runtimeConfiguration,
        IAppLogService appLog,
        TemplateModelOwner owner,
        ImageFrame learningFrame,
        RoiDefinition wholeProductTemplateRoi,
        RoiDefinition? searchRoi,
        ImageFrame productionFrame,
        CancellationToken cancellationToken)
    {
        ITemplateModelStore store = new FileTemplateModelStore(runtimePaths);
        TemplateMatchingRuntime runtime = HalconTemplateMatchingFactory.Create(
            store,
            runtimeConfiguration,
            new AppLogDiagnosticSink(appLog));

        try
        {
            Dictionary<string, string> parameters =
                TemplateMatchingParameterCatalog.CreateStrictDefaults(
                    TemplateMatchCardinality.Single);

            var learningRequest = new TemplateLearningRequest(
                owner,
                learningFrame,
                wholeProductTemplateRoi,
                searchRoi,
                parameters);
            TemplateLearningResult learning = await runtime.Service.LearnAsync(
                learningRequest,
                cancellationToken);

            ReportResultDiagnostic(appLog, learning.Diagnostic);
            if (!learning.Success)
            {
                return new TemplateExampleResult(learning, null);
            }

            // learning.Parameters 包含新一代 .shm/.json 引用、校验和标准位姿。
            // 配方保存必须持久化这个完整字典，不能仍保存学习前的 parameters。
            var matchingRequest = new TemplateMatchingRequest(
                owner,
                productionFrame,
                searchRoi,
                learning.Parameters,
                TemplateMatchCardinality.Single,
                ExpectedCount: 1);
            TemplateMatchBatchResult matching = await runtime.Service.MatchAsync(
                matchingRequest,
                cancellationToken);

            ReportResultDiagnostic(appLog, matching.Diagnostic);
            return new TemplateExampleResult(learning, matching);
        }
        finally
        {
            // Service 拥有 HALCON handle、cache 和 worker；关闭时会先等在途调用退出。
            await runtime.Service.DisposeAsync();
        }
    }

    private static void ReportResultDiagnostic(
        IAppLogService appLog,
        TemplateMatchingDiagnostic? diagnostic)
    {
        if (diagnostic is null)
        {
            return;
        }

        appLog.Warning(
            "TemplateMatching",
            $"{diagnostic.Code}; Stage={diagnostic.FailureStage}; " +
            $"Details={diagnostic.TechnicalDetails ?? diagnostic.UserMessage}");
    }

    private sealed class AppLogDiagnosticSink : ITemplateMatchingDiagnosticSink
    {
        private readonly IAppLogService _appLog;

        public AppLogDiagnosticSink(IAppLogService appLog)
        {
            _appLog = appLog ?? throw new ArgumentNullException(nameof(appLog));
        }

        public void Warning(string source, string message)
        {
            _appLog.Warning(source, message);
        }

        public void Error(string source, string message)
        {
            _appLog.Error(source, message);
        }
    }
}
```

请同时遵守三个请求生命期规则：

- `TemplateModelOwner(RecipeId, FlowId, ToolId)` 必须与配方中的真实工具身份一致，三段均非空且不得带首尾空白。
- 构造请求时会快照参数字典和 ROI；`ImageFrame.Pixels` 是在请求期间借用的 buffer，完成前不得修改或复用。
- `TemplateLearningResult.Parameters` 是学习后的唯一完整配方状态。只保存 `Geometry` 或模型绝对路径都会破坏完整性契约。

`TemplateMatchingRuntime.Resources` 要与 `Service` 一起注入配方生命周期。不需要 HALCON 时可用 `TemplateMatchingService.CreateLegacyOnly()`；该工厂仅注册 OpenCV 和 Managed NCC，如果配方选了 HALCON，将稳定返回 `CONFIG_SERVICE_REQUIRED`，不会静默回退。

## 2. 三种 engine 的兼容边界

`engine` 大小写不敏感；缺失时为了旧配方兼容默认选 `OpenCv`。未知值必须 fail closed 为 `CONFIG_UNKNOWN_ENGINE`。

| 能力 | `ManagedNcc` | `OpenCv` | `Halcon` |
| --- | --- | --- | --- |
| 单目标 `Single` | 支持 | 支持 | 支持 |
| 精确数量 `ExactCount` | 不支持，返回 `CONFIG_UNSUPPORTED_MODE` | 支持 | 支持 |
| 学习入口 | `ITemplateMatchingService.LearnAsync` | 同左 | 同左 |
| 模型形式 | 旧参数内联模型 | 旧参数/资源模型 | 不可变 `.shm` + `.json` generation |
| 旋转搜索 | 无形状旋转契约 | 按 OpenCV 旧参数支持 | 支持，参数为 UI 顺时针角度 |
| 统一缩放搜索 | 不支持 | 不承诺 scaled-shape 变尺度搜索 | 支持 `scaleMin..scaleMax` |
| HALCON 六道硬门 | 不适用 | 不适用 | 支持 |
| 外部 runtime/许可 | 不需要 | 不需要 HALCON | 必须为锁定的 x64 runtime 与有效许可 |
| 无结果自动换 backend | 不支持 | 不支持 | 不支持 |

HALCON 目前只接受 `matchMode=Shape`。多目标时如配置了 `multiMatchMode`，它优先于 `matchMode`，但仍必须是 `Shape`。不要以“本次找不到”为条件改用另一 backend，否则同一配方的分数、位姿和 NG 语义会变成不可追溯。

## 3. HALCON 参数分类

### 3.1 引擎与数量

| 键 | 含义 | 约束 |
| --- | --- | --- |
| `engine` | backend 路由 | HALCON 必须为 `Halcon` |
| `matchMode` / `multiMatchMode` | 匹配模式 | HALCON 仅 `Shape` |
| `expectedCount` | `ExactCount` 期望数量 | `1..100`，必须与 request 的 `ExpectedCount` 相同 |
| `matchCount` | 旧多目标兼容键 | 仅在没有 `expectedCount` 时读取；新代码应写 `expectedCount` |

### 3.2 模型 generation 参数

`halcon.angleStartDeg`、`halcon.angleExtentDeg`、`halcon.scaleMin`、`halcon.scaleMax` 和 `halcon.numLevels` 参与 SHA-256 generation fingerprint。修改任意一项都必须重新学习；旧 `.shm` 不会被新搜索区间勉强使用，而是返回 `MODEL_RELEARN_REQUIRED`。

- `angleExtentDeg` 必须大于 `0`，与 start 相加后仍须为有限值。
- `scaleMin`/`scaleMax` 必须为正有限数，且 min 不大于 max。
- `numLevels` 仅接受小写 `auto` 或大于等于 `1` 的整数。`auto` 在内部解析为 `0`。

### 3.3 HALCON 原生候选搜索参数

| 键 | 用途 | 范围/关系 |
| --- | --- | --- |
| `halcon.candidateMinScore` | `FindScaledShapeModel` 原生候选分门槛 | `0..1` |
| `halcon.candidateMaxOverlap` | HALCON 原生候选重叠预筛 | `0..1` |
| `halcon.greediness` | HALCON 搜索 greediness | `0..1` |
| `halcon.subPixel` | 亚像素方法 | 目前仅接受 `least_squares` |
| `halcon.candidateLimit` | 原生候选安全上限 | `2..512`；`ExactCount` 时必须大于 `expectedCount` |
| `halcon.operatorTimeoutMs` | HALCON shape model 算子 timeout | `100..60000` ms |

### 3.4 托管验证参数

| 键 | 用途 | 范围/关系 |
| --- | --- | --- |
| `halcon.outerCoverageMin` | 外轮廓有效覆盖率 | `0..1` |
| `halcon.innerCoverageMin` | 内部特征整体覆盖率 | `0..1`，同时必须达到学习时的内特征组 quorum |
| `halcon.edgeTolerancePx` | 边缘支持距离与 P95 上限 | `(0,100]` 原图像素 |
| `halcon.polarityAgreementMin` | 暗前景明暗极性一致率 | `0..1` |
| `halcon.maxOverlap` | 通过硬门后的 filled-support IoU 去重 | `0..1`，且不得大于 `candidateMaxOverlap` |

`candidateMaxOverlap` 和 `maxOverlap` 不是同一层。前者减少 HALCON 返回的原生候选，后者使用实际产品 filled-support mask 做 IoU 去重。二次开发不能用外接矩形 IoU 替代后者。

### 3.5 三个 preset

三个 preset 共用 `angleStart=-180`、`angleExtent=360`、`scaleMin=0.90`、`scaleMax=1.10`、`subPixel=least_squares` 和 `numLevels=auto`。

| 参数 | Strict | Balanced | HighRecall |
| --- | ---: | ---: | ---: |
| `candidateMinScore` | 0.65 | 0.58 | 0.50 |
| `outerCoverageMin` | 0.90 | 0.85 | 0.78 |
| `innerCoverageMin` | 0.82 | 0.75 | 0.65 |
| `edgeTolerancePx` | 3.0 | 4.0 | 5.0 |
| `polarityAgreementMin` | 0.90 | 0.85 | 0.78 |
| `candidateMaxOverlap` | 0.70 | 0.75 | 0.80 |
| `maxOverlap` | 0.25 | 0.30 | 0.35 |
| `greediness` | 0.80 | 0.75 | 0.65 |
| `operatorTimeoutMs` | 5000 | 7000 | 10000 |
| `candidateLimit` Single / ExactCount | 32 / 128 | 48 / 160 | 64 / 192 |

`ExactCount` preset 会先写 `expectedCount=1`；调用方必须再写入真实工位数量。现场建议从 Strict 开始，只在漏检样本证明哪一道门过严后单项调整；HighRecall 不是默认的“更准”模式。

## 4. 学习整个产品

`TemplateRoi` 是学习域，不是用于显示的粗略外接框。对整体产品学习时：

1. ROI 要包含完整产品外轮廓和稳定内部孔/缺口，但不要把相邻产品和大片随机背景收进来。
2. 当前 metadata 契约锁定为暗前景，学习会要求至少 100 个外轮廓点、至少 3 组内部特征，内特征有效组数为 `max(2, ceil(groupCount * 0.67))`。
3. `SearchRoi=null` 表示生产匹配时使用整图；显式搜索 ROI 会先裁成紧密图像，最终位姿只加一次 crop-to-image 偏移。
4. 学习成功后必须用一张独立生产图调用 `MatchAsync`，不能只看学习 preview 宣布验收成功。

`TemplateLearningPreview` 仅是用于刚学习后显示的托管轮廓，不是 runtime 模型输入，也不应写回 Tool 参数。

## 5. 坐标、角度与 `Pose.Scale`

- 对外坐标为原图像像素坐标：左上角为原点，`X` 向右，`Y` 向下。HALCON 原生的 `Column` 对应 `X`，`Row` 对应 `Y`。
- `Pose.X/Y` 是学习时设置的模型 reference origin，不是候选外接框左上角。实现会先做 HALCON 像素角原点到本项目像素中心约定的 `0.5` 补偿，再加搜索 crop 偏移。
- 对外 `Pose.Angle` 为度，图像/UI 坐标下顺时针为正。输入的 start/extent 也用该约定；适配器转成 HALCON 逆时针弧度。
- HALCON 返回角度会归一化到 `[-180, 180)`；`+180` 表示为 `-180`。不要直接用普通减法计算跨越边界的角度误差。
- `Pose.Scale` 是当前候选相对于学习模型的统一缩放，必须为正有限数。学习标准位姿的 Scale 为 `1`。
- 后续 ROI 跟随使用 `PoseSimilarityTransform`，缩放比为 `currentPose.Scale / referencePose.Scale`，再应用 `current.Angle - reference.Angle` 的旋转。忽略 Scale 会使下游卡尺、缺陷 ROI 和坐标转换错位。

## 6. 候选的六道硬门

HALCON 先按 `candidateMinScore`、`candidateMaxOverlap`、`candidateLimit` 产生原生候选，然后托管验证严格按下列顺序 fail closed。一个候选只记录首个失败原因。

1. `MATCH_INVALID_POSE`：几何不可用，位姿/分数非有限值，Scale 非正或超出学习 generation 范围，或 reference origin 不在真实搜索域内。
2. `MATCH_INCOMPLETE_AT_BOUNDARY`：变换后的完整产品支持域被图像或搜索 ROI 边界截断。
3. `MATCH_POLARITY_MISMATCH`：明暗极性指标非 `0..1` 或低于 `polarityAgreementMin`。
4. `MATCH_OUTER_CONTOUR_WEAK`：外轮廓覆盖率不足，或边缘距离 P95 非法/超过 `edgeTolerancePx`。
5. `MATCH_INNER_FEATURES_WEAK`：内特征覆盖率不足，或有效内特征组没达到学习时 quorum。
6. `MATCH_DUPLICATE_OVERLAP`：前五道门都通过后，按分数降序、外轮廓覆盖率降序、X/Y 和源序号稳定排序；与已接受候选的 filled-support IoU 大于 `maxOverlap` 时拒绝。

这六道门只能评估 HALCON 已经返回的候选。例如整体极性完全相反时，原生 `use_polarity` 匹配可能直接返回零候选，此时不会伪造 `MATCH_POLARITY_MISMATCH`。`ExactCount` 不足时可能优先返回首个硬门诊断；没有更具体拒绝原因时才用 `MATCH_COUNT_MISMATCH`。

## 7. 结果、Tool 与端口契约

`TemplateMatchBatchResult` 中只有 `HasMatch=true` 且 `Outcome=Ok` 才是可供下游使用的定位结果。`ExactCount` NG 时 `Matches` 可以仍包含已通过验证的候选，用于调试显示；这不等于可发布位置端口。

- `TemplateLocateTool` 每次执行前清除 `PositionOutput`、`OriginOutput`、`ScoreOutput`、`XOutput`、`YOutput`、`AngleOutput` 和 `ScaleOutput`；只在可运行结果上重新发布。
- `MultiTargetMatchTool` 总是发布 `CountOutput`；只在实际数精确等于 expected count 时发布 best pose、全部 poses/scores/scales 等操作端口。
- 单目标 `ToolResult.Data` 会写 `engine`、`scale`、四项硬门指标、`failureCode`、`failureStage` 和 `overlaySchemaVersion=2`。NG 但有候选时指标使用 `rejectedCandidate.` 前缀。

### `matchesV2` 与旧八列 `matches`

`MultiTargetMatchTool` 同时保留两种序列化：

- `matches` 是旧格式，多个候选用分号分隔，每个候选固定八列：`x,y,angle,score,width,height,shape,radius`。该格式不能携带 Scale 和硬门指标，不得增列破坏旧消费者。
- `matchesV2` 是 JSON array，`matchSchemaVersion=2`。每项字段为 `x`、`y`、`angle`、`scale`、`score`、`outerCoverage`、`innerCoverage`、`edgeDistanceP95Px`、`polarityAgreement`、`width`、`height`、`shape`、`radius`。
- 新消费者应优先读 `matchesV2`，只在字段缺失时回退旧八列。`scores` 和 `scales` 是便于旧 UI 绑定的并行列表，不是第三种主 schema。

## 8. Model layout、metadata 与完整性

`FileTemplateModelStore` 在 `RuntimePaths.TemplateResourceDirectory` 下按 owner 分层。可读名称会转成最长 48 字符的安全段，再追加 12 位 SHA-256 前缀，因此不会因同名或非 ASCII 标识符冲突。逻辑 layout 为：

```text
Templates/
  {recipe-segment}/
    {flow-segment}/
      {tool-segment}/
        model-{generation}.shm
        model-{generation}.json
```

配方中保存的 `halcon.modelPath` 和 `halcon.modelMetadataPath` 是 store 控制的四段相对路径。绝对路径、`..`、冒号、UNC、空段、尾随点/空格和 reparse point 都会被拒绝。

metadata schema 当前锁定：

- `schemaVersion=1`、`engine=Halcon`、`modelFormat=halcon-scaled-shape`、`modelVersion=halcon-scaled-shape-v1`。
- managed package `26050.0.0`、managed assembly `26050.0.0.0`、native runtime `26.05.0.0`。
- owner、generation、模型文件名、模型 SHA-256、标准位姿和模板宽高。
- reference row/column、模型域质心、暗前景标志、外轮廓、内特征组与 quorum、filled-support runs。
- 不可变 generation 参数及指纹，以及学习时的验证默认值。

recipe reference 还保存 metadata 文件的 SHA-256。加载前会同时校验路径归属、owner、generation、文件名、两个 checksum、几何、generation fingerprint 和全部锁定版本。不要手工修改 `.json` 或为绕过校验而改配方 checksum；正确处理是恢复同一 generation 的成对备份，或重新学习。

## 9. Owner、cache、lease 和 retire

- cache key 由规范化绝对模型路径、model checksum 和 metadata checksum 组成。同一 generation 共享一次加载，一个等待者的取消不会取消共享 load。
- `HalconTemplateModelLease` 保证 native handle 在使用期间存活；每个模型的 operation lease 串行 timeout 参数修改和 `FindScaledShapeModel`，避免两个请求串参。
- `RetireToolAsync(owner)` 建立 owner retirement fence，阻止旧加载结果重新成为 active generation。它等已经在读文件的 load 结束，但不抢夺活跃 lease；native handle 在最后一个 lease/operation 释放后精确 Dispose。
- `ITemplateMatchingService.DisposeAsync()` 先拒绝新操作，等当前操作排空，再优先关闭 HALCON cache/worker，最后关闭其他 backend。应用不能在该 await 完成前卸载 runtime 或删除资源。

参数对话框的“取消”和窗口 X 只调用 `CancelAndDrainAsync`，不调用 `RetireToolAsync(owner)`。对话框与生产流程共享同一个 Recipe/Flow/Tool owner 和 cache；在生产 lease 尚未归还时做 owner-wide retire，会让下一帧同 generation 获取命中 retirement fence 并返回 NG。只有 reset、已确认的 active reference 切换、配方删除等真实失效边界才允许 retire。取消后未被配方引用的学习 generation 由后续资源治理清理，不用破坏当前生产模型来即时回收。

### 配方复制

`PrepareRecipeCopyAsync(source, newRecipeId, token)` 会为目标 Recipe/Flow/Tool owner 复制每个当前引用的精确 generation，并重写目标配方引用。正确顺序是：准备 copy session → 发布新配方 JSON → `copy.CommitAsync(CancellationToken.None)` → 始终 Dispose session。配方 JSON 是持久化 commit point；未 commit 的 Dispose 只回滚本次新建的精确 generation。

### 配方删除

先删除已持久化的配方，再调用 `DeleteRecipeResourcesAsync`。资源管理器会先 retire 配方引用的全部 owner，全部 retire 成功后再不可取消地删除 owner 资源。不得跳过 retire 直接删 `.shm`。

### 工具 reset

HALCON reset 会用 `TemplateModelParameterCodec.RemoveHalcon` 清除配方中的模型引用和标准位姿，清空 UI 结果，然后 `RetireToolAsync(owner)`。当前 reset 不立即删除磁盘上的 generation，因为编辑可能尚未保存，旧配方仍可能需要它。物理回收要由已提交的配方删除/资源治理流程完成，不要在 reset 按钮中无条件删 owner 目录。

## 10. 取消与 timeout

这两个结果必须分开：

- 请求在队列等待时取消，会立即抛 `OperationCanceledException`。
- 请求一旦获准进入同步 HALCON 算子，token 不会粗暴终止 native call。代码等算子安全返回、释放 operation lease，然后在托管边界抛 `OperationCanceledException`。
- 取消不会生成不存在的 `MATCH_CANCELLED` 诊断，也不会发布部分 `ToolResult` 或旧位姿端口。`ConfiguredVisionPipeline` 会停止，后续工具不执行。
- `operatorTimeoutMs` 由同一模型 operation gate 内的 `SetShapeModelParam("timeout")` 限制。HALCON error `9400` 稳定映射为 NG + `MATCH_TIMEOUT`，不是“零候选”，也不是用户取消。
- shutdown 会无上限等待已进入 native 的调用安全退出；所以每个生产配方都必须配置有限的原生 timeout，不能用应用关闭超时替代它。

## 11. 28 个稳定诊断码

操作员显示使用 `UserMessage`，流程分支使用 `Code`，日志再记录 `FailureStage` 和 `TechnicalDetails`。不要解析英文 exception message。

| 稳定码 | Stage | 开发含义 |
| --- | --- | --- |
| `CONFIG_UNKNOWN_ENGINE` | Configuration | `engine` 缺少支持，值无法路由 |
| `CONFIG_SERVICE_REQUIRED` | Configuration | 配方选了未注册的 backend，常见于 legacy-only service 运行 HALCON 配方 |
| `CONFIG_UNSUPPORTED_MODE` | Configuration | backend 不支持当前 mode/cardinality |
| `CONFIG_INVALID_PARAMETER` | Configuration | 参数格式、范围、owner、ROI 或公开结果契约无效 |
| `RUNTIME_NOT_FOUND` | Runtime | 没有可用 runtime 候选或 native DLL/依赖无法加载 |
| `RUNTIME_ARCH_MISMATCH` | Runtime | 非 Windows x64 进程或 native image 不是 AMD64 PE32+ |
| `RUNTIME_VERSION_MISMATCH` | Runtime | native、managed package/assembly、system file 或模型记录版本不一致 |
| `LICENSE_UNAVAILABLE` | Runtime | HALCON 许可算子返回许可错误 |
| `MODEL_PATH_INVALID` | Model | 模型路径越界、非 store 相对路径或文件系统保护失败 |
| `MODEL_NOT_FOUND` | Model | 期望的 generation、metadata 或 owner 目录不存在 |
| `MODEL_CHECKSUM_MISMATCH` | Model | `.shm` 字节与参考/metadata SHA-256 不一致 |
| `MODEL_METADATA_INVALID` | Model | metadata JSON、owner、generation、文件名、metadata checksum 或几何不一致 |
| `MODEL_VERSION_MISMATCH` | Model | schema/model format 的应用版本不支持 |
| `MODEL_RELEARN_REQUIRED` | Model | 没有完整 HALCON model state，或 generation fingerprint 已改变 |
| `MODEL_LOAD_FAILED` | Model | 学习、写入、回读或 native model load 失败 |
| `MODEL_TEMPLATE_INCOMPLETE` | Model | 学习 ROI/产品在图像边界不完整 |
| `MODEL_CONTRAST_WEAK` | Model | 学习前景与背景对比不足 |
| `MODEL_INTERNAL_FEATURES_WEAK` | Model | 学习到的稳定内部特征组不足 |
| `MATCH_INVALID_POSE` | Match | 第一硬门失败 |
| `MATCH_INCOMPLETE_AT_BOUNDARY` | Match | 第二硬门失败 |
| `MATCH_POLARITY_MISMATCH` | Match | 第三硬门失败 |
| `MATCH_OUTER_CONTOUR_WEAK` | Match | 第四硬门失败 |
| `MATCH_INNER_FEATURES_WEAK` | Match | 第五硬门失败 |
| `MATCH_DUPLICATE_OVERLAP` | Match | 第六硬门的 filled-support IoU 去重失败 |
| `MATCH_TIMEOUT` | Match | HALCON 原生算子 timeout（error 9400） |
| `MATCH_COUNT_MISMATCH` | Match | 通过验证的数量与 `expectedCount` 不一致 |
| `MATCH_CANDIDATE_LIMIT_REACHED` | Match | 原生返回数达到 `candidateLimit`，结果可能被截断，不得判 OK |
| `MATCH_OPERATOR_FAILED` | Match | 未归类的 HALCON 算子或 backend 契约失败 |

## 12. 新增第四个 backend

当前 backend registry 是编译期组合，不是动态插件。`ITemplateMatchingBackend` 和 `TemplateMatchingService.ForTests` 都是 internal，因此第四 backend 必须先在 `VisionStation.Vision` 内完成中性契约适配：

1. 向 `TemplateMatchingEngine` 增加明确枚举值，并扩展 `TemplateMatchingEngineResolver`的配方文本解析；未知值仍 fail closed。
2. 实现 `ITemplateMatchingBackend`的 Learn/Match/Dispose，只返回中性 `TemplateLearningResult` 和 `TemplateMatchBatchResult`。不得把供应商 handle 放进公开 result。
3. 更新 `TemplateMatchingService` 的可注册 engine allow-list，并在正确的 composition root 显式注册。定义稳定的关闭顺序和重复 Dispose 语义。
4. 保持 `TemplateMatchResultProjector` 契约：有限正 Scale、有效宽高、完整 template ROI contour、明确 `HasMatch/Outcome`。如指标不适用，要在设计中明确中性值，不让 UI 按 backend 分支。
5. 更新三引擎兼容表、UI engine 选项、配方迁移、single/exact-count 端口安全测试，以及未注册/取消/关闭测试。

如新 backend 也是 native runtime，请重用“runtime probe → 专用 scheduler → cache lease → resource retire”的所有权形状，不要直接共享可变 native handle。

## 13. Fake backend 和 validator 测试

应用层和 UI 测试应实现公开 `ITemplateMatchingService` 小 fake，通过构造器注入 `TemplateLocateTool`、`MultiTargetMatchTool` 或 `VisionPipelineFactory.CreateDefault`。这能精确测试 NG 清端口、exact-count 和取消停流程，且不需要 HALCON 许可。

`VisionStation.Vision.Tests` 内部可使用 `RecordingTemplateMatchingBackend` + `TemplateMatchingService.ForTests(...)` 测试路由、请求快照和 Dispose。不要 mock `TemplateCandidateValidator`：它是一个确定性、无 I/O 的具体策略，应像 `TemplateCandidateValidatorTests` 一样构造 `TemplateCandidateEvidence`，对六道门的首失败码、排序和 filled-support IoU 直接断言。需要隔离 HALCON backend 编排时，可 fake `IHalconCandidateSource`、`ITemplateCandidateEvidenceBuilder`、`IHalconModelLoader` 和 `IHalconOperatorBackend`，不要用一个“万能 validator mock”跳过核心安全策略。

## 14. 二次开发导航

| 要改的东西 | 先读的实现/测试 |
| --- | --- |
| 公开请求和结果 | `TemplateMatchingContracts.cs`、`TemplateMatchingServiceTests.cs` |
| engine 路由和参数 | `TemplateMatchingEngineResolver.cs`、`TemplateMatchingParameterCatalog.cs` |
| HALCON 学习 | `HalconTemplateLearner.cs`、`HalconTemplateFeatureExtractorTests.cs` |
| 原生搜索与坐标 | `HalconScaledShapeCandidateSource.cs`、`HalconPoseConverter.cs` |
| 六道硬门 | `TemplateCandidateValidator.cs`、`TemplateCandidateValidatorTests.cs` |
| 模型完整性 | `FileTemplateModelStore.cs`、`HalconModelMetadataValidator.cs` |
| cache/lease/retire | `HalconTemplateModelCache.cs`、`HalconTemplateModelCacheTests.cs` |
| 配方复制和删除 | `TemplateModelResourceManager.cs`、`RecipeTemplateLifecycleService.cs` |
| Tool 端口与 `matchesV2` | `TemplateLocateTool.cs`、`MultiTargetMatchTool.cs`、`TemplateMatchingToolPortSafetyTests.cs` |
| 真实 runtime 验收 | `VisionStation.Vision.Halcon.Tests` 和部署指南的 TestHost 命令 |

任何阈值、新 backend 或 model schema 修改，都要同时证明：正样本位姿与 Scale、反面/相似品/残缺/边界/极性负样本、exact-count、取消不发布端口、model 备份恢复和可接受的现场性能基线。
