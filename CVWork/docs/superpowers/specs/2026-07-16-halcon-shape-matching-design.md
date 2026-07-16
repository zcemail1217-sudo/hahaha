# HALCON 两阶段整产品模板匹配设计

**状态：** 待用户书面审阅

**日期：** 2026-07-16

**适用方案：** `CVWork.sln`

## 1. 背景与结论

当前 OpenCV 模板匹配已经补齐大角度旋转画布、轮廓覆盖率、反向距离、`hasMatch` 和统一叠加层等基础能力，但候选生成与最终判定仍不足以满足现场“宁可漏检，也不能把错误产品判成正确产品”的要求。继续围绕少量现场图片调阈值，无法解决算法本身对相似轮廓、反面、局部碎片和重复候选的误接受问题。

本阶段把新建的单目标与多目标整产品匹配默认切换为 HALCON 26.05 scaled shape model，并采用两阶段策略：

1. `find_scaled_shape_model` 只负责在全角度和小尺度变化范围内产生候选。
2. 每个候选必须依次通过外轮廓、内部关键特征、边缘距离、极性、边界完整性和重叠唯一性硬门；任意一项失败即拒绝，不使用加权平均把弱项“补分”。

现有 OpenCV 与 Managed NCC 后端继续保留。旧配方不自动迁移、不静默改变算法；只有用户明确选择 HALCON 并重新学习后，工具才使用 `.shm` 模型。

## 2. 目标

1. 同时替换单目标 `TemplateLocate` 和多目标 `MultiTargetMatch` 的新建默认后端。
2. 对完整或近完整的正面产品支持 `0°..360°` 旋转和约 `0.90..1.10` 等比缩放。
3. 正面完整产品可以稳定定位；反面、相似零件、严重遮挡、局部碎片和边界截断不得被接受。
4. 多目标结果只有在接受数量与期望数量完全相等时才为 OK，并返回每件产品的 `X/Y/Angle/Scale/Score`。
5. 单目标和多目标复用同一 HALCON 学习、候选生成和候选验证核心，只改变请求数量与最终计数规则。
6. UI、配方、流程工具和结果显示不引用 `HImage`、`HTuple`、`HShapeModel` 等 HALCON 类型。
7. 模型、运行时、参数和结果均有明确版本与诊断契约，方便后续增加其他 HALCON 模型或第三方后端。
8. 运行时、许可、模型或配置异常只产生可诊断的 NG，不使生产流程崩溃，也不弹出阻塞式对话框。

## 3. 非目标与边界

本阶段不实现：

- 非等比缩放、透视形变、柔性/可变形模型。
- 对严重遮挡目标的强制恢复；严重遮挡是应拒绝负例。
- 单独训练一个正反面分类器或反面模型；反面拒绝由正面模型的内部关键特征与极性硬门完成。
- HDevEngine、`.hdev` 脚本运行时或 HALCON WPF 显示控件。
- 自动把旧 OpenCV `.bin`/Base64 模型转换为 `.shm`。
- 删除 OpenCV、Managed NCC 或 OpenCV 多目标的 `CircularBlob` 能力。
- 用当前几张现场图片作为默认自动化测试数据或把本机绝对图片路径写入仓库。

如果正反面在相机成像下没有任何可观察差异，单个正面形状模型无法凭空区分；学习阶段因此必须验证内部特征是否足够，特征不足时拒绝生成“看似可用”的正面模型。

## 4. 总体架构

新增一个面向上层的深模块 `ITemplateMatchingService`。UI 和流程工具只认识中立请求、结果和错误码；严格路由、模型资源、缓存、坐标转换以及所有 HALCON 生命周期都隐藏在模块内部。

```text
TemplateLocateTool / MultiTargetMatchTool / 参数对话框
                         |
                         v
             ITemplateMatchingService
               | strict engine routing
       +-------+----------+----------------+
       |                  |                |
 ManagedNcc adapter   OpenCV adapter   HALCON scaled-shape adapter
       |                  |                |
  现有实现兼容层      现有 V2 实现      候选生成 + 六项硬门
                                            |
                            model store / runtime probe / cache
```

### 4.1 上层服务 seam

服务以中立数据工作，核心语义为：

```csharp
public interface ITemplateMatchingService : IAsyncDisposable
{
    Task<TemplateLearningResult> LearnAsync(
        TemplateLearningRequest request,
        CancellationToken cancellationToken);

    Task<TemplateMatchBatchResult> MatchAsync(
        TemplateMatchingRequest request,
        CancellationToken cancellationToken);
}
```

`TemplateLearningRequest` 至少包含 `RecipeId`、`FlowId`、`ToolId`、图像、搜索/模板 ROI 和参数快照。`TemplateMatchingRequest` 另外包含单/多目标模式和期望数量。`TemplateMatchBatchResult` 始终返回一个候选集合；单目标投影为最多一个结果，多目标投影为有序集合。

`TemplateMatcher.Learn/Match` 与 `MultiTargetMatcher.Match` 保留为 OpenCV/Managed 旧代码兼容入口，现有行为和测试不被删除。静态 `Match` 遇到 `engine=Halcon` 时返回带 `CONFIG_SERVICE_REQUIRED` 的 NG result；静态 `Learn` 因现有返回类型只是 Dictionary，抛出带同一稳定 Code 的专用配置异常。未知引擎以相同方式使用 `CONFIG_UNKNOWN_ENGINE`。这些入口绝不能自行寻找全局 service 或进入 OpenCV。生产工具和新的二次开发代码统一改用注入的 `ITemplateMatchingService`，避免通过 service locator 或可变静态状态注入 HALCON 运行时、模型根目录和 fake backend。

