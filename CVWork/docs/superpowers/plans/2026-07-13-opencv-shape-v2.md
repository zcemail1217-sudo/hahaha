# OpenCV Shape V2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复整产品长条模板的大角度旋转裁剪和局部虚高分数，并让评分边缘、模板 ROI 与质量诊断在生产页和调参页中保持一致语义。

**Architecture:** 保持 `TemplateMatcher.Learn/Match` 作为唯一外部 seam，把 keep-bounds 几何、粗精坐标映射和 Shape V2 双向质量验证隐藏在 `OpenCvTemplateMatcher` implementation 内。匹配结果通过 init-only 属性暴露覆盖率、反向得分和模板 ROI；UI 使用共享 overlay factory 与 result projector，避免生产页和调试页规则分叉。

**Tech Stack:** .NET 8、C#、OpenCvSharp 4.13、xUnit 2.9、WPF、Prism。

---

设计依据：`docs/superpowers/specs/2026-07-13-opencv-shape-v2-design.md`。

## 文件职责

**新增：**

- `VisionStation.Vision.Tests/OpenCvTemplateMatcherGeometryTests.cs`：旋转与粗精定位回归。
- `VisionStation.Vision.Tests/TemplateMatcherTestData.cs`：Vision 测试项目内共享的确定性合成图和已学习参数。
- `VisionStation.Vision.Tests/OpenCvTemplateMatcherQualityTests.cs`：V1/V2 分数、覆盖率和反向距离。
- `VisionStation.Vision.Tests/TemplateLocateToolTests.cs`：ToolResult 分离语义。
- `VisionStation.Vision.UI/Services/TemplateLocateOverlayFactory.cs`：运行时与持久化结果的统一叠加语义。
- `VisionStation.Vision.UI/Services/VisionResultOverlayProjector.cs`：生产与调试结果投影规则。
- `VisionStation.Vision.UI.Tests/TemplateLocateOverlayFactoryTests.cs`
- `VisionStation.Vision.UI.Tests/VisionResultOverlayProjectorTests.cs`
- `VisionStation.Vision.Tests/LocalTemplateDatasetAcceptanceTests.cs`：环境变量驱动的现场验收，不携带图片。
- `docs/development/opencv-shape-v2.md`：二次开发契约。

**修改：**

- `VisionStation.Vision/OpenCvTemplateMatcher.cs`
- `VisionStation.Vision/TemplateMatcher.cs`
- `VisionStation.Vision/Tools/TemplateLocateTool.cs`
- `VisionStation.Infrastructure/DefaultRecipeFactory.cs`
- `VisionStation.Vision.UI/ViewModels/VisionToolCatalog.cs`
- `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs`
- `VisionStation.Vision.UI/Services/VisionOverlayBuilder.cs`
- `VisionStation.Vision.UI/Models/VisionOverlayItem.cs`
- `VisionStation.Vision.UI/Controls/ResultOverlayCanvas.cs`
- `VisionStation.Client/ViewModels/ProductionDashboardViewModel.cs`
- `VisionStation.Vision.UI/ViewModels/VisionDebugViewModel.cs`
- 对应 `VisionStation.Vision.UI.Tests` 测试文件。

## Task 1：Shape 旋转保留完整边界

**Files:**

- Create: `VisionStation.Vision.Tests/OpenCvTemplateMatcherGeometryTests.cs`
- Modify: `VisionStation.Vision/OpenCvTemplateMatcher.cs:58-106, 617-663`

- [ ] **Step 1：写公开 seam 的 RED 测试**

创建纯内存不对称长条产品。模板 ROI 固定为 `100×300`；测试 `0°/35°/90°/-135°`。测试图形直接按点坐标旋转，不调用生产旋转方法。

```csharp
[Theory]
[InlineData(0)]
[InlineData(35)]
[InlineData(90)]
[InlineData(-135)]
public void WholeShapeRotationPreservesCanvasCenterAndPose(double clockwiseAngle)
{
    var expectedCenter = new Point2d(430, 330);
    var result = MatchSynthetic(
        clockwiseAngle,
        new Size(700, 700),
        expectedCenter,
        forceDirectPass: true);
    var expected = ExpectedCanvasSize(100, 300, clockwiseAngle);

    Assert.True(result.HasMatch, result.Message);
    Assert.Equal(expected.Width, result.TemplateWidth);
    Assert.Equal(expected.Height, result.TemplateHeight);
    Assert.InRange(Math.Abs(result.Pose.X - expectedCenter.X), 0, 2);
    Assert.InRange(Math.Abs(result.Pose.Y - expectedCenter.Y), 0, 2);
    Assert.InRange(AngleDistance(result.Pose.Angle, clockwiseAngle), 0, 1);
}

[Fact]
public void ShapeCanMatchWhenOnlyRotatedCanvasFitsSearchRegion()
{
    var result = MatchSynthetic(
        90,
        new Size(320, 120),
        new Point2d(160, 60),
        forceDirectPass: true);

    Assert.True(result.HasMatch, result.Message);
    Assert.Equal(300, result.TemplateWidth);
    Assert.Equal(100, result.TemplateHeight);
}
```

测试 helper 使用下列固定多边形和参数；`angleExtent` 必须非零，否则现有枚举逻辑只搜索 0°。

```csharp
private static readonly Point2d[][] ProductPolygons =
[
    [new(-16, -130), new(16, -130), new(16, 130), new(-16, 130)],
    [new(-16, -115), new(38, -115), new(38, -82), new(-16, -82)],
    [new(-42, 70), new(16, 70), new(16, 110), new(-42, 110)]
];

private static Dictionary<string, string> CreateParameters(double clockwiseAngle, bool direct)
{
    return new(StringComparer.OrdinalIgnoreCase)
    {
        ["engine"] = "OpenCv",
        ["matchMode"] = "Shape",
        ["autoContrast"] = "false",
        ["contrast"] = "30",
        ["cannyHigh"] = "80",
        ["minScore"] = "0",
        ["angleStart"] = (-clockwiseAngle).ToString("0.###", CultureInfo.InvariantCulture),
        ["angleExtent"] = "0.5",
        ["angleStep"] = "1",
        ["shapeCoarseScale"] = direct ? "1" : "0",
        ["templateRoiX"] = "60",
        ["templateRoiY"] = "40",
        ["templateRoiWidth"] = "100",
        ["templateRoiHeight"] = "300"
    };
}

private static TemplateMatchResult MatchSynthetic(
    double clockwiseAngle,
    Size searchSize,
    Point2d expectedCenter,
    bool forceDirectPass)
{
    var parameters = CreateParameters(clockwiseAngle, forceDirectPass);
    var training = CreateGrayFrame(220, 380, new Point2d(110, 190), 0);
    foreach (var learned in TemplateMatcher.Learn(training, null, parameters))
    {
        parameters[learned.Key] = learned.Value;
    }

    return TemplateMatcher.Match(
        CreateGrayFrame(searchSize.Width, searchSize.Height, expectedCenter, clockwiseAngle),
        null,
        parameters);
}

private static ImageFrame CreateGrayFrame(int width, int height, Point2d center, double angle)
{
    using var image = new Mat(height, width, MatType.CV_8UC1, Scalar.White);
    var radians = angle * Math.PI / 180.0;
    var cos = Math.Cos(radians);
    var sin = Math.Sin(radians);
    foreach (var local in ProductPolygons)
    {
        var points = local.Select(point => new Point(
            (int)Math.Round(center.X + point.X * cos - point.Y * sin),
            (int)Math.Round(center.Y + point.X * sin + point.Y * cos))).ToArray();
        Cv2.FillConvexPoly(image, points, Scalar.Black);
    }

    image.GetArray(out byte[] pixels);
    return new ImageFrame(
        Guid.NewGuid().ToString("N"), width, height, width,
        PixelFormatKind.Gray8, pixels, DateTimeOffset.UtcNow, "Synthetic");
}

private static Size ExpectedCanvasSize(int width, int height, double angle)
{
    var radians = angle * Math.PI / 180.0;
    var cos = Math.Abs(Math.Cos(radians));
    var sin = Math.Abs(Math.Sin(radians));
    cos = Math.Abs(cos - 1) < 1e-12 ? 1 : Math.Abs(cos) < 1e-12 ? 0 : cos;
    sin = Math.Abs(sin - 1) < 1e-12 ? 1 : Math.Abs(sin) < 1e-12 ? 0 : sin;
    return new Size(
        (int)Math.Ceiling(width * cos + height * sin),
        (int)Math.Ceiling(width * sin + height * cos));
}

private static double AngleDistance(double left, double right)
{
    var difference = Math.Abs(left - right) % 360;
    return Math.Min(difference, 360 - difference);
}
```

