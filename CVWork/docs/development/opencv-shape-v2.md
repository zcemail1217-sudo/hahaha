# OpenCV Shape V2 二次开发指南

本文说明单目标 `TemplateLocate` 的 OpenCV Shape V2 契约。调用方不应直接依赖 `OpenCvTemplateMatcher` 的内部旋转、Chamfer 或粗精搜索实现。

## 1. 公开入口

学习和运行都只通过现有公开 seam：

```csharp
var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["engine"] = "OpenCv",
    ["matchMode"] = "Shape",
    ["shapeScoreVersion"] = "2",
    ["shapeCoverageDistance"] = "3",
    ["minScore"] = "0.85",
    ["angleStart"] = "-180",
    ["angleExtent"] = "360",
    ["angleStep"] = "2"
};

foreach (var item in TemplateMatcher.Learn(trainingFrame, searchRoi, parameters))
{
    parameters[item.Key] = item.Value;
}

var result = TemplateMatcher.Match(frame, searchRoi, parameters, cancellationToken);
```

`Learn` 返回模型像素、模板范围、掩膜和评分版本等运行字段。配方保存时必须把返回字典合入原参数；只保存调用前参数会丢失模型。工具执行路径也应继续调用 `TemplateMatcher.Match`，不要在 Tool、ViewModel 或 UI 中复制匹配算法。

## 2. 评分版本与配方迁移

| `shapeScoreVersion` | 行为 | V2 诊断 |
| --- | --- | --- |
| 缺失、空字符串或显式 `1` | 使用兼容的 v1 单向评分 | `ShapeCoverage`、`ShapeReverseScore` 为 `null` |
| `2` | 使用双向质量验证 | 有效 V2 候选返回覆盖率和反向分 |
| 其他整数或不可解析文本 | fail-closed，不产生匹配 | 结果为 NG，消息为 `Unsupported OpenCV Shape score version.` |

- 新建的单目标 Shape 工具、默认配方以及新学习的 OpenCV Shape 模型默认写入 V2。
- 旧配方如果没有版本字段，在未编辑、未保存时仍按 v1 运行。
- 旧的单目标 Shape 工具在模板定位参数对话框中确认保存后才写入 `shapeScoreVersion=2` 和 `shapeCoverageDistance`，这是显式迁移点。
- 显式 `shapeScoreVersion=1` 不会被运行时静默提升。
- `MultiTargetMatch` 尚未接入 Shape V2；不要向多目标工具写入这两个字段，也不要假定它返回 V2 诊断。

V2 的分数分布不同于旧分数。配方迁移后必须用正样本、反面、散乱、密集和堆叠等现场图重新标定 `minScore`，不能直接沿用 v1 的经验阈值。

## 3. 参数和安全行为

### `shapeCoverageDistance`

- 含义：模板边缘可被搜索边缘视为“覆盖”的最大距离。
- 单位：原始学习模板分辨率下的像素。
- 默认值：`3`。
- 有效范围：`0.5..20`，超出范围会被夹紧。
- 粗搜索：使用 `max(0.75, shapeCoverageDistance * passScale)`，随当前缩放比例同步缩小。
- 原分辨率精搜索：使用原始配置值；最终返回的是精搜索质量值，不是粗分辨率的临时值。

`shapeCoverageDistance` 为 `NaN`、`Infinity` 等非有限值时使用安全默认值 `3`。`shapeScoreScale` 为非有限值时也回退到模板短边推导的安全尺度，非有限配置不能传播为非有限结果。版本字段无效则按上一节 fail-closed，而不是猜测版本。

### 其他相关参数

- `shapeScoreScale`：前向和反向距离共用的指数映射尺度。
  - V2：未显式设置时，根据 keep-bounds 后完整旋转画布的原分辨率短边推导；显式值按原分辨率解释，粗搜索时随 `passScale` 缩放。
  - V1 兼容：未显式设置时，根据当前搜索 pass 的未旋转模板短边推导；显式值沿用历史行为直接使用，避免旧配方的默认分数随旋转角度变化。
