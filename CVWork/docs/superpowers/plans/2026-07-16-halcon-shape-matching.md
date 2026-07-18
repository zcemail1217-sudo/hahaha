# HALCON Two-Stage Whole-Product Shape Matching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把新建的单目标与多目标整产品模板工具默认切换到 HALCON 26.05 scaled shape model，并通过候选生成加六项独立硬门实现全角度、小尺度变化、反面/相似件/残缺件拒绝、精确多目标计数和完整 `X/Y/Angle/Scale/Score` 输出，同时保证旧 OpenCV/Managed 配方、模型与结果格式继续可用。

**Architecture:** 以异步 `ITemplateMatchingService` 作为 UI、流程工具和二次开发唯一入口；服务严格路由 Managed NCC、OpenCV 与 HALCON 三个后端。HALCON 类型只存在于 `VisionStation.Vision/TemplateMatching/Halcon` 内部边界，文件存储、运行时探测、模型缓存、候选验证、结果投影和配方资源生命周期各自形成可替换 seam。HALCON 只产生候选，最终接受由纯中立数据的六硬门 validator 决定；单目标与多目标共享同一批匹配核心。

**Tech Stack:** .NET 8、C#、WPF/Prism、OpenCvSharp 4.13、MVTec.HalconDotNet 26050.0.0、HALCON 26.05.0.0 Progress、xUnit 2.9、Windows x64。

---

设计依据：`docs/superpowers/specs/2026-07-16-halcon-shape-matching-design.md`。

## 实施纪律与不可回退约束

- 每项任务严格执行 RED → GREEN → 相关回归 → `git status` → 小提交；不得把多个未验证阶段压成一个提交。
- 每个安全小提交成功后立即 `git push` 当前分支；push 失败、测试/构建失败、敏感信息或无关改动出现时停止，不继续下一 Task。
- 当前基线为 Application 112、Vision 51、Vision UI 91，共 254 项；已知 `SignalWaiter` 并行计时偶发波动不属于本功能，不修改其代码或测试。
- 缺少 `engine` 只能解释为旧配方 `OpenCv`；显式 `Halcon`、显式 `OpenCv`、`ManagedNcc` 和未知字符串必须严格区分。
- `TemplateMatcher`/`MultiTargetMatcher` 静态 API 只承担旧 OpenCV/Managed 兼容，禁止通过全局变量、service locator 或隐式 fallback 进入 HALCON。
- 只有 `HasMatch && Outcome == InspectionOutcome.Ok` 才是操作性位姿；NG、timeout、取消和多目标计数错误均不得发布位置端口或遗留全局 pose。
- HALCON Score 只决定候选顺序，不是最终综合分；六硬门任一失败即拒绝，不加入权重补偿或自动降阈值。
- 本阶段保持批准设计的取消策略：不启用 `InterruptOperator`，不使用线程局部 `SetOperatorTimeout`；在每模型锁内设置 shape model 的 `timeout` 参数，native 返回后再传播用户取消。
- 不把现场绝对图片路径、HALCON 安装绝对路径、许可内容或生成的 `.shm` 提交到仓库。
- HALCON model、元数据和缓存均不可泄漏 `HImage`、`HTuple`、`HShapeModel` 到 Domain、Application、Infrastructure、Vision.UI 或 Client 的公开接口。

## 依赖与交付顺序

```text
x64/package
  -> Pose Scale + shared similarity transform
  -> strict engine/parameter catalog
  -> neutral service + legacy adapters
  -> operational tool safety
  -> model store/lifecycle
  -> runtime probe + cache/scheduler
  -> evidence + six hard gates
  -> HALCON learn/find orchestration
  -> result schema/defaults/UI
  -> composition root/licensed tests/docs/full verification
```

在 Task 12 完成前不把新工具默认改为 HALCON；这样每个中间提交仍可运行，且不会产生“界面已经选择 HALCON、生产却没有完整后端”的危险状态。

## 文件职责总表

**新增核心契约与纯逻辑：**

- `VisionStation.Vision/TemplateMatching/TemplateMatchingContracts.cs`：公共学习/匹配请求、批结果、候选、诊断与 service 接口。
- `VisionStation.Vision/TemplateMatching/TemplateMatchingEngineResolver.cs`：唯一引擎规范化规则。
- `VisionStation.Vision/TemplateMatching/TemplateMatchingParameterCatalog.cs`：参数名、严格/均衡/高召回预设、默认值和强类型校验。
- `VisionStation.Vision/TemplateMatching/TemplateMatchingService.cs`：只读 backend registry、异步路由与关闭状态机。
- `VisionStation.Vision/TemplateMatching/TemplateMatchResultProjector.cs`：中立候选向旧单/多结果的唯一投影器。
- `VisionStation.Vision/TemplateMatching/HalconTemplateMatchingFactory.cs`：二次开发可调用的公共中立组合工厂；不暴露 HALCON handle。
- `VisionStation.Vision/Geometry/PoseSimilarityTransform.cs`：Scale-aware 点与 ROI 相似变换。
- `VisionStation.Vision/TemplateMatching/TemplateModelContracts.cs`：受控模型存储与配方资源管理接口；不包含 HALCON 类型。
- `VisionStation.Vision/TemplateMatching/TemplateModelParameterCodec.cs`：模型 reference 与配方参数的唯一编解码/清理清单。

**新增 HALCON 内部边界：**

- `VisionStation.Vision/TemplateMatching/Halcon/HalconRuntimeLocator.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconNativeLibraryBootstrapper.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconRuntimeProbe.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconExceptionClassifier.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/IHalconOperatorBackend.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconDotNetOperatorBackend.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconImageFactory.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateModelCache.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconModelLoader.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconOperationScheduler.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateModelMetadata.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateFeatureExtractor.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconScaledShapeCandidateSource.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/TemplateCandidateEvidenceBuilder.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/TemplateCandidateValidator.cs`
- `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateMatchingBackend.cs`

**新增基础设施与集成宿主：**

- `VisionStation.Infrastructure/FileTemplateModelStore.cs`：slug+hash 路径、checksum、原子 generation 与所有权防护。
- `VisionStation.Application/Recipes/RecipeTemplateLifecycleService.cs`：配方复制/删除与模型资源事务顺序。
- `VisionStation.Client/Services/TemplateMatchingComposition.cs`：把受控文件 store 交给公共工厂，并把同一生命周期对象注册到应用。
- `VisionStation.Vision.Halcon.TestHost/*`：进程隔离的 runtime/native/model smoke 命令，只输出稳定 JSON。
- `VisionStation.Vision.Halcon.Tests/*`：显式启用、有真实许可、全程序集非并行的合成集成测试。

**主要修改：**

- `CVWork.sln`、`Directory.Build.props`、相关 `.csproj`
- `VisionStation.Domain/Models.cs`、`DeviceConfigurationModels.cs`
- `VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs`、`DefaultRecipeFactory.cs`
- `VisionStation.Vision/TemplateMatcher.cs`、`MultiTargetMatcher.cs`
- `VisionStation.Vision/Tools/TemplateLocateTool.cs`、`MultiTargetMatchTool.cs`、`GeometryToolSupport.cs`、`GeometryComputeTools.cs`、`CoordinateTransformTool.cs`
- `VisionStation.Vision/VisionPipelineFactory.cs`
- `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs` 与对应 XAML
- `VisionStation.Vision.UI/ViewModels/VisionToolCatalog.cs`、`UiModels.cs`、`VisionDebugViewModel.cs`
- `VisionStation.Vision.UI/Services/WpfToolParameterDialogService.cs`、`VisionOverlayBuilder.cs`、`TemplateLocateOverlayFactory.cs`
- `VisionStation.Vision.UI/Services/ToolResultPoseReader.cs`：所有 PositionInput 调试页共用的 invariant Scale 读取规则。
- FindLine、FindCircle、Blob 三个参数 ViewModel
- `VisionStation.Application/Inspection/VisionPortValueFormatter.cs`
- `VisionStation.Client/App.xaml.cs`、`RecipeManagementViewModel.cs`

## Task 1：固定 x64 与 HalconDotNet 包边界

**Files:**

- Create: `Directory.Build.props`
- Create: `VisionStation.Vision/Properties/AssemblyInfo.cs`
- Create: `VisionStation.Vision.Tests/HalconPackageContractTests.cs`
- Modify: `CVWork.sln`
- Modify: `VisionStation.Vision/VisionStation.Vision.csproj`
- Modify: `VisionStation.Vision.Tests/VisionStation.Vision.Tests.csproj`
- Modify: `VisionStation.Application.Tests/VisionStation.Application.Tests.csproj`
- Modify: `VisionStation.Vision.UI.Tests/VisionStation.Vision.UI.Tests.csproj`

- [ ] **Step 1：写包版本和进程架构 RED 测试**

```csharp
using HalconDotNet;

public sealed class HalconPackageContractTests
{
    [Fact]
    public void ManagedAssemblyAndTestHostArePinnedToApprovedVersions()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(
            new Version(26050, 0, 0, 0),
            typeof(HShapeModel).Assembly.GetName().Version);
    }
}
```

- [ ] **Step 2：运行 RED，确认尚未引用包**

Run:

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~HalconPackageContractTests
```

Expected: 编译以 `CS0246 HalconDotNet` 失败，而不是测试被跳过。

- [ ] **Step 3：加入精确依赖和全方案 x64 约束**

`VisionStation.Vision.csproj` 只增加 managed operator 包：

```xml
<PackageReference Include="MVTec.HalconDotNet" Version="26050.0.0" />
```

`Directory.Build.props` 固定进程架构但不把 `RuntimeIdentifier` 强加给类库：

```xml
<Project>
  <PropertyGroup>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <Prefer32Bit>false</Prefer32Bit>
  </PropertyGroup>
</Project>
```

从 `CVWork.sln` 删除 Any CPU/x86 的 solution 与 project mapping，只保留 Debug/Release x64；发布 RID 仅在最终 Client publish 命令指定。增加：

```csharp
[assembly: InternalsVisibleTo("VisionStation.Vision.Tests")]
[assembly: InternalsVisibleTo("VisionStation.Application.Tests")]
```

- [ ] **Step 4：恢复、构建并运行 GREEN**

Run:

```powershell
dotnet restore CVWork.sln
dotnet build CVWork.sln -c Release -p:Platform=x64 --no-restore
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-build --filter FullyQualifiedName~HalconPackageContractTests
```

Expected: restore/build 成功；测试 1 passed；输出目录只有 managed `MVTec.HalconDotNet.dll`，不把 `halcon.dll` 当自包含文件复制进项目。

- [ ] **Step 5：提交平台切片**

```powershell
git status --short
git add CVWork.sln Directory.Build.props VisionStation.Vision/VisionStation.Vision.csproj VisionStation.Vision/Properties/AssemblyInfo.cs VisionStation.Vision.Tests/VisionStation.Vision.Tests.csproj VisionStation.Application.Tests/VisionStation.Application.Tests.csproj VisionStation.Vision.UI.Tests/VisionStation.Vision.UI.Tests.csproj VisionStation.Vision.Tests/HalconPackageContractTests.cs
git commit -m "build: 固定 HALCON x64 依赖"
git push
```

## Task 2：为 Pose 增加兼容 Scale，并建立唯一相似变换

**Files:**

- Create: `VisionStation.Vision/Geometry/PoseSimilarityTransform.cs`
- Create: `VisionStation.Vision.Tests/Pose2DCompatibilityTests.cs`
- Create: `VisionStation.Vision.Tests/PoseSimilarityTransformTests.cs`
- Modify: `VisionStation.Domain/Models.cs`
- Modify: `VisionStation.Vision/Tools/GeometryToolSupport.cs`
- Modify: `VisionStation.Vision/Tools/GeometryComputeTools.cs`
- Modify: `VisionStation.Vision/Tools/CoordinateTransformTool.cs`
- Modify: `VisionStation.Vision/Tools/FindLineTool.cs`
- Modify: `VisionStation.Vision/Tools/FindCircleTool.cs`
- Modify: `VisionStation.Vision/Tools/DefectDetectTool.cs`
- Modify: `VisionStation.Vision/Tools/MultiTargetMatchTool.cs`
- Modify: `VisionStation.Vision/Tools/TemplateLocateTool.cs`
- Modify: `VisionStation.Vision/MultiTargetMatcher.cs`
- Modify: `VisionStation.Vision/VisionContracts.cs`
- Modify: `VisionStation.Vision/TemplateMatcher.cs`
- Modify: `VisionStation.Vision/OpenCvTemplateMatcher.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/FindLineToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/FindCircleToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/BlobAnalysisToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.UI.Tests/TemplateLocateToolDialogViewModelTests.cs`
- Modify: `VisionStation.Vision.Tests/TemplateLocateToolTests.cs`
- Create: `VisionStation.Vision.Tests/PositionInputScaleToolTests.cs`
- Create: `VisionStation.Vision.UI.Tests/PositionInputScaleDialogViewModelTests.cs`

- [ ] **Step 1：写 Scale 兼容和几何 RED 测试**

测试必须锁定旧构造/旧 JSON/三变量解构，以及 `reference.Scale != 1` 时使用比例：

```csharp
[Fact]
public void LegacyPoseContractKeepsThreeValueConstructionAndDeconstruction()
{
    var pose = new Pose2D(10, 20, 30);
    var (x, y, angle) = pose;

    Assert.Equal((10d, 20d, 30d, 1d), (x, y, angle, pose.Scale));
    Assert.Equal(1d, JsonSerializer.Deserialize<Pose2D>("{\"X\":10,\"Y\":20,\"Angle\":30}")!.Scale);
}

[Fact]
public void MapPointUsesCurrentToReferenceScaleRatio()
{
    var reference = new Pose2D(100, 100, 0) { Scale = 0.5 };
    var current = new Pose2D(200, 150, 90) { Scale = 1.0 };

    var mapped = PoseSimilarityTransform.MapPoint(new Point2D(110, 100), reference, current);

    Assert.Equal(200, mapped.X, 6);
    Assert.Equal(170, mapped.Y, 6);
}
```

另以 Theory 覆盖 Rectangle、RotatedRectangle、Circle、Polygon 的 `0.90/1.00/1.10`，以及 TemplatePoint offset 与 CoordinateTransform 保留 Scale。四个生产调用方（FindLine、FindCircle、DefectDetect、MultiTargetMatch）还要覆盖：显式 `roiReferencePoseScale=NaN/0/负数` 时返回 `CONFIG_INVALID_PARAMETER` NG、清除旧端口且不执行算法；仅缺该 key 时兼容为 1。

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Pose2DCompatibilityTests|FullyQualifiedName~PoseSimilarityTransformTests|FullyQualifiedName~PositionInputScaleToolTests"
```

Expected: `Pose2D.Scale` 与 `PoseSimilarityTransform` 不存在导致编译失败。

- [ ] **Step 3：实现兼容属性和纯变换核心**

保持 positional record 的三参数主构造不变：

```csharp
public sealed record Pose2D(double X, double Y, double Angle)
{
    public double Scale { get; init; } = 1.0;
}
```

共享 helper 的核心公式必须是：

```csharp
var scaleRatio = currentPose.Scale / referencePose.Scale;
var dx = (point.X - referencePose.X) * scaleRatio;
var dy = (point.Y - referencePose.Y) * scaleRatio;
var radians = (currentPose.Angle - referencePose.Angle) * Math.PI / 180.0;

return new Point2D(
    currentPose.X + dx * Math.Cos(radians) - dy * Math.Sin(radians),
    currentPose.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
```

helper 在入口拒绝非有限或 `<= 0` Scale；旧参考参数缺 `roiReferencePoseScale` 时才使用 1。把 `MapRoiForPositionInput` 改成窄结果 API `TryMapRoiForPositionInput(VisionToolContext context, VisionToolDefinition definition, RoiDefinition sourceRoi, out RoiDefinition mappedRoi, out PositionInputMappingFailure? failure)`：无 PositionInput/无参考位姿仍成功返回原 ROI，显式非法 Scale 则返回 `CONFIG_INVALID_PARAMETER`，不通过异常越过 ToolResult。四个调用方统一把 failure 投影为 NG 并禁止发布位置端口。Rectangle 以中心映射并转 RotatedRectangle；宽高、圆半径和所有多边形点同步乘 `scaleRatio`。

- [ ] **Step 4：替换生产几何复制公式**

`GeometryToolSupport`、TemplatePoint 和 CoordinateTransform 全部调用 helper；世界坐标 Pose 显式保留：

```csharp
return new Pose2D(worldCenter.X, worldCenter.Y, NormalizeAngle(worldAngle))
{
    Scale = imagePose.Scale
};
```

现有 OpenCV 学习状态补写通用 `standardScale=1`；HALCON 在 Task 11 改用独立 `halcon.standardScale`，两者不得覆盖。下游示教统一写后端中立的 `roiReferencePoseScale`，旧配方缺失按 1 读取。