- [ ] **Step 2：运行并确认 RED**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~OpenCvTemplateMatcherGeometryTests" --nologo
```

Expected: 大角度仍返回 `100×300`；窄 ROI 用例在匹配前置尺寸检查处返回 `HasMatch=false`。

- [ ] **Step 3：实现唯一 keep-bounds 几何源**

```csharp
private sealed class KeepBoundsRotation : IDisposable
{
    public KeepBoundsRotation(Size canvasSize, Mat matrix)
    {
        CanvasSize = canvasSize;
        Matrix = matrix;
    }

    public Size CanvasSize { get; }
    public Mat Matrix { get; }
    public void Dispose() => Matrix.Dispose();
}

private static Size GetRotatedCanvasSize(Size size, double angle)
{
    var radians = angle * Math.PI / 180.0;
    var cos = SnapTrig(Math.Abs(Math.Cos(radians)));
    var sin = SnapTrig(Math.Abs(Math.Sin(radians)));
    return new Size(
        Math.Max(1, (int)Math.Ceiling(size.Width * cos + size.Height * sin)),
        Math.Max(1, (int)Math.Ceiling(size.Width * sin + size.Height * cos)));
}

private static double SnapTrig(double value) =>
    Math.Abs(value) < 1e-12 ? 0 : Math.Abs(value - 1) < 1e-12 ? 1 : value;

private static KeepBoundsRotation CreateKeepBoundsRotation(Size size, double angle)
{
    var canvas = GetRotatedCanvasSize(size, angle);
    var center = new Point2f(size.Width / 2f, size.Height / 2f);
    var matrix = Cv2.GetRotationMatrix2D(center, angle, 1.0);
    matrix.Set(0, 2, matrix.At<double>(0, 2) + canvas.Width / 2.0 - center.X);
    matrix.Set(1, 2, matrix.At<double>(1, 2) + canvas.Height / 2.0 - center.Y);
    return new KeepBoundsRotation(canvas, matrix);
}

private static Mat WarpBinaryKeepBounds(Mat source, KeepBoundsRotation rotation)
{
    var result = new Mat();
    Cv2.WarpAffine(
        source, result, rotation.Matrix, rotation.CanvasSize,
        InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.Black);
    return result;
}
```

`RotateShapeEdges` 调用该实现。把模板尺寸前置拒绝移动到 `ResolveEffectiveMode` 之后，并只对非 Shape 模式执行；Shape 由每角度真实画布决定是否跳过。

- [ ] **Step 4：GREEN、回归并提交**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~OpenCvTemplateMatcherGeometryTests" --nologo
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --nologo
git diff --check
git add .\VisionStation.Vision\OpenCvTemplateMatcher.cs .\VisionStation.Vision.Tests\OpenCvTemplateMatcherGeometryTests.cs
git commit -m "fix: 保留形状模板旋转边界"
git push
```

## Task 2：修正粗搜索到精搜索的中心和尺寸

**Files:**

- Modify: `VisionStation.Vision.Tests/OpenCvTemplateMatcherGeometryTests.cs`
- Modify: `VisionStation.Vision/OpenCvTemplateMatcher.cs:461-510, 913-927, 1302-1312`

- [ ] **Step 1：增加强制粗精路径 RED**

```csharp
[Fact]
public void CoarseToRefinePreservesRotatedCenterAndCanvas()
{
    var expectedCenter = new Point2d(780, 480);
    var result = MatchSynthetic(90, new Size(1200, 960), expectedCenter, false);

    Assert.True(result.HasMatch, result.Message);
    Assert.Equal(300, result.TemplateWidth);
    Assert.Equal(100, result.TemplateHeight);
    Assert.InRange(Math.Abs(result.Pose.X - expectedCenter.X), 0, 2);
    Assert.InRange(Math.Abs(result.Pose.Y - expectedCenter.Y), 0, 2);
}
```

- [ ] **Step 2：运行并确认 RED 来自 coarse 映射**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~CoarseToRefinePreservesRotatedCenterAndCanvas" --nologo
```

Expected: Task 1 完成后，宽高或中心仍失败，因为 `scaledCoarse` 恢复成未旋转尺寸。

- [ ] **Step 3：以候选中心映射 coarse 结果**

```csharp
public double CenterX => X + Width / 2.0;
public double CenterY => Y + Height / 2.0;

private static MatchCandidate ScaleCoarseCandidate(
    MatchCandidate coarse,
    Size coarseSearch,
    Size fullSearch,
    Size fullTemplate)
{
    var centerX = coarse.CenterX * fullSearch.Width / coarseSearch.Width;
    var centerY = coarse.CenterY * fullSearch.Height / coarseSearch.Height;
    var canvas = GetRotatedCanvasSize(fullTemplate, coarse.Angle);
    var x = Math.Clamp(
        (int)Math.Round(centerX - canvas.Width / 2.0), 0,
        Math.Max(0, fullSearch.Width - canvas.Width));
    var y = Math.Clamp(
        (int)Math.Round(centerY - canvas.Height / 2.0), 0,
        Math.Max(0, fullSearch.Height - canvas.Height));
    return coarse with { X = x, Y = y, Width = canvas.Width, Height = canvas.Height };
}
```

- [ ] **Step 4：用全部 refine 角度的最大画布创建 ROI**

```csharp
private static Rect CreateRefineRegion(
    Size search,
    Size template,
    MatchCandidate coarse,
    IReadOnlyCollection<double> angles,
    IReadOnlyDictionary<string, string> parameters)
{
    var sizes = angles.Select(angle => GetRotatedCanvasSize(template, angle)).ToArray();
    if (sizes.Length == 0)
    {
        return new Rect();
    }

    var requiredWidth = sizes.Max(size => size.Width);
    var requiredHeight = sizes.Max(size => size.Height);
    var factor = Math.Clamp(GetDouble(parameters, "shapeRefineMargin", 0.9), 0.25, 3);
    var width = Math.Min(search.Width,
        requiredWidth + 2 * Math.Max(24, (int)Math.Ceiling(requiredWidth * factor)));
    var height = Math.Min(search.Height,
        requiredHeight + 2 * Math.Max(24, (int)Math.Ceiling(requiredHeight * factor)));
    var left = Math.Clamp((int)Math.Round(coarse.CenterX - width / 2.0), 0, search.Width - width);
    var top = Math.Clamp((int)Math.Round(coarse.CenterY - height / 2.0), 0, search.Height - height);
    return new Rect(left, top, width, height);
}
```

`FindBestShapePass` 增加 `double passScale`。直接路径和精搜传 `1.0`；粗搜传实际粗模板尺寸与原模板尺寸的最小比率。精搜失败返回 `MatchCandidate.None`，不得返回低分辨率候选。

- [ ] **Step 5：GREEN、回归并提交**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~OpenCvTemplateMatcherGeometryTests" --nologo
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --nologo
git diff --check
git add .\VisionStation.Vision\OpenCvTemplateMatcher.cs .\VisionStation.Vision.Tests\OpenCvTemplateMatcherGeometryTests.cs
git commit -m "fix: 统一形状匹配粗精定位几何"
git push
```

