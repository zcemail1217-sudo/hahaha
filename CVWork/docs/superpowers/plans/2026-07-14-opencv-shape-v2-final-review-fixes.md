# OpenCV Shape V2 终审修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复整分支终审发现的四个兼容性缺口，使矩形完整模板 ROI 可显示、V1 评分尺度保持历史语义、MultiTarget 不携带 V2 字段，并且无有效候选时不伪造 `(0,0)` 定位叠加。

**Architecture:** 保持现有 `TemplateMatcher`、`TemplateLocateTool`、`TemplateLocateOverlayFactory` 和共享对话框 seam，不新增 matcher interface。每个缺口先通过最小端到端回归测试复现，再只在产生错误语义的模块内修复；新结果用显式 `hasMatch` 区分“真实低分候选”和“无候选”，旧结果继续通过有限主字段兼容解析。

**Tech Stack:** .NET 8、C#、OpenCvSharp、WPF、xUnit、Git worktree。

---

### Task 1: 为 Rectangle 生成完整模板 ROI 轮廓

**Files:**
- Modify: `VisionStation.Vision/OpenCvTemplateMatcher.cs:221-235`
- Modify: `VisionStation.Vision.Tests/TemplateLocateToolTests.cs`
- Modify: `VisionStation.Vision.Tests/LocalTemplateDatasetAcceptanceTests.cs`

- [ ] **Step 1: 写矩形结果 RED**

在 `TemplateLocateToolTests` 增加默认矩形模板测试：

```csharp
[Fact]
public void MatchReturnsCompleteRectangleTemplateRoiContour()
{
    var parameters = TemplateMatcherTestData.LearnRuntimeParameters();

    var match = TemplateMatcher.Match(TemplateMatcherTestData.CreateSearchFrame(), null, parameters);

    Assert.True(match.HasMatch, match.Message);
    var contour = Assert.Single(match.MatchedTemplateRoiContours!);
    Assert.Equal(4, contour.Count);
    Assert.Equal(60, contour.Min(point => point.X), 3);
    Assert.Equal(160, contour.Max(point => point.X), 3);
    Assert.Equal(40, contour.Min(point => point.Y), 3);
    Assert.Equal(340, contour.Max(point => point.Y), 3);
}
```

并把 `ExecuteAsyncSerializesVersionedSeparatedOverlayDiagnostics` 的 fixture 改为：

```csharp
var fixture = new TemplateMatcherFixture(
    TemplateMatcherTestData.CreateSearchFrame(),
    TemplateMatcherTestData.LearnRuntimeParameters());
```

在现场验收正例断言：

```csharp
Assert.NotNull(positive.V2.MatchedTemplateRoiContours);
Assert.NotEmpty(positive.V2.MatchedTemplateRoiContours);
```

- [ ] **Step 2: 运行并确认 RED**

Run:

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MatchReturnsCompleteRectangleTemplateRoiContour|FullyQualifiedName~ExecuteAsyncSerializesVersionedSeparatedOverlayDiagnostics" --nologo
```

Expected: `MatchReturnsCompleteRectangleTemplateRoiContour` 因 `MatchedTemplateRoiContours` 为空失败。

- [ ] **Step 3: 最小实现 Rectangle 四角**

在 `GetTemplateRoiContour` 增加：

```csharp
RoiShapeKind.Rectangle =>
[
    new Point2D(roi.X, roi.Y),
    new Point2D(roi.X + roi.Width, roi.Y),
    new Point2D(roi.X + roi.Width, roi.Y + roi.Height),
    new Point2D(roi.X, roi.Y + roi.Height)
],
```

不改变 Polygon、Circle、RotatedRectangle 的坐标语义。

- [ ] **Step 4: GREEN、现场检查并提交**

Run:

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TemplateLocateToolTests" --nologo
$env:VISIONSTATION_TEMPLATE_DATASET='C:\Users\Time\Desktop\框架AI优化\新程序\图像'
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~LocalTemplateDatasetAcceptanceTests" --logger "console;verbosity=detailed" --nologo
Remove-Item Env:VISIONSTATION_TEMPLATE_DATASET -ErrorAction SilentlyContinue
git diff --check
git add .\VisionStation.Vision\OpenCvTemplateMatcher.cs .\VisionStation.Vision.Tests\TemplateLocateToolTests.cs .\VisionStation.Vision.Tests\LocalTemplateDatasetAcceptanceTests.cs
git commit -m "fix: 显示矩形完整模板轮廓"
git push
```