- [ ] **Step 5：运行 GREEN 与相关几何回归**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Pose2DCompatibilityTests|FullyQualifiedName~PoseSimilarityTransformTests|FullyQualifiedName~PositionInputScaleToolTests|FullyQualifiedName~Geometry|FullyQualifiedName~CoordinateTransform|FullyQualifiedName~TemplatePoint"
```

Expected: 全部通过；三参数构造/解构调用点无需批量改写。

- [ ] **Step 6：提交 Scale 核心**

```powershell
git status --short
git add VisionStation.Domain/Models.cs VisionStation.Vision/Geometry/PoseSimilarityTransform.cs VisionStation.Vision/VisionContracts.cs VisionStation.Vision/TemplateMatcher.cs VisionStation.Vision/OpenCvTemplateMatcher.cs VisionStation.Vision/MultiTargetMatcher.cs VisionStation.Vision/Tools/GeometryToolSupport.cs VisionStation.Vision/Tools/GeometryComputeTools.cs VisionStation.Vision/Tools/CoordinateTransformTool.cs VisionStation.Vision/Tools/FindLineTool.cs VisionStation.Vision/Tools/FindCircleTool.cs VisionStation.Vision/Tools/DefectDetectTool.cs VisionStation.Vision/Tools/MultiTargetMatchTool.cs VisionStation.Vision/Tools/TemplateLocateTool.cs VisionStation.Vision.UI/ViewModels/FindLineToolDialogViewModel.cs VisionStation.Vision.UI/ViewModels/FindCircleToolDialogViewModel.cs VisionStation.Vision.UI/ViewModels/BlobAnalysisToolDialogViewModel.cs VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs VisionStation.Vision.Tests/Pose2DCompatibilityTests.cs VisionStation.Vision.Tests/PoseSimilarityTransformTests.cs VisionStation.Vision.Tests/PositionInputScaleToolTests.cs VisionStation.Vision.Tests/TemplateLocateToolTests.cs VisionStation.Vision.UI.Tests/PositionInputScaleDialogViewModelTests.cs VisionStation.Vision.UI.Tests/TemplateLocateToolDialogViewModelTests.cs docs/superpowers/plans/2026-07-16-halcon-shape-matching.md
git commit -m "feat: 增加尺度感知位姿变换"
git push
```

## Task 3：集中参数目录并关闭静态入口的静默兜底

**Files:**

- Create: `VisionStation.Vision/TemplateMatching/TemplateMatchingDiagnostics.cs`
- Create: `VisionStation.Vision/TemplateMatching/TemplateMatchingTypes.cs`
- Create: `VisionStation.Vision/TemplateMatching/TemplateMatchingConfigurationException.cs`
- Create: `VisionStation.Vision/TemplateMatching/TemplateMatchingEngineResolver.cs`
- Create: `VisionStation.Vision/TemplateMatching/TemplateMatchingParameterCatalog.cs`
- Create: `VisionStation.Vision/TemplateMatching/TemplateReferencePoseCodec.cs`
- Create: `VisionStation.Vision.Tests/TemplateMatchingRoutingTests.cs`
- Create: `VisionStation.Vision.Tests/TemplateMatchingParameterCatalogTests.cs`
- Create: `VisionStation.Vision.Tests/TemplateReferencePoseCodecTests.cs`
- Modify: `VisionStation.Vision/TemplateMatcher.cs`
- Modify: `VisionStation.Vision/MultiTargetMatcher.cs`
- Modify: `VisionStation.Vision/Tools/GeometryToolSupport.cs`
- Modify: `VisionStation.Vision.Tests/PositionInputScaleToolTests.cs`

- [ ] **Step 1：写严格路由矩阵 RED 测试**

```csharp
[Theory]
[InlineData(null, TemplateMatchingEngine.OpenCv)]
[InlineData("OpenCv", TemplateMatchingEngine.OpenCv)]
[InlineData("opencv", TemplateMatchingEngine.OpenCv)]
[InlineData("ManagedNcc", TemplateMatchingEngine.ManagedNcc)]
[InlineData("Halcon", TemplateMatchingEngine.Halcon)]
public void ResolverNormalizesOnlySupportedEngines(string? value, TemplateMatchingEngine expected)
{
    var parameters = value is null
        ? new Dictionary<string, string>()
        : new Dictionary<string, string> { ["engine"] = value };

    Assert.Equal(expected, TemplateMatchingEngineResolver.Resolve(parameters));
}

[Theory]
[InlineData("")]
[InlineData("Halconn")]
[InlineData("Shape")]
public void ExplicitUnknownEngineNeverFallsBackToOpenCv(string value)
{
    var exception = Assert.Throws<TemplateMatchingConfigurationException>(
        () => TemplateMatchingEngineResolver.Resolve(new Dictionary<string, string> { ["engine"] = value }));

    Assert.Equal("CONFIG_UNKNOWN_ENGINE", exception.Code);
}
```

再锁定：静态 Halcon Match 返回 `CONFIG_SERVICE_REQUIRED` NG；静态 Halcon Learn 抛同码异常；ManagedNcc 多目标返回 `CONFIG_UNSUPPORTED_MODE`；HALCON+CircularBlob 返回 `CONFIG_UNSUPPORTED_MODE`。`TemplateReferencePoseCodecTests` 用冲突的两套键证明：Halcon 只读 `halcon.standard*/halcon.template*`，OpenCv/Managed 只读通用旧键；任何一侧缺字段都不跨 namespace 猜测。生产 PositionInput 测试覆盖 Halcon 上游驱动 FindLine/FindCircle/DefectDetect/MultiTargetMatch。

- [ ] **Step 2：写参数范围 RED 测试**

严格预设断言设计表全部具体值；`halcon.operatorTimeoutMs` 的 100、60000 合法，0、负数、60001、非数字均为 `CONFIG_INVALID_PARAMETER`；candidateLimit 必须大于 expectedCount；scaleMin/scaleMax、coverage、overlap 和 angle 均做有限值与顺序校验。HALCON Multi 缺 `expectedCount` 但有旧草稿 `matchCount` 时把后者读为精确数量且不回写参数；OpenCV 的 `matchCount` 始终保持 maxMatches 语义。再加交叉污染矩阵：engine=OpenCv 且 `halcon.operatorTimeoutMs=garbage` 仍正常路由；engine=Halcon 且 inactive OpenCV angle/model 字段损坏也不影响 `ParseHalcon`。必须先 resolve engine，再调用活动 backend 的 parser。

- [ ] **Step 3：运行 RED**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateMatchingRoutingTests|FullyQualifiedName~TemplateMatchingParameterCatalogTests|FullyQualifiedName~TemplateReferencePoseCodecTests|FullyQualifiedName~PositionInputScaleToolTests"
```

Expected: 新 resolver/catalog 类型不存在。

- [ ] **Step 4：实现唯一解析与预设源**

公共枚举与解析结果：

```csharp
public enum TemplateMatchingEngine { Unknown, ManagedNcc, OpenCv, Halcon }
public enum TemplateMatchCardinality { Single, ExactCount }
public sealed record TemplateModelOwner(string RecipeId, string FlowId, string ToolId);

public static TemplateMatchingEngine Resolve(IReadOnlyDictionary<string, string> parameters)
{
    if (!parameters.TryGetValue("engine", out var raw))
        return TemplateMatchingEngine.OpenCv;

    return raw.Trim().ToLowerInvariant() switch
    {
        "opencv" => TemplateMatchingEngine.OpenCv,
        "managedncc" => TemplateMatchingEngine.ManagedNcc,
        "halcon" => TemplateMatchingEngine.Halcon,
        _ => throw new TemplateMatchingConfigurationException(
            "CONFIG_UNKNOWN_ENGINE", $"不支持的匹配引擎：{raw}")
    };
}
```

`TemplateMatchingParameterCatalog` 定义 `halcon.*` 常量、三套具体 preset 字典和 `ParseHalcon(parameters, cardinality)`；UI、默认配方和 HALCON backend 后续只能调用它，不能再次声明默认数值。OpenCV/Managed 不调用此 parser；service 只负责先 resolve engine，再由选中的 backend 解析自己的命名空间。

`Unknown` 不能由 resolver 的合法输入返回，也不能注册为 backend；它只用于 `CONFIG_UNKNOWN_ENGINE` 这类 pre-routing NG 的结果契约。两个 Tool 在该路径写 `Data["engine"]="Unknown"`，不能写 OpenCv 或原始坏字符串。

`TemplateReferencePoseCodec.ReadActive` 先调用 resolver，再返回中立 `TemplateLearnedGeometry(Pose2D StandardPose, int TemplateWidth, int TemplateHeight)`：Halcon 的 Scale 缺失即 reference 不完整，旧 OpenCv/Managed 的通用 `standardScale` 缺失才兼容 1。`GeometryToolSupport` 和 Task 16 的 UI 预览只能复用该 codec，不再各自读取 `standardX/templateX`。

三套 preset 在此固定并由测试逐字段锁定；Single/Multi 的 candidateLimit 分别取斜杠两侧值：

| 参数 | 严格 | 均衡 | 高召回 |
|---|---:|---:|---:|
| `halcon.angleStartDeg` | -180 | -180 | -180 |
| `halcon.angleExtentDeg` | 360 | 360 | 360 |
| `halcon.scaleMin` | 0.90 | 0.90 | 0.90 |
| `halcon.scaleMax` | 1.10 | 1.10 | 1.10 |
| `halcon.candidateMinScore` | 0.65 | 0.58 | 0.50 |
| `halcon.outerCoverageMin` | 0.90 | 0.85 | 0.78 |
| `halcon.innerCoverageMin` | 0.82 | 0.75 | 0.65 |
| `halcon.edgeTolerancePx` | 3.0 | 4.0 | 5.0 |
| `halcon.polarityAgreementMin` | 0.90 | 0.85 | 0.78 |
| `halcon.candidateMaxOverlap` | 0.70 | 0.75 | 0.80 |
| `halcon.maxOverlap` | 0.25 | 0.30 | 0.35 |
| `halcon.greediness` | 0.80 | 0.75 | 0.65 |
| `halcon.subPixel` | least_squares | least_squares | least_squares |
| `halcon.numLevels` | auto | auto | auto |
| `halcon.candidateLimit` | 32 / 128 | 48 / 160 | 64 / 192 |
| `halcon.operatorTimeoutMs` | 5000 | 7000 | 10000 |
| `expectedCount`（Multi） | 1 | 1 | 1 |

`ParseHalcon` 还固定这些合法域：Scale 为有限正数且 Min≤Max；coverage/polarity/overlap/greediness 为 `[0,1]`；edgeTolerance 为 `(0,100]`；candidateLimit 为 `[2,512]` 且 Multi 时大于 expectedCount；expectedCount 为 `[1,100]`；operatorTimeoutMs 为 `[100,60000]`；subPixel 只接受 `least_squares`，numLevels 只接受 `auto` 或正整数，运行查找时 `auto` 映射为 0。

`TemplateMatchingDiagnostics` 同时集中声明全部稳定 code，测试逐个断言中文用户消息非空、技术详情不进入操作员消息：

```csharp
public static class TemplateMatchingDiagnosticCodes
{
    public const string ConfigUnknownEngine = "CONFIG_UNKNOWN_ENGINE";
    public const string ConfigServiceRequired = "CONFIG_SERVICE_REQUIRED";
    public const string ConfigUnsupportedMode = "CONFIG_UNSUPPORTED_MODE";
    public const string ConfigInvalidParameter = "CONFIG_INVALID_PARAMETER";
    public const string RuntimeNotFound = "RUNTIME_NOT_FOUND";
    public const string RuntimeArchMismatch = "RUNTIME_ARCH_MISMATCH";
    public const string RuntimeVersionMismatch = "RUNTIME_VERSION_MISMATCH";
    public const string LicenseUnavailable = "LICENSE_UNAVAILABLE";
    public const string ModelPathInvalid = "MODEL_PATH_INVALID";
    public const string ModelNotFound = "MODEL_NOT_FOUND";
    public const string ModelChecksumMismatch = "MODEL_CHECKSUM_MISMATCH";
    public const string ModelMetadataInvalid = "MODEL_METADATA_INVALID";
    public const string ModelVersionMismatch = "MODEL_VERSION_MISMATCH";
    public const string ModelRelearnRequired = "MODEL_RELEARN_REQUIRED";
    public const string ModelLoadFailed = "MODEL_LOAD_FAILED";
    public const string ModelTemplateIncomplete = "MODEL_TEMPLATE_INCOMPLETE";
    public const string ModelContrastWeak = "MODEL_CONTRAST_WEAK";
    public const string ModelInternalFeaturesWeak = "MODEL_INTERNAL_FEATURES_WEAK";
    public const string MatchInvalidPose = "MATCH_INVALID_POSE";
    public const string MatchIncompleteAtBoundary = "MATCH_INCOMPLETE_AT_BOUNDARY";
    public const string MatchPolarityMismatch = "MATCH_POLARITY_MISMATCH";
    public const string MatchOuterContourWeak = "MATCH_OUTER_CONTOUR_WEAK";
    public const string MatchInnerFeaturesWeak = "MATCH_INNER_FEATURES_WEAK";
    public const string MatchDuplicateOverlap = "MATCH_DUPLICATE_OVERLAP";
    public const string MatchTimeout = "MATCH_TIMEOUT";
    public const string MatchCandidateLimitReached = "MATCH_CANDIDATE_LIMIT_REACHED";
    public const string MatchOperatorFailed = "MATCH_OPERATOR_FAILED";
}
```

- [ ] **Step 5：改造旧静态 facade**

给现有 positional result record 增加 init-only `Engine`、`FailureCode`、`FailureStage`、`TechnicalDetails`，保持旧构造/解构。静态 Match 必须在 resolver 外层捕获专用配置异常，unknown 才能按契约返回 NG；Halcon/unknown 不得调用 OpenCV：

```csharp
try
{
    var engine = TemplateMatchingEngineResolver.Resolve(parameters);
    return engine switch
    {
        TemplateMatchingEngine.OpenCv => OpenCvTemplateMatcher.Match(
            frame, searchRoi, parameters, gray, cancellationToken),
        TemplateMatchingEngine.ManagedNcc => MatchManaged(
            frame, searchRoi, parameters, cancellationToken),
        TemplateMatchingEngine.Halcon => CreateConfigurationFailure("CONFIG_SERVICE_REQUIRED"),
        _ => throw new UnreachableException()
    };
}
catch (TemplateMatchingConfigurationException exception)
{
    return CreateConfigurationFailure(exception.Code, exception.Message);
}
```

`MultiTargetMatcher` 把当前实现提为内部 `MatchOpenCv`，public facade 仅做严格兼容路由，避免 service adapter 回调 facade 形成递归。

静态 `TemplateMatcher.Learn` 保持返回类型限制，配置错误直接抛 `TemplateMatchingConfigurationException`；两个静态 Match 和 Multi Match 捕获该专用异常并返回同 Code 的结构化 NG result。它们只捕获配置异常，不吞用户取消或未知运行异常。

- [ ] **Step 6：运行 GREEN 与旧 OpenCV 回归**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateMatchingRoutingTests|FullyQualifiedName~TemplateMatchingParameterCatalogTests|FullyQualifiedName~TemplateReferencePoseCodecTests|FullyQualifiedName~PositionInputScaleToolTests|FullyQualifiedName~TemplateMatcher|FullyQualifiedName~MultiTargetMatcher|FullyQualifiedName~OpenCv"
```

Expected: 新测试与原 OpenCV Shape V2 测试全部通过；缺 engine 的旧 fixture 仍走 OpenCV。

- [ ] **Step 7：提交严格路由**

```powershell
git status --short
git add VisionStation.Vision/TemplateMatching VisionStation.Vision/TemplateMatcher.cs VisionStation.Vision/MultiTargetMatcher.cs VisionStation.Vision/Tools/GeometryToolSupport.cs VisionStation.Vision.Tests/TemplateMatchingRoutingTests.cs VisionStation.Vision.Tests/TemplateMatchingParameterCatalogTests.cs VisionStation.Vision.Tests/TemplateReferencePoseCodecTests.cs VisionStation.Vision.Tests/PositionInputScaleToolTests.cs
git commit -m "refactor: 建立模板匹配严格路由"
git push
```

## Task 4：建立中立异步 service 与可注入 backend

**Files:**

- Create: `VisionStation.Vision/TemplateMatching/TemplateMatchingContracts.cs`
- Create: `VisionStation.Vision/TemplateMatching/ITemplateMatchingBackend.cs`
- Create: `VisionStation.Vision/TemplateMatching/TemplateMatchingService.cs`
- Create: `VisionStation.Vision/TemplateMatching/OpenCvTemplateMatchingBackend.cs`
- Create: `VisionStation.Vision/TemplateMatching/ManagedNccTemplateMatchingBackend.cs`
- Create: `VisionStation.Vision/TemplateMatching/TemplateMatchResultProjector.cs`
- Create: `VisionStation.Vision.Tests/TemplateMatchingServiceTests.cs`
- Create: `VisionStation.Vision.Tests/TemplateMatchResultProjectorTests.cs`
- Create: `VisionStation.Vision.Tests/TemplateMatchingTestDoubles.cs`

- [ ] **Step 1：写 service 路由、批语义和关闭 RED 测试**

```csharp
[Fact]
public async Task SingleAndMultiUseTheSameBatchBackendContract()
{
    var halcon = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon);
    await using var service = TemplateMatchingService.ForTests(halcon);

    await service.MatchAsync(Request(TemplateMatchCardinality.Single, expectedCount: 1), default);
    await service.MatchAsync(Request(TemplateMatchCardinality.ExactCount, expectedCount: 3), default);

    Assert.Collection(
        halcon.MatchRequests,
        request => Assert.Equal(TemplateMatchCardinality.Single, request.Cardinality),
        request => Assert.Equal((TemplateMatchCardinality.ExactCount, 3), (request.Cardinality, request.ExpectedCount)));
}
```

另覆盖：missing/OpenCv/Halcon 精确调用一个 backend；unknown 不调用任何 backend 且 batch Engine 为 `Unknown`；后端 NG 诊断原样传递；token 已取消与后端抛 OCE 均传播；`DisposeAsync` 后拒绝新请求，并且各 backend 只释放一次。`TemplateMatchResultProjectorTests` 用 35°、Scale=1.1 的完整模板 ROI 轮廓断言 `MatchX/MatchY` 等于轴对齐外接框最小 X/Y 的 `Math.Floor`，保持现有 int positional 契约。

```csharp
[Fact]
public void ProjectorUsesAxisAlignedBoundsOfTheWholeTransformedTemplateRoi()
{
    var candidate = Candidate(
        pose: new Pose2D(100, 100, 35) { Scale = 1.1 },
        templateRoiContours:
        [
            [
                new Point2D(88.288, 78.371),
                new Point2D(124.330, 103.608),
                new Point2D(111.712, 121.629),
                new Point2D(75.670, 96.392)
            ]
        ]);

    var result = TemplateMatchResultProjector.ToSingle(OkBatch(candidate));

    Assert.Equal(75, result.MatchX);
    Assert.Equal(78, result.MatchY);
}
```

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateMatchingServiceTests|FullyQualifiedName~TemplateMatchResultProjectorTests"
```