## Task 3：实现版本化 Shape V2 质量评分

**Files:**

- Create: `VisionStation.Vision.Tests/OpenCvTemplateMatcherQualityTests.cs`
- Create: `VisionStation.Vision.Tests/TemplateMatcherTestData.cs`
- Modify: `VisionStation.Vision.Tests/OpenCvTemplateMatcherGeometryTests.cs`
- Modify: `VisionStation.Vision/OpenCvTemplateMatcher.cs:13-45, 461-570, 878-887, 1280-1312`
- Modify: `VisionStation.Vision/TemplateMatcher.cs:14-27`

- [ ] **Step 1：写版本、反向距离、覆盖率和完整正例 RED**

把 Task 1 的 frame/parameter helper 移入测试项目的 `internal static TemplateMatcherTestData`。公开给同项目测试的成员固定为 `CreateTrainingFrame`、`CreateLearningParameters`、`LearnRuntimeParameters`、`CreateSearchFrame` 和 `CreatePolygonTemplateFixture`。`fragmentOnly` 只绘制产品中部 35%；`extraEdges` 在支持域内按 `y=95,115,...,235` 绘制 8 条横线，不使用随机数：

```csharp
internal sealed record TemplateMatcherFixture(
    ImageFrame SearchFrame,
    Dictionary<string, string> Parameters);

internal static class TemplateMatcherTestData
{
private static readonly Point2d[][] ProductPolygons =
[
    [new(-16, -130), new(16, -130), new(16, 130), new(-16, 130)],
    [new(-16, -115), new(38, -115), new(38, -82), new(-16, -82)],
    [new(-42, 70), new(16, 70), new(16, 110), new(-42, 110)]
];

public static ImageFrame CreateTrainingFrame()
{
    using var image = CreateProductMat(1.0);
    return ToFrame(image, "SyntheticTraining");
}

public static Dictionary<string, string> CreateLearningParameters() =>
    new(StringComparer.OrdinalIgnoreCase)
    {
        ["engine"] = "OpenCv",
        ["matchMode"] = "Shape",
        ["autoContrast"] = "false",
        ["contrast"] = "30",
        ["cannyHigh"] = "80",
        ["minScore"] = "0.85",
        ["angleStart"] = "-180",
        ["angleExtent"] = "360",
        ["angleStep"] = "2",
        ["shapeCoarseScale"] = "1",
        ["templateRoiX"] = "60",
        ["templateRoiY"] = "40",
        ["templateRoiWidth"] = "100",
        ["templateRoiHeight"] = "300"
    };

public static ImageFrame CreateSearchFrame(bool fragmentOnly = false, bool extraEdges = false)
{
    using var image = CreateProductMat(fragmentOnly ? 0.35 : 1.0);
    if (extraEdges)
    {
        for (var y = 95; y <= 235; y += 20)
        {
            Cv2.Line(image, new Point(70, y), new Point(150, y), Scalar.Black, 2);
        }
    }

    return ToFrame(image, "SyntheticSearch");
}

public static Dictionary<string, string> LearnRuntimeParameters()
{
    var parameters = CreateLearningParameters();
    foreach (var item in TemplateMatcher.Learn(CreateTrainingFrame(), null, parameters))
    {
        parameters[item.Key] = item.Value;
    }

    parameters["minScore"] = "0.85";
    return parameters;
}

public static TemplateMatcherFixture CreatePolygonTemplateFixture()
{
    var parameters = CreateLearningParameters();
    var roi = new RoiDefinition
    {
        Id = "template-roi",
        Name = "Template",
        Shape = RoiShapeKind.Polygon,
        Points =
        [
            new Point2D(60, 40),
            new Point2D(160, 40),
            new Point2D(160, 340),
            new Point2D(60, 340)
        ]
    };
    parameters["templateRoiJson"] = JsonSerializer.Serialize(roi);
    parameters["templateRoiShape"] = "Polygon";
    foreach (var item in TemplateMatcher.Learn(CreateTrainingFrame(), null, parameters))
    {
        parameters[item.Key] = item.Value;
    }

    return new TemplateMatcherFixture(CreateSearchFrame(), parameters);
}

private static Mat CreateProductMat(double visibleFraction)
{
    var image = new Mat(380, 220, MatType.CV_8UC1, Scalar.White);
    foreach (var local in ProductPolygons)
    {
        var points = local.Select(point => new Point(
            (int)Math.Round(110 + point.X),
            (int)Math.Round(190 + point.Y))).ToArray();
        Cv2.FillConvexPoly(image, points, Scalar.Black);
    }

    if (visibleFraction < 1)
    {
        var visibleHeight = (int)Math.Round(300 * visibleFraction);
        var top = 190 - visibleHeight / 2;
        Cv2.Rectangle(image, new Rect(0, 0, image.Width, top), Scalar.White, -1);
        Cv2.Rectangle(
            image,
            new Rect(0, top + visibleHeight, image.Width, image.Height - top - visibleHeight),
            Scalar.White,
            -1);
    }

    return image;
}

private static ImageFrame ToFrame(Mat image, string source)
{
    image.GetArray(out byte[] pixels);
    return new ImageFrame(
        Guid.NewGuid().ToString("N"),
        image.Width,
        image.Height,
        image.Width,
        PixelFormatKind.Gray8,
        pixels,
        DateTimeOffset.UtcNow,
        source);
}
}
```

```csharp
[Fact]
public void LearnOpenCvShapeWritesV2ScoringMetadata()
{
    var learned = TemplateMatcher.Learn(
        TemplateMatcherTestData.CreateTrainingFrame(),
        null,
        TemplateMatcherTestData.CreateLearningParameters());

    Assert.Equal("2", learned["shapeScoreVersion"]);
    Assert.Equal("3", learned["shapeCoverageDistance"]);
}

[Fact]
public void ShapeV2ExtraEdgesInsideSupportLowerReverseScore()
{
    var runtime = TemplateMatcherTestData.LearnRuntimeParameters();
    var legacy = new Dictionary<string, string>(runtime, StringComparer.OrdinalIgnoreCase);
    legacy.Remove("shapeScoreVersion");
    var cluttered = TemplateMatcherTestData.CreateSearchFrame(extraEdges: true);

    var v1 = TemplateMatcher.Match(cluttered, null, legacy);
    var v2 = TemplateMatcher.Match(cluttered, null, runtime);

    Assert.True(v1.Score > 0.95);
    Assert.Null(v1.ShapeCoverage);
    Assert.Null(v1.ShapeReverseScore);
    Assert.True(v2.ShapeCoverage > 0.95);
    Assert.True(v2.ShapeReverseScore < 0.85);
    Assert.True(v2.Score < 0.85);
}

[Fact]
public void ShapeV2CentralFragmentHasLowCoverageAndFailsThreshold()
{
    var result = TemplateMatcher.Match(
        TemplateMatcherTestData.CreateSearchFrame(fragmentOnly: true),
        null,
        TemplateMatcherTestData.LearnRuntimeParameters());

    Assert.True(result.HasMatch);
    Assert.InRange(result.ShapeCoverage!.Value, 0, 0.75);
    Assert.True(result.Score < 0.85);
    Assert.Equal(InspectionOutcome.Ng, result.Outcome);
}

[Fact]
public void ShapeV2CompleteProductPreservesHighQuality()
{
    var result = TemplateMatcher.Match(
        TemplateMatcherTestData.CreateSearchFrame(),
        null,
        TemplateMatcherTestData.LearnRuntimeParameters());

    Assert.True(result.HasMatch, result.Message);
    Assert.True(result.Score > 0.90);
    Assert.True(result.ShapeCoverage > 0.95);
    Assert.True(result.ShapeReverseScore > 0.95);
}
```