- `angleStart`、`angleExtent`、`angleStep`：控制候选旋转角区间和步长。`angleExtent=0` 时只搜索 `0°`。
- `autoContrast=true`：通过 Otsu 阈值推导 Canny 高低阈值；关闭时使用 `contrast`/`cannyLow` 和 `cannyHigh`。
- `shapeCoarseScale`、`shapeCoarseAngleStep` 和精搜索 margin 属于性能/搜索配置，改变后应同时验证中心、角度、覆盖率和耗时。

## 4. 分数与诊断语义

V2 对每个角度先用前向 Chamfer 找到候选位置，再在该候选的模板支持域内执行质量验证：

```text
ForwardScore = exp(-ForwardMeanDistance / Scale)
ReverseScore = exp(-ReverseMeanDistance / Scale)
Score        = min(ForwardScore, ReverseScore) * Coverage
```

- `ForwardScore`：模板边缘到最近搜索边缘的解释程度。
- `ShapeCoverage`：实际评分模板边缘中，距离搜索边缘不超过容差的比例，范围 `0..1`。
- `ShapeReverseScore`：候选支持域内的搜索边缘能被模板边缘解释的程度，范围 `0..1`。模板附近的多余边缘会降低该值。
- `Score`：最终判定分；`Outcome` 由它与 `minScore` 比较得到。

诊断字段的空值规则：

- v1、Managed、Gray 和 ORB 路径不承诺 V2 诊断，覆盖率和反向分为 `null`。
- V2 只有在形成有效 Shape 候选时才返回两项诊断；无足够边缘、支持域内无有效搜索边缘、旋转画布超过搜索区或匹配失败时可以为空。
- V2 不会在低边缘模型上偷偷回退到 Gray；它会返回无匹配，避免改变分数语义。
- 消费方必须显式处理 `null`，不能把缺失诊断显示成 `0`。

## 5. 几何、坐标和支持域

- 图像坐标原点位于左上角，X 向右、Y 向下。
- 对外 `Pose.Angle` 与 ROI/卡尺一致，为图像空间顺时针为正，并归一化到 `(-180, 180]`。
- 内部 OpenCV warp 角是屏幕上的逆时针旋转，最终位姿会取反。直接构造底层 `angleStart` 区间时要包含对应的 OpenCV 角；例如期望输出 `+35°`，搜索区间需包含 `-35°`。使用对称全角区间时不受符号换算影响。
- 每个角度都重新计算 keep-bounds 外接画布，并把原模板中心平移到新画布中心；旋转后宽高不再强行恢复为未旋转模板尺寸。
- 粗搜索回映射按 X/Y 实际缩放比例还原候选中心，精搜索、最终 `Pose`、评分轮廓和完整 ROI 共用同一候选中心。
- 旋转后的边缘和支持掩膜使用同一个仿射变换；二值数据使用最近邻插值。画布大于搜索区时跳过该角度，不进行二次裁剪评分。
- 存在 Polygon、Circle、RotatedRectangle 模板掩膜时，反向统计仅限于旋转后的掩膜；存在排除掩膜时同样沿用学习结果。没有掩膜时，支持域是完整的旋转模板矩形。支持域外的邻近产品边缘不会被计入反向惩罚。

## 6. 结果数据契约

`TemplateMatchResult` 中各轮廓不可混用：

- `ShapeContours`：真正参与 Shape 评分的模板边缘在最终位姿下的显示轮廓；为控制显示量可能按 `shapeOverlayMaxPoints` 抽样。
- `MatchedTemplateRoiContours`：完整训练 ROI 变换到最终位姿后的轮廓，不是评分边缘。
- `ShapeCoverage`、`ShapeReverseScore`：上一节定义的 V2 质量诊断。

`TemplateLocateTool` 将结果写入 `ToolResult.Data`：

- `hasMatch`：是否存在真实定位候选；新结果始终写入 `True` 或 `False`
- `shapeContours`
- `matchedTemplateRoiContours`
- `shapeCoverage`
- `shapeReverseScore`
- `overlaySchemaVersion=2`