HALCON operator 本身是同步 native 调用。服务在受控、有限并发的专用 worker 上执行它，学习与匹配调用方始终 `await`；禁止在 ViewModel 中拼接无跟踪的 fire-and-forget `Task.Run`。服务关闭时停止接收新请求，等待在途 lease 安全归还，再释放模型 handle、worker 和 semaphore。

### 4.2 后端 seam

内部异步 `ITemplateMatchingBackend` 只接收中立请求并返回中立批结果。注册表的键只允许：

- `ManagedNcc`：保留现有单目标行为，多目标请求返回明确的“不支持”诊断。
- `OpenCv`：包装当前单目标 Shape V2 与多目标实现。
- `Halcon`：本设计新增的 scaled shape 后端。

后端注册表通过构造函数注入且创建后只读。测试可以注入 fake backend；生产代码不提供“运行中替换全局 backend”的开关。

HALCON 后端内部再拆分 `IHalconCandidateSource` 与纯中立数据的 `TemplateCandidateValidator`：前者是唯一调用 HalconDotNet 的候选源，后者执行六项硬门。无许可 CI 可以注入预制候选、距离图、极性采样和支持区域，真实运行与测试仍走同一个 validator，而不是用 fake backend 绕过最终判定。

`MVTec.HalconDotNet` 类型只能出现在 `VisionStation.Vision` 内部的 `Halcon` 适配目录中，不进入 Domain、Application、Infrastructure、Vision.UI 或 Client 的公开契约。

## 5. 引擎路由与旧配方兼容

当前 `TemplateMatcher.ShouldUseOpenCv` 会把除 `ManagedNcc` 外的任意字符串都静默路由到 OpenCV；`MultiTargetMatcher` 则完全忽略 `engine`。这两个行为必须被替换为严格解析：

| 配方值 | 行为 |
|---|---|
| 缺少 `engine` | 按旧配方处理为 `OpenCv` |
| `OpenCv` | 保持当前行为 |
| `ManagedNcc` | 保持当前单目标行为；多目标返回不支持 NG |
| `Halcon` | 进入 HALCON 后端 |
| 其他值 | `CONFIG_UNKNOWN_ENGINE`，不得回退到 OpenCV |

兼容规则：

1. `VisionToolCatalog` 和 `DefaultRecipeFactory` 创建的新模板工具显式写入 `engine=Halcon`。
2. 加载旧配方时不补写 `engine`；运行时缺省解释为 OpenCV。用户保存对话框后才持久化当前选择。
3. 切换后端不删除另一后端的参数与模型引用，但活动后端只读取自己的命名空间。
4. 从 OpenCV 切到 HALCON 后必须重新学习；`.bin`、`templatePixels` 和 `.shm` 不互相冒充。
5. HALCON 只支持 `matchMode=Shape`。若配方把 HALCON 与 `CircularBlob`、ORB 或 NCC 组合，返回 `CONFIG_UNSUPPORTED_MODE`，不得猜测替代算法。
6. 旧 OpenCV 的绝对 `modelPath` 继续兼容；新的 HALCON 模型路径必须是受控根目录下的相对路径。
7. 当前 UI 对旧角度字段的自动迁移只在规范化后端为 OpenCV 时执行，不能把 OpenCV 角度参数改写成 `halcon.*` 参数。

## 6. HALCON 学习流程

学习输入必须是完整正面产品，产品明显深于背景，模板 ROI 周围保留少量背景。学习过程按以下顺序执行：

1. **输入与运行时预检**：验证非空 `RecipeId/FlowId/ToolId`、图像格式、ROI 尺寸、x64 HALCON 运行时、DotNet/native 版本和许可。缺少配方/流程上下文时拒绝学习，不能把模型落入共享“临时配方”目录。
2. **建立参考坐标**：以模板 ROI 中心作为稳定模型原点，`Scale=1`、`Angle=0`。所有验证轮廓保存为相对该原点的坐标。
3. **分离暗前景**：从 ROI 边框估计亮背景，提取与参考中心关联的主要暗区域，去除孤立噪点。产品触碰模板 ROI 边界时学习失败，而不是生成残缺模型。
4. **提取外轮廓**：从主要产品区域得到闭合外边界，等距采样形成外轮廓验证点。
5. **提取内部关键特征**：向内收缩外轮廓形成内部支持域，在该域内提取亚像素边缘，按连通长度和空间分布保留多个关键轮廓组。外轮廓附近的边缘不重复计入内部特征。
6. **学习质量硬门**：外轮廓点数、内部特征点数/分组数、前景与背景对比度或模板完整性不足时，学习以具体错误码失败，旧模型参数保持不变。
7. **创建 scaled shape model**：将图像 domain 限制在外轮廓及内部支持域的受控并集，避免模板 ROI 背景纹理进入模型；再调用 `create_scaled_shape_model`。角度范围来自最终持久化参数，默认全圆；尺度默认 `0.90..1.10`；Metric 固定 `use_polarity`。角度步长、尺度步长、金字塔层级和模型对比度的 `auto` 意图随模型元数据保存，不使用忽略极性的模式。
8. **生成预览**：返回中立 PNG/轮廓预览，分别标出外轮廓和内部关键特征，帮助用户确认“整个产品”实际进入了模型。预览不是运行时算法输入。
9. **写入模型资源**：通过模型存储服务把 `.shm` 与 JSON 元数据写入新一代文件，校验哈希后才把新路径合并到待保存参数中。

内部特征不是一个总像素数。元数据保留多个轮廓组，运行验证时既检查总体覆盖率，也要求有效组数达到学习时的最低比例，避免一小段高对比边缘替代整个产品内部结构。

## 7. HALCON 匹配流程

单目标和多目标都调用同一批匹配核心：