- [ ] **Step 2：运行并确认 RED**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~OpenCvTemplateMatcherQualityTests" --nologo
```

Expected: 学习结果缺少版本字段，公共诊断属性不存在或为空，杂边候选仍接近满分。

- [ ] **Step 3：添加兼容的结果属性和内部候选质量**

保持位置构造函数与 `Deconstruct` 不变：

```csharp
public sealed record TemplateMatchResult(
    bool HasMatch,
    InspectionOutcome Outcome,
    double Score,
    Pose2D Pose,
    int MatchX,
    int MatchY,
    int TemplateWidth,
    int TemplateHeight,
    TemplateSearchRegion SearchRegion,
    string Message,
    bool UsedAutoTemplate,
    IReadOnlyList<Point2D>? ShapePoints = null,
    IReadOnlyList<IReadOnlyList<Point2D>>? ShapeContours = null)
{
    public IReadOnlyList<IReadOnlyList<Point2D>>? MatchedTemplateRoiContours { get; init; }
    public double? ShapeCoverage { get; init; }
    public double? ShapeReverseScore { get; init; }
}

private readonly record struct ShapeQuality(
    double Score,
    double Coverage,
    double ReverseScore);
```

`MatchCandidate` 追加可空 `ShapeCoverage/ShapeReverseScore`；v1、Managed、Gray 和 ORB 保持 null。

- [ ] **Step 4：实现支持域、覆盖率和反向 Chamfer**

V2 每个角度使用同一个 `KeepBoundsRotation` 变换边缘与支持掩膜。有模板掩膜时使用它；否则旋转原模板大小的全白矩形，keep-bounds 黑角不属于支持域。

```csharp
private static ShapeQuality? EvaluateShapeV2(
    Mat searchEdges,
    Mat searchDistance,
    Mat rotatedEdges,
    Mat rotatedSupport,
    Point location,
    double forwardMean,
    double passScale,
    IReadOnlyDictionary<string, string> parameters)
{
    var rect = new Rect(location.X, location.Y, rotatedEdges.Width, rotatedEdges.Height);
    using var distancePatch = new Mat(searchDistance, rect);
    using var covered = new Mat();
    Cv2.Compare(
        distancePatch,
        GetShapeCoverageTolerance(parameters, passScale),
        covered,
        CmpTypes.LE);
    Cv2.BitwiseAnd(covered, rotatedEdges, covered);
    var templateEdgeCount = Cv2.CountNonZero(rotatedEdges);
    if (templateEdgeCount < 8)
    {
        return null;
    }

    var coverage = Cv2.CountNonZero(covered) / (double)templateEdgeCount;
    using var searchPatch = new Mat(searchEdges, rect);
    using var supportedSearchEdges = new Mat();
    Cv2.BitwiseAnd(searchPatch, rotatedSupport, supportedSearchEdges);
    if (Cv2.CountNonZero(supportedSearchEdges) < 8)
    {
        return null;
    }

    using var inverseTemplate = new Mat();
    Cv2.Threshold(rotatedEdges, inverseTemplate, 0, 255, ThresholdTypes.BinaryInv);
    using var templateDistance = new Mat();
    Cv2.DistanceTransform(
        inverseTemplate, templateDistance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
    var reverseMean = Cv2.Mean(templateDistance, supportedSearchEdges).Val0;
    var scale = GetShapeV2ScoreScale(rotatedEdges.Size(), parameters, passScale);
    var forward = Math.Exp(-Math.Max(0, forwardMean) / scale);
    var reverse = Math.Exp(-Math.Max(0, reverseMean) / scale);
    return new ShapeQuality(
        Math.Clamp(Math.Min(forward, reverse) * coverage, 0, 1),
        coverage,
        Math.Clamp(reverse, 0, 1));
}

private static double GetShapeCoverageTolerance(
    IReadOnlyDictionary<string, string> parameters,
    double passScale)
{
    var value = GetDouble(parameters, "shapeCoverageDistance", 3);
    var finite = double.IsFinite(value) ? value : 3;
    return Math.Max(0.75, Math.Clamp(finite, 0.5, 20) * passScale);
}

private static double GetShapeV2ScoreScale(
    Size currentTemplate,
    IReadOnlyDictionary<string, string> parameters,
    double passScale)
{
    if (TryGetDouble(parameters, "shapeScoreScale", out var configured) &&
        double.IsFinite(configured))
    {
        return Math.Max(0.25, configured * passScale);
    }

    var safeScale = Math.Max(0.01, passScale);
    var fullShortSide = Math.Min(
        currentTemplate.Width / safeScale,
        currentTemplate.Height / safeScale);
    return Math.Max(
        0.25,
        Math.Clamp(fullShortSide * 0.18, 12, 30) * safeScale);
}
```

`GetShapeV2ScoreScale` 对显式 `shapeScoreScale` 先做 finite 检查再乘 `passScale`；默认按全分辨率短边计算 `clamp(short*0.18,12,30)` 后乘 `passScale`。最终分严格为 `min(forward, reverse) * coverage`。

- [ ] **Step 5：实现版本规则和学习元数据**

```csharp
private static bool TryGetShapeScoreVersion(
    IReadOnlyDictionary<string, string> parameters,
    out int version)
{
    if (!parameters.TryGetValue("shapeScoreVersion", out var raw) ||
        string.IsNullOrWhiteSpace(raw))
    {
        version = 1;
        return true;
    }

    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) &&
           version is 1 or 2;
}
```

缺失/`1` 走 v1，`2` 走 v2；非法或大于 2 返回 NG，消息为 `Unsupported OpenCV Shape score version.`。`Learn` 对 Shape 写 `shapeScoreVersion=2` 和归一化后的 `shapeCoverageDistance`；`templateVersion=opencv-1` 保持模型像素格式语义。

- [ ] **Step 6：GREEN、回归并提交**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~OpenCvTemplateMatcherQualityTests" --nologo
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --nologo
git diff --check
git add .\VisionStation.Vision\OpenCvTemplateMatcher.cs .\VisionStation.Vision\TemplateMatcher.cs .\VisionStation.Vision.Tests\TemplateMatcherTestData.cs .\VisionStation.Vision.Tests\OpenCvTemplateMatcherGeometryTests.cs .\VisionStation.Vision.Tests\OpenCvTemplateMatcherQualityTests.cs
git commit -m "feat: 增加 Shape V2 完整轮廓评分"
git push
```

## Task 4：分离结果语义并序列化 ToolResult

**Files:**

- Create: `VisionStation.Vision.Tests/TemplateLocateToolTests.cs`
- Modify: `VisionStation.Vision/OpenCvTemplateMatcher.cs:119-152`
- Modify: `VisionStation.Vision/Tools/TemplateLocateTool.cs:35-78`

- [ ] **Step 1：写 ToolResult 分离字段 RED**

测试 fixture 使用 Polygon 模板 ROI，并通过真实 `TemplateLocateTool.ExecuteAsync`：

```csharp
var fixture = TemplateMatcherTestData.CreatePolygonTemplateFixture();
fixture.Parameters["inputImageToolId"] = "source";
var sourceTool = new VisionToolDefinition
{
    Id = "source",
    Name = "Source",
    Kind = VisionToolKind.AcquireImage
};
var locateTool = new VisionToolDefinition
{
    Id = "locate",
    Name = "Locate",
    Kind = VisionToolKind.TemplateLocate,
    Parameters = fixture.Parameters
};
var recipe = new Recipe { Tools = [sourceTool, locateTool] };
using var context = new VisionToolContext(recipe, fixture.SearchFrame);
context.SetImageOutput(sourceTool, fixture.SearchFrame);
var result = await new TemplateLocateTool().ExecuteAsync(locateTool, context);

Assert.Equal("2", result.Data["overlaySchemaVersion"]);
Assert.NotEmpty(result.Data["shapeContours"]);
Assert.NotEmpty(result.Data["matchedTemplateRoiContours"]);
Assert.InRange(
    double.Parse(result.Data["shapeCoverage"], CultureInfo.InvariantCulture), 0, 1);
Assert.InRange(
    double.Parse(result.Data["shapeReverseScore"], CultureInfo.InvariantCulture), 0, 1);

var restored = JsonSerializer.Deserialize<ToolResult>(
    JsonSerializer.Serialize(result))!;
Assert.Equal(
    result.Data["matchedTemplateRoiContours"],
    restored.Data["matchedTemplateRoiContours"]);
```