Expected: service/contracts 尚不存在。

- [ ] **Step 3：实现稳定公共 seam**

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

请求以明确字段保存 owner、图像和 ROI，不要求二次开发者再从参数字典反解上下文：

```csharp
public sealed record TemplateLearningRequest(
    TemplateModelOwner Owner,
    ImageFrame Frame,
    RoiDefinition TemplateRoi,
    RoiDefinition? SearchRoi,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record TemplateMatchingRequest(
    TemplateModelOwner Owner,
    ImageFrame Frame,
    RoiDefinition? SearchRoi,
    IReadOnlyDictionary<string, string> Parameters,
    TemplateMatchCardinality Cardinality,
    int ExpectedCount);
```

参数字典在请求构造时复制为不可变快照。候选的 Scale 唯一来源是：

```csharp
public sealed record TemplateMatchBatchCandidate(
    Pose2D Pose,
    double Score,
    int TemplateWidth,
    int TemplateHeight,
    IReadOnlyList<IReadOnlyList<Point2D>> ShapeContours,
    IReadOnlyList<IReadOnlyList<Point2D>> TemplateRoiContours)
{
    public double OuterCoverage { get; init; }
    public double InnerCoverage { get; init; }
    public double EdgeDistanceP95Px { get; init; }
    public double PolarityAgreement { get; init; }
}
```

`TemplateMatchBatchResult` 包含规范化 Engine、Outcome、HasMatch、Matches、SearchRegion 和结构化诊断。不得把参数字典原始 engine 文本回显为实际 engine。

每个 backend 都必须提供完整变换后的 `TemplateRoiContours`。projector 以所有轮廓点 `min(X/Y)` 的 `Math.Floor` 计算 int 兼容字段 `MatchX/MatchY`，精确浮点边界仍由 contours 表达；没有完整轮廓的成功候选是内部契约错误，不能退回未缩放矩形猜测。

- [ ] **Step 4：实现只读 registry 与 legacy adapters**

registry 在构造函数复制并校验唯一键，之后不可变；OpenCV adapter 直接调用 `OpenCvTemplateMatcher`/`MultiTargetMatcher.MatchOpenCv`，Managed adapter 复用旧单目标且对多目标返回稳定不支持诊断。adapter 必须把旧结果已有的完整 ROI contours 原样带入候选；Managed 仅有矩形尺寸时，在 adapter 内根据 Pose/Scale/Angle 精确变换四角生成完整 contour，不能留到 projector 猜测。service 将 resolver/parameter/model/runtime 的预期专用异常转换为结构化 batch NG，但不捕获用户 OCE，不提供运行时替换全局 backend 的方法。

保留一个显式 `TemplateMatchingService.CreateLegacyOnly()` 组合入口，只注册 OpenCv/Managed；收到 Halcon 请求时返回 `CONFIG_SERVICE_REQUIRED`。它供 Task 5 的中间可运行提交和纯 OpenCV 部署使用，不创建 HALCON runtime/cache，也不使用全局状态。

- [ ] **Step 5：运行 GREEN**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateMatchingServiceTests|FullyQualifiedName~TemplateMatchResultProjectorTests|FullyQualifiedName~TemplateMatchingRoutingTests"
```

Expected: 全部通过；fake backend 可在无 HALCON runtime 下运行。

- [ ] **Step 6：提交 service seam**

```powershell
git status --short
git add VisionStation.Vision/TemplateMatching VisionStation.Vision.Tests/TemplateMatchingServiceTests.cs VisionStation.Vision.Tests/TemplateMatchResultProjectorTests.cs VisionStation.Vision.Tests/TemplateMatchingTestDoubles.cs
git commit -m "feat: 建立异步模板匹配服务"
git push
```

## Task 5：注入流程工具并收紧操作性位姿输出

**Files:**

- Create: `VisionStation.Vision.Tests/TemplateMatchingToolPortSafetyTests.cs`
- Modify: `VisionStation.Vision/TemplateMatcher.cs`
- Modify: `VisionStation.Vision/MultiTargetMatcher.cs`
- Modify: `VisionStation.Vision/Tools/TemplateLocateTool.cs`
- Modify: `VisionStation.Vision/Tools/MultiTargetMatchTool.cs`
- Modify: `VisionStation.Vision/VisionPipelineFactory.cs`
- Modify: `VisionStation.Client/App.xaml.cs`
- Modify: `VisionStation.Vision.Tests/TemplateLocateToolTests.cs`
- Create: `VisionStation.Vision.Tests/ConfiguredVisionPipelineTests.cs`

- [ ] **Step 1：写 NG/OK/取消端口安全 RED 测试**

至少构造以下 fake service 结果：

```csharp
[Fact]
public async Task NgCandidateClearsLegacyPoseAndPublishesNoOperationalPorts()
{
    var candidate = Candidate(new Pose2D(50, 60, 10) { Scale = 1.1 });
    var service = FakeService.Returning(Batch(InspectionOutcome.Ng, hasMatch: true, candidate));
    var (definition, context) = ToolFixture.WithExistingLegacyPose();

    var result = await new TemplateLocateTool(service).ExecuteAsync(definition, context);

    Assert.Equal(InspectionOutcome.Ng, result.Outcome);
    Assert.False(context.Properties.ContainsKey("pose"));
    var consumer = ToolFixture.ConsumerOf(definition, "PositionOutput");
    Assert.False(context.TryGetPortInput<Pose2D>(consumer, "PositionInput", out _));
    Assert.Equal("1.1", result.Data["rejectedCandidate.scale"]);
}
```

覆盖单目标 OK Scale、OpenCV 低分 `HasMatch=true/Outcome=Ng`、多目标少一/多一 NG、精确数量 OK、Count 始终存在、缺图像输入也清除旧 pose、NG Data 保留红色诊断候选、fake 抛 OCE 后 pipeline 不执行下一工具也不添加部分 ToolResult。再锁定 `Data["engine"]` 只来自 batch Engine：缺 engine 输出 `OpenCv`，输入 `halcon` 输出 `Halcon`，未知字符串输出 `Unknown`，绝不原样回显参数文本或伪装 OpenCv；单目标 HALCON OK 以 invariant 格式写出 `outerCoverage/innerCoverage/edgeDistanceP95Px/polarityAgreement` 四项指标。

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateMatchingToolPortSafetyTests|FullyQualifiedName~ConfiguredVisionPipelineTests|FullyQualifiedName~TemplateLocateToolTests"
```

Expected: Tool 仍无参构造并调用静态 matcher；NG 仍发布 pose，测试失败。

- [ ] **Step 3：改 Tool 为真正 await 注入 service**

```csharp
public sealed class TemplateLocateTool(ITemplateMatchingService matchingService) : IVisionTool
{
    public async Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        context.Properties.Remove("pose");
        // 清除必须发生在 TryGetInputImage 之前，缺图也不能复用上一工具位姿。
        var batch = await matchingService.MatchAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = TemplateMatchResultProjector.ToSingle(batch);
        var operational = result.HasMatch && result.Outcome == InspectionOutcome.Ok;
        // 只有 operational 分支发布所有位置/尺度端口。
    }
}
```

Multi 使用相同 service；只有精确计数整体 OK 才发布 Best/All/Position/Score/Scales。`CountOutput` 和诊断始终发布。静态旧 API 不再出现在生产 Tool 或参数 ViewModel 中。

两个 Tool 构造请求时使用 `new TemplateModelOwner(context.Recipe.Id, context.Recipe.GetActiveFlow().Id, definition.Id)`；不得用工具名称、流程显示名或空的临时 owner 解析模型。

- [ ] **Step 4：让 projector 成为 Scale 唯一赋值点**

`TemplateMatchResult.Scale` 为 `Pose.Scale` 计算属性；`MultiTargetMatchCandidate.Scale` 是 init-only 兼容属性。所有旧结果对象由 projector 创建：

```csharp
new MultiTargetMatchCandidate(
    source.Pose.X,
    source.Pose.Y,
    source.Pose.Angle,
    source.Score,
    source.TemplateWidth,
    source.TemplateHeight,
    "Template",
    0)
{
    Scale = source.Pose.Scale,
    OuterCoverage = source.OuterCoverage,
    InnerCoverage = source.InnerCoverage,
    EdgeDistanceP95Px = source.EdgeDistanceP95Px,
    PolarityAgreement = source.PolarityAgreement
};
```

projector 同时把完整模板 ROI 的 AABB、Scale 和四项验证指标带入兼容结果；Tool 只做 invariant 序列化，不重新计算几何、实际 Engine 或指标。

- [ ] **Step 5：更新 factory 构造链并运行 GREEN**

`VisionPipelineFactory.CreateDefault` 增加非空 `ITemplateMatchingService` 参数，并把同一实例传给两个工具；不提供会私自创建 service 的生产无参构造。为保证这个提交之后整个 Client 仍可运行，App 立即创建和注册 `TemplateMatchingService.CreateLegacyOnly()`，保存到字段，并在 `OnExit` 等待 `DisposeAsync`；Task 12 再把它替换成完整 HALCON composition。默认值此时仍是 OpenCv。

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateMatchingToolPortSafetyTests|FullyQualifiedName~ConfiguredVisionPipelineTests|FullyQualifiedName~TemplateLocateToolTests"
dotnet build VisionStation.Client\VisionStation.Client.csproj -c Release -p:Platform=x64 --no-restore
```

Expected: 全部通过；取消由现有 ConfiguredVisionPipeline 自然传播，不生成 `MATCH_CANCELLED`。

- [ ] **Step 6：提交工具安全切片**

```powershell
git status --short
git add VisionStation.Vision/TemplateMatcher.cs VisionStation.Vision/MultiTargetMatcher.cs VisionStation.Vision/Tools/TemplateLocateTool.cs VisionStation.Vision/Tools/MultiTargetMatchTool.cs VisionStation.Vision/VisionPipelineFactory.cs VisionStation.Vision/TemplateMatching/TemplateMatchResultProjector.cs VisionStation.Client/App.xaml.cs VisionStation.Vision.Tests/TemplateMatchingToolPortSafetyTests.cs VisionStation.Vision.Tests/TemplateLocateToolTests.cs VisionStation.Vision.Tests/ConfiguredVisionPipelineTests.cs
git commit -m "fix: 阻止 NG 模板位姿下发"
git push
```

## Task 6：实现受控模型存储、元数据校验与原子 generation

**Files:**

- Create: `VisionStation.Vision/TemplateMatching/TemplateModelContracts.cs`
- Create: `VisionStation.Vision/TemplateMatching/TemplateModelParameterCodec.cs`
- Create: `VisionStation.Infrastructure/FileTemplateModelStore.cs`
- Create: `VisionStation.Infrastructure/TemplateModelPathGuard.cs`
- Create: `VisionStation.Application.Tests/FileTemplateModelStoreTests.cs`
- Modify: `VisionStation.Infrastructure/VisionStation.Infrastructure.csproj`

- [ ] **Step 1：写路径、所有权、checksum 和原子性 RED 测试**

测试使用临时目录和固定 ID，禁止依赖 HALCON：

```csharp
[Fact]
public async Task OwnerSegmentsCombineReadableSlugWithCollisionResistantHash()
{
    var first = await StoreGenerationAsync(new("Recipe/A", "Flow:1", "Tool?1"));
    var second = await StoreGenerationAsync(new("Recipe_A", "Flow_1", "Tool_1"));

    Assert.NotEqual(first.ModelPath, second.ModelPath);
    Assert.Matches(@"recipe-a-[0-9a-f]{12}", first.ModelPath);
    Assert.False(Path.IsPathRooted(first.ModelPath));
}

[Theory]
[InlineData("..\\outside\\model.shm")]
[InlineData("C:\\outside\\model.shm")]
[InlineData("\\\\server\\share\\model.shm")]
[InlineData("recipe//tool/model.shm")]
public async Task ResolveRejectsUntrustedRelativePaths(string path)
{
    var error = await Assert.ThrowsAsync<TemplateModelStoreException>(
        () => _store.ResolveAsync(Owner, Reference(path), default));

    Assert.Equal("MODEL_PATH_INVALID", error.Code);
}
```

另覆盖：SHA-256 前 12 位、metadata owner 三元组不符、model/metadata checksum 损坏、缺文件、父目录 junction/symlink/reparse point、提交中途异常后旧 reference 仍可解析、两份最终文件齐全后才返回 reference。`TemplateModelParameterCodec` 对 Task 6 定义的精确 namespaced key 清单逐键 round-trip，缺任一 reference/geometry/fingerprint 必填 key 都 fail closed；`RemoveHalcon` 删除全部且仅删除该清单并保留通用 OpenCV 状态及未知未来 `halcon.*`。OpenCV Reset 另测其通用 key，两套清单不得混用。

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~FileTemplateModelStoreTests
```

Expected: 模型存储契约与实现不存在。

- [ ] **Step 3：定义不含 HALCON 类型的存储 seam**

```csharp
public sealed record TemplateModelReference(
    string ModelPath,
    string MetadataPath,
    string ModelFormat,
    string ModelChecksum,
    string MetadataChecksum,
    string Generation,
    string ModelVersion,
    string RuntimeVersion,
    string GenerationParameterFingerprint);

public interface ITemplateModelStore
{
    Task<TemplateModelWriteSession> BeginWriteAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken);

    Task<TemplateModelReference> CommitAsync(
        TemplateModelWriteSession session,
        ReadOnlyMemory<byte> metadataJson,
        CancellationToken cancellationToken);

    Task<ResolvedTemplateModel> ResolveAsync(
        TemplateModelOwner owner,
        TemplateModelReference reference,
        CancellationToken cancellationToken);

    Task<TemplateModelReference> CopyGenerationAsync(
        TemplateModelOwner sourceOwner,
        TemplateModelReference sourceReference,
        TemplateModelOwner targetOwner,
        CancellationToken cancellationToken);

    Task DeleteOwnerResourcesAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken);
}
```

write session 只暴露 store 创建的 staging model full path 和 generation；`DisposeAsync` 未 Commit 时清理 staging。`ResolvedTemplateModel` 返回受控绝对只读路径与已验证 metadata bytes，不返回 native handle。Resolve/Commit 必须交叉校验 reference 与 metadata 的 model format、generation、版本、两个 checksum 和 generation fingerprint；CopyGeneration 更新 JSON 顶层 owner/filename/model checksum 后走同一 Commit 路径，并保持同一 fingerprint。DeleteOwnerResources 必须逐份验证目录 hash 和 metadata owner，遇到不明文件只报告 orphan 而不越权删除。

`TemplateModelParameterCodec.ReadHalcon/WriteHalcon/RemoveHalcon` 是 UI、learner、backend 和配方复制唯一允许操作完整 HALCON 学习状态的位置，精确 key 清单为：`halcon.modelPath/modelMetadataPath/modelFormat/modelVersion/modelRuntimeVersion/modelGeneration/modelChecksum/metadataChecksum/generationParameterFingerprint`、`halcon.standardX/Y/Angle/Scale`、`halcon.templateWidth/Height`。`ReadHalcon` 一次返回 `HalconTemplateModelState(TemplateModelReference Reference, TemplateLearnedGeometry Geometry)`，禁止调用方分别读取后拼接。调用方不得各自维护 key 清单，也不得用 `halcon.*` 通配删除未来扩展。验证审计数据只写 metadata JSON，预览只随 `TemplateLearningResult` 返回，不进入 recipe。旧 OpenCV 的通用 `standard*`、`template*`、`modelPath/modelVersion/templatePixels` 仍由 OpenCV 兼容代码读取，HALCON codec 永不覆盖或删除它们。

- [ ] **Step 4：实现 slug+hash 与路径防护**

目录段算法固定：

```csharp
var readable = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
if (readable.Length == 0) readable = "item";
if (readable.Length > 48) readable = readable[..48].TrimEnd('-');
var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
    .ToLowerInvariant()[..12];
return $"{readable}-{hash}";
```

解析时同时执行 `Path.IsPathRooted`、空段/盘符/UNC/`..` 拒绝、`GetFullPath` + `GetRelativePath` containment、不区分大小写 Windows 比较，并逐级检查已存在父目录的 `FileAttributes.ReparsePoint`。最终还要核对目录 hash 后缀和 metadata 内完整 owner。

- [ ] **Step 5：实现 generation 提交**

布局严格保持：

```text
Resources/Templates/recipe-a-1a2b3c4d5e6f/flow-main-2b3c4d5e6f70/tool-locate-3c4d5e6f7081/
  model-20260716T120000000-4d5e6f70.shm
  model-20260716T120000000-4d5e6f70.json
```

在同一工具目录先写随机临时文件；关闭句柄、计算 model SHA、验证 metadata 的 model filename/checksum/owner 后，将两个临时文件分别原子移动为 generation 文件。只有两者存在且重新 Resolve 成功才返回 reference。中途崩溃最多留下未引用 generation，旧参数从未改变，因此旧模型仍可运行。

- [ ] **Step 6：增加 Infrastructure → Vision 的端口实现引用并运行 GREEN**

这是有意的依赖倒置：Vision 声明存储端口，Infrastructure 实现；Vision 不引用 Infrastructure，因此无循环。

```powershell
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~FileTemplateModelStoreTests
```

Expected: 所有正常、攻击和故障注入场景通过。

- [ ] **Step 7：提交模型存储**

```powershell
git status --short
git add VisionStation.Vision/TemplateMatching/TemplateModelContracts.cs VisionStation.Vision/TemplateMatching/TemplateModelParameterCodec.cs VisionStation.Infrastructure/VisionStation.Infrastructure.csproj VisionStation.Infrastructure/FileTemplateModelStore.cs VisionStation.Infrastructure/TemplateModelPathGuard.cs VisionStation.Application.Tests/FileTemplateModelStoreTests.cs
git commit -m "feat: 增加受控模板模型存储"
git push
```