1. 严格解析参数并解析受控相对模型路径；结果中的 `engine` 写服务实际使用的规范化后端键，不照抄原始参数文本。
2. 获取以“绝对模型路径 + `.shm` SHA-256 + 元数据 SHA-256”为键的模型 lease。
3. 将搜索 ROI 的轴对齐外接框裁成原点为 `(0,0)` 的局部 HALCON 图像；若原 ROI 为圆、旋转矩形或多边形，再在局部图上应用对应 domain mask。HALCON 返回局部 `Row/Column`，适配器只在出口处加一次 `(searchX, searchY)`；禁止对原图 `reduce_domain` 后再次加偏移。
4. 调用 `find_scaled_shape_model` 产生按 HALCON 原始 Score 降序排列的候选。候选阶段使用较低但明确的 `candidateMinScore`，为后续硬门保留召回率。
5. 将 HALCON `Row/Column/Angle/Scale` 转换为当前系统坐标，并对每个候选执行第 8 节硬门。
6. 只把通过所有硬门的候选加入接受集合；拒绝原因记录在诊断中，不作为有效定位输出。
7. 按旋转后完整模板支持区域执行最终去重。候选生成使用独立且更宽松的 `candidateMaxOverlap`；它只是粗过滤，不能复用最终 `maxOverlap` 或代替最终唯一性验证。
8. 单目标取第一个已接受候选；多目标继续验证，直到候选耗尽、达到安全上限，或已经确认“多于期望数量”。

候选 Score 保持 HALCON 原始含义，最终是否接受由硬门决定。系统不生成一个加权“综合分”覆盖失败项，也不通过自动降低阈值来凑足数量。

## 8. 候选验证硬门

所有硬门均独立通过才接受候选，执行顺序从便宜到昂贵：

### 8.1 位姿与范围

- `Row/Column/Angle/Scale/Score` 必须全部为有限数。
- Scale 必须位于保存的允许范围内。
- 转换后的模型原点必须位于搜索 ROI 内。

失败码：`MATCH_INVALID_POSE`。

### 8.2 完整性与边界

把完整模板 ROI 和外轮廓按候选位姿变换到原图。四角、外轮廓及配置的安全边距必须位于图像与搜索 ROI 内。边界截断或只有局部产品进入搜索域时直接拒绝。

失败码：`MATCH_INCOMPLETE_AT_BOUNDARY`。

### 8.3 极性一致性

模型固定为暗产品/亮背景。沿外轮廓法线比较轮廓内外灰度或梯度方向，计算极性一致率；同时依赖 HALCON `use_polarity`。低于阈值直接拒绝，不能由高 Shape Score 抵消。

失败码：`MATCH_POLARITY_MISMATCH`。

### 8.4 外轮廓支持

在搜索图边缘距离图上采样变换后的外轮廓点：

- `outerCoverage` = 距离不超过 `edgeTolerancePx` 的外轮廓点比例。
- `edgeDistanceP95` = 外轮廓点距离的 95 分位值。

两者必须分别满足 `outerCoverageMin` 与 `edgeTolerancePx`，以防平均距离掩盖一整段缺失轮廓。

失败码：`MATCH_OUTER_CONTOUR_WEAK`。

### 8.5 内部关键特征支持

以相同方法验证内部轮廓组：

- 总体 `innerCoverage` 必须达到 `innerCoverageMin`。
- 达标的内部轮廓组数必须达到元数据记录的最低组数。
- 不允许把外轮廓支持计入内部覆盖。

反面或外形相似但内部结构不同的产品应在此门失败。

失败码：`MATCH_INNER_FEATURES_WEAK`。

### 8.6 重叠唯一性

学习时把产品外轮廓填孔后保存为“完整模板支持区域”。运行时将该填充区域按候选位姿在原图像素网格上变换，使用 `IoU = Area(Intersection) / Area(Union)` 计算两个候选的重叠率；内部孔洞不参与分母差异，不允许退化为中心距离、轴对齐包围框 IoU 或一维轮廓线重合率。超过 `maxOverlap` 的较低优先级候选作为重复项拒绝。候选排序依次为 HALCON Score 降序、OuterCoverage 降序、X/Y 升序，保证同分结果确定。

失败码：`MATCH_DUPLICATE_OVERLAP`。

## 9. 单目标与多目标判定

### 9.1 单目标

- 请求接受上限为 1。
- 存在一个通过全部硬门的候选则 `HasMatch=true`、Outcome=OK。
- 所有候选均被拒绝、无候选或发生可恢复运行错误时 `HasMatch=false`、Outcome=NG。
- 最佳拒绝候选只进入诊断字段，不生成定位 Cross，也不写入位置端口。
- 所有后端的“操作性位姿”统一只在 `HasMatch && Outcome==Ok` 时发布。OpenCV 低分候选仍可作为红色调试叠加和诊断保存，但不再写位置端口或 legacy `context.Properties["pose"]`；这是生产安全收紧，不改变旧配方的匹配分数算法。

### 9.2 多目标

新增 `expectedCount` 表示精确期望数量。HALCON 新配方必须保存该字段；若早期 HALCON 草稿配方缺少它，可读取 `matchCount` 作为一次性兼容值。OpenCV 仍保留旧 `matchCount=maxMatches` 语义。

- 接受数量恰好等于 `expectedCount`：Outcome=OK。
- 少于或多于 `expectedCount`：Outcome=NG。
- 为判断“多一个”，候选容量至少允许得到 `expectedCount + 1` 个已接受结果；内部候选扫描上限由期望数量派生并设置安全上限。
- NG 时仍在 `ToolResult.Data` 和红色调试叠加中报告所有已接受候选，便于排查；但位置、最佳位置和全部位置流程端口只在整体 OK 时发布，避免下游设备使用不完整或超量集合。
- `CountOutput` 与诊断始终可用。