- [ ] **Step 2：确认 RED**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~TemplateLocateToolTests" --nologo
```

Expected: 缺少 schema 和独立 ROI 字段；当前 `shapeContours` 仍混合两类轮廓。

- [ ] **Step 3：分别返回评分边缘和模板 ROI**

```csharp
return new TemplateMatchResult(
    true, outcome, candidate.Score, pose,
    searchRegion.X + candidate.X,
    searchRegion.Y + candidate.Y,
    candidate.Width,
    candidate.Height,
    searchRegion,
    message,
    usedAutoTemplate,
    ShapeContours: shapeContours)
{
    MatchedTemplateRoiContours = matchedRoiContours,
    ShapeCoverage = candidate.ShapeCoverage,
    ShapeReverseScore = candidate.ShapeReverseScore
};
```

- [ ] **Step 4：使用一个编码器写 ToolResult.Data**

```csharp
private static string SerializeContours(IEnumerable<IReadOnlyList<Point2D>> contours)
{
    return string.Join(
        "|",
        contours
            .Where(contour => contour.Count >= 2)
            .Select(contour => string.Join(
                ";",
                contour.Select(point =>
                    $"{point.X.ToInvariant()},{point.Y.ToInvariant()}"))));
}
```

新结果始终写 `overlaySchemaVersion=2`。两类轮廓分别编码；仅在 nullable 值存在时写 `shapeCoverage` 和 `shapeReverseScore`。schema 只表示叠加数据格式，不表示评分版本。

- [ ] **Step 5：GREEN、回归并提交**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~TemplateLocateToolTests" --nologo
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --nologo
git diff --check
git add .\VisionStation.Vision\OpenCvTemplateMatcher.cs .\VisionStation.Vision\Tools\TemplateLocateTool.cs .\VisionStation.Vision.Tests\TemplateLocateToolTests.cs
git commit -m "feat: 分离模板匹配结果诊断"
git push
```

## Task 5：统一生产和调参的 TemplateLocate overlay

**Files:**

- Create: `VisionStation.Vision.UI/Services/TemplateLocateOverlayFactory.cs`
- Create: `VisionStation.Vision.UI.Tests/TemplateLocateOverlayFactoryTests.cs`
- Modify: `VisionStation.Vision.UI/Services/VisionOverlayBuilder.cs:35, 118-181`
- Modify: `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs:56-70, 945-1073, 1416-1562`
- Modify: `VisionStation.Vision.UI/Models/VisionOverlayItem.cs:22-28`
- Modify: `VisionStation.Vision.UI/Controls/ResultOverlayCanvas.cs:287-328`
- Modify: `VisionStation.Vision.UI.Tests/ResultOverlayCanvasTests.cs`
- Modify: `VisionStation.Vision.UI.Tests/TemplateLocateToolDialogViewModelTests.cs`

- [ ] **Step 1：写 V2、legacy 和结构化结果语义 RED**

```csharp
[Fact]
public void CreateV2ResultSeparatesScoredEdgesAndTemplateRoi()
{
    var overlays = TemplateLocateOverlayFactory.Create(CreateV2ToolResult());
    Assert.Contains(overlays, item =>
        item.Kind == VisionOverlayKind.Polyline &&
        item.State == VisionOverlayState.Warning);
    Assert.Contains(overlays, item =>
        item.Kind == VisionOverlayKind.Polyline &&
        item.State == VisionOverlayState.Info);
    var cross = Assert.Single(overlays.Where(item => item.Kind == VisionOverlayKind.Cross));
    Assert.Contains("S=0.923", cross.Label);
    Assert.Contains("C=0.887", cross.Label);
}

[Fact]
public void CreateLegacyResultKeepsMixedContoursOrange()
{
    var overlays = TemplateLocateOverlayFactory.Create(CreateLegacyToolResult());
    Assert.All(
        overlays.Where(item => item.Kind == VisionOverlayKind.Polyline),
        item => Assert.Equal(VisionOverlayState.Warning, item.State));
    Assert.DoesNotContain(overlays, item => item.State == VisionOverlayState.Info);
}

[Fact]
public void StructuredAndPersistedResultsUseTheSameRoles()
{
    var runtime = TemplateLocateOverlayFactory.Create(CreateTemplateMatchResult());
    var persisted = TemplateLocateOverlayFactory.Create(CreateV2ToolResult());
    Assert.Equal(
        runtime.Select(item => (item.Kind, item.State)),
        persisted.Select(item => (item.Kind, item.State)));
}

private static ToolResult CreateV2ToolResult() => new()
{
    ToolId = "locate",
    ToolName = "Locate",
    Kind = VisionToolKind.TemplateLocate,
    Outcome = InspectionOutcome.Ok,
    Data = new Dictionary<string, string>
    {
        ["x"] = "100",
        ["y"] = "120",
        ["angle"] = "35",
        ["templateWidth"] = "300",
        ["templateHeight"] = "100",
        ["score"] = "0.923",
        ["shapeCoverage"] = "0.887",
        ["overlaySchemaVersion"] = "2",
        ["shapeContours"] = "90,110;100,100;110,110",
        ["matchedTemplateRoiContours"] = "70,70;130,70;130,170;70,170"
    }
};

private static ToolResult CreateLegacyToolResult()
{
    var result = CreateV2ToolResult();
    var data = new Dictionary<string, string>(result.Data);
    data.Remove("overlaySchemaVersion");
    data.Remove("matchedTemplateRoiContours");
    return result with { Data = data };
}

private static TemplateMatchResult CreateTemplateMatchResult() =>
    new(
        true,
        InspectionOutcome.Ok,
        0.923,
        new Pose2D(100, 120, 35),
        0,
        0,
        300,
        100,
        new TemplateSearchRegion(0, 0, 400, 400),
        "OK",
        false,
        ShapeContours: [[new Point2D(90, 110), new Point2D(100, 100)]])
    {
        MatchedTemplateRoiContours =
            [[new Point2D(70, 70), new Point2D(130, 70)]],
        ShapeCoverage = 0.887
    };

[Fact]
public void InfoLineRendersCyan()
{
    var bitmap = RenderLine(scale: 1, state: VisionOverlayState.Info).Bitmap;
    var pixel = ReadPixel(bitmap, 50, 50);
    Assert.True(pixel.Green > 180 && pixel.Blue > 180 && pixel.Red < 100);
}
```

- [ ] **Step 2：运行并确认 RED**

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --filter "FullyQualifiedName~TemplateLocateOverlayFactoryTests|FullyQualifiedName~InfoLineRendersCyan" --nologo
```

Expected: factory 与 `VisionOverlayState.Info` 尚不存在。

- [ ] **Step 3：追加 Info 状态并明确青色映射**

为保持现有枚举数值，把 Info 追加到末尾：

```csharp
public enum VisionOverlayState
{
    Neutral,
    Ok,
    Ng,
    Warning,
    Info
}
```

`CreatePen/CreateFill/CreateBrush` 分别增加 Info 映射：`RGB(35,211,245)`、`ARGB(18,35,211,245)`、`ARGB(220,35,211,245)`。在 Canvas 测试中把现有 `RenderLine` 增加 state 参数，并实现 `ReadPixel` 读取 BGRA 通道。

```csharp
private static (byte Blue, byte Green, byte Red, byte Alpha) ReadPixel(
    BitmapSource bitmap,
    int x,
    int y)
{
    var pixel = new byte[4];
    bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
    return (pixel[0], pixel[1], pixel[2], pixel[3]);
}
```

- [ ] **Step 4：实现共享 overlay factory**

```csharp
public static class TemplateLocateOverlayFactory
{
    public static IReadOnlyList<VisionOverlayItem> Create(TemplateMatchResult match)
    {
        return CreateCore(
            match.Pose.X,
            match.Pose.Y,
            match.Pose.Angle,
            match.TemplateWidth,
            match.TemplateHeight,
            match.Score,
            match.ShapeCoverage,
            match.Outcome,
            match.ShapePoints ?? [],
            match.ShapeContours ?? [],
            match.MatchedTemplateRoiContours ?? []);
    }