`overlaySchemaVersion=2` 表示评分轮廓和完整 ROI 已分离。旧历史结果没有版本字段时，`shapeContours` 只能按旧的混合橙色轮廓显示；禁止根据“最后一条轮廓”或外接框猜测并拆分旧数据。

`hasMatch=False` 表示没有真实候选，UI 不得创建定位 Cross 或回退旋转矩形；合法的点云、评分轮廓和模板 ROI 仍可独立容错显示。持久化结果中即使 `hasMatch=True`，也只有 `x`、`y`、`angle`、`score` 四个主字段都存在且为有限数时才允许创建位置叠加；空值、`1` 或其他不可解析文本均按无候选关闭。兼容没有 `hasMatch` 的旧历史结果时，同样只用这四个有限主字段推断候选，`Outcome` 不能代替候选标志，因此真实的低分 NG 候选仍可显示。回退旋转矩形还要求模板宽高均为有限正数，轮廓是否显示不依赖这些位置字段。

## 7. UI 消费约束

- 运行时 `TemplateMatchResult` 和持久化 `ToolResult` 都交给 `TemplateLocateOverlayFactory.Create`；不要在页面中分别解析轮廓字符串。
- 评分边缘使用 `Warning`（橙色），完整模板 ROI 使用 `Info`（青色），定位中心使用最终 `Outcome` 的绿/红状态。
- V2 定位标签显示 `S` 和 `C`；v1 只显示 `S`。factory 会为定位标签设置 `PreserveLabelInResult=true`。
- 最终结果页必须再经过 `VisionResultOverlayProjector.Project`。它过滤方向轴和配方编辑用 `Neutral` ROI、保留 `Info` ROI，并且只保留显式标记 `PreserveLabelInResult` 的标签。
- 新增 overlay 时不要仅凭 `Cross` 类型保留标签；结果页标签保留是一项显式契约。

## 8. 后端和性能扩展边界

当前只有一个真实 Shape 后端，不新增只有一个实现的公共 matcher interface。等 OpenCV 与 HALCON 两个可运行 adapter 同时存在时，再在 `TemplateMatcher` seam 后提取接口。新后端必须保持相同的位姿、分数、空值、评分轮廓、完整 ROI 和 schema 语义，不能让 UI 按后端分支。

粗精搜索和资源生命周期有以下约束：

1. 粗搜索按实际 X/Y 比例映射候选中心和 keep-bounds 宽高，不能恢复成原模板尺寸。
2. 精搜索必须检查配置范围内的全部 refine 角度，最终只返回原分辨率结果。
3. 每个角度先生成一个前向候选，再只在候选支持域内计算覆盖率和反向距离；不要为反向评分扫描整张搜索图。
4. OpenCvSharp `Mat` 的 view、旋转边缘、掩膜、距离图和临时结果必须在所属 pass/角度结束时释放，不能跨角度累积。
5. 如性能回归，优先减少重复 `Mat` 构造或复用不变的支持域数据，但不能删除覆盖率/反向分门槛，也不能返回粗分辨率质量值。

## 9. 本地六图 opt-in 验收

默认测试不读取本机图片。设置环境变量后，验收测试会从 `正.bmp` 自动选择靠近图像中心、面积和长边满足现场基线的完整产品，四边扩展 14 px 学习模板；图像加载不计时，v1/v2 分别预热并交替计时。

```powershell
$env:VISIONSTATION_TEMPLATE_DATASET='C:\现场数据\图像'
dotnet restore .\CVWork.sln
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~LocalTemplateDatasetAcceptanceTests" `
  --logger "console;verbosity=detailed" --nologo
```

验收门槛是：`正.bmp` 为 Ok、覆盖率至少 `0.90`、V2 诊断与位姿均为有限值，且六图 V2 总耗时不超过同进程 v1 的 `1.30` 倍。其余图片用于输出诊断，不强制为 Ok。

验证后清理当前 PowerShell 进程中的变量：

```powershell
Remove-Item Env:VISIONSTATION_TEMPLATE_DATASET -ErrorAction SilentlyContinue
```

现场 BMP、裁剪模板、二值图、边缘图、截图以及任何由工业图派生的文件都不得提交 Git。仓库只保留 opt-in 测试代码和文字诊断。