每个模板定位工具执行前先清除 legacy 全局 pose 槽；只有本工具整体 OK 才写入新的 `context.Properties["pose"]`。单目标 NG、HALCON 多目标计数 NG、timeout 和取消都不得留下或复用上一工具的 pose。显式 Position/Best/All 端口遵循相同规则，Count 与诊断不受影响。

## 10. 坐标、结果与显示契约

### 10.1 坐标契约

- `X` = HALCON Column 加搜索 ROI 的原图偏移。
- `Y` = HALCON Row 加搜索 ROI 的原图偏移。
- 原点是学习时显式设置的模板 ROI 中心，不依赖 HALCON 默认重心。
- `Angle` 使用现有 `Pose2D` 契约：屏幕图像坐标中顺时针为正，单位度，规范到 `[-180, 180]`。HALCON 弧度及方向转换只存在于适配器，并由 `35°/-135°` 回归测试锁定。
- `Scale=1` 表示学习尺寸；仅允许等比缩放。
- `MatchX/MatchY` 为完整变换模板 ROI 的轴对齐外接框左上角；`TemplateWidth/Height` 保留学习尺寸。

`Pose2D` 在 record body 中增加 init-only `Scale=1`，保持现有三参数构造函数和 `Deconstruct` 不变。新增唯一的纯数据 `PoseSimilarityTransform`：使用 `scaleFactor = currentPose.Scale / referencePose.Scale`，将相对原点向量先等比缩放再旋转平移，矩形宽高、旋转矩形宽高、圆半径和多边形各点同步缩放。生产 `GeometryToolSupport`、TemplatePoint、CoordinateTransform 以及 TemplateLocate/FindLine/FindCircle/Blob 等调试与示教预览都复用该 helper；任何消费 PositionInput 或重建 Pose2D 的路径必须显式保留 Scale，禁止各 ViewModel 继续维护独立的旋转/平移公式。新学习参数写 `standardScale=1`，重新示教 ROI 时写 `roiReferencePoseScale`；旧参考位姿缺少 Scale 时按 1 解释。

### 10.2 中立候选

每个接受候选至少包含：

- `X`、`Y`、`Angle`、`Scale`、`Score`
- 完整模板 ROI 变换轮廓与实际评分轮廓
- `OuterCoverage`、`InnerCoverage`、`EdgeDistanceP95Px`、`PolarityAgreement`
- 候选宽高与后端键

服务层唯一尺度真值是 `TemplateMatchBatchCandidate.Pose.Scale`。`TemplateMatchResult` 在 record body 增加只读计算属性 `Scale => Pose.Scale` 和中立诊断对象，保持现有位置构造函数及 `Deconstruct` 不变。`MultiTargetMatchCandidate` 为兼容现有位置构造增加 init-only `Scale`，但只能由统一 result projector 从服务候选 `Pose.Scale` 赋值；其计算得到的 Pose、`PositionOutput`、`BestPositionOutput`、`AllPositionsOutput`、Scale 端口和序列化字段均来自同一来源。测试必须断言这些投影值完全相等，禁止 UI、Tool 或 formatter 各自复制赋值逻辑。

### 10.3 ToolResult 与端口

单目标增加：

- `scale`
- `outerCoverage`、`innerCoverage`、`edgeDistanceP95Px`、`polarityAgreement`
- `failureCode`、`failureStage`（失败时）

HALCON 单目标 `hasMatch=false` 时不写 `x/y/angle/scale` 主定位字段；最佳拒绝候选只能写入带 `rejectedCandidate.*` 前缀的诊断。旧历史/OpenCV 结果即使仍含坐标，也必须由统一的 `HasMatch && Outcome==Ok` 规则阻止操作性位置端口；红色调试 Cross 仍可保留。

多目标：

- 保留现有 8 列 `matches` 文本，保证历史解析器可读。
- 新增 `matchSchemaVersion=2` 和 `matchesV2` JSON 数组，包含每件产品的 `x/y/angle/scale/score` 及验证指标。
- 新 UI 优先读取 `matchesV2`，缺失时回退旧 `matches`。
- `scores` 与 `scales` 以统一 invariant 格式写入 Data，端口格式化器和调试表不能再从缺失字段得到空值。
- 增加 `ScaleOutput`（单目标）和 `ScalesOutput`（多目标）；原有 `Pose2D` 端口不改变类型。

评分轮廓、完整模板 ROI、定位中心继续复用现有 `TemplateLocateOverlayFactory`、`VisionResultOverlayProjector` 和 `hasMatch` 语义，不在 HALCON 页面复制第二套叠加规则。`overlaySchemaVersion` 继续保持 `2`；`matchesV2` 使用独立版本，避免现有只识别 overlay v2 的解析器失效。

## 11. 模型文件、元数据与路径安全

### 11.1 资源布局

HALCON 模型按配方和工具隔离：

```text
Resources/Templates/<recipe-slug>-<id-hash>/<flow-slug>-<id-hash>/<tool-slug>-<id-hash>/
  model-<generation>.shm
  model-<generation>.json
```

配方保存相对 `RuntimePaths.TemplateResourceDirectory` 的 `modelPath` 和 `modelMetadataPath`，不得保存本机 HALCON 安装路径或新的绝对模型路径。

每个目录段使用“可读 slug + 原始 ID 的 SHA-256 前 12 个十六进制字符”，不能只调用会产生碰撞的字符替换函数。元数据保存完整原始 `RecipeId/FlowId/ToolId` 三元组；加载、复制和删除前都验证目录后缀及元数据所有权。

### 11.2 元数据