    public static IReadOnlyList<VisionOverlayItem> Create(ToolResult result)
    {
        TryGetDouble(result.Data, "x", out var x);
        TryGetDouble(result.Data, "y", out var y);
        TryGetDouble(result.Data, "angle", out var angle);
        TryGetDouble(result.Data, "templateWidth", out var width);
        TryGetDouble(result.Data, "templateHeight", out var height);
        TryGetDouble(result.Data, "score", out var score);
        var isV2 = result.Data.GetValueOrDefault("overlaySchemaVersion") == "2";
        double? coverage = TryGetDouble(result.Data, "shapeCoverage", out var parsed)
            ? parsed
            : null;
        TryParsePointList(result.Data.GetValueOrDefault("shapePoints"), out var points);
        return CreateCore(
            x, y, angle, width, height, score, coverage, result.Outcome,
            points,
            ParseContours(result.Data.GetValueOrDefault("shapeContours")),
            isV2
                ? ParseContours(result.Data.GetValueOrDefault("matchedTemplateRoiContours"))
                : []);
    }

    private static IReadOnlyList<VisionOverlayItem> CreateCore(
        double x,
        double y,
        double angle,
        double width,
        double height,
        double score,
        double? coverage,
        InspectionOutcome outcome,
        IReadOnlyList<Point2D> shapePoints,
        IReadOnlyList<IReadOnlyList<Point2D>> shapeContours,
        IReadOnlyList<IReadOnlyList<Point2D>> roiContours)
    {
        var overlays = new List<VisionOverlayItem>();
        if (shapePoints.Count > 0)
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.PointCloud,
                State = VisionOverlayState.Warning,
                Points = shapePoints
            });
        }

        overlays.AddRange(shapeContours
            .Where(contour => contour.Count >= 2)
            .Select(contour => new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Polyline,
                State = VisionOverlayState.Warning,
                Points = contour
            }));
        overlays.AddRange(roiContours
            .Where(contour => contour.Count >= 2)
            .Select(contour => new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Polyline,
                State = VisionOverlayState.Info,
                Points = contour
            }));

        if (shapePoints.Count == 0 && shapeContours.Count == 0 && roiContours.Count == 0)
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangle,
                State = outcome == InspectionOutcome.Ok
                    ? VisionOverlayState.Ok
                    : VisionOverlayState.Ng,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                Angle = angle
            });
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Cross,
            State = outcome == InspectionOutcome.Ok
                ? VisionOverlayState.Ok
                : VisionOverlayState.Ng,
            X = x,
            Y = y,
            Angle = angle,
            Label = coverage is double value
                ? $"匹配 S={score:0.000} C={value:0.000}"
                : $"匹配 S={score:0.000}"
        });
        return overlays;
    }

    private static bool TryGetDouble(
        IReadOnlyDictionary<string, string> data,
        string key,
        out double value)
    {
        value = 0;
        return data.TryGetValue(key, out var raw) &&
               double.TryParse(
                   raw,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryParsePointList(string? raw, out IReadOnlyList<Point2D> points)
    {
        points = ParsePointList(raw);
        return points.Count > 0;
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> ParseContours(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParsePointList)
                .Where(points => points.Count >= 2)
                .ToArray();

    private static IReadOnlyList<Point2D> ParsePointList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var points = new List<Point2D>();
        foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split(',', StringSplitOptions.TrimEntries);
            if (pair.Length == 2 &&
                double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                points.Add(new Point2D(x, y));
            }
        }

        return points;
    }
}
```

- [ ] **Step 5：让 builder 和调参 ViewModel 使用同一 factory**

```csharp
case VisionToolKind.TemplateLocate:
    overlays.AddRange(TemplateLocateOverlayFactory.Create(result));
    break;
```

调参 ViewModel 增加 `private TemplateMatchResult? _lastTemplateMatch;`；单目标运行后赋值，Reset 与多目标分支清空。单目标预览替换为：

```csharp
if (_lastTemplateMatch is not null)
{
    foreach (var overlay in TemplateLocateOverlayFactory.Create(_lastTemplateMatch))
    {
        PreviewOverlays.Add(overlay);
    }
}
```

保留 `_matchX/_matchY/_matchAngle` 供标准位姿使用，只删除被 factory 取代的 `_matchShapePoints/_matchShapeContours`。

- [ ] **Step 6：增加调参命令集成测试**

在 `TemplateLocateToolDialogViewModelTests` 内通过公共 `TemplateMatcher.Learn` 生成已学习参数，再用 `string.Join("; ", parameters.Select(...))` 写入 `VisionToolItem.ParametersText`；不要引用另一个测试项目的 helper。执行命令并等待异步结束：

```csharp
viewModel.RunToolCommand.Execute();
Assert.True(SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(10)));
Assert.Contains(viewModel.PreviewOverlays, item =>
    item.Kind == VisionOverlayKind.Polyline &&
    item.State == VisionOverlayState.Warning);
Assert.Contains(viewModel.PreviewOverlays, item =>
    item.Kind == VisionOverlayKind.Polyline &&
    item.State == VisionOverlayState.Info);
```

- [ ] **Step 7：GREEN、回归并提交**

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --filter "FullyQualifiedName~TemplateLocateOverlayFactoryTests|FullyQualifiedName~TemplateLocateToolDialogViewModelTests|FullyQualifiedName~InfoLineRendersCyan" --nologo
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --nologo
git diff --check
git add .\VisionStation.Vision.UI\Services\TemplateLocateOverlayFactory.cs .\VisionStation.Vision.UI\Services\VisionOverlayBuilder.cs .\VisionStation.Vision.UI\ViewModels\TemplateLocateToolDialogViewModel.cs .\VisionStation.Vision.UI\Models\VisionOverlayItem.cs .\VisionStation.Vision.UI\Controls\ResultOverlayCanvas.cs .\VisionStation.Vision.UI.Tests\TemplateLocateOverlayFactoryTests.cs .\VisionStation.Vision.UI.Tests\TemplateLocateToolDialogViewModelTests.cs .\VisionStation.Vision.UI.Tests\ResultOverlayCanvasTests.cs
git commit -m "feat: 统一模板定位叠加语义"
git push
```

## Task 6：让 Info ROI 和覆盖率标签进入最终结果预览

**Files:**

- Create: `VisionStation.Vision.UI/Services/VisionResultOverlayProjector.cs`
- Create: `VisionStation.Vision.UI.Tests/VisionResultOverlayProjectorTests.cs`
- Modify: `VisionStation.Client/ViewModels/ProductionDashboardViewModel.cs:536-542`
- Modify: `VisionStation.Vision.UI/ViewModels/VisionDebugViewModel.cs:4293-4299`

- [ ] **Step 1：写 projector RED**

```csharp
[Fact]
public void ProjectKeepsInfoRoiAndLocateLabelButHidesConfigurationRoi()
{
    var result = VisionResultOverlayProjector.Project(
    [
        new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Rectangle,
            State = VisionOverlayState.Neutral,
            Label = "配置 ROI"
        },
        new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Polyline,
            State = VisionOverlayState.Info,
            Label = "模板 ROI"
        },
        new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Cross,
            State = VisionOverlayState.Ok,
            Label = "匹配 S=0.923 C=0.887"
        }
    ]);

    Assert.DoesNotContain(result, item => item.State == VisionOverlayState.Neutral);
    Assert.Contains(result, item => item.State == VisionOverlayState.Info);
    Assert.Equal(
        "匹配 S=0.923 C=0.887",
        Assert.Single(result.Where(item => item.Kind == VisionOverlayKind.Cross)).Label);
}
```