## Task 7：实现配方模型复制、删除和缓存 retire 协调

**Files:**

- Create: `VisionStation.Vision/TemplateMatching/TemplateModelResourceManager.cs`
- Create: `VisionStation.Application/Recipes/IRecipeTemplateLifecycleService.cs`
- Create: `VisionStation.Application/Recipes/RecipeTemplateLifecycleService.cs`
- Create: `VisionStation.Application.Tests/TemplateModelResourceManagerTests.cs`
- Modify: `VisionStation.Vision/TemplateMatching/TemplateModelContracts.cs`
- Modify: `VisionStation.Infrastructure/FileTemplateModelStore.cs`

- [ ] **Step 1：写复制全有全无、JSON-first 删除 RED 测试**

```csharp
[Fact]
public async Task DuplicateCopiesEveryActiveGenerationAndRewritesOwnershipBeforeSave()
{
    await using var copy = await _resources.PrepareRecipeCopyAsync(SourceRecipe, "recipe-copy", default);

    Assert.All(copy.Recipe.GetAllToolsWithHalconReferences(), tool =>
    {
        Assert.Contains("recipe-copy-", tool.Parameters["halcon.modelPath"]);
        Assert.NotEqual(SourceReference(tool.Id), tool.Parameters["halcon.modelPath"]);
    });

    await _repository.SaveAsync(copy.Recipe);
    await copy.CommitAsync(default);
}
```

故障注入覆盖：第二个 tool copy 失败则不调用 repository Save 且删除新 recipe staging；save 失败则 session Dispose 清理副本；delete 必须先成功删除 JSON，再 retire 缓存和校验 owner 删除目录；资源清理失败只记录 orphan warning，不回滚 JSON。关键回归是 `engine=OpenCv` 但保留完整 inactive `halcon.*` reference 的工具：Duplicate 仍必须复制 generation、重写新 owner/path/checksum，Delete 仍必须 retire 并清理；只有完全没有 HALCON reference 的工具才保持不变。这些 repository 顺序由可单测的 `RecipeTemplateLifecycleService` 承担，ViewModel 只在 Task 17 调用它。

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~TemplateModelResourceManagerTests
```

Expected: resource manager/copy session 不存在。

- [ ] **Step 3：实现窄生命周期接口**

```csharp
public interface ITemplateModelResourceManager
{
    Task<TemplateRecipeCopySession> PrepareRecipeCopyAsync(
        Recipe source,
        string newRecipeId,
        CancellationToken cancellationToken);

    Task RetireToolAsync(TemplateModelOwner owner, CancellationToken cancellationToken);

    Task DeleteRecipeResourcesAsync(Recipe deletedRecipe, CancellationToken cancellationToken);
}

internal interface ITemplateModelRetirementSink
{
    ValueTask RetireAsync(TemplateModelOwner owner, CancellationToken cancellationToken);
}
```

copy session 未 Commit 时删除新 owner 目录；Commit 后仅保留资源。复制时遍历每个完整 namespaced HALCON reference（不以当前 engine 过滤）、复制为新 generation、重写 metadata owner/checksum，再一次性返回完整 Recipe。

- [ ] **Step 4：实现可单测的配方生命周期协调器**

复制顺序固定为：prepare resources → repository save → copy commit → 刷新列表。删除顺序固定为：保存删除前 Recipe 快照 → repository delete → retire/cache → owner-verified file delete；最后一步异常只 `_log.Warning` 记录 orphan。

```csharp
await using var copy = await _modelResources.PrepareRecipeCopyAsync(source, copyId, token);
await _recipes.SaveAsync(copy.Recipe, token);
await copy.CommitAsync(token);
```

`IRecipeTemplateLifecycleService` 与实现均放在 Application 的 Recipes 命名空间，公开接口只提供 `DuplicateAsync(source, newRecipeId, token)` 和 `DeleteAsync(recipe, token)`；所有文件/仓储顺序隐藏在 service 内，Client 不复制事务逻辑。

- [ ] **Step 5：运行 GREEN**

```powershell
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateModelResourceManagerTests|FullyQualifiedName~JsonRecipeRepository"
```

Expected: 生命周期测试通过；任何失败都不产生半成品配方 JSON。

- [ ] **Step 6：提交资源生命周期**

```powershell
git status --short
git add VisionStation.Vision/TemplateMatching/TemplateModelContracts.cs VisionStation.Vision/TemplateMatching/TemplateModelResourceManager.cs VisionStation.Infrastructure/FileTemplateModelStore.cs VisionStation.Application/Recipes/IRecipeTemplateLifecycleService.cs VisionStation.Application/Recipes/RecipeTemplateLifecycleService.cs VisionStation.Application.Tests/TemplateModelResourceManagerTests.cs
git commit -m "feat: 管理配方模板模型生命周期"
git push
```

## Task 8：发现、绑定并探测 HALCON 26.05 runtime

**Files:**

- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconRuntimeLocator.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconNativeLibraryBootstrapper.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconRuntimeProbe.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconExceptionClassifier.cs`
- Create: `VisionStation.Vision.Tests/HalconRuntimeLocatorTests.cs`
- Create: `VisionStation.Vision.Tests/HalconRuntimeProbeTests.cs`
- Create: `VisionStation.Vision.Tests/HalconExceptionClassifierTests.cs`
- Modify: `VisionStation.Domain/DeviceConfigurationModels.cs`
- Modify: `VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs`
- Modify: `VisionStation.Application.Tests/ProductionSettingsConfigurationTests.cs`

- [ ] **Step 1：写发现优先级和拒绝原因 RED 测试**

通过 fake environment、fake Registry64 uninstall reader、fake file inspector 覆盖：

```csharp
[Fact]
public void LocateUsesEnvironmentThenDeviceConfigThenRegistry()
{
    var result = _locator.Locate(new HalconRuntimeConfiguration { RuntimeRoot = @"D:\configured" });

    Assert.Equal(@"D:\environment", result.RuntimeRoot);
    Assert.Equal(HalconRuntimeSource.Environment, result.Source);
    Assert.Equal("x64-win64", result.Architecture);
}
```

环境候选不完整时记录拒绝原因并继续配置；配置不完整再枚举 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` 的 Registry64 项，清理 InstallLocation 外层引号。验证进程 x64、`bin\x64-win64\halcon.dll`、PE AMD64 和文件版本精确 `26.05.0.0`。

- [ ] **Step 2：写异常分类与配置归一化 RED 测试**

锁定：9400 → `MATCH_TIMEOUT`；2003..2384 → `LICENSE_UNAVAILABLE`；缺 DLL、架构、版本分别映射；其他 HalconException → `MATCH_OPERATOR_FAILED`。旧 devices.json 缺 `SystemSettings.Halcon` 后得到空 RuntimeRoot，不写死本机路径。

- [ ] **Step 3：运行 RED**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconRuntime|FullyQualifiedName~HalconExceptionClassifier"
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~ProductionSettingsConfigurationTests
```

Expected: runtime 类型和配置节点不存在。

- [ ] **Step 4：实现配置和 locator**

```csharp
public sealed record HalconRuntimeConfiguration
{
    public string RuntimeRoot { get; init; } = string.Empty;
}
```

固定候选约束为 managed package `26050.0.0`、managed assembly `26050.0.0.0`、native/file/system `26.05.0.0`、arch `x64-win64`。只修改当前进程 `HALCONROOT/HALCONARCH`，不得修改机器环境。

- [ ] **Step 5：实现一次性 native bootstrap**

在任何 HALCON operator/type 初始化前调用 `NativeLibrary.SetDllImportResolver(typeof(HSystem).Assembly, ResolveHalconImport)`，只将 P/Invoke 名 `halcon` 映射到验证后的绝对 DLL；Windows loader 使用 `LoadLibraryExW` 与 `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR` 让同目录传递依赖可解析。root 成功固定后，第二个不同 root 返回稳定配置诊断。随后调用：

```csharp
HalconAPI.DoLicenseError(false);
```

以关闭 native 许可弹窗/终止行为并改为异常；生产页面不得弹框。

- [ ] **Step 6：实现版本与许可 probe**

probe 先调用不依赖许可的：

```csharp
var runtimeVersion = HSystem.GetSystemInfo("file_version").S;
```

再执行一个微型但有效的 scaled-shape create/dispose smoke，真正验证 Matching 许可；不能只看 `current_license_info`。所有 HALCON 类型都留在 `Halcon` 目录内部。真实签名在 Task 11 使用。

- [ ] **Step 7：运行 GREEN**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconRuntime|FullyQualifiedName~HalconExceptionClassifier"
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~ProductionSettingsConfigurationTests
```

Expected: 默认测试完全使用 fake native boundary，不要求本机许可；所有稳定错误码通过。

- [ ] **Step 8：提交 runtime 边界**

```powershell
git status --short
git add VisionStation.Domain/DeviceConfigurationModels.cs VisionStation.Infrastructure/JsonDeviceConfigurationRepository.cs VisionStation.Vision/TemplateMatching/Halcon/HalconRuntimeLocator.cs VisionStation.Vision/TemplateMatching/Halcon/HalconNativeLibraryBootstrapper.cs VisionStation.Vision/TemplateMatching/Halcon/HalconRuntimeProbe.cs VisionStation.Vision/TemplateMatching/Halcon/HalconExceptionClassifier.cs VisionStation.Vision.Tests/HalconRuntimeLocatorTests.cs VisionStation.Vision.Tests/HalconRuntimeProbeTests.cs VisionStation.Vision.Tests/HalconExceptionClassifierTests.cs VisionStation.Application.Tests/ProductionSettingsConfigurationTests.cs
git commit -m "feat: 探测 HALCON 运行时与许可"
git push
```

## Task 9：实现模型 cache、lease、并发 gate 与安全关闭

**Files:**

- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateModelCache.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateModelLease.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/IHalconModelLoader.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconOperationScheduler.cs`
- Create: `VisionStation.Vision.Tests/HalconTemplateModelCacheTests.cs`
- Create: `VisionStation.Vision.Tests/HalconOperationSchedulerTests.cs`
- Modify: `VisionStation.Vision/TemplateMatching/TemplateModelResourceManager.cs`
- Modify: `VisionStation.Vision/TemplateMatching/TemplateMatchingService.cs`

- [ ] **Step 1：写 cache 生命周期与并发 RED 测试**

使用 disposable sentinel handle，不触发 HalconDotNet：

```csharp
[Fact]
public async Task RetiredEntryWaitsForLastLeaseBeforeDisposal()
{
    await using var first = await _cache.AcquireAsync(Key("a", "model-v1", "meta-v1"), default);

    await _cache.RetireOwnerAsync(Owner, default);
    Assert.False(first.Handle.IsDisposed);

    await first.DisposeAsync();
    Assert.True(first.Handle.IsDisposed);
}
```

覆盖：同 key 并发只 Load 一次；同模型 operation gate 串行；不同模型可并行；新 checksum 创建新 entry 并 retire 旧 entry；faulted load 从字典移除可重试；等待 gate 可取消；DisposeAsync 停止新 acquire、等待在途 lease、每 handle/gate 只释放一次。

Scheduler 测试还要锁定：同步 native delegate 不在调用/UI 线程执行；默认两个专用 worker 时最大并发恰为 2；排队任务取消后不进入 delegate；关闭后拒收新任务并等待正在执行的 delegate 安全返回；worker 异常只完成对应 Task，不杀死后续 worker。

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconTemplateModelCacheTests|FullyQualifiedName~HalconOperationSchedulerTests"
```

Expected: cache/lease 不存在。

- [ ] **Step 3：实现不可变 key 与引用计数**

```csharp
internal sealed record HalconTemplateModelCacheKey(
    string AbsoluteModelPath,
    string ModelSha256,
    string MetadataSha256);
```

`AcquireAsync(owner, key, resolvedMetadata, token)` 以 key 查表，同时维护 owner → active key 索引。entry 持有 owner、一次性 `Task<IHalconModelHandle>`、`SemaphoreSlim(1,1)`、refCount、retired；新 key 必须先完整 load 成功，再原子替换 owner 的 active key 并 retire 旧 entry。所有状态变化在私有锁内，真正 Dispose 在锁外执行。lease 只暴露中立 handle interface 和 `EnterOperationAsync`，上层不能绕过 per-model gate。

- [ ] **Step 4：实现有限并发专用 native worker**

```csharp
internal interface IHalconOperationScheduler : IAsyncDisposable
{
    Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken);
}
```

实现使用容量 64 的 bounded `Channel<IHalconWorkItem>` 和两个 `TaskCreationOptions.LongRunning` worker；每个 worker 用同步 reader loop 保持同一专用线程，不把读取后的 native delegate 漂移到普通线程池。调用方 token 只取消尚未执行的排队项，已经进入 native delegate 后等待其安全返回。worker 内完成 `TaskCompletionSource` 时使用 `RunContinuationsAsynchronously`。学习、匹配和许可 smoke 后续都通过 scheduler；UI/ViewModel 不自行 Task.Run。

- [ ] **Step 5：接入 retire 与 service dispose**

cache 实现 `ITemplateModelRetirementSink`；resource manager retire owner 时标记全部 generation。matching service 关闭顺序：拒收新请求 → 等待 backend 在途调用 → dispose HALCON backend/cache → dispose 其他 backend；禁止依赖 finalizer。

- [ ] **Step 6：运行 GREEN**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconTemplateModelCacheTests|FullyQualifiedName~HalconOperationSchedulerTests|FullyQualifiedName~TemplateMatchingServiceTests"
```

Expected: 并发/retire/取消/关闭测试稳定重复通过。

- [ ] **Step 7：提交 cache 与 scheduler**

```powershell
git status --short
git add VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateModelCache.cs VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateModelLease.cs VisionStation.Vision/TemplateMatching/Halcon/IHalconModelLoader.cs VisionStation.Vision/TemplateMatching/Halcon/HalconOperationScheduler.cs VisionStation.Vision/TemplateMatching/TemplateModelResourceManager.cs VisionStation.Vision/TemplateMatching/TemplateMatchingService.cs VisionStation.Vision.Tests/HalconTemplateModelCacheTests.cs VisionStation.Vision.Tests/HalconOperationSchedulerTests.cs
git commit -m "feat: 管理 HALCON 模型缓存与调度"
git push
```

## Task 10：提取模板特征并实现六项独立硬门

**Files:**

- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateModelMetadata.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateFeatureExtractor.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/TemplateModelGenerationFingerprint.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/TemplateCandidateEvidenceBuilder.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/TemplateCandidateValidator.cs`
- Create: `VisionStation.Vision.Tests/HalconTemplateFeatureExtractorTests.cs`
- Create: `VisionStation.Vision.Tests/TemplateModelGenerationFingerprintTests.cs`
- Create: `VisionStation.Vision.Tests/TemplateCandidateEvidenceBuilderTests.cs`
- Create: `VisionStation.Vision.Tests/TemplateCandidateValidatorTests.cs`

- [ ] **Step 1：写学习质量 RED 测试**

用确定性合成暗产品/亮背景，覆盖完整非对称长条正例、产品触碰 ROI、对比度不足、内部组不足：

```csharp
[Fact]
public void LearningRejectsTemplateThatTouchesAnyRoiBoundary()
{
    var result = _extractor.Extract(SyntheticTemplate(touchesBoundary: true), TemplateRoi);

    Assert.False(result.Success);
    Assert.Equal("MODEL_TEMPLATE_INCOMPLETE", result.Diagnostic.Code);
}

[Fact]
public void LearningPersistsSeparateOuterAndDistributedInnerGroups()
{
    var result = _extractor.Extract(SyntheticTemplate(), TemplateRoi);

    Assert.True(result.Success, result.Diagnostic.Message);
    Assert.True(result.Metadata.OuterContour.Count >= 100);
    Assert.True(result.Metadata.InnerFeatureGroups.Count >= 3);
    Assert.DoesNotContain(result.Metadata.InnerFeatureGroups.SelectMany(x => x),
        point => result.Metadata.IsOuterBand(point));
}
```

- [ ] **Step 2：写六硬门与确定性去重 RED 测试**

每个测试只破坏一个条件并锁定首个拒绝码：NaN/Scale/原点越界 → `MATCH_INVALID_POSE`；模板角/轮廓越界 → `MATCH_INCOMPLETE_AT_BOUNDARY`；暗外亮内反转 → `MATCH_POLARITY_MISMATCH`；outer coverage 或 P95 → `MATCH_OUTER_CONTOUR_WEAK`；总体 inner 或有效组数 → `MATCH_INNER_FEATURES_WEAK`；填充支持区域 IoU → `MATCH_DUPLICATE_OVERLAP`。

```csharp
[Fact]
public void DuplicateGateUsesFilledSupportRegionNotBoundingBox()
{
    var accepted = _validator.ValidateAndDeduplicate(
        [Candidate("high", .95), Candidate("neighbor", .90)],
        EvidenceWithOverlappingBoxesButDisjointSupportMasks(),
        StrictParameters);

    Assert.Equal(2, accepted.Count);
}
```

再断言排序为 Score 降序、OuterCoverage 降序、X/Y 升序；候选粗 overlap 不能代替最终 IoU。

指纹测试逐个改变 angleStart/angleExtent/scaleMin/scaleMax/numLevels 都必须改变 hash；只改变 score/coverage/edge/polarity/overlap/greediness/subPixel/candidateLimit/timeout 不得改变 hash。`auto` 先由 catalog 归一化为整数 0，避免字符串形式产生伪差异。

- [ ] **Step 3：运行 RED**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconTemplateFeatureExtractorTests|FullyQualifiedName~TemplateModelGenerationFingerprintTests|FullyQualifiedName~TemplateCandidateEvidenceBuilderTests|FullyQualifiedName~TemplateCandidateValidatorTests"
```