JSON 元数据至少包含：

- `schemaVersion`、`engine=Halcon`、`modelFormat=halcon-scaled-shape`
- 应用模型版本、`MVTec.HalconDotNet` 包版本和学习时 HALCON 完整运行时版本
- `.shm` 文件名、SHA-256、元数据 generation
- 模板尺寸、模型原点、角度/尺度范围、暗前景极性
- 外轮廓采样点、内部关键轮廓组及最低有效组数
- 影响模型生成的不可变参数，以及仅供审计的 `validationDefaultsAtLearn`

配方参数同时保存 `modelVersion`、`modelRuntimeVersion`、`modelChecksum` 和 `metadataChecksum`，便于在加载 HALCON handle 前快速给出明确诊断。

元数据中的模型几何、特征组和生成参数不可变；`validationDefaultsAtLearn` 只记录学习当时建议门限，不与当前配方做一致性校验。每次匹配都从当前请求读取 outer/inner coverage、edge distance、polarity、最终 overlap 和 timeout 等运行门限。模型缓存不得固化这些可热改门限；若未来缓存编译后的 validator 配置，缓存键必须加入完整验证参数哈希。

“已学习”判断必须同时验证活动 `engine`、model format、两条相对路径和 checksum，不能因为目录里存在旧 `template.bin` 就把 HALCON 显示为已学习。

### 11.3 原子激活

学习不覆盖活动模型：

1. 在同一目标目录写入带随机 generation 的临时 `.shm` 和元数据。
2. 关闭写句柄并计算 SHA-256，重新读取元数据验证交叉引用。
3. 在同一卷内把临时文件原子移动为新的 generation 文件。
4. 只有两份文件都成功后，才返回包含新相对路径的参数；对话框确认与配方保存后，新 generation 才成为活动模型。
5. 失败、取消或用户取消对话框时，旧参数和旧文件仍可运行。未引用 generation 可后续清理，但清理不属于本阶段正确性路径。

### 11.4 配方资源生命周期

- **重置模型**：一次性移除 `modelPath`、`modelMetadataPath`、版本、generation、两个 checksum、HALCON 验证元数据和预览字段，并让对应缓存项 retire。现有 OpenCV 重置也必须清理遗留的 `modelPath/modelVersion`，不能重置后仍被判断为已学习。
- **复制配方**：把每个活动 generation 复制到新 `RecipeId/FlowId/ToolId` 目录，重新计算 checksum 并重写新配方相对路径；任一资源复制失败则不保存半成品配方。新旧配方不共享可变模型文件。
- **删除配方**：先确认配方 JSON 删除成功并 retire 其缓存，再验证目录哈希和元数据中的完整原始 RecipeId，只删除确属该配方的独占模型目录；清理失败只记录 orphan 日志，不回滚已经完成的配方删除。
- **切换引擎**：保留非活动后端数据用于用户切回，但当前模型状态只由所选引擎的完整元数据决定。

### 11.5 路径防护

`ITemplateModelStore` 负责生成和解析路径：拒绝 rooted path、盘符、UNC、`..`、空段以及最终 `GetFullPath` 不位于模板根目录下的路径；现有父目录若含重解析点也拒绝。解析后还必须核对 slug-hash 目录与元数据原始三元组的所有权。适配器不能自行拼接配方字符串与文件系统路径。

## 12. HALCON 运行时与部署

### 12.1 固定依赖

- NuGet：`MVTec.HalconDotNet` `26050.0.0`
- HALCON：26.05.0.0 Progress；`26050.0.0` managed 包与 native 维护版本必须精确匹配
- 进程：仅 x64，`PlatformTarget=x64`、`Prefer32Bit=false`
- 架构目录：`x64-win64`

不引用 `MVTec.HalconDotNet-Windows`，因为本阶段不嵌入 HALCON WPF 控件。应用直接调用 HalconDotNet operator API，不运行 HDevelop/HDevEngine。

解决方案删除/禁用 x86 配置，Vision、测试宿主和发布配置均固定 x64，发布 RID 为 `win-x64`。当前项目使用 .NET 8，而本机 HALCON 26.05 必须通过真实的“.NET 8 + x64 + native operator”smoke test后才能标记支持；不能只以 NuGet 的 `netstandard2.0` 可还原作为兼容证据。

### 12.2 根目录发现顺序

运行时根目录按以下顺序寻找第一个完整且版本匹配的候选：

1. `HALCONROOT`/`HALCONARCH` 环境变量。
2. `devices.json` 中可选的 `SystemSettings.Halcon.RuntimeRoot`。
3. Windows 卸载注册表中与 26.05、x64 匹配的 MVTec HALCON 安装项；枚举候选并去除 `InstallLocation` 外层引号，不依赖一个假定存在的固定 MVTec key。

代码和配方均不得硬编码当前机器的 `D:\HALCON\...`。探测结果记录来源和被拒绝原因，但生产结果不暴露敏感许可内容。

### 12.3 启动与许可探测

`HalconRuntimeProbe` 在第一次选择 HALCON 时惰性执行一次：

1. 校验进程位数和 native DLL 文件。
2. 在第一次 HALCON 类型初始化前一次性注册 native DLL 搜索目录，并在干净子进程中证明 `halcon.dll` 的传递 native 依赖也能解析；仅为一个 DLL 设置 resolver 不视为完成。
3. 调用系统版本查询，验证 DotNet/native 完整维护版本精确匹配。
4. 执行最小无副作用 operator 以触发真实许可检查。

缺 DLL、位数错误、版本不匹配和许可错误分别映射为稳定错误码。应用仍可启动并继续使用 OpenCV 配方。