- [ ] **Step 2：运行并确认 RED**

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --filter "FullyQualifiedName~VisionResultOverlayProjectorTests" --nologo
```

Expected: projector 尚不存在。

- [ ] **Step 3：实现共享 projector 并替换两处复制逻辑**

```csharp
public static class VisionResultOverlayProjector
{
    public static IReadOnlyList<VisionOverlayItem> Project(
        IEnumerable<VisionOverlayItem> overlays)
    {
        return overlays
            .Where(item => item.Kind != VisionOverlayKind.DirectionAxis)
            .Where(item => item.State != VisionOverlayState.Neutral)
            .Select(item => item.Kind == VisionOverlayKind.Cross
                ? item
                : item with { Label = string.Empty })
            .ToArray();
    }
}
```

`ProductionDashboardViewModel` 与 `VisionDebugViewModel` 都调用 `VisionResultOverlayProjector.Project(_overlayBuilder.Build(...))`，随后删除两个私有 `CreateResultPreviewOverlays`。

- [ ] **Step 4：GREEN、回归并提交**

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --filter "FullyQualifiedName~VisionResultOverlayProjectorTests" --nologo
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --nologo
git diff --check
git add .\VisionStation.Vision.UI\Services\VisionResultOverlayProjector.cs .\VisionStation.Client\ViewModels\ProductionDashboardViewModel.cs .\VisionStation.Vision.UI\ViewModels\VisionDebugViewModel.cs .\VisionStation.Vision.UI.Tests\VisionResultOverlayProjectorTests.cs
git commit -m "feat: 保留模板匹配结果诊断显示"
git push
```

## Task 7：默认启用 V2，并让旧工具确认后迁移

**Files:**

- Modify: `VisionStation.Infrastructure/DefaultRecipeFactory.cs:55-71`
- Modify: `VisionStation.Vision.UI/ViewModels/VisionToolCatalog.cs:277-292`
- Modify: `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs:1894-1920`
- Modify: `VisionStation.Vision.UI.Tests/VisionToolCatalogTests.cs`
- Modify: `VisionStation.Vision.UI.Tests/TemplateLocateToolDialogViewModelTests.cs`

- [ ] **Step 1：写默认值与旧参数迁移 RED**

```csharp
[Fact]
public void TemplateLocateDefaultsUseShapeV2()
{
    var catalog = VisionToolCatalog.GetDefaultParameters(VisionToolKind.TemplateLocate);
    var recipeTool = DefaultRecipeFactory.Create().Tools.Single(tool =>
        tool.Kind == VisionToolKind.TemplateLocate);

    Assert.Equal("2", catalog["shapeScoreVersion"]);
    Assert.Equal("3", catalog["shapeCoverageDistance"]);
    Assert.Equal("2", recipeTool.Parameters["shapeScoreVersion"]);
    Assert.Equal("3", recipeTool.Parameters["shapeCoverageDistance"]);
}

[Fact]
public void ApplyToLegacyShapeToolWritesV2ScoringParameters()
{
    var tool = CreateLegacyTemplateToolWithoutScoreVersion();
    var viewModel = CreateViewModel(tool);

    viewModel.ApplyTo(tool);

    Assert.Contains("shapeScoreVersion=2", tool.ParametersText, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("shapeCoverageDistance=3", tool.ParametersText, StringComparison.OrdinalIgnoreCase);
}

private static VisionToolItem CreateLegacyTemplateToolWithoutScoreVersion() => new()
{
    Id = "template-tool",
    Name = "Template",
    Kind = VisionToolKind.TemplateLocate,
    Enabled = true,
    ParametersText = "engine=OpenCv; matchMode=Shape; minScore=0.85"
};

private static TemplateLocateToolDialogViewModel CreateViewModel(VisionToolItem tool) =>
    new(
        tool,
        Array.Empty<RoiChoiceItem>(),
        Array.Empty<RoiDefinition>(),
        "Flow",
        CreateFrame(320, 240),
        new RuntimePaths(Path.GetTempPath()),
        new NullAppLogService());
```

- [ ] **Step 2：运行并确认 RED**

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --filter "FullyQualifiedName~TemplateLocateDefaultsUseShapeV2|FullyQualifiedName~ApplyToLegacyShapeToolWritesV2ScoringParameters" --nologo
```

Expected: 默认字典和 `BuildCurrentParameters` 都缺少两个字段。

- [ ] **Step 3：写入默认值和确认迁移规则**

Catalog 与 DefaultRecipeFactory 的 TemplateLocate 字典加入：

```csharp
["shapeScoreVersion"] = "2",
["shapeCoverageDistance"] = "3",
```

`BuildCurrentParameters` 在单目标 Shape 确认时加入：

```csharp
if (_toolKind == VisionToolKind.TemplateLocate &&
    SelectedMatchMode.Equals("Shape", StringComparison.OrdinalIgnoreCase))
{
    parameters["shapeScoreVersion"] = "2";
    parameters["shapeCoverageDistance"] =
        GetParameterValue("shapeCoverageDistance", "3");
}
```

未打开、未保存的旧配方继续因缺少版本字段运行 v1；确认后才迁移。不得给 `MultiTargetMatch` 写该版本。

- [ ] **Step 4：GREEN、回归并提交**

```powershell
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --filter "FullyQualifiedName~TemplateLocateDefaultsUseShapeV2|FullyQualifiedName~ApplyToLegacyShapeToolWritesV2ScoringParameters" --nologo
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --nologo
git diff --check
git add .\VisionStation.Infrastructure\DefaultRecipeFactory.cs .\VisionStation.Vision.UI\ViewModels\VisionToolCatalog.cs .\VisionStation.Vision.UI\ViewModels\TemplateLocateToolDialogViewModel.cs .\VisionStation.Vision.UI.Tests\VisionToolCatalogTests.cs .\VisionStation.Vision.UI.Tests\TemplateLocateToolDialogViewModelTests.cs
git commit -m "feat: 默认启用 Shape V2 评分"
git push
```

## Task 8：本地六图验收、二次开发文档和最终验证

**Files:**

- Create: `VisionStation.Vision.Tests/LocalTemplateDatasetAcceptanceTests.cs`
- Create: `docs/development/opencv-shape-v2.md`

- [ ] **Step 1：增加不携带图片的 opt-in 验收测试**

测试从 `VISIONSTATION_TEMPLATE_DATASET` 读取目录。未设置时输出 SKIP 文本并返回；设置后从 `正.bmp` 提取中央完整产品、扩展 14 px 作为模板，分别运行 v1/v2。

```csharp
public sealed class LocalTemplateDatasetAcceptanceTests
{
    private readonly ITestOutputHelper _output;

    public LocalTemplateDatasetAcceptanceTests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    [Trait("Category", "LocalDataset")]
    public void ShapeV2ReportsWholeProductQualityAndStaysWithinPerformanceBudget()
    {
        var directory = Environment.GetEnvironmentVariable("VISIONSTATION_TEMPLATE_DATASET");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            _output.WriteLine("SKIP: VISIONSTATION_TEMPLATE_DATASET is not configured.");
            return;
        }

        var fixture = LocalDatasetFixture.LearnFromPositive(
            Path.Combine(directory, "正.bmp"));
        var legacy = new Dictionary<string, string>(
            fixture.Parameters,
            StringComparer.OrdinalIgnoreCase);
        legacy.Remove("shapeScoreVersion");
        var v1Elapsed = TimeSpan.Zero;
        var v2Elapsed = TimeSpan.Zero;
        TemplateMatchResult? positive = null;