Expected: extractor/evidence/validator 不存在。

- [ ] **Step 4：实现中立 metadata 和特征提取**

metadata 保存 schema/engine/format、完整 owner、生成版本、模板尺寸/原点、角度/尺度、暗前景、外轮廓采样点、多个内部组、最低有效组数、填充支持区域、不可变模型参数、`generationParameterFingerprint` 和仅审计 `validationDefaultsAtLearn`。运行门限不得进入 cache metadata 的判定键。

指纹只吃 catalog 解析后的生成参数，并用 invariant round-trip 数字形成 canonical UTF-8 后 SHA-256：

```csharp
var canonical = string.Join('\n',
    parameters.AngleStartDeg.ToString("R", CultureInfo.InvariantCulture),
    parameters.AngleExtentDeg.ToString("R", CultureInfo.InvariantCulture),
    parameters.ScaleMin.ToString("R", CultureInfo.InvariantCulture),
    parameters.ScaleMax.ToString("R", CultureInfo.InvariantCulture),
    parameters.NumLevels.ToString(CultureInfo.InvariantCulture));
return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
    .ToLowerInvariant();
```

版本常量固定为 `schemaVersion=1`、`engine=Halcon`、`modelFormat=halcon-scaled-shape`、`modelVersion=halcon-scaled-shape-v1`、`managedPackageVersion=26050.0.0`、`managedAssemblyVersion=26050.0.0.0`、`nativeRuntimeVersion=26.05.0.0`；任一精确版本不符都在加载 native handle 前返回 `MODEL_VERSION_MISMATCH` 或 `RUNTIME_VERSION_MISMATCH`。

特征提取使用 OpenCvSharp 的边框背景估计、暗前景二值化、与参考中心关联的主连通域、形态学去噪、闭合外轮廓等距采样和向内腐蚀支持域。内部边缘先由梯度非极大值抑制定位，再沿法线以相邻三个梯度样本做二次插值获得亚像素坐标，最后按连通性与空间分布分组。学习至少要求 3 个内部组，metadata 的 `minimumValidInnerGroupCount` 固定为 `max(2, ceil(groupCount * 0.67))`。产品边界、对比度、点数或空间分组不足均 fail closed。

- [ ] **Step 5：实现 evidence builder**

对当前 Gray8 图只构建一次边缘距离图；每候选通过 `PoseSimilarityTransform` 变换 outer/inner/support geometry，采样距离、P95、内外法线灰度并生成原图像素网格的填充支持 mask。P95 使用 `sorted[ceil(0.95 * count) - 1]` 的 nearest-rank 定义；outer/inner coverage 的分母始终是对应完整学习点数。每个内部组用与总体相同的 `innerCoverageMin` 判断是否有效，再与 metadata 最低组数比较。极性沿有向外轮廓法线在内外各 3 个学习像素（随候选 Scale 等比缩放）采样，要求暗内亮外；边界安全边距取 `ceil(edgeTolerancePx)`。非零 search ROI 的偏移只在候选源出口加一次；evidence builder 接收的 Pose 已是原图坐标。

- [ ] **Step 6：实现 validator 固定顺序**

```csharp
foreach (var gate in new ITemplateCandidateGate[]
{
    _poseGate,
    _boundaryGate,
    _polarityGate,
    _outerContourGate,
    _innerFeatureGate
})
{
    var failure = gate.Validate(candidate, evidence, parameters);
    if (failure is not null) return Rejected(candidate, failure);
}
```

通过前五门的候选排序后执行 support mask IoU 去重。不得计算加权总分；原始 HALCON Score 原样保留。

- [ ] **Step 7：运行 GREEN 并验证热门限**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconTemplateFeatureExtractorTests|FullyQualifiedName~TemplateModelGenerationFingerprintTests|FullyQualifiedName~TemplateCandidateEvidenceBuilderTests|FullyQualifiedName~TemplateCandidateValidatorTests"
```

Expected: 六个失败码分别通过；同一 metadata 下改变当前 outer/inner/edge/polarity/overlap 参数会立即改变纯验证结果。operator timeout 的热改由 Task 12 backend 测试证明，因为纯 validator 不消费 native timeout。

- [ ] **Step 8：提交纯判定核心**

```powershell
git status --short
git add VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateModelMetadata.cs VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateFeatureExtractor.cs VisionStation.Vision/TemplateMatching/Halcon/TemplateModelGenerationFingerprint.cs VisionStation.Vision/TemplateMatching/Halcon/TemplateCandidateEvidenceBuilder.cs VisionStation.Vision/TemplateMatching/Halcon/TemplateCandidateValidator.cs VisionStation.Vision.Tests/HalconTemplateFeatureExtractorTests.cs VisionStation.Vision.Tests/TemplateModelGenerationFingerprintTests.cs VisionStation.Vision.Tests/TemplateCandidateEvidenceBuilderTests.cs VisionStation.Vision.Tests/TemplateCandidateValidatorTests.cs
git commit -m "feat: 实现 HALCON 候选六项硬门"
git push
```

## Task 11：封装真实 HalconDotNet operator 并完成学习持久化

**Files:**

- Create: `VisionStation.Vision/TemplateMatching/Halcon/IHalconOperatorBackend.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconDotNetOperatorBackend.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconImageFactory.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconModelMetadataValidator.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconModelLoader.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateLearner.cs`
- Create: `VisionStation.Vision.Tests/HalconImageConversionTests.cs`
- Create: `VisionStation.Vision.Tests/HalconTemplateLearnerTests.cs`
- Create: `VisionStation.Vision.Tests/HalconModelLoaderTests.cs`
- Modify: `VisionStation.Vision/TemplateMatching/Halcon/IHalconModelLoader.cs`
- Modify: `VisionStation.Vision/TemplateMatching/Halcon/HalconRuntimeProbe.cs`

- [ ] **Step 1：写图像复制、模型参数和失败不激活 RED 测试**

```csharp
[Theory]
[InlineData(PixelFormatKind.Gray8)]
[InlineData(PixelFormatKind.Bgr24)]
[InlineData(PixelFormatKind.Bgra32)]
public void ImageConversionProducesTightGray8WithoutReadingStridePadding(PixelFormatKind format)
{
    var buffer = HalconImageFactory.CreateTightGray8(FrameWithPadding(format));

    Assert.Equal(buffer.Width * buffer.Height, buffer.Pixels.Length);
    Assert.Equal(ExpectedGrayPixels, buffer.Pixels);
}

[Fact]
public async Task FailedOrCancelledLearningNeverReturnsNewModelParameters()
{
    _operators.CreateModelFailure = new HalconOperatorFailure("MODEL_LOAD_FAILED");

    var result = await _learner.LearnAsync(ValidRequest(), default);

    Assert.False(result.Success);
    Assert.Empty(result.Parameters);
    Assert.True(await _store.CanStillResolveAsync(OldReference));
}
```

fake operator 记录：模型 domain 是 outer+inner support 受控并集、Metric=`use_polarity`、角度/尺度/numLevels 来自解析后的最终值、origin 是 ROI 中心相对 model domain centroid 的偏移、写入路径只能是 store session staging path。另测试写后回读/校验失败不返回 reference。

`HalconModelLoaderTests` 使用 recording operator 锁定两段式边界：store 已完成路径/checksum/owner 防护后，纯 `HalconModelMetadataValidator.Validate(resolved, modelState, currentParameters)` 校验 metadata JSON 的 schema、engine、format、完整 owner、model version、managed/native runtime version、recipe standard/template geometry = metadata geometry，以及 recipe fingerprint = metadata fingerprint = 当前生成参数 fingerprint；几何不符返回 `MODEL_METADATA_INVALID`，fingerprint 不符返回 `MODEL_RELEARN_REQUIRED`。每种不符断言 runtime probe/native load count 都为 0。只有得到 `ValidatedHalconModelDescriptor` 后，loader 才能在 scheduler 内 `new HShapeModel`；合法 metadata 但损坏 `.shm` 映射 `MODEL_LOAD_FAILED`，不把原生异常正文暴露给操作员。

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconImageConversionTests|FullyQualifiedName~HalconTemplateLearnerTests|FullyQualifiedName~HalconModelLoaderTests"
```

Expected: native boundary/learner 不存在。

- [ ] **Step 3：实现唯一 HALCON 图像边界**

先把 ImageFrame/Gray Mat 转成紧密 Gray8；pin 只跨越 HImage 构造调用：

```csharp
var handle = GCHandle.Alloc(gray.Pixels, GCHandleType.Pinned);
try
{
    return new HImage("byte", gray.Width, gray.Height, handle.AddrOfPinnedObject());
}
finally
{
    handle.Free();
}
```

`GenImage1`/上述构造会复制像素；不使用 `GenImage1Extern`，不让 HALCON 图像引用托管 buffer。所有 HImage/HRegion/HXLDCont/HTuple 在内部作用域确定 Dispose。

- [ ] **Step 4：按已核实签名封装模型创建**

```csharp
model.CreateScaledShapeModel(
    templateImage,
    parameters.NumLevels == 0 ? new HTuple("auto") : new HTuple(parameters.NumLevels),
    DegreesToRadians(parameters.AngleStartDeg),
    DegreesToRadians(parameters.AngleExtentDeg),
    new HTuple("auto"),
    parameters.ScaleMin,
    parameters.ScaleMax,
    new HTuple("auto"),
    new HTuple("auto"),
    "use_polarity",
    new HTuple("auto"),
    new HTuple("auto"));

model.SetShapeModelParam("border_shape_models", "false");
model.SetShapeModelOrigin(
    metadata.ReferenceRow - metadata.ModelDomainCentroidRow,
    metadata.ReferenceColumn - metadata.ModelDomainCentroidColumn);
```

HALCON origin 参数是相对模型图像 domain 重心的 Row/Column 偏移；`ReferenceRow/Column` 固定为模板 ROI 局部中心。生产统一 `Dispose` HShapeModel，不对外暴露 `ClearShapeModel`。

catalog 把 `halcon.numLevels=auto` 规范化为 `NumLevels=0`，正整数保持原值；同一解析值同时进入创建和 Task 12 查找。numLevels 属于模型生成参数，修改后必须重新学习。

- [ ] **Step 5：实现学习编排和持久化参数**

顺序固定：请求/owner/参数严格解析 → runtime/license probe → 特征提取质量门 → store BeginWrite → scheduler 内 create model → `WriteShapeModel(session.StagingModelPath)` → scheduler 内 `new HShapeModel(stagingPath)` 回读并读取 level-1 contour → 写入 immutable 生成参数及其 fingerprint 的 metadata JSON → store Commit → 返回参数。参数至少包含：

```text
engine=Halcon
halcon.modelFormat=halcon-scaled-shape
halcon.modelPath/halcon.modelMetadataPath
halcon.modelVersion/halcon.modelRuntimeVersion/halcon.modelGeneration
halcon.modelChecksum/halcon.metadataChecksum
halcon.generationParameterFingerprint
halcon.standardX/halcon.standardY/halcon.standardAngle/halcon.standardScale
halcon.templateWidth/halcon.templateHeight
```

preview PNG/中立 contours 作为学习 result 返回，不是 runtime 算法输入。

`HalconModelMetadataValidator` 不引用 HalconDotNet，固定执行 metadata 解析，并将 `modelState` 中的 reference、owner、standard/template geometry、schema、engine、format、全部精确版本及 fingerprint 与 metadata/当前生成参数逐项交叉验证，返回 immutable descriptor；backend 在 runtime probe 前调用它。`HalconModelLoader.LoadAsync(descriptor)` 只负责 scheduler 内构造 `HShapeModel`、读取 level-1 contour 做健康检查并返回可缓存 handle。读模型或健康检查的 HALCON 异常统一映射 `MODEL_LOAD_FAILED` 并 Dispose 半构造资源。

- [ ] **Step 6：让 runtime 许可 smoke 复用同一 operator seam**

微型 smoke 创建带足够非对称边缘的有效图与 shape model 后立即 Dispose；许可范围 2003..2384 映射 `LICENSE_UNAVAILABLE`，模型点不足等非许可错误不得误报许可成功。

- [ ] **Step 7：运行 GREEN**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconImageConversionTests|FullyQualifiedName~HalconTemplateLearnerTests|FullyQualifiedName~HalconModelLoaderTests|FullyQualifiedName~HalconRuntimeProbeTests"
```

Expected: 无许可 CI 通过 fake operator；旧 reference 在每个失败/取消路径仍可解析。

- [ ] **Step 8：提交 HALCON 学习链路**

```powershell
git status --short
git add VisionStation.Vision/TemplateMatching/Halcon/IHalconOperatorBackend.cs VisionStation.Vision/TemplateMatching/Halcon/HalconDotNetOperatorBackend.cs VisionStation.Vision/TemplateMatching/Halcon/HalconImageFactory.cs VisionStation.Vision/TemplateMatching/Halcon/HalconModelMetadataValidator.cs VisionStation.Vision/TemplateMatching/Halcon/HalconModelLoader.cs VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateLearner.cs VisionStation.Vision/TemplateMatching/Halcon/IHalconModelLoader.cs VisionStation.Vision/TemplateMatching/Halcon/HalconRuntimeProbe.cs VisionStation.Vision.Tests/HalconImageConversionTests.cs VisionStation.Vision.Tests/HalconTemplateLearnerTests.cs VisionStation.Vision.Tests/HalconModelLoaderTests.cs
git commit -m "feat: 实现 HALCON 模型学习持久化"
git push
```

## Task 12：实现 HALCON 候选源、批匹配和精确多目标计数

**Files:**

- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconScaledShapeCandidateSource.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/IHalconCandidateSource.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconPoseConverter.cs`
- Create: `VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateMatchingBackend.cs`
- Create: `VisionStation.Vision/TemplateMatching/HalconTemplateMatchingFactory.cs`
- Create: `VisionStation.Client/Services/TemplateMatchingComposition.cs`
- Create: `VisionStation.Client/Properties/AssemblyInfo.cs`
- Create: `VisionStation.Vision.Tests/HalconScaledShapeCandidateSourceTests.cs`
- Create: `VisionStation.Vision.Tests/HalconTemplateMatchingBackendTests.cs`
- Modify: `VisionStation.Vision/TemplateMatching/Halcon/HalconDotNetOperatorBackend.cs`
- Modify: `VisionStation.Vision/TemplateMatching/TemplateMatchingService.cs`
- Modify: `VisionStation.Client/App.xaml.cs`

- [ ] **Step 1：写 ROI 偏移、角度/尺度和 timeout RED 测试**

fake native 返回局部 `Row=40, Column=30, Angle=35°rad, Scale=1.1`，search bounds `(X=100,Y=200)`：

```csharp
[Fact]
public async Task CandidateSourceAddsSearchOffsetExactlyOnce()
{
    _operators.FindResult = NativeCandidate(row: 40, column: 30, angleDeg: 35, scale: 1.1);

    var batch = await _source.FindAsync(Request(searchX: 100, searchY: 200), default);
    var candidate = Assert.Single(batch.Candidates);

    Assert.Equal((130d, 240d, 35d, 1.1),
        (candidate.Pose.X, candidate.Pose.Y, candidate.Pose.Angle, candidate.Pose.Scale));
    Assert.False(batch.LimitReached);
}
```

另覆盖圆/旋转矩形/多边形 ROI 的局部 domain mask；35°/-135° 规范化；`numLevels=0`、具体 candidateLimit、candidateMaxOverlap；timeout 9400 返回 `MATCH_TIMEOUT`；用户 token 在 native 调用期间取消，native 安全返回后抛 OCE；第一期不调用 InterruptOperator。

- [ ] **Step 2：写单/多共享核心和 exact-count RED 测试**

同一 fake candidate source + validator 下：Single 最多一个；ExactCount=3 的 3 个接受为 OK，2 个/4 个均 NG；NG 保留接受候选诊断但 `HasMatch=false`；所有候选被门拒绝为零接受。candidate source 返回 `HalconCandidateBatch(Candidates, LimitReached)`：raw tuple 数恰好等于 candidateLimit 时 `LimitReached=true`；ExactCount 若尚未接受到 `expectedCount+1` 就必须返回 `MATCH_CANDIDATE_LIMIT_REACHED` NG，不能把截断集合判成精确数量，已明确接受过多则仍返回数量过多诊断。

再锁定前置顺序：非法参数/模式不调用 store 或 probe；路径/checksum/metadata/fingerprint 错误完成 store resolve 后仍不调用 probe/native；完整 reference 才 probe，probe 失败不进入 cache loader/candidate source；首次并发合法 Halcon Learn/Match 共享一次 probe，后续复用；OpenCv/Managed probe 计数始终为零。同一已缓存模型连续请求 5000/7000 timeout，recording operator 必须分别收到新值且无需重载模型，证明 timeout 是热参数。

- [ ] **Step 3：运行 RED**

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconScaledShapeCandidateSourceTests|FullyQualifiedName~HalconTemplateMatchingBackendTests"
```

Expected: candidate source/backend 不存在。

- [ ] **Step 4：按真实签名封装 find_scaled_shape_model**

在已获取的 per-model operation gate 内，每次请求先设置持久模型 timeout：

```csharp
model.SetShapeModelParam("timeout", parameters.OperatorTimeoutMs);
model.FindScaledShapeModel(
    searchImage,
    DegreesToRadians(parameters.AngleStartDeg),
    DegreesToRadians(parameters.AngleExtentDeg),
    parameters.ScaleMin,
    parameters.ScaleMax,
    parameters.CandidateMinScore,
    parameters.CandidateLimit,
    parameters.CandidateMaxOverlap,
    parameters.SubPixel,
    parameters.NumLevels,
    parameters.Greediness,
    out var rows,
    out var columns,
    out var angles,
    out var scales,
    out var scores);