模型记录完整运行时版本；本阶段模型运行时、managed 包和 native HALCON 均要求精确的 26.05.0.0/26050.0.0 组合，任何维护版本差异都返回版本不匹配 NG，升级必须经过显式兼容验证和重新基线。

目标机部署必须安装完整匹配版本的 HALCON runtime 与有效许可，不能通过复制单个 `halcon.dll` 伪装为自包含发布。若现场使用 Windows floating license，还要把 `hlwd` 可用性和退出时归还许可纳入部署检查；本机许可类型不得靠文件名猜测。

## 13. 缓存、线程与取消

### 13.1 模型缓存

缓存键为解析后的绝对 `.shm` 路径、模型 SHA-256 和元数据 SHA-256。缓存项同时持有 HALCON 模型 handle、只读验证描述和每模型 `SemaphoreSlim`。

- 同一模型的 HALCON 调用初期串行化，避免共享 handle 的并发语义不明确。
- 不同模型拥有不同锁，可以并行运行。
- 新 checksum 创建新缓存项；旧项先标记 retired，等所有 lease 归还后再 Dispose，禁止运行中释放 handle。
- 所有 `HObject`、`HTuple`、临时图像、区域和轮廓在适配器作用域内确定释放。
- cache 与 matching service 实现 `IAsyncDisposable`。应用退出先停止接收新视觉任务，等待活动 lease 归零，再依次释放 handle、`SemaphoreSlim` 和 native worker；组合根必须把该关闭步骤接入 `App.OnExit`，不能只依赖终结器。

### 13.2 取消

HALCON 26.05 的 `find_scaled_shape_model` 支持模型 timeout 和 `InterruptOperator` 软中断，但都不提供即时硬实时保证。本阶段采用可预测的第一期策略：

- 每次在模型锁内设置持久化的 `halcon.operatorTimeoutMs`，使用 shape model 自身 timeout 限制搜索阶段预算；不使用会污染线程池后续调用的线程局部 `set_operator_timeout`。HALCON 超时错误 `9400` 映射为 `MATCH_TIMEOUT` NG。timeout 后的缓存清理可能延长实际 operator 返回时间，且启用 timeout 有搜索开销，因此它不是总墙钟硬上界。
- 在图像转换前、`WaitAsync` 等待模型锁时、调用 operator 前、operator 返回后和逐候选验证之间检查 CancellationToken。
- 第一期不把 token 直接映射到 `InterruptOperator`，因为 worker 复用时若缺少调用代次保护，可能取消错误任务。后续只有在专用 worker、HALCON thread id 与调用 generation 一一绑定并有回归测试后才能启用软中断。
- native operator 已进入后不杀线程、不提前释放模型 handle、不让后台任务逃逸。UI 显示“已请求取消，将在当前算子安全返回后生效；搜索受超时限制，但清理阶段可能延长实际返回时间”。
- 用户取消不是普通 NG：operator 安全返回后再次检查 token 并抛出 `OperationCanceledException`，由 `ConfiguredVisionPipeline` 立即终止整条流程，不创建 `MATCH_CANCELLED` ToolResult，也不发布任何端口。运行时 timeout 才返回可诊断 NG。

## 14. 参数与 UI

HALCON 参数使用 `halcon.` 前缀，避免与 OpenCV 同名字段产生隐式耦合。新工具使用“严格”预设，默认满足本项目“误检优先抑制”的方向。

| 参数 | 严格预设初值 | UI/运行语义 |
|---|---:|---|
| `halcon.angleStartDeg` | `-180` | 起始角，度 |
| `halcon.angleExtentDeg` | `360` | 全圆搜索 |
| `halcon.scaleMin` | `0.90` | 最小等比尺度 |
| `halcon.scaleMax` | `1.10` | 最大等比尺度 |
| `halcon.candidateMinScore` | `0.65` | 仅用于候选生成，不代表最终接受 |
| `halcon.outerCoverageMin` | `0.90` | 外轮廓硬门 |
| `halcon.innerCoverageMin` | `0.82` | 内部关键特征硬门 |
| `halcon.edgeTolerancePx` | `3.0` | 覆盖判定与 P95 距离上限 |
| `halcon.polarityAgreementMin` | `0.90` | 暗内亮外一致率 |
| `halcon.candidateMaxOverlap` | `0.70` | HALCON 候选阶段宽松重叠上限 |
| `halcon.maxOverlap` | `0.25` | 填充支持区域 IoU 最终去重上限 |
| `halcon.greediness` | `0.80` | HALCON 候选搜索参数 |
| `halcon.subPixel` | `least_squares` | HALCON 亚像素模式 |
| `halcon.numLevels` | `auto` | 金字塔层级 |
| `halcon.candidateLimit` | 单目标 `32` / 多目标 `128` | 实际持久化的候选扫描安全上限；必须大于 `expectedCount` |
| `halcon.operatorTimeoutMs` | `5000` | shape 搜索阶段预算，允许 `100..60000` ms；清理时间不计入硬上界 |
| `expectedCount` | 多目标 `1` | HALCON 多目标精确期望数量；单目标不显示 |

参数对话框布局：

1. 顶部始终显示“匹配引擎”，新工具默认 HALCON；旧工具按实际/缺省引擎显示。
2. 常用区显示角度、尺度、候选分数、外/内部覆盖和多目标期望数量。
3. “高级参数”默认折叠，包含边缘距离、极性、候选/最终两级重叠、greediness、subpixel、层级、operator timeout 和候选安全上限；候选上限与 timeout 保存具体整数，不在运行时使用不透明的 `auto` 值。现有仅显示提示文字的 `MoreCommand` 必须替换为真实可编辑面板。
4. 提供“严格 / 均衡 / 高召回”三个起始预设；选择预设只是把具体数值复制到字段。用户修改任意值后显示“自定义”。
5. 配方保存所有最终数值，运行时不根据预设名重新计算，保证复现和二次开发。
6. 修改模型生成参数后标记“需要重新学习”；只修改运行验证门限时允许使用现有模型，并明确区分两类参数。
7. 学习预览分别显示外轮廓、内部特征和模型原点；运行预览显示每个硬门指标和首个拒绝原因。