        foreach (var path in Directory.EnumerateFiles(directory, "*.bmp")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var frame = LocalDatasetFixture.LoadFrame(path);
            var watch = Stopwatch.StartNew();
            _ = TemplateMatcher.Match(frame, null, legacy);
            watch.Stop();
            v1Elapsed += watch.Elapsed;

            watch.Restart();
            var v2 = TemplateMatcher.Match(frame, null, fixture.Parameters);
            watch.Stop();
            v2Elapsed += watch.Elapsed;
            _output.WriteLine(
                $"{Path.GetFileName(path)} score={v2.Score:0.0000} " +
                $"coverage={v2.ShapeCoverage:0.0000} reverse={v2.ShapeReverseScore:0.0000} " +
                $"pose=({v2.Pose.X:0.0},{v2.Pose.Y:0.0},{v2.Pose.Angle:0.0}) " +
                $"ms={watch.Elapsed.TotalMilliseconds:0}");
            if (Path.GetFileName(path).Equals("正.bmp", StringComparison.OrdinalIgnoreCase))
            {
                positive = v2;
            }
        }

        Assert.NotNull(positive);
        Assert.Equal(InspectionOutcome.Ok, positive.Outcome);
        Assert.True(positive.ShapeCoverage >= 0.90);
        Assert.True(v2Elapsed.TotalMilliseconds <= v1Elapsed.TotalMilliseconds * 1.30);
    }
}
```

同文件实现以下 fixture；`ToFrame` 用 `Mat.GetArray` 构造公开 `ImageFrame`，`Expand` 把边界限制在图像范围内：

```csharp
private sealed record LocalDatasetLearned(
    ImageFrame PositiveFrame,
    Dictionary<string, string> Parameters);

private static class LocalDatasetFixture
{
    public static LocalDatasetLearned LearnFromPositive(string path)
    {
        var frame = LoadFrame(path);
        using var gray = Cv2.ImRead(path, ImreadModes.Grayscale);
        var bounds = FindProductBounds(gray);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "Shape",
            ["minScore"] = "0.85",
            ["angleStart"] = "-180",
            ["angleExtent"] = "360",
            ["angleStep"] = "2",
            ["autoContrast"] = "true",
            ["shapeScoreVersion"] = "2",
            ["shapeCoverageDistance"] = "3",
            ["templateRoiX"] = bounds.X.ToString(CultureInfo.InvariantCulture),
            ["templateRoiY"] = bounds.Y.ToString(CultureInfo.InvariantCulture),
            ["templateRoiWidth"] = bounds.Width.ToString(CultureInfo.InvariantCulture),
            ["templateRoiHeight"] = bounds.Height.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var item in TemplateMatcher.Learn(frame, null, parameters))
        {
            parameters[item.Key] = item.Value;
        }

        return new LocalDatasetLearned(frame, parameters);
    }

    public static ImageFrame LoadFrame(string path)
    {
        using var gray = Cv2.ImRead(path, ImreadModes.Grayscale);
        Assert.False(gray.Empty(), $"Unable to load {path}");
        gray.GetArray(out byte[] pixels);
        return new ImageFrame(
            Guid.NewGuid().ToString("N"), gray.Width, gray.Height, gray.Width,
            PixelFormatKind.Gray8, pixels, DateTimeOffset.UtcNow, path);
    }

    private static Rect FindProductBounds(Mat gray)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 100, 255, ThresholdTypes.BinaryInv);
        using var contourSource = binary.Clone();
        Cv2.FindContours(
            contourSource,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);
        var candidate = contours
            .Select(contour => new
            {
                Bounds = Cv2.BoundingRect(contour),
                Area = Cv2.ContourArea(contour)
            })
            .Where(item => item.Area is >= 55_000 and <= 70_000)
            .Where(item => Math.Max(item.Bounds.Width, item.Bounds.Height) is >= 900 and <= 1_050)
            .OrderBy(item => Math.Abs(
                item.Bounds.X + item.Bounds.Width / 2.0 - gray.Width / 2.0))
            .First();
        return Expand(candidate.Bounds, 14, gray.Size());
    }

    private static Rect Expand(Rect value, int margin, Size bounds)
    {
        var left = Math.Max(0, value.X - margin);
        var top = Math.Max(0, value.Y - margin);
        var right = Math.Min(bounds.Width, value.Right + margin);
        var bottom = Math.Min(bounds.Height, value.Bottom + margin);
        return new Rect(left, top, right - left, bottom - top);
    }
}
```

- [ ] **Step 2：验证无环境变量时不读本机文件**

```powershell
Remove-Item Env:VISIONSTATION_TEMPLATE_DATASET -ErrorAction SilentlyContinue
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~LocalTemplateDatasetAcceptanceTests" --logger "console;verbosity=detailed" --nologo
```

Expected: PASS，并输出 `SKIP: VISIONSTATION_TEMPLATE_DATASET is not configured.`。

- [ ] **Step 3：运行现场六图**

```powershell
$env:VISIONSTATION_TEMPLATE_DATASET='C:\Users\Time\Desktop\框架AI优化\新程序\图像'
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --filter "FullyQualifiedName~LocalTemplateDatasetAcceptanceTests" --logger "console;verbosity=detailed" --nologo
```

Expected: `正.bmp` 为 Ok、覆盖率至少 0.90；六图都输出质量诊断；V2 总耗时不超过同进程 v1 的 1.3 倍。性能失败时只优化 Mat 复用和支持域计算，不删除质量门槛。

- [ ] **Step 4：写二次开发文档**

```markdown
# OpenCV Shape V2 二次开发指南

## 启用方式

- `shapeScoreVersion=2`
- `shapeCoverageDistance=3`，单位为原模板像素，范围 `0.5..20`
- 旧配方缺少版本时运行 v1；模板定位对话框确认后写入 v2

## 分数语义

`Score = min(ForwardScore, ShapeReverseScore) * ShapeCoverage`

- `ShapeCoverage`：模板边缘被搜索边缘支持的比例
- `ShapeReverseScore`：候选支持域内搜索边缘被模板解释的程度
- `minScore` 必须使用现场数据重新标定

## 结果语义

- `ShapeContours`：真正参与评分的模板边缘
- `MatchedTemplateRoiContours`：完整训练 ROI 的位姿变换轮廓
- `overlaySchemaVersion=2`：ToolResult 使用分离轮廓格式
- 旧历史没有 schemaVersion 时继续按混合橙色轮廓显示

## 扩展约束

- 不要在 UI 或工具层复制旋转、评分或轮廓解析算法
- 新后端继续返回相同结果语义
- 只有 OpenCV 与 HALCON 两个真实 adapter 同时存在时才提取后端 interface
- MultiTargetMatch 尚未接入 Shape V2
```

- [ ] **Step 5：执行最终验证**

```powershell
dotnet test .\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release --nologo
dotnet test .\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release --nologo
dotnet test .\VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release --nologo
dotnet build .\CVWork.sln -c Release --no-restore --nologo
git diff --check
git status --short
```

Expected: 三个测试项目均 0 failed，Release 构建退出码 0，diff check 无输出，状态只包含 Task 8 文件。

- [ ] **Step 6：提交、推送并检查工作区**

```powershell
git add .\VisionStation.Vision.Tests\LocalTemplateDatasetAcceptanceTests.cs .\docs\development\opencv-shape-v2.md
git commit -m "test: 增加 Shape V2 现场验收基线"
git push
git status --short --branch
```

Expected: push 成功，当前分支与远端同步且无未提交文件。

## 执行硬约束

1. 每个 Task 严格执行 RED → GREEN → 相关全量测试 → `git diff --check` → commit。
2. 新测试首次运行若 PASS，先修正测试使其覆盖缺失行为，不能直接写生产实现。
3. 不提交六张 BMP 或任何派生工业图片。
4. 不修改 `MultiTargetMatcher`、HALCON、亚像素、极性或金字塔实现。
5. 不把粗分辨率候选或质量值作为最终结果。
6. V2 参数为 NaN、Infinity 或不支持版本时 fail-closed。
7. 发现无关用户改动时停止自动提交并先确认。