```

model timeout 单位毫秒且设置/调用都在同一模型锁中；清理可能延长返回时间，因此不是墙钟硬上限。调用前后检查 token；不注册 InterruptOperator；异常 9400 → `MATCH_TIMEOUT` NG，native 返回后 token 已取消 → OCE。

- [ ] **Step 5：实现局部图与唯一坐标出口**

先把搜索 ROI 轴对齐 bounds 裁为局部图，非矩形 ROI 在局部坐标生成 domain mask。HALCON Row/Column 只在 `HalconPoseConverter` 出口加一次 search `(Y/X)`；角度弧度转当前系统图像坐标的顺时针度数并规范到 `[-180,180]`；Scale 原样进入 `Pose.Scale`。

- [ ] **Step 6：编排 resolve → lease → find → evidence → validate → deduplicate**

Match 固定执行 `resolve engine/mode → ParseHalcon → ReadHalcon modelState → store.Resolve(modelState.Reference 的 path/checksum/owner) → metadata validator(reference/geometry/version/fingerprint) → HalconRuntimeProbe.EnsureReadyAsync → cache lease/load → find/evidence/validate`。配置、路径、metadata 与 `MODEL_RELEARN_REQUIRED` 都不能被 runtime/license 错误掩盖。Learn 在输入/owner/参数解析成功后才 probe。probe 用线程安全的一次性 Task 合并并发首次合法调用，预期失败作为稳定诊断返回。cache 只缓存 immutable handle/metadata。`IHalconCandidateSource` 是 fake 与真实 native 候选源的内部 seam，并显式报告是否命中候选上限。候选按 HALCON Score 进入六硬门，最终 deterministic IoU 去重。Single 取第一个接受；ExactCount 继续到候选耗尽、确认多出一件或发现上限饱和；未能证明候选已耗尽时绝不返回精确 OK。

lease 的 per-model gate 在进入 scheduler 前异步获取，真实 create/find/read/write/smoke delegate 全部由 `IHalconOperationScheduler.RunAsync` 执行；这样同模型串行、不同模型最多两个并行，同时不阻塞 UI 调用线程。

- [ ] **Step 7：注册完整 composition 并运行 GREEN**

HALCON 具体类继续保持 internal，Client 不获得 `InternalsVisibleTo`。Vision 暴露窄的 `HalconTemplateMatchingFactory.Create(ITemplateModelStore store, HalconRuntimeConfiguration configuration, ITemplateMatchingDiagnosticSink log)`；同文件定义公共中立的 `ITemplateMatchingDiagnosticSink` 与只暴露 `Service/Resources` 的 `TemplateMatchingRuntime`。factory 在程序集内部组装 runtime probe、scheduler、loader、cache、Halcon/OpenCv/Managed backends、service 和 resource manager。这既保护 HALCON 边界，也给二次开发保留可替换 store/configuration/log 的稳定工厂。

Client 层 `internal sealed TemplateMatchingComposition` 只创建 `FileTemplateModelStore`、适配配置/日志并调用该公共 factory，绝不直接 `new` internal HALCON 类型。它只向 App 暴露中立的 Service/Store/Resources；`VisionStation.Client/Properties/AssemblyInfo.cs` 增加 `[assembly: InternalsVisibleTo("VisionStation.Vision.UI.Tests")]` 供 composition root 测试，公共二次开发入口仍只有 Vision 的 factory。App 用它替换 Task 5 的 LegacyOnly service，并注册同一实例的 `ITemplateMatchingService`、`ITemplateModelStore`、`ITemplateModelResourceManager`；`IRecipeTemplateLifecycleService` 延迟到 Task 17 注册。此时 UI 默认仍 OpenCv，但用户显式 Halcon 已能走完整 backend。

```powershell
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~HalconScaledShapeCandidateSourceTests|FullyQualifiedName~HalconTemplateMatchingBackendTests|FullyQualifiedName~TemplateMatchingServiceTests"
dotnet build VisionStation.Client\VisionStation.Client.csproj -c Release -p:Platform=x64 --no-restore
```

Expected: Halcon engine 精确路由新 backend；OpenCV/Managed 回归仍通过；无许可测试不调用真实 operator。

- [ ] **Step 8：提交批匹配核心**

```powershell
git status --short
git add VisionStation.Vision/TemplateMatching/Halcon/HalconScaledShapeCandidateSource.cs VisionStation.Vision/TemplateMatching/Halcon/IHalconCandidateSource.cs VisionStation.Vision/TemplateMatching/Halcon/HalconPoseConverter.cs VisionStation.Vision/TemplateMatching/Halcon/HalconTemplateMatchingBackend.cs VisionStation.Vision/TemplateMatching/Halcon/HalconDotNetOperatorBackend.cs VisionStation.Vision/TemplateMatching/HalconTemplateMatchingFactory.cs VisionStation.Vision/TemplateMatching/TemplateMatchingService.cs VisionStation.Client/Services/TemplateMatchingComposition.cs VisionStation.Client/Properties/AssemblyInfo.cs VisionStation.Client/App.xaml.cs VisionStation.Vision.Tests/HalconScaledShapeCandidateSourceTests.cs VisionStation.Vision.Tests/HalconTemplateMatchingBackendTests.cs
git commit -m "feat: 实现 HALCON 单多目标批匹配"
git push
```

## Task 13：发布 Scale、matchesV2 与统一结果解析

**Files:**

- Create: `VisionStation.Vision.UI/Services/MultiTargetMatchResultReader.cs`
- Create: `VisionStation.Vision.UI.Tests/MultiTargetMatchResultReaderTests.cs`
- Modify: `VisionStation.Vision/Tools/TemplateLocateTool.cs`
- Modify: `VisionStation.Vision/Tools/MultiTargetMatchTool.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/VisionToolCatalog.cs`
- Modify: `VisionStation.Application/Inspection/VisionPortValueFormatter.cs`
- Modify: `VisionStation.Application.Tests/VisionPortValueFormatterTests.cs`
- Modify: `VisionStation.Vision.UI/Services/VisionOverlayBuilder.cs`
- Modify: `VisionStation.Vision.UI/Services/TemplateLocateOverlayFactory.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/VisionDebugViewModel.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/UiModels.cs`
- Modify: `VisionStation.Vision.UI.Tests/VisionToolCatalogTests.cs`
- Modify: `VisionStation.Vision.UI.Tests/TemplateLocateOverlayFactoryTests.cs`
- Modify: `VisionStation.Vision.Tests/TemplateMatchingToolPortSafetyTests.cs`

- [ ] **Step 1：写 ToolResult schema 和 Scale 同源 RED 测试**

```csharp
[Fact]
public async Task MultiV2AndEveryPortUseCandidatePoseScale()
{
    var pose = new Pose2D(12, 34, 56) { Scale = 1.1 };
    var result = await RunMultiOkAsync(BatchCandidate(pose));
    var json = JsonDocument.Parse(result.Data["matchesV2"]);

    Assert.Equal("2", result.Data["matchSchemaVersion"]);
    Assert.Equal(1.1, json.RootElement[0].GetProperty("scale").GetDouble());
    Assert.Equal("1.1", result.Data["scales"]);
    Assert.Equal(1.1, Port<Pose2D>("BestPositionOutput").Scale);
    Assert.Equal(1.1, Assert.Single(Port<double[]>("ScalesOutput")));
}

[Fact]
public async Task SingleScaleDataPoseAndPortHaveOneSourceOfTruth()
{
    var pose = new Pose2D(12, 34, 56) { Scale = 1.1 };
    var result = await RunSingleOkAsync(BatchCandidate(pose));

    Assert.Equal("1.1", result.Data["scale"]);
    Assert.Equal(1.1, Port<Pose2D>("PositionOutput").Scale);
    Assert.Equal(1.1, Port<double>("ScaleOutput"));
}
```

单目标 HALCON hasMatch=false 时断言不存在 x/y/angle/scale 主字段，只存在 `failureCode/failureStage` 和可选 `rejectedCandidate.*`。单目标 OK 必须把 `outerCoverage/innerCoverage/edgeDistanceP95Px/polarityAgreement` 四指标按 InvariantCulture 写入 Data；缺 engine 与小写 engine 仍分别记录规范化的 `OpenCv/Halcon`。另以 35°、Scale=1.1 的非方形模板断言 `MatchX/MatchY` 与 overlay fallback rectangle 都使用完整变换 ROI 的 AABB。旧 8 列 `matches` 必须字节级保持原列序。

- [ ] **Step 2：写 v2-first/legacy-fallback RED 测试**

reader 覆盖：合法 matchesV2 优先于冲突 legacy；缺 v2 回退 8 列；malformed JSON、NaN/Infinity、缺必填字段 fail closed；Scale/四指标保留。OverlayBuilder、VisionDebug 和 UI 表必须调用同一 reader，不能各留一套 parser。

- [ ] **Step 3：运行 RED**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~MultiTargetMatchResultReaderTests|FullyQualifiedName~VisionToolCatalogTests|FullyQualifiedName~TemplateLocateOverlayFactoryTests"
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~VisionPortValueFormatterTests
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~TemplateMatchingToolPortSafetyTests
```

Expected: v2 reader/Scale ports 不存在或断言失败。

- [ ] **Step 4：实现版本化序列化和诊断**

Multi 保留：

```text
matches = x,y,angle,score,width,height,shape,radius
```

另用 `System.Text.Json` invariant 序列化：

```json
[{"x":12,"y":34,"angle":56,"scale":1.1,"score":0.95,"outerCoverage":0.93,"innerCoverage":0.86,"edgeDistanceP95Px":2.2,"polarityAgreement":0.94}]
```

写 `matchSchemaVersion=2`、`scores`、`scales`。单目标同时 invariant 写四项指标；两个 Tool 的 `engine` 一律取 `TemplateMatchBatchResult.Engine.ToString()`，不得读 `definition.Parameters["engine"]`。Catalog 增加单目标 `ScaleOutput` 和多目标 `ScalesOutput`；formatter 从 Data 的 scale/scales 输出，不重新推导。

- [ ] **Step 5：统一显示和 overlay**

`MultiTargetMatchPointItem` 增加 init-only/default Scale 与指标；VisionDebug、OverlayBuilder 共用 reader。单目标优先绘制完整模板 ROI contours；仅兼容旧结果时，fallback rectangle 先绕 Pose 旋转并乘 `Pose.Scale`，再取四角 AABB，不能只放大未旋转矩形。已有 scoring contours、Cross 与红/绿 hasMatch 规则继续走 `TemplateLocateOverlayFactory`。`overlaySchemaVersion` 仍为 `2`。

- [ ] **Step 6：运行 GREEN**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~MultiTargetMatchResultReaderTests|FullyQualifiedName~VisionToolCatalogTests|FullyQualifiedName~TemplateLocateOverlayFactoryTests|FullyQualifiedName~VisionResultOverlayProjectorTests"
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~VisionPortValueFormatterTests
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~TemplateMatchingToolPortSafetyTests
```

Expected: 新旧结果均可读；所有 Scale 投影严格等于 `Pose.Scale`。

- [ ] **Step 7：提交结果契约**

```powershell
git status --short
git add VisionStation.Vision/Tools/TemplateLocateTool.cs VisionStation.Vision/Tools/MultiTargetMatchTool.cs VisionStation.Vision.UI/Services/MultiTargetMatchResultReader.cs VisionStation.Vision.UI/Services/VisionOverlayBuilder.cs VisionStation.Vision.UI/Services/TemplateLocateOverlayFactory.cs VisionStation.Vision.UI/ViewModels/VisionDebugViewModel.cs VisionStation.Vision.UI/ViewModels/VisionToolCatalog.cs VisionStation.Vision.UI/ViewModels/UiModels.cs VisionStation.Application/Inspection/VisionPortValueFormatter.cs VisionStation.Vision.UI.Tests/MultiTargetMatchResultReaderTests.cs VisionStation.Vision.UI.Tests/VisionToolCatalogTests.cs VisionStation.Vision.UI.Tests/TemplateLocateOverlayFactoryTests.cs VisionStation.Application.Tests/VisionPortValueFormatterTests.cs VisionStation.Vision.Tests/TemplateMatchingToolPortSafetyTests.cs
git commit -m "feat: 发布模板匹配尺度结果"
git push
```

## Task 14：切换新工具默认值并实现 HALCON 参数面板

**Files:**

- Create: `VisionStation.Vision.UI.Tests/TemplateLocateToolDialogXamlTests.cs`
- Modify: `VisionStation.Infrastructure/DefaultRecipeFactory.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/VisionToolCatalog.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.UI/Views/TemplateLocateToolDialog.xaml`
- Modify: `VisionStation.Vision.UI/Services/WpfToolParameterDialogService.cs`
- Modify: `VisionStation.Vision.UI.Tests/VisionToolCatalogTests.cs`
- Modify: `VisionStation.Vision.UI.Tests/TemplateLocateToolDialogViewModelTests.cs`

- [ ] **Step 1：写默认/兼容/预设 RED 测试**

```csharp
[Fact]
public void NewSingleAndMultiToolsUseExplicitHalconStrictDefaults()
{
    foreach (var kind in new[] { VisionToolKind.TemplateLocate, VisionToolKind.MultiTargetMatch })
    {
        var parameters = VisionToolCatalog.GetDefaultParameters(kind);
        Assert.Equal("Halcon", parameters["engine"]);
        Assert.Equal("0.90", parameters["halcon.scaleMin"]);
        Assert.Equal("0.90", parameters["halcon.outerCoverageMin"]);
        Assert.Equal("5000", parameters["halcon.operatorTimeoutMs"]);
    }
}
```

覆盖 DefaultRecipeFactory 与 Catalog 同源；旧 tool 缺 engine 时 VM 显示 OpenCv 且构造阶段不修改原字典，用户 Apply 后才写显式 OpenCv；旧 OpenCV -10/20 角度迁移只在规范化 OpenCv 发生；HALCON Multi 使用 expectedCount，不改变 OpenCV matchCount=maxMatches。增加 `HalconLearnAndTrialRunUseInjectedAsyncService`：稳定 Recipe/Flow/Tool ID 进入 request，命令直接 await Task 12 的 service，token 可取消且不存在 `Task.Run(() => TemplateMatcher.Match(frame, roi, parameters))`。再做两条完整往返回归：OpenCv（通用 `standard*`、`template*`、`modelPath/modelVersion/templatePixels`）→ Halcon 学习（只写完整 `halcon.*` learned state）→ OpenCv，原 OpenCV 学习状态逐键未变；Halcon → OpenCv 学习 → Halcon，完整 namespaced reference/standard/template/checksum/generation 未变且仍可解析。切换 engine 本身和学习当前 backend 都不得删除 inactive backend 参数或模型引用。

- [ ] **Step 2：写 XAML 结构 RED 测试**

断言存在 engine selector、严格/均衡/高召回、Scale min/max、outer/inner、expectedCount 条件显示、真实 Advanced Expander、edge/polarity/two-overlap/greediness/subpixel/numLevels/timeout/candidateLimit 的 binding，以及结果表 Scale 列；旧 MoreCommand 提示按钮不再承担高级配置。

- [ ] **Step 3：运行 RED**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~VisionToolCatalogTests|FullyQualifiedName~TemplateLocateToolDialogViewModelTests|FullyQualifiedName~TemplateLocateToolDialogXamlTests"
```

Expected: 默认仍 OpenCv、高级面板/属性不存在。

- [ ] **Step 4：先接入异步 service，再切换新默认**

`WpfToolParameterDialogService` 先注入 Task 12 已注册的同一 `ITemplateMatchingService` 并传给 VM；学习/试跑改成 async command，owner 只从当前 RecipeId/FlowId/ToolId 构造，直接 await service 并传播 token，不再调用静态 matcher 或 untracked `Task.Run`。完成这条可运行链后，`VisionToolCatalog` 与 `DefaultRecipeFactory` 才都调用 `TemplateMatchingParameterCatalog.CreateStrictDefaults(cardinality)`；禁止各自再写数值。这个步骤末尾才真正把新工具默认切成 `engine=Halcon`。

- [ ] **Step 5：实现强类型 VM 与三预设**

增加 `SelectedEngine/EngineOptions/IsHalconEngine/IsAdvancedParametersExpanded/SelectedPreset/RequiresRelearn` 和全部 `halcon.*` 属性。选择 preset 只复制 catalog 的具体值；任一字段手工变更后显示“自定义”。模型生成字段 `halcon.angleStartDeg/angleExtentDeg/scaleMin/scaleMax/numLevels` 变化标 `RequiresRelearn`；candidate score/coverage/edge/polarity/two-overlap/greediness/subpixel/timeout/candidateLimit 是查找或验证热参数，不要求重学。

- [ ] **Step 6：实现真实高级面板与共同校验**

Apply 与运行都先 resolve SelectedEngine，再只校验活动 backend；只有 Halcon 调用 `ParseHalcon`。非法 timeout 等显示 `CONFIG_INVALID_PARAMETER`，不能解释为关闭超时。回归锁定 OpenCv + 损坏 inactive `halcon.operatorTimeoutMs` 可正常 Apply/试跑，Halcon + 损坏 inactive OpenCV 字段也可正常运行。HALCON 只允许 Shape；Multi 的 candidateLimit 必须大于 expectedCount。XAML 不复制默认数值，只绑定 VM。