“均衡”和“高召回”预设的具体数值在实现时由同一参数目录集中定义并用测试锁定，不能分别散落在 XAML、ViewModel 和 matcher 中。严格预设以上表为唯一默认来源。

UI 保存和运行时加载都通过同一参数目录校验 `operatorTimeoutMs` 为 `100..60000` 的正整数；`0`、负数、超大值和不可解析文本一律返回 `CONFIG_INVALID_PARAMETER`，不能把非法值解释成“关闭超时”。

## 15. 错误与诊断契约

生产匹配捕获预期的 HALCON、I/O、模型和配置异常，返回 `HasMatch=false`/Outcome=NG。至少提供：

- `CONFIG_UNKNOWN_ENGINE`
- `CONFIG_SERVICE_REQUIRED`
- `CONFIG_UNSUPPORTED_MODE`
- `CONFIG_INVALID_PARAMETER`
- `RUNTIME_NOT_FOUND`
- `RUNTIME_ARCH_MISMATCH`
- `RUNTIME_VERSION_MISMATCH`
- `LICENSE_UNAVAILABLE`
- `MODEL_PATH_INVALID`
- `MODEL_NOT_FOUND`
- `MODEL_CHECKSUM_MISMATCH`
- `MODEL_METADATA_INVALID`
- `MODEL_VERSION_MISMATCH`
- `MODEL_LOAD_FAILED`
- `MODEL_INTERNAL_FEATURES_WEAK`
- 第 8 节各候选拒绝码
- `MATCH_TIMEOUT`
- `MATCH_OPERATOR_FAILED`

诊断包含 `failureCode`、阶段、用户可读中文消息和日志用技术详情。生产页面不弹框；参数学习页面可在原页面显示失败原因。未知异常可以记录完整堆栈，但对操作员只显示稳定消息，且不得把无候选伪造成 `(0,0)` 定位。用户主动取消遵循第 13.2 节的 pipeline cancellation 契约，不转换成此处普通 NG 错误码。

## 16. 测试与验收

实施采用 Red-Green-Refactor。默认 CI 不要求 HALCON 许可，许可集成测试单独分类。

### 16.1 默认 CI：无 HALCON 许可

通过 fake `ITemplateMatchingBackend`、fake runtime probe 和临时模型存储验证：

1. 缺少 `engine` 精确路由 OpenCV；`Halcon` 精确路由 HALCON；未知值返回配置 NG。
2. 新建单/多目标默认 HALCON，旧参数不被改写。
3. 单目标与多目标调用同一批匹配核心，只改变数量与精确计数规则。
4. 直接测试真实 `TemplateCandidateValidator`：注入预制候选、距离图、极性样本和支持区域，六项硬门任一失败都拒绝候选，不存在加权补偿；fake 整体 backend 不承担此证明。
5. HALCON 路径逃逸、绝对路径、slug 清洗碰撞、所有权不一致和 checksum 不一致被拒绝，删除不会越过原始 ID 三元组。
6. 模型临时写入失败或取消时旧模型仍被引用；成功后 generation、路径和哈希一致；重置、复制配方和删除配方遵守第 11.4 节资源生命周期。
7. 缓存相同键只加载一次；checksum 变化安全换代；有活动 lease 时不提前 Dispose。
8. 缺运行时、许可失败、模型损坏、版本不匹配和 operator timeout 均返回诊断 NG；`operatorTimeoutMs` 的 min/max 合法，`0`、负值、超上限和非数字返回参数 NG；用户取消则传播 `OperationCanceledException` 并终止 pipeline，不发布部分结果。
9. 旧 8 列 `matches`、旧历史结果和 overlay schema v2 继续可读；新 `matchesV2` 包含 Scale；所有 Scale 投影与唯一的 `Pose.Scale` 真值一致。
10. `Pose2D.Scale` 保持旧构造/解构兼容，共享 similarity helper 在生产与各调试/示教路径正确缩放矩形、旋转矩形、圆和多边形 ROI，重建 Pose 的工具不丢 Scale。
11. 非零搜索 ROI 偏移只加一次，单/多目标 Row/Column 坐标无双重偏移。
12. 所有后端只有 `HasMatch && Outcome==Ok` 才发布操作性位姿；NG 会清除 legacy pose，不写位置端口，红色调试候选仍可显示。
13. 热改验证门限立即作用于缓存模型，学习审计默认值不覆盖当前请求；缓存中不存在旧门限污染。
14. x64 构建和所有现有 OpenCV V2 回归测试通过。

### 16.2 有许可 HALCON 集成测试

使用确定性合成的不对称长条产品：暗产品、亮背景、具有多个非对称内部结构。测试直接通过公共服务 seam 学习与匹配，不测试私有 operator 包装函数。

正例矩阵：

- 角度：`0°`、`35°`、`90°`、`-135°`
- 尺度：`0.90`、`1.00`、`1.10`
- 每个组合均验证 `HasMatch`、中心误差不超过 2 px、角度误差不超过 1°、尺度误差不超过 0.02。

负例必须零接受：

- 正面内部特征被改成反面布局。
- 外轮廓相似但内部结构不同的产品。
- 产品越过图像或搜索 ROI 边界。
- 只剩局部中段或严重遮挡。
- 极性相反的图像。

多目标：