Expected: 合成矩形与现场 `正.bmp` 的完整 ROI 均非空，现场质量和 1.3 性能门槛不变。

### Task 2: 恢复 V1 角度无关的默认评分尺度

**Files:**
- Modify: `VisionStation.Vision/OpenCvTemplateMatcher.cs:599-629`
- Modify: `VisionStation.Vision.Tests/TemplateMatcherTestData.cs`
- Modify: `VisionStation.Vision.Tests/OpenCvTemplateMatcherQualityTests.cs`

- [ ] **Step 1: 写 V1 斜角尺度 RED**

在 `TemplateMatcherTestData` 增加一个旋转后擦除上段的确定性搜索图：

```csharp
public static ImageFrame CreateRotatedFragmentSearchFrame(double clockwiseAngle)
{
    const int width = 500;
    const int height = 500;
    var center = new Point2d(250, 250);
    using var image = CreateProductMat(width, height, center, clockwiseAngle);
    var radians = clockwiseAngle * Math.PI / 180.0;
    var cos = Math.Cos(radians);
    var sin = Math.Sin(radians);
    Point Transform(Point2d point) => new(
        (int)Math.Round(center.X + point.X * cos - point.Y * sin),
        (int)Math.Round(center.Y + point.X * sin + point.Y * cos));
    var eraser = new[]
    {
        Transform(new Point2d(-60, -150)),
        Transform(new Point2d(60, -150)),
        Transform(new Point2d(60, -60)),
        Transform(new Point2d(-60, -60))
    };
    Cv2.FillPoly(image, [eraser], Scalar.White);
    return CreateFrame(image, "synthetic-rotated-fragment-search");
}
```

在质量测试增加：

```csharp
[Theory]
[InlineData(0)]
[InlineData(35)]
[InlineData(90)]
[InlineData(-135)]
public void LegacyShapeDefaultScaleUsesUnrotatedPassSize(double clockwiseAngle)
{
    var defaults = TemplateMatcherTestData.LearnRuntimeParameters();
    defaults.Remove("shapeScoreVersion");
    defaults["angleStart"] = (-clockwiseAngle).ToString(CultureInfo.InvariantCulture);
    defaults["angleExtent"] = "0.5";
    defaults["angleStep"] = "1";
    defaults["minScore"] = "0";
    var explicitLegacyScale = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase)
    {
        ["shapeScoreScale"] = "18"
    };
    var search = TemplateMatcherTestData.CreateRotatedFragmentSearchFrame(clockwiseAngle);

    var actual = TemplateMatcher.Match(search, null, defaults);
    var expected = TemplateMatcher.Match(search, null, explicitLegacyScale);

    Assert.True(actual.HasMatch, actual.Message);
    Assert.True(expected.HasMatch, expected.Message);
    Assert.InRange(expected.Score, 0.01, 0.999999);
    Assert.Equal(expected.Score, actual.Score, 6);
}
```

- [ ] **Step 2: 运行并确认 RED**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~LegacyShapeDefaultScaleUsesUnrotatedPassSize" --nologo
```

Expected: `35°`、`-135°` 至少一例因默认 scale 使用 keep-bounds 画布短边而分数不等。

- [ ] **Step 3: 仅让 V1 使用未旋转 pass 尺寸**

将 pass 内初始分数改为：

```csharp
var score = scoreVersion == 1
    ? CreateShapeScore(minDistance, templateEdges.Width, templateEdges.Height, parameters)
    : 0;
```

V2 继续由 `EvaluateShapeV2` 使用旋转画布和 V2 scale 计算最终值；粗搜中的 `templateEdges` 本身已经按 passScale 缩放，因此保持旧粗搜量纲。

- [ ] **Step 4: GREEN、回归并提交**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OpenCvTemplateMatcherQualityTests|FullyQualifiedName~OpenCvTemplateMatcherGeometryTests" --nologo
git diff --check
git add .\VisionStation.Vision\OpenCvTemplateMatcher.cs .\VisionStation.Vision.Tests\TemplateMatcherTestData.cs .\VisionStation.Vision.Tests\OpenCvTemplateMatcherQualityTests.cs
git commit -m "fix: 保持 V1 旋转评分尺度"
git push
```

Expected: V1 四个角度与显式旧 scale 等价；V2 旋转画布默认 scale 测试仍通过。