- [ ] **Step 7：运行 GREEN**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~VisionToolCatalogTests|FullyQualifiedName~TemplateLocateToolDialogViewModelTests|FullyQualifiedName~TemplateLocateToolDialogXamlTests"
dotnet build VisionStation.Client\VisionStation.Client.csproj -c Release -p:Platform=x64 --no-restore
```

Expected: 新默认、旧配方兼容、三 preset 和全部 binding 通过。

- [ ] **Step 8：提交参数 UI**

```powershell
git status --short
git add VisionStation.Infrastructure/DefaultRecipeFactory.cs VisionStation.Vision.UI/Services/WpfToolParameterDialogService.cs VisionStation.Vision.UI/ViewModels/VisionToolCatalog.cs VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs VisionStation.Vision.UI/Views/TemplateLocateToolDialog.xaml VisionStation.Vision.UI.Tests/VisionToolCatalogTests.cs VisionStation.Vision.UI.Tests/TemplateLocateToolDialogViewModelTests.cs VisionStation.Vision.UI.Tests/TemplateLocateToolDialogXamlTests.cs
git commit -m "feat: 启用 HALCON 参数页与异步调试"
git push
```

## Task 15：完善参数页原子学习、模型校验与重置语义

**Files:**

- Modify: `VisionStation.Vision.UI/Services/WpfToolParameterDialogService.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.UI/Views/TemplateLocateToolDialog.xaml`
- Modify: `VisionStation.Vision.UI.Tests/TemplateLocateToolDialogViewModelTests.cs`
- Modify: `VisionStation.Vision.UI.Tests/TemplateLocateOverlayFactoryTests.cs`

- [ ] **Step 1：写真实上下文、原子参数合并和取消 RED 测试**

```csharp
[Fact]
public async Task LearnUsesStableRecipeFlowToolIdentityAndMergesOnlyAfterSuccess()
{
    _service.LearnResult = SuccessfulLearning(NewHalconParameters);
    var vm = CreateVm(recipeId: "recipe-1", flowId: "flow-2", toolId: "tool-3");

    await vm.LearnTemplateCommand.ExecuteAsync();

    Assert.Equal(new TemplateModelOwner("recipe-1", "flow-2", "tool-3"), _service.LastLearningRequest!.Owner);
    Assert.All(NewHalconParameters, pair => Assert.Equal(pair.Value, vm.PendingParameters[pair.Key]));
}
```

覆盖缺任一 ID → 配置错误且不落文件；Learn 失败/OCE/对话框 Cancel 保持旧参数；试跑直接 await service、token 可取消且无 untracked Task.Run；学习预览区分 outer/inner/origin；运行预览显示所有门指标和首个拒绝原因。

- [ ] **Step 2：写完整 reset/已学习判断 RED 测试**

HALCON 已学习必须同时满足活动 engine、`halcon.modelFormat`、两个 namespaced 相对路径、model/runtime version、两个 checksum、standard/template geometry 与 generation fingerprint；旧 template.bin/Base64 不能冒充。HALCON Reset 通过 codec 一次移除上述精确 key 清单并保留 OpenCV 通用键；禁止 `halcon.*` 通配删除。OpenCV Reset 才清理其通用 `standard*`、`template*`、`modelPath/modelVersion/templatePixels`。两者都 retire 当前 owner，但不删除仍可能被取消对话框引用的旧 generation。

- [ ] **Step 3：运行 RED**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateLocateToolDialogViewModelTests|FullyQualifiedName~TemplateLocateOverlayFactoryTests"
```

Expected: Task 14 的异步路由已通过；但失败学习的原子参数合并、store-backed 已学习判断和 namespaced reset 尚未完整实现，新增断言失败。

- [ ] **Step 4：扩展同一 service 的 store/resource 依赖**

在 Task 14 已注入 `ITemplateMatchingService` 的基础上，`WpfToolParameterDialogService` 再接收同一 composition 的 `ITemplateModelStore`、`ITemplateModelResourceManager` 并传 VM。owner 继续只能来自 `_previewRecipe.Id`、`_previewRecipe.GetActiveFlow().Id`、`_toolId`，不得使用 display-only flowName。

- [ ] **Step 5：实现原子 UI 状态更新**

```csharp
var result = await _matchingService.LearnAsync(request, _operationCts.Token);
if (!result.Success)
{
    StatusText = result.Diagnostic.UserMessage;
    return;
}

var merged = new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase);
foreach (var pair in result.Parameters) merged[pair.Key] = pair.Value;
_parameters = merged;
```

Learn 生成不可变新 generation；只有成功才一次替换 VM pending 字典。用户取消对话框最多留下可后续清理的未引用 generation，旧 recipe reference 未变。试跑使用相同 batch service，不再调用 `Task.Run(() => TemplateMatcher.Match(frame, roi, parameters))`。

- [ ] **Step 6：实现运行/学习诊断和 reset**

学习预览渲染 outer、inner group、origin 三类中立 overlay；运行预览继续走共用 overlay factory，增加指标文本。对话框 Loaded 时显式 await `InitializeAsync`：活动 engine 为 Halcon 时先用 `TemplateModelParameterCodec.ReadHalcon` 检查 namespaced format/reference 完整性，再用 store Resolve 验证两条路径、owner 和 checksum，最后更新缓存的 `HasLearnedTemplateModel`；不能用同步 File.Exists、通用 OpenCV modelPath 或旧 template.bin 猜测。Reset 先更新 pending 参数，再调用 `RetireToolAsync`；retire 只释放安全缓存，不删除文件，Cancel 后旧配方下次可重新加载。

- [ ] **Step 7：运行 GREEN**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateLocateToolDialogViewModelTests|FullyQualifiedName~TemplateLocateOverlayFactoryTests"
```

Expected: 所有异步、身份、取消、原子参数和 reset 测试通过。

- [ ] **Step 8：提交异步参数页**

```powershell
git status --short
git add VisionStation.Vision.UI/Services/WpfToolParameterDialogService.cs VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs VisionStation.Vision.UI/Views/TemplateLocateToolDialog.xaml VisionStation.Vision.UI.Tests/TemplateLocateToolDialogViewModelTests.cs VisionStation.Vision.UI.Tests/TemplateLocateOverlayFactoryTests.cs
git commit -m "feat: 接入异步 HALCON 学习调试"
git push
```

## Task 16：让所有 PositionInput 调试/示教链路保留 Scale

**Files:**

- Create: `VisionStation.Vision.UI.Tests/PositionInputScalePreviewTests.cs`
- Create: `VisionStation.Vision.UI/Services/ToolResultPoseReader.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/FindLineToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/FindCircleToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/BlobAnalysisToolDialogViewModel.cs`
- Modify: `VisionStation.Vision.Tests/PoseSimilarityTransformTests.cs`

- [ ] **Step 1：写四条 UI 跟随 Scale RED 测试**

fake pipeline 返回 `x=200,y=150,angle=35,scale=1.1`；分别验证 TemplateLocate 多目标跟随、FindLine、FindCircle、Blob：

```csharp
[Theory]
[InlineData(VisionToolKind.MultiTargetMatch)]
[InlineData(VisionToolKind.FindLine)]
[InlineData(VisionToolKind.FindCircle)]
[InlineData(VisionToolKind.DefectDetect)]
public async Task PositionInputPreviewPersistsAndUsesReferenceScale(VisionToolKind kind)
{
    var vm = CreateDialog(kind, runtimePoseScale: 1.1);

    await vm.RefreshPreviewAsync();
    await vm.ApplyAsync();

    Assert.Equal("1.1", vm.Parameters["roiReferencePoseScale"]);
    AssertScaledRuntimeRoi(vm.PreviewRoi, expectedScale: 1.1);
}
```

另断言旧结果缺 scale 与旧参考缺 `roiReferencePoseScale` 均按 1；显式 NaN/0/负 reference Scale 显示配置错误；圆 Radius、矩形宽高、多边形 bounds 随 Scale；CoordinateTransform/TemplatePoint 不丢 Scale。

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~PositionInputScalePreviewTests
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~PoseSimilarityTransformTests
```

Expected: 三个 VM 与 TemplateLocate VM 只读 x/y/angle，预览尺寸不变。

- [ ] **Step 3：建立 invariant Pose reader 并删除 UI 私有公式**

四个 VM 从 ToolResult 读取 Pose 时统一调用 `ToolResultPoseReader.TryRead`；该 reader 使用 `NumberStyles.Float` 与 `CultureInfo.InvariantCulture`，缺 `scale` 兼容为 1，显式非数字、NaN、Infinity、0 或负数返回 `CONFIG_INVALID_PARAMETER`，不能默默降级为 1：

```csharp
var scale = 1.0;
if (data.TryGetValue("scale", out _) &&
    (!TryGetDouble(data, "scale", out scale) || !double.IsFinite(scale) || scale <= 0))
{
    failure = PositionInputFailure.InvalidScale();
    return false;
}

pose = new Pose2D(x, y, angle) { Scale = scale };
return true;
```

示教时写 `roiReferencePoseScale=currentPose.Scale`，清除示教时同时移除该 key；从上游模板参数回退参考位姿时只调用 `TemplateReferencePoseCodec.ReadActive`，ROI 映射只调用 `PoseSimilarityTransform.MapRoi`。删除各 VM 私有的 `TryGetStandardPose/TryGetLearnedTemplatePose/TransformRoi/MapPoint`，不保留第二套解析或数学实现。

- [ ] **Step 4：让预览优先使用生产回传 ROI**

FindLine/FindCircle/Blob 继续优先读取 pipeline `searchRoi*` 数据；该数据已经由 Task 2 的生产 GeometryToolSupport 生成 Scale-aware ROI。仅在没有 runtime data 时调用共享 helper，本地 fallback 也必须同语义。

- [ ] **Step 5：运行 GREEN**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~PositionInputScalePreviewTests|FullyQualifiedName~TemplateLocateToolDialogViewModelTests"
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~PoseSimilarityTransformTests|FullyQualifiedName~Geometry|FullyQualifiedName~TemplatePoint|FullyQualifiedName~CoordinateTransform"
```

Expected: 生产与四个调试/示教路径的 ROI 几何一致。

- [ ] **Step 6：提交下游 Scale**

```powershell
git status --short
git add VisionStation.Vision.UI/Services/ToolResultPoseReader.cs VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs VisionStation.Vision.UI/ViewModels/FindLineToolDialogViewModel.cs VisionStation.Vision.UI/ViewModels/FindCircleToolDialogViewModel.cs VisionStation.Vision.UI/ViewModels/BlobAnalysisToolDialogViewModel.cs VisionStation.Vision.UI.Tests/PositionInputScalePreviewTests.cs VisionStation.Vision.Tests/PoseSimilarityTransformTests.cs
git commit -m "fix: 保留定位下游尺度变换"
git push
```

## Task 17：接入配方管理生命周期并核验完整组合

**Files:**

- Create: `VisionStation.Vision.UI.Tests/TemplateMatchingCompositionTests.cs`
- Modify: `VisionStation.Client/App.xaml.cs`
- Modify: `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs`

- [ ] **Step 1：写 composition 惰性启动和配方依赖 RED 测试**

```csharp
[Fact]
public async Task CompositionCanRunOpenCvWithoutTouchingHalconRuntime()
{
    var composition = TemplateMatchingComposition.Create(
        TemporaryRuntimePaths,
        new HalconRuntimeConfiguration { RuntimeRoot = @"Z:\missing-halcon" },
        FakeLog);

    var result = await composition.Service.MatchAsync(OpenCvRequest(), default);

    Assert.NotEqual("RUNTIME_NOT_FOUND", result.Diagnostic?.Code);
    Assert.NotNull(composition.Store);
    Assert.NotNull(composition.Resources);
    await composition.Service.DisposeAsync();
}

[Fact]
public void RecipeManagementDependsOnLifecycleServiceInsteadOfFileStore()
{
    var parameters = typeof(RecipeManagementViewModel)
        .GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType).ToArray();

    Assert.Contains(typeof(IRecipeTemplateLifecycleService), parameters);
    Assert.DoesNotContain(typeof(FileTemplateModelStore), parameters);
}
```

再反射断言 `WpfToolParameterDialogService` 依赖同一 `ITemplateMatchingService/ITemplateModelStore/ITemplateModelResourceManager` 三个端口；service 关闭/lease 等待由 Task 9/12 单元测试继续覆盖。

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~TemplateMatchingCompositionTests
```

Expected: RecipeManagementViewModel 尚未依赖生命周期 service，反射断言失败。

- [ ] **Step 3：把配方 UI 改为窄生命周期调用**

App 注册：

```csharp
containerRegistry.RegisterSingleton<IRecipeTemplateLifecycleService, RecipeTemplateLifecycleService>();
```

RecipeManagementViewModel 构造注入该接口；Duplicate/Delete 不再直接复制 record、保存共享 modelPath 或只删 JSON：

```csharp
var copy = await _recipeTemplateLifecycle.DuplicateAsync(source, copyId, cancellationToken);
await RefreshAfterDuplicateAsync(copy);

await _recipeTemplateLifecycle.DeleteAsync(deletedRecipe, cancellationToken);
await RefreshAfterDeleteAsync(deletedRecipe.Id);
```

repository/model 事务顺序仍由 Task 7 已测试的 Application service 隐藏，ViewModel 只负责 busy/status/列表选择。

- [ ] **Step 4：复核 App 的共享生命周期与显式关闭**

确认 Task 12 composition 注册的 Service/Store/Resources 与 pipeline/dialog/recipe service 引用相同对象。`App.OnExit` 保持 Task 5 已加入的顺序：先取消视觉任务，再等待 `_templateMatchingService.DisposeAsync()`，最后释放 communication runtime；异常使用 `IAppLogService.Error(nameof(App), message)` 记录，不弹窗。Task 17 必须同时收口 Task 5 明确保留的生命周期债：Dashboard 的直接 `RunSingleAsync` 与 RecipeManagement 的试运行要统一接入 app-lifetime cancellation，并由 `IInspectionRunner`（或等价的单一运行 owner）拒绝 shutdown 后的新任务、取消并 drain 所有 active run。该验收完成前只能声明连续生产循环已停止，不能声明全部视觉任务均已停止。

- [ ] **Step 5：运行 GREEN 与启动相关回归**

```powershell
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~TemplateMatchingCompositionTests|FullyQualifiedName~TemplateLocateToolDialogViewModelTests"
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~TemplateModelResourceManagerTests
dotnet build VisionStation.Client\VisionStation.Client.csproj -c Release -p:Platform=x64 --no-restore
```

Expected: 测试通过；Client 无 HALCON runtime 时仍能启动和运行 OpenCv；配方 copy/delete 使用受控资源生命周期。

- [ ] **Step 6：提交最终组合接线**

```powershell
git status --short
git add VisionStation.Client/App.xaml.cs VisionStation.Client/ViewModels/RecipeManagementViewModel.cs VisionStation.Vision.UI.Tests/TemplateMatchingCompositionTests.cs
git commit -m "feat: 接入配方模型资源生命周期"
git push
```

## Task 18：增加隔离子进程和真实许可 HALCON 合成验收

**Files:**

- Create: `VisionStation.Vision.Halcon.TestHost/VisionStation.Vision.Halcon.TestHost.csproj`
- Create: `VisionStation.Vision.Halcon.TestHost/Program.cs`
- Create: `VisionStation.Vision.Halcon.Tests/VisionStation.Vision.Halcon.Tests.csproj`
- Create: `VisionStation.Vision.Halcon.Tests/HalconIntegrationFactAttribute.cs`
- Create: `VisionStation.Vision.Halcon.Tests/SyntheticHalconProductFactory.cs`
- Create: `VisionStation.Vision.Halcon.Tests/HalconScaledShapeIntegrationTests.cs`
- Create: `VisionStation.Vision.Halcon.Tests/HalconPersistenceIntegrationTests.cs`
- Create: `VisionStation.Vision.Halcon.Tests/HalconRuntimeProbeProcessTests.cs`
- Create: `VisionStation.Vision.Halcon.Tests/xunit.runner.json`
- Modify: `CVWork.sln`

- [ ] **Step 1：建立显式启用且启用后不得 skip 的测试门**

TestHost 使用纯 `net8.0` x64 控制台项目，只引用 Vision 与 Infrastructure；集成测试项目沿用仓库已锁定的测试包版本，并引用 TestHost 的可复用命令/报告类型：

```xml
<!-- VisionStation.Vision.Halcon.TestHost.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\VisionStation.Vision\VisionStation.Vision.csproj" />
    <ProjectReference Include="..\VisionStation.Infrastructure\VisionStation.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

```xml
<!-- VisionStation.Vision.Halcon.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\VisionStation.Vision\VisionStation.Vision.csproj" />
    <ProjectReference Include="..\VisionStation.Infrastructure\VisionStation.Infrastructure.csproj" />
    <ProjectReference Include="..\VisionStation.Vision.Halcon.TestHost\VisionStation.Vision.Halcon.TestHost.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <None Update="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

把两个项目加入 `CVWork.sln` 的 Debug/Release x64 映射；不得重新引入 Any CPU。

```powershell
dotnet restore VisionStation.Vision.Halcon.TestHost\VisionStation.Vision.Halcon.TestHost.csproj
dotnet restore VisionStation.Vision.Halcon.Tests\VisionStation.Vision.Halcon.Tests.csproj
```

Expected: 两个新项目生成 `obj/project.assets.json`，测试输出将复制 `xunit.runner.json`。

```csharp
internal static class HalconIntegrationGate
{
    public const string EnableMessage =
        "Set VISIONSTATION_HALCON_INTEGRATION=1 to run licensed HALCON tests.";

    public static bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable("VISIONSTATION_HALCON_INTEGRATION"),
        "1",
        StringComparison.Ordinal);
}

public sealed class HalconIntegrationFactAttribute : FactAttribute
{
    public HalconIntegrationFactAttribute()
    {
        if (!HalconIntegrationGate.IsEnabled)
            Skip = HalconIntegrationGate.EnableMessage;
    }
}

public sealed class HalconIntegrationTheoryAttribute : TheoryAttribute
{
    public HalconIntegrationTheoryAttribute()
    {
        if (!HalconIntegrationGate.IsEnabled)
            Skip = HalconIntegrationGate.EnableMessage;
    }
}
```

两个 attribute 共用 `HalconIntegrationGate`，避免启用逻辑漂移。环境变量为 1 后，runtime/license 缺失必须 Assert fail，不能再次动态 skip。`xunit.runner.json` 设置 `parallelizeAssembly=false`、`parallelizeTestCollections=false`。

- [ ] **Step 2：先写最小真实 tracer-bullet RED 测试**