- 放置多个不同角度/尺度的完整目标，验证精确数量、无重复、每件位姿和尺度。
- 少一件与多一件都为 NG。
- 相邻目标不能被一个目标拆成多个重复候选。
- 合法相邻目标不会被宽松候选 overlap 提前丢弃，最终支持区域 IoU 仍能删除同一产品的重复候选。

持久化：

- 学习并保存 `.shm`，释放所有 handle，清空缓存，从文件重新加载后结果等价。
- checksum、元数据或完整维护版本任一不匹配均不可运行。

运行时失败场景通过独立 x64 子进程验证 native resolver、错架构、错版本和缺 DLL，避免 HALCON 进程级初始化污染同一 testhost。许可集成测试使用非并行 collection；明确启用该测试集时，缺运行时或许可必须失败而不是静默 skip。

### 16.3 本地现场验收与性能

现场图片仅作为本地可选验收，不进入默认测试，也不围绕当前几张图建立算法分支。验收首先关注错误产品是否零接受，其次记录正例漏检；禁止为了让个别图片通过而自动放宽硬门。

首次集成后在目标工控机记录：硬件/软件指纹、冷加载、缓存命中单目标、不同目标数量的多目标耗时，以及内存/handle 数。先单独 warm-up HALCON 许可与内部线程池，再以多轮运行的 median、P95 和波动范围建立本机基线。结果写入版本化的 `docs/development/halcon-shape-performance-baseline.md`，并据实测方差给出下一次回归的相对门限；自动化测试不写一个脱离硬件的绝对毫秒常量。

## 17. 预计修改范围

主要新增或修改：

- `CVWork.sln`、相关 `.csproj`：HalconDotNet 依赖、禁用 x86、测试宿主与发布固定 x64/win-x64。
- `VisionStation.Domain/Models.cs`：`Pose2D` 的兼容 Scale。
- `VisionStation.Domain/DeviceConfigurationModels.cs`、`VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs`：HALCON runtime 配置、默认值与归一化。
- `VisionStation.Vision/TemplateMatching/*`：中立服务、请求/结果、严格路由、参数目录。
- `VisionStation.Vision/TemplateMatching/Halcon/*`：唯一 HalconDotNet 边界、学习、匹配、验证、缓存、运行时探测。
- `VisionStation.Vision/TemplateMatcher.cs`、`MultiTargetMatcher.cs`：旧实现兼容适配，移除静默路由。
- `VisionStation.Vision/Tools/TemplateLocateTool.cs`、`MultiTargetMatchTool.cs`：注入服务、Scale 和精确计数端口。
- `VisionStation.Vision` 的共享 `PoseSimilarityTransform`、`GeometryToolSupport`、TemplatePoint 与 CoordinateTransform：Scale 感知且不丢失的位姿/ROI 映射。
- `VisionStation.Infrastructure`：受控 `ITemplateModelStore` 实现。
- `VisionStation.Client/App.xaml.cs`、`VisionPipelineFactory.cs`：组合根注册/释放服务、后端和缓存。
- `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs`、配方仓储：模型资源复制、删除与失败回滚。
- `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs` 及对应 XAML：引擎选择、HALCON 参数、预设、学习预览与诊断。
- FindLine、FindCircle、Blob 等消费 PositionInput 的 UI ViewModel：改用共享 Scale-aware similarity helper。
- `VisionStation.Vision.UI/ViewModels/VisionToolCatalog.cs`：新工具默认和 Scale 端口。
- `VisionStation.Vision.UI` 的结果解析：优先 `matchesV2`，回退旧格式。
- `VisionStation.Vision.Tests`、`VisionStation.Vision.UI.Tests`：fake backend、合成图和兼容回归。
- `docs/development/halcon-shape-matching.md`：二次开发、部署和故障排查说明。
- `docs/development/halcon-shape-performance-baseline.md`：目标机版本化基线。

不进行无关目录重组、全方案格式化，也不把一次并行负载触发的 `SignalWaiter` 计时测试波动混入本功能提交。

## 18. 完成标准

1. 新建单目标和多目标工具默认 HALCON；旧配方缺少 `engine` 时仍运行 OpenCV。
2. 生产 `engine=Halcon` 只通过注入服务进入 HALCON；旧静态入口对 HALCON 返回 `CONFIG_SERVICE_REQUIRED`，未知引擎不再静默兜底，ToolResult 记录实际规范化后端。
3. 单/多目标确实共享一个 HALCON 批匹配与六硬门核心。
4. 指定角度和尺度的合成正例全部满足位姿容差。
5. 反面、相似件、边界截断、严重遮挡和反极性负例零接受。
6. 多目标只有精确计数时 OK，每件均返回 `X/Y/Angle/Scale/Score`，且无重复；Scale 的唯一真值正确驱动生产及调试/示教下游 ROI 等比映射。
7. `.shm` 的 Recipe/Flow/Tool 相对路径、元数据、checksum、原子 generation、配方复制/重置/删除和重新加载等价性通过测试。
8. 缺运行时、缺许可、损坏模型、版本不匹配和 operator timeout 均以诊断 NG 返回；用户取消终止 pipeline；两种路径都不崩溃或发布无效位置。
9. HALCON 类型不泄漏到 UI、业务和领域模型；fake backend 可以在无许可 CI 中覆盖路由与业务规则。
10. 任一后端 NG、timeout 或取消都不发布位置端口或 legacy pose；OpenCV 低分红色调试候选不再成为操作性位姿。
11. Release x64 构建、现有 254 项基线测试和新增测试通过；许可集成测试在本机 HALCON 26.05 环境通过。
12. 目标工控机的 cold/warm median、P95、内存与 handle 基线报告已生成，并记录基于实测波动的后续相对回归门限。