### Task 3: 清理 MultiTarget 学习产生的 V2 字段

**Files:**
- Modify: `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs:1849-1883`
- Modify: `VisionStation.Vision.UI.Tests/TemplateLocateToolDialogViewModelTests.cs`

- [ ] **Step 1: 写 Learn→Apply RED**

增加真实公共命令测试：

```csharp
[Fact]
public void MultiTargetLearnThenApplyDoesNotPersistSingleTargetShapeV2Parameters()
{
    using var tempDirectory = new TempDirectory();
    var frame = CreateShapeFrame();
    var tool = new VisionToolItem
    {
        Id = "multi-target",
        Name = "Multi target",
        Kind = VisionToolKind.MultiTargetMatch,
        Enabled = true,
        ParametersText = "engine=OpenCv; matchMode=Shape; templateRoiX=60; templateRoiY=40; templateRoiWidth=100; templateRoiHeight=300"
    };
    var viewModel = new TemplateLocateToolDialogViewModel(
        tool,
        Array.Empty<RoiChoiceItem>(),
        Array.Empty<RoiDefinition>(),
        "Flow",
        frame,
        new RuntimePaths(tempDirectory.Path),
        new NullAppLogService());

    viewModel.LearnTemplateCommand.Execute();
    Assert.True(SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(10)));
    viewModel.ApplyTo(tool);

    var parameters = tool.ToDefinition().Parameters;
    Assert.True(parameters.ContainsKey("templateImagePng"));
    Assert.False(parameters.ContainsKey("shapeScoreVersion"));
    Assert.False(parameters.ContainsKey("shapeCoverageDistance"));
}
```

- [ ] **Step 2: 运行并确认 RED**

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MultiTargetLearnThenApplyDoesNotPersistSingleTargetShapeV2Parameters" --nologo
```

Expected: Learn 返回的两个 V2 键被 `_parameters` 合并，Apply 后断言失败。

- [ ] **Step 3: 在保存 seam 清理 MultiTarget 专属无效键**

在 `BuildCurrentParameters` 的单目标 V2 分支前加入明确互斥逻辑：

```csharp
if (_toolKind == VisionToolKind.MultiTargetMatch)
{
    parameters.Remove("shapeScoreVersion");
    parameters.Remove("shapeCoverageDistance");
}
else if (SelectedMatchMode.Equals("Shape", StringComparison.OrdinalIgnoreCase))
{
    parameters["shapeScoreVersion"] = "2";
    parameters["shapeCoverageDistance"] = GetParameterValue("shapeCoverageDistance", "3");
}
```

这同时清理已被旧学习流程污染的 MultiTarget 配方；TemplateLocate Gray 模式继续保留其既有休眠参数，避免无关数据删除。

- [ ] **Step 4: GREEN、全 UI 回归并提交**

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TemplateLocateToolDialogViewModelTests" --nologo
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --no-restore --nologo
git diff --check
git add .\VisionStation.Vision.UI\ViewModels\TemplateLocateToolDialogViewModel.cs .\VisionStation.Vision.UI.Tests\TemplateLocateToolDialogViewModelTests.cs
git commit -m "fix: 隔离多目标模板学习参数"
git push
```

### Task 4: 用显式 hasMatch 阻止伪定位叠加

**Files:**
- Modify: `VisionStation.Vision/Tools/TemplateLocateTool.cs:15-106`
- Modify: `VisionStation.Vision.UI/Services/TemplateLocateOverlayFactory.cs`
- Modify: `VisionStation.Vision.Tests/TemplateLocateToolTests.cs`
- Modify: `VisionStation.Vision.UI.Tests/TemplateLocateOverlayFactoryTests.cs`
- Modify: `docs/development/opencv-shape-v2.md`

- [ ] **Step 1: 写缺失输入、损坏字段和旧结果兼容 RED**

在 Vision Tool 测试补：

```csharp
Assert.Equal("False", result.Data["hasMatch"]);
```

在 UI 测试新增：

```csharp
[Fact]
public void MissingInputResultDoesNotCreatePositionOverlay()
{
    var result = new ToolResult
    {
        ToolId = "locate",
        Kind = VisionToolKind.TemplateLocate,
        Outcome = InspectionOutcome.Ng,
        Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["overlaySchemaVersion"] = "2",
            ["hasMatch"] = "False",
            ["missingInput"] = "ImageInput"
        }
    };

    var projected = VisionResultOverlayProjector.Project(TemplateLocateOverlayFactory.Create(result));

    Assert.Empty(projected);
}
```