通过公共 `ITemplateMatchingService` 学习 1.0/0° 合成不对称产品，保存 `.shm`，清 cache，重新加载并匹配；验证中心 ≤2px、角度 ≤1°、Scale ≤0.02。首次运行若 create/find/write/read 任一真实调用不通必须停在此修正边界，不能继续堆 UI 调参。

```powershell
$env:VISIONSTATION_HALCON_INTEGRATION='1'
dotnet test VisionStation.Vision.Halcon.Tests\VisionStation.Vision.Halcon.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~HalconScaledShapeIntegrationTests.TracerBullet
```

Expected: 测试被发现，并因 `SyntheticHalconProductFactory`/真实 tracer orchestration 尚未实现而编译或断言失败；不得显示 skipped 或 0 tests。

- [ ] **Step 3：实现只输出 JSON 的 x64 TestHost**

支持命令：

```text
probe --root $env:HALCONROOT --expected-version 26.05.0.0
license-smoke --root $env:HALCONROOT
model-roundtrip --root $env:HALCONROOT --working-directory artifacts\halcon-roundtrip
timeout --root $env:HALCONROOT --milliseconds 5000
```

stdout 仅一个 JSON 对象，字段为 success/code/stage/runtimeVersion/technicalSummary；stderr 可记录堆栈。每个 runtime root/resolver/错架构/错版本/缺 DLL 场景启动新进程，避免 `SetDllImportResolver` 与 HALCON 初始化污染同一 testhost。

- [ ] **Step 4：写完整正例矩阵**

合成产品为暗前景亮背景、非对称长条外形和至少三个非对称内部组。测试使用 `[HalconIntegrationTheory]`，矩阵为 Angle `0/35/90/-135` × Scale `0.90/1.00/1.10`；每项从公共 service 学习/匹配并断言 HasMatch/OK、中心 ≤2px、角度 ≤1°、Scale ≤0.02。禁止普通 `[Theory]` 绕过许可门。

- [ ] **Step 5：写必须零接受的负例**

分别构造反面内部布局、相似外形不同内部、越过图像边界、越过 search ROI、只剩局部中段、严重遮挡、极性相反。每项断言 Matches empty/HasMatch false，并核对对应首个硬门诊断；不允许为了使正例通过而在测试中降低严格 preset。

- [ ] **Step 6：写多目标和持久化验收**

不同角度/尺度多件目标验证精确数量和每件 Pose；少一、多一均 NG 且不发布端口；相邻合法件不被 candidate overlap 丢失，同件重复由 support IoU 删除。保存后 Dispose/cache clear/reload 结果等价；model checksum、metadata checksum、owner、runtime version 任一损坏均不可运行。

- [ ] **Step 7：写子进程 runtime/timeout 验收**

覆盖缺 DLL、非 AMD64 PE、故意不同 expected version、resolver 第二 root、许可失败、损坏 `.shm`、9400 timeout。用户取消测试在 native 调用中触发 token，等待 operator 安全返回后断言 OCE 且无 `MATCH_CANCELLED`/端口。

- [ ] **Step 8：运行真实 GREEN**

```powershell
$env:VISIONSTATION_HALCON_INTEGRATION='1'
$env:HALCONARCH='x64-win64'
dotnet test VisionStation.Vision.Halcon.Tests\VisionStation.Vision.Halcon.Tests.csproj -c Release -p:Platform=x64 --no-restore
```

Expected: 在已安装 HALCON 26.05.0.0 且许可有效的目标机上全通过；明确启用时任何 runtime/license 问题是失败。

- [ ] **Step 9：提交真实验收项目**

```powershell
git status --short
git add CVWork.sln VisionStation.Vision.Halcon.TestHost VisionStation.Vision.Halcon.Tests
git commit -m "test: 增加 HALCON 真实许可验收"
git push
```

## Task 19：编写二次开发、部署、现场验收和性能基线文档

**Files:**

- Create: `docs/development/halcon-shape-matching.md`
- Create: `docs/development/halcon-shape-deployment.md`
- Create: `docs/development/halcon-shape-performance-baseline.md`
- Create: `VisionStation.Vision.Halcon.Tests/LocalHalconDatasetAcceptanceTests.cs`
- Create: `VisionStation.Vision.Halcon.Tests/HalconBenchmarkCommandTests.cs`
- Create: `VisionStation.Vision.Halcon.Tests/LocalHalconDatasetManifestTests.cs`
- Create: `VisionStation.Vision.Halcon.TestHost/HalconBenchmarkReport.cs`
- Create: `VisionStation.Vision.Halcon.TestHost/HalconBenchmarkRunner.cs`
- Create: `VisionStation.Vision.Halcon.TestHost/LocalHalconDatasetManifest.cs`
- Modify: `VisionStation.Vision.Halcon.TestHost/HalconTestHostContracts.cs`
- Modify: `VisionStation.Vision.Halcon.TestHost/HalconTestHostRunner.cs`

- [ ] **Step 1：写 dataset manifest 和 benchmark 聚合 RED 测试**

```csharp
[Fact]
public void BenchmarkReportComputesMedianP95RangeAndResourceDeltas()
{
    var report = HalconBenchmarkReport.Create(
        durationsMs: new[] { 10d, 11d, 12d, 13d, 30d },
        workingSetBefore: 1000,
        workingSetAfter: 1200,
        handlesBefore: 50,
        handlesAfter: 51);

    Assert.Equal(12, report.MedianMs);
    Assert.Equal(30, report.P95Ms);
    Assert.Equal(20, report.RangeMs);
    Assert.Equal(200, report.WorkingSetDeltaBytes);
    Assert.Equal(1, report.HandleDelta);
}
```

`HalconBenchmarkReport` 与 `LocalHalconDatasetManifest` 是 TestHost 程序集中的 public 纯类型，Program 保持统一 stdout/退出码边界，`HalconTestHostRunner` 编排 benchmark，测试通过 Task 18 的 ProjectReference 直接复用。另锁定现场 manifest 的 label 只允许 positive/front、back、similar、partial、boundary、polarity，未知标签 fail closed；输出 JSON 必含机器/软件指纹和 cold/warm/1/3/5 targets 分组。

- [ ] **Step 2：运行 RED**

```powershell
dotnet test VisionStation.Vision.Halcon.Tests\VisionStation.Vision.Halcon.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~HalconBenchmarkCommandTests
```

Expected: benchmark report/manifest parser 尚不存在；该纯逻辑测试不要求许可。

- [ ] **Step 3：增加可选现场数据集入口**

`LocalHalconDatasetAcceptanceTests` 只在 `VISIONSTATION_HALCON_DATASET` 根本未定义时 skip；变量一旦定义，空白值、目录不存在或 manifest 非法都必须 fail closed。目录内用 manifest 标注 positive/front、back、similar、partial、boundary、polarity 等预期，case 不得复用学习模板图。代码不写本机绝对路径、不把图片复制进仓库；验收使用 `ExactCount=1`，负例首要断言零接受，正例要求恰好一个接受并记录漏检。

- [ ] **Step 4：增加 benchmark 命令**

TestHost 增加：

```text
benchmark --root $env:HALCONROOT --iterations 50 --output artifacts\halcon-benchmark.json
```

先独立 warm-up 许可与 HALCON 内部线程池，再记录硬件/OS/.NET/HALCON/包版本、cold load、warm single、1/3/5 targets 的 median/P95/range、进程 working set、private bytes 和 handle count。原始 JSON 不含许可详情。

- [ ] **Step 5：运行纯逻辑 GREEN，再在目标工控机采集真实基线**

```powershell
dotnet test VisionStation.Vision.Halcon.Tests\VisionStation.Vision.Halcon.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~HalconBenchmarkCommandTests
```

Expected: 纯逻辑测试通过。

```powershell
dotnet run --project VisionStation.Vision.Halcon.TestHost\VisionStation.Vision.Halcon.TestHost.csproj -c Release -- benchmark --root $env:HALCONROOT --iterations 50 --output artifacts\halcon-benchmark.json
```

Expected: 50 次有效样本，零 operator failure；cold/warm、不同目标数、内存与 handle 数据齐全。`artifacts/` 不提交。

- [ ] **Step 6：写二次开发主文档**

必须包含：公共 service 请求/结果完整例子、三 engine 兼容表、参数分类与三 preset、六硬门顺序、Scale/角度/坐标契约、model metadata/layout/checksum、Recipe/Flow/Tool ownership、cache/lease/retire、取消/timeout、所有稳定错误码、如何新增第四 backend、如何 fake backend/validator、matchesV2 与旧格式、配方复制/删除/reset。

- [ ] **Step 7：写部署与故障排查**

明确需要完整 HALCON 26.05.0.0 x64 runtime 与有效许可；NuGet 26050.0.0 精确对应；不复制单个 halcon.dll；HALCONROOT/HALCONARCH、devices 配置、卸载注册表顺序；floating license/hlwd 检查；缺 DLL/架构/版本/许可/checksum/model load/timeout 的操作员处理。文档不得写任何开发机绝对安装目录。

- [ ] **Step 8：写入真实性能数值和相对回归门限**

把 benchmark JSON 的机器指纹、median/P95/range、内存/handle 实测值写入版本化文档，并依据该机器多轮波动给出后续相对阈值；不得填脱离硬件的固定绝对毫秒门限，也不得留下空表或未完成字段。

- [ ] **Step 9：验证文档与可选验收**

```powershell
rg -n "ITemplateMatchingService|CONFIG_UNKNOWN_ENGINE|MATCH_TIMEOUT|matchesV2|Pose.Scale|HALCONROOT|26050.0.0|26.05.0.0" docs\development -g 'halcon-shape-*.md'
$markers = @('T' + 'ODO', 'T' + 'BD', '待' + '补', '占' + '位') -join '|'
rg -n "D:\\\\HALCON|C:\\\\Users\\\\Time|$markers" docs\development -g 'halcon-shape-*.md'
```

Expected: 第一条覆盖所有关键契约；第二条无输出。

- [ ] **Step 10：提交文档和可选验收入口**

```powershell
git status --short
git add docs/development/halcon-shape-matching.md docs/development/halcon-shape-deployment.md docs/development/halcon-shape-performance-baseline.md docs/superpowers/plans/2026-07-16-halcon-shape-matching.md VisionStation.Vision.Halcon.TestHost/HalconTestHostContracts.cs VisionStation.Vision.Halcon.TestHost/HalconTestHostRunner.cs VisionStation.Vision.Halcon.TestHost/HalconBenchmarkReport.cs VisionStation.Vision.Halcon.TestHost/HalconBenchmarkRunner.cs VisionStation.Vision.Halcon.TestHost/LocalHalconDatasetManifest.cs VisionStation.Vision.Halcon.Tests/LocalHalconDatasetAcceptanceTests.cs VisionStation.Vision.Halcon.Tests/LocalHalconDatasetManifestTests.cs VisionStation.Vision.Halcon.Tests/HalconBenchmarkCommandTests.cs
git commit -m "docs: 记录 HALCON 二次开发与性能基线"
git push
```

## Task 20：执行全量回归、边界审计和发布验证

**Files:**

- Modify only if verification exposes a defect: 本计划中已列出的相关实现或测试文件

- [ ] **Step 1：扫描 HALCON 类型泄漏与旧静态生产调用**

```powershell
rg -n "using HalconDotNet|\bH(Image|Tuple|ShapeModel|Object|Region|XLD)\b" VisionStation.Domain VisionStation.Application VisionStation.Infrastructure VisionStation.Vision.UI VisionStation.Client
rg -n "using HalconDotNet|\bH(Image|Tuple|ShapeModel|Object|Region|XLD)\b" VisionStation.Vision --glob "!**/TemplateMatching/Halcon/**"
rg -n "TemplateMatcher\.(Learn|Match)|MultiTargetMatcher\.Match" VisionStation.Vision\Tools VisionStation.Vision.UI
```

Expected: 三条均无输出。测试项目的包版本契约可直接引用 HalconDotNet；生产 HALCON 类型只在 `VisionStation.Vision/TemplateMatching/Halcon`。

- [ ] **Step 2：扫描兼容与安全不变量**

```powershell
rg -n "engine.*OpenCv|CONFIG_SERVICE_REQUIRED|CONFIG_UNKNOWN_ENGINE|expectedCount|matchCount|matchesV2|overlaySchemaVersion|roiReferencePoseScale" VisionStation.Vision VisionStation.Vision.UI VisionStation.Infrastructure VisionStation.Client
$markers = @('T' + 'ODO', 'T' + 'BD', 'NotImplemented' + 'Exception', '待' + '补', '占' + '位', 'similar' + ' to') -join '|'
rg -n $markers VisionStation.Vision/TemplateMatching VisionStation.Infrastructure/FileTemplateModelStore.cs docs/development/halcon-shape-*.md
```

Expected: 第一条能定位集中 resolver/catalog、v2/legacy 和 Scale 兼容点；第二条无输出。

- [ ] **Step 3：全量 Release x64 GREEN build**

```powershell
dotnet restore CVWork.sln
dotnet build CVWork.sln -c Release -p:Platform=x64 --no-restore
```

Expected: 0 errors，0 warnings；不得通过屏蔽 warning 达成。

- [ ] **Step 4：运行默认无许可测试四组**

```powershell
dotnet test VisionStation.Application.Tests\VisionStation.Application.Tests.csproj -c Release -p:Platform=x64 --no-build
dotnet test VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj -c Release -p:Platform=x64 --no-build
dotnet test VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj -c Release -p:Platform=x64 --no-build
Remove-Item Env:\VISIONSTATION_HALCON_INTEGRATION -ErrorAction SilentlyContinue
dotnet test VisionStation.Vision.Halcon.Tests\VisionStation.Vision.Halcon.Tests.csproj -c Release -p:Platform=x64 --no-build
```

Expected: 原 254 项基线和全部新增无许可纯逻辑测试通过；真实许可 Fact/Theory 明确 skipped，benchmark/manifest/gate 测试必须 passed。若仅出现已知 SignalWaiter 并行计时波动，单独复跑确认，但不修改它或把失败隐藏在本提交。

- [ ] **Step 5：运行显式真实许可测试**

```powershell
$env:VISIONSTATION_HALCON_INTEGRATION='1'
$env:HALCONARCH='x64-win64'
dotnet test VisionStation.Vision.Halcon.Tests\VisionStation.Vision.Halcon.Tests.csproj -c Release -p:Platform=x64 --no-build
```

Expected: 角度/尺度矩阵、所有负例、多目标、持久化、runtime 子进程、timeout/cancel 全通过。

- [ ] **Step 6：验证 win-x64 发布物**

```powershell
dotnet restore VisionStation.Client\VisionStation.Client.csproj -r win-x64
dotnet publish VisionStation.Client\VisionStation.Client.csproj -c Release -r win-x64 --self-contained false -p:Platform=x64 --no-restore
```

Expected: publish 成功；应用依赖已安装的完整匹配 HALCON runtime，不把单个 native DLL 伪装为自包含。

- [ ] **Step 7：做最终 diff 与二次开发审查**

```powershell
git status --short
git diff --check
$mergeBase = git merge-base origin/codex/halcon-shape-matching HEAD
git diff --stat $mergeBase HEAD
```

逐项对照设计第 18 节完成标准；重点人工复核：服务 seam 无 HALCON 类型、单/多共用 backend、NG 无端口、Scale 单一真值、model 路径 owner/checksum、热改门限、旧配方/旧 matches、App dispose。

- [ ] **Step 8：请求独立代码审查并只修复可验证问题**

使用 `superpowers:requesting-code-review`，审查范围为设计文档、此计划和分支全部实现；对反馈先复现再改。若有修复，运行受影响测试与全量回归后提交：

```powershell
git add -u
git commit -m "fix: 完成 HALCON 匹配终审修正"
git push
```

- [ ] **Step 9：确认干净工作树并进入分支收尾**

```powershell
git status --short
git log --oneline --decorate -20
```

Expected: 工作树干净、每个阶段都有独立可回滚提交。使用 `superpowers:finishing-a-development-branch` 选择合并、PR 或保留分支；未经用户选择不擅自合并主分支。

## 设计追踪检查表

| 设计要求 | 计划落点 |
|---|---|
| 新工具 Halcon、旧缺 engine OpenCv、未知不兜底 | Task 3、14 |
| 单/多同一异步 batch service | Task 4、5、12 |
| HALCON 26.05 / 26050 / x64 / 无控件 | Task 1、8、18 |
| 全角度、0.90..1.10、暗产品亮背景 | Task 10、11、12、18 |
| 六项独立硬门、无加权补偿 | Task 10、12 |
| expectedCount 精确计数与多一个确认 | Task 12、13 |
| Pose.Scale 单一真值与所有 ROI 相似变换 | Task 2、5、13、16 |
| NG/timeout/cancel 不发布 pose/ports | Task 5、12、13、18 |
| matchesV2 + 旧 8 列 + overlay schema 2 | Task 13 |
| Recipe/Flow/Tool slug+hash、相对路径、checksum | Task 6 |
| generation 原子激活、复制/reset/delete/orphan | Task 6、7、15 |
| runtime env/config/registry、version/arch/license | Task 8、18 |
| cache key、per-model lock、跨模型并行、lease retire | Task 9 |
| timeout 100..60000、第一期安全取消 | Task 3、9、12、18 |
| engine/preset/高级参数/学习诊断 UI | Task 14、15 |
| 无许可 fake CI + 真实许可非并行矩阵 | Task 4、8、10、12、18 |
| 二次开发、部署、现场与性能基线 | Task 19 |

## 实施完成的证据包

交付时必须同时具备：

1. Release x64 构建和三组默认测试完整输出。
2. 显式真实许可测试完整输出及环境版本摘要。
3. win-x64 publish 输出与 runtime 依赖检查。
4. 真实 performance baseline 文档和原始 JSON 的本地保存位置说明；原始 artifacts 不提交。
5. `git diff --check` 无输出、HALCON 类型边界扫描无输出、工作树干净。
6. 每个设计要求在上表对应的测试名或文档章节，可由二次开发人员快速导航。