把旧的 `CreateDefaultsInvalidPrimaryFieldsAndStillAddsCrossAndFallback` 改为断言：无有效 x/y/angle/score 时没有 Cross 或 fallback rectangle；合法 contours 仍独立显示。再增加旧历史兼容：删除 `hasMatch` 但保留有限主字段时仍创建 Cross。

- [ ] **Step 2: 运行并确认 RED**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ExecuteAsyncMissingInputStillWritesOverlaySchemaVersion" --nologo
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TemplateLocateOverlayFactoryTests" --nologo
```

Expected: ToolResult 缺少 `hasMatch`；factory 仍在 `(0,0)` 创建 Cross/rectangle。

- [ ] **Step 3: 写入 hasMatch 并收紧 factory 主字段契约**

`TemplateLocateTool` 的 missing-input Data 写入 `hasMatch=False`，普通路径 Data 写入：

```csharp
["hasMatch"] = match.HasMatch.ToString(),
```

factory 的结构化入口使用 `result.HasMatch`；持久化入口规则：

```csharp
var hasPosition = TryGetDouble(data, "x", out var x) &&
                  TryGetDouble(data, "y", out var y) &&
                  TryGetDouble(data, "angle", out var angle) &&
                  TryGetDouble(data, "score", out var score);
if (data.TryGetValue("hasMatch", out var rawHasMatch))
{
    hasPosition = bool.TryParse(rawHasMatch, out var parsedHasMatch) && parsedHasMatch && hasPosition;
}
```

把 `hasPosition` 传给 `CreateCore`。contours 始终可独立创建；只有 `hasPosition` 为真才创建 Cross；fallback rectangle 还必须满足 `templateWidth > 0 && templateHeight > 0`。旧历史没有 `hasMatch` 时，只要四个主字段有限就继续显示低分候选。

- [ ] **Step 4: 更新二次开发文档**

在结果契约中加入：

```markdown
- `hasMatch`：是否存在真实定位候选。新结果为 `false` 时禁止创建定位 Cross 或回退矩形。
- 兼容旧历史时，只有 `x`、`y`、`angle`、`score` 均为有限值才推断为可显示候选；轮廓字段可独立容错显示。
```

- [ ] **Step 5: GREEN、全量回归并提交**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --no-restore --nologo
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --no-restore --nologo
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release --no-restore --nologo
dotnet build .\CVWork.sln -c Release --no-restore --nologo
git diff --check
git add .\VisionStation.Vision\Tools\TemplateLocateTool.cs .\VisionStation.Vision.UI\Services\TemplateLocateOverlayFactory.cs .\VisionStation.Vision.Tests\TemplateLocateToolTests.cs .\VisionStation.Vision.UI.Tests\TemplateLocateOverlayFactoryTests.cs .\docs\development\opencv-shape-v2.md
git commit -m "fix: 禁止无候选结果伪造定位"
git push
```

Expected: 无输入/无候选结果无位置叠加，真实 NG 候选和旧历史有效字段仍显示；三个测试项目与 Release build 均成功。

### Task 5: 最终现场与版本库验证

**Files:**
- Verify only.

- [ ] **Step 1: 运行无环境变量和真实现场验收**

```powershell
Remove-Item Env:VISIONSTATION_TEMPLATE_DATASET -ErrorAction SilentlyContinue
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~LocalTemplateDatasetAcceptanceTests" --logger "console;verbosity=detailed" --nologo
$env:VISIONSTATION_TEMPLATE_DATASET='C:\Users\Time\Desktop\框架AI优化\新程序\图像'
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~LocalTemplateDatasetAcceptanceTests" --logger "console;verbosity=detailed" --nologo
Remove-Item Env:VISIONSTATION_TEMPLATE_DATASET -ErrorAction SilentlyContinue
```

Expected: 无环境变量准确 SKIP；真实正图 Ok、Coverage >= 0.90、完整矩形 ROI 非空、V2/V1 <= 1.30。

- [ ] **Step 2: 核对分支与图片边界**

```powershell
git diff --check fb2f7b7..HEAD
git status --short --branch
git rev-list --left-right --count '@{upstream}'...HEAD
git diff --name-only fb2f7b7..HEAD
```

Expected: 工作区干净、ahead/behind 为 `0 0`，功能范围没有 BMP/PNG/JPG/TIFF/GIF。
