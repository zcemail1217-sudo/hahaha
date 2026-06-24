# Calibration Page Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redesign the visual calibration page into a two-tab engineer workbench with plane calibration as the default workflow and camera intrinsic calibration as a focused secondary workflow.

**Architecture:** Preserve the existing calibration algorithms, recipe persistence, and navigation target. Add small ViewModel-facing quality/validation helpers, then reshape `CalibrationView.xaml` into `平面标定` and `相机内参` tabs while keeping the current `CalibrationViewModel` as the backing context for this iteration.

**Tech Stack:** .NET 8, WPF, Prism, xUnit, existing `VisionStation.Domain`, `VisionStation.Vision`, and `VisionStation.Vision.UI`.

---

## File Structure

- Modify: `CVWork/VisionStation.Vision.UI/ViewModels/CalibrationViewModel.cs`
  - Add public quality summary properties for plane and camera results.
  - Add advanced settings expansion state.
  - Add selected-tab state with plane tab selected by default.
  - Surface selected-sample overlay updates and homography point-count validation.
- Modify: `CVWork/VisionStation.Vision.UI/Views/CalibrationView.xaml`
  - Replace the current side-by-side page body with a tabbed layout.
  - Move plane calibration into the default tab.
  - Move camera intrinsic calibration into the second tab.
  - Collapse advanced motion settings by default.
- Create: `CVWork/VisionStation.Vision.UI.Tests/CalibrationViewModelTests.cs`
  - Cover quality summary thresholds and selected overlay behavior through testable ViewModel/static helper behavior.
- Keep unchanged: `CVWork/VisionStation.Vision/OpenCvCalibrationService.cs`
  - Algorithms stay untouched.
- Keep unchanged: recipe persistence model in `CVWork/VisionStation.Domain`.

Current workspace note: no `.git` directory exists under `C:\Users\Time\Desktop\新软件\AICode`, so commit steps are not executable in this environment. If the same plan is run inside a git checkout, commit after each task.

---

### Task 1: Add Calibration Quality State

**Files:**
- Modify: `CVWork/VisionStation.Vision.UI/ViewModels/CalibrationViewModel.cs`
- Test: `CVWork/VisionStation.Vision.UI.Tests/CalibrationViewModelTests.cs`

- [ ] **Step 1: Write failing quality summary tests**

Create `CVWork/VisionStation.Vision.UI.Tests/CalibrationViewModelTests.cs`:

```csharp
using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class CalibrationViewModelTests
{
    [Theory]
    [InlineData(0.049, CalibrationQualityLevel.Good, "可用")]
    [InlineData(0.2, CalibrationQualityLevel.Warning, "警告")]
    [InlineData(0.201, CalibrationQualityLevel.Bad, "不建议保存")]
    public void PlaneQualitySummary_UsesConfiguredThresholds(
        double rms,
        CalibrationQualityLevel expectedLevel,
        string expectedLabel)
    {
        var result = new PlaneCalibrationResult
        {
            Model = PlaneCalibrationModel.Affine,
            Unit = "mm",
            RmsError = rms,
            MaxError = 0.31,
            PointCount = 9,
            InlierCount = 8,
            ImageToWorldMatrix = [1, 0, 0, 0, 1, 0],
            PointErrors =
            [
                new PlaneCalibrationPointError { Error = 0.05 },
                new PlaneCalibrationPointError { Error = 0.31 }
            ]
        };

        var summary = CalibrationQualitySummary.FromPlane(result);

        Assert.Equal(expectedLevel, summary.Level);
        Assert.Equal(expectedLabel, summary.Label);
        Assert.Contains("RMS", summary.Details);
        Assert.Equal("最大误差点：#2，误差 0.31 mm", summary.MaxErrorPointText);
    }

    [Theory]
    [InlineData(0.5, CalibrationQualityLevel.Good, "可用")]
    [InlineData(1.0, CalibrationQualityLevel.Warning, "警告")]
    [InlineData(1.01, CalibrationQualityLevel.Bad, "不建议保存")]
    public void CameraQualitySummary_UsesReprojectionThresholds(
        double rms,
        CalibrationQualityLevel expectedLevel,
        string expectedLabel)
    {
        var result = new CameraCalibrationResult
        {
            ImageWidth = 1280,
            ImageHeight = 960,
            RmsReprojectionError = rms,
            Views =
            [
                new CameraCalibrationViewResult { ReprojectionError = rms }
            ]
        };

        var summary = CalibrationQualitySummary.FromCamera(result);

        Assert.Equal(expectedLevel, summary.Level);
        Assert.Equal(expectedLabel, summary.Label);
        Assert.Contains("RMS", summary.Details);
        Assert.Equal(string.Empty, summary.MaxErrorPointText);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test CVWork\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj --filter CalibrationViewModelTests --no-restore
```

Expected: FAIL because `CalibrationQualityLevel` and `CalibrationQualitySummary` do not exist.

- [ ] **Step 3: Add quality summary types**

Append these public types near the bottom of `CVWork/VisionStation.Vision.UI/ViewModels/CalibrationViewModel.cs`, before `VisionToolOptionItem`:

```csharp
public enum CalibrationQualityLevel
{
    None,
    Good,
    Warning,
    Bad
}

public sealed record CalibrationQualitySummary
{
    public CalibrationQualityLevel Level { get; init; } = CalibrationQualityLevel.None;

    public string Label { get; init; } = "-";

    public string Details { get; init; } = "尚未计算";

    public string MaxErrorPointText { get; init; } = string.Empty;

    public static CalibrationQualitySummary Empty(string details = "尚未计算")
    {
        return new CalibrationQualitySummary
        {
            Level = CalibrationQualityLevel.None,
            Label = "-",
            Details = details
        };
    }

    public static CalibrationQualitySummary FromPlane(PlaneCalibrationResult result)
    {
        var level = result.RmsError <= 0.05
            ? CalibrationQualityLevel.Good
            : result.RmsError <= 0.2
                ? CalibrationQualityLevel.Warning
                : CalibrationQualityLevel.Bad;
        var label = level == CalibrationQualityLevel.Good
            ? "可用"
            : level == CalibrationQualityLevel.Warning
                ? "警告"
                : "不建议保存";
        var maxErrorIndex = ResolveMaxErrorIndex(result);
        return new CalibrationQualitySummary
        {
            Level = level,
            Label = label,
            Details = $"模型={result.Model}  RMS={result.RmsError:0.###} {result.Unit}  Max={result.MaxError:0.###} {result.Unit}  点数={result.PointCount}  内点={result.InlierCount}",
            MaxErrorPointText = maxErrorIndex <= 0
                ? string.Empty
                : $"最大误差点：#{maxErrorIndex}，误差 {result.MaxError:0.###} {result.Unit}"
        };
    }

    public static CalibrationQualitySummary FromCamera(CameraCalibrationResult result)
    {
        var level = result.RmsReprojectionError <= 0.5
            ? CalibrationQualityLevel.Good
            : result.RmsReprojectionError <= 1.0
                ? CalibrationQualityLevel.Warning
                : CalibrationQualityLevel.Bad;
        var label = level == CalibrationQualityLevel.Good
            ? "可用"
            : level == CalibrationQualityLevel.Warning
                ? "警告"
                : "不建议保存";
        return new CalibrationQualitySummary
        {
            Level = level,
            Label = label,
            Details = $"RMS={result.RmsReprojectionError:0.###} px  图像={result.ImageWidth}x{result.ImageHeight}  视图={result.Views.Count}"
        };
    }

    private static int ResolveMaxErrorIndex(PlaneCalibrationResult result)
    {
        if (result.PointErrors.Count == 0)
        {
            return 0;
        }

        var maxIndex = 0;
        var maxError = result.PointErrors[0].Error;
        for (var index = 1; index < result.PointErrors.Count; index++)
        {
            if (result.PointErrors[index].Error > maxError)
            {
                maxError = result.PointErrors[index].Error;
                maxIndex = index;
            }
        }

        return maxIndex + 1;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:

```powershell
dotnet test CVWork\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj --filter CalibrationViewModelTests --no-restore
```

Expected: PASS.

- [ ] **Step 5: Commit if git is available**

Run:

```powershell
git status --short
```

Expected in this workspace: `fatal: not a git repository`. If run inside a git checkout, commit:

```powershell
git add CVWork\VisionStation.Vision.UI\ViewModels\CalibrationViewModel.cs CVWork\VisionStation.Vision.UI.Tests\CalibrationViewModelTests.cs
git commit -m "feat: add calibration quality summary"
```

---

### Task 2: Wire Quality and Validation Into the ViewModel

**Files:**
- Modify: `CVWork/VisionStation.Vision.UI/ViewModels/CalibrationViewModel.cs`
- Test: `CVWork/VisionStation.Vision.UI.Tests/CalibrationViewModelTests.cs`

- [ ] **Step 1: Add failing validation tests**

Append tests to `CalibrationViewModelTests`:

```csharp
[Fact]
public void CanCalculatePlane_ReturnsFalseForHomographyWithThreePoints()
{
    var result = CalibrationPlaneValidation.CanCalculate(PlaneCalibrationModel.Homography, validPointCount: 3, out var message);

    Assert.False(result);
    Assert.Equal("Homography 至少需要 4 个有效点", message);
}

[Fact]
public void CanCalculatePlane_AllowsAffineWithThreePoints()
{
    var result = CalibrationPlaneValidation.CanCalculate(PlaneCalibrationModel.Affine, validPointCount: 3, out var message);

    Assert.True(result);
    Assert.Equal(string.Empty, message);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test CVWork\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj --filter CalibrationViewModelTests --no-restore
```

Expected: FAIL because `CalibrationPlaneValidation` does not exist.

- [ ] **Step 3: Add ViewModel properties and validation helper**

In `CalibrationViewModel`, add backing fields:

```csharp
private int _selectedCalibrationTabIndex;
private bool _isAdvancedPlaneSettingsExpanded;
private CalibrationQualitySummary _planeQualitySummary = CalibrationQualitySummary.Empty();
private CalibrationQualitySummary _cameraQualitySummary = CalibrationQualitySummary.Empty();
```

Add public properties near existing UI state properties:

```csharp
public int SelectedCalibrationTabIndex
{
    get => _selectedCalibrationTabIndex;
    set => SetProperty(ref _selectedCalibrationTabIndex, value);
}

public bool IsAdvancedPlaneSettingsExpanded
{
    get => _isAdvancedPlaneSettingsExpanded;
    set => SetProperty(ref _isAdvancedPlaneSettingsExpanded, value);
}

public CalibrationQualitySummary PlaneQualitySummary
{
    get => _planeQualitySummary;
    private set => SetProperty(ref _planeQualitySummary, value);
}

public CalibrationQualitySummary CameraQualitySummary
{
    get => _cameraQualitySummary;
    private set => SetProperty(ref _cameraQualitySummary, value);
}
```

Add helper type near `CalibrationQualitySummary`:

```csharp
public static class CalibrationPlaneValidation
{
    public static bool CanCalculate(PlaneCalibrationModel model, int validPointCount, out string message)
    {
        if (validPointCount < 3)
        {
            message = "至少需要 3 个有效点";
            return false;
        }

        if (model == PlaneCalibrationModel.Homography && validPointCount < 4)
        {
            message = "Homography 至少需要 4 个有效点";
            return false;
        }

        message = string.Empty;
        return true;
    }
}
```

- [ ] **Step 4: Update result application and clear paths**

In `ClearAutoPlaneSamples`, set:

```csharp
PlaneQualitySummary = CalibrationQualitySummary.Empty();
```

In `ClearCameraImages`, set:

```csharp
CameraQualitySummary = CalibrationQualitySummary.Empty();
```

In `ApplyPlaneResult`, add:

```csharp
PlaneQualitySummary = CalibrationQualitySummary.FromPlane(result);
```

In `ApplyCameraResult`, add:

```csharp
CameraQualitySummary = CalibrationQualitySummary.FromCamera(result);
```

In `CalculatePlaneFromSamples`, after building `pairs` and before calling `_calibration.CalibratePlane`, add:

```csharp
if (!CalibrationPlaneValidation.CanCalculate(SelectedPlaneModel, pairs.Length, out var validationMessage))
{
    PlaneStatusText = validationMessage;
    PlaneQualityText = SelectedPlaneModel == PlaneCalibrationModel.Homography
        ? "Homography 至少需要 4 个有效点；请补齐九点采样或切换到 Affine/Auto"
        : "至少需要 3 个有效点；请检查失败点并重新采集";
    PlaneQualitySummary = CalibrationQualitySummary.Empty(validationMessage);
    return;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run:

```powershell
dotnet test CVWork\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj --filter CalibrationViewModelTests --no-restore
```

Expected: PASS.

- [ ] **Step 6: Commit if git is available**

Run:

```powershell
git status --short
```

Expected in this workspace: `fatal: not a git repository`. If run inside a git checkout, commit:

```powershell
git add CVWork\VisionStation.Vision.UI\ViewModels\CalibrationViewModel.cs CVWork\VisionStation.Vision.UI.Tests\CalibrationViewModelTests.cs
git commit -m "feat: wire calibration quality state"
```

---

### Task 3: Rebuild the Calibration Page as Two Tabs

**Files:**
- Modify: `CVWork/VisionStation.Vision.UI/Views/CalibrationView.xaml`

- [ ] **Step 1: Replace the mixed body with a TabControl shell**

Replace the current `Grid Grid.Row="2"` body with a `TabControl` bound to `SelectedCalibrationTabIndex`:

```xml
<TabControl Grid.Row="2"
            SelectedIndex="{Binding SelectedCalibrationTabIndex}"
            IsEnabled="{Binding CanRunAutomation}">
    <TabItem Header="平面标定">
        <!-- Plane calibration workbench goes here. -->
    </TabItem>
    <TabItem Header="相机内参">
        <!-- Camera intrinsic workbench goes here. -->
    </TabItem>
</TabControl>
```

Preserve the existing top header and workflow summary strip.

- [ ] **Step 2: Move plane controls into the first tab**

Inside the `平面标定` tab, create a three-row workbench:

```xml
<Grid Margin="0,12,0,0">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="*" />
        <RowDefinition Height="126" />
    </Grid.RowDefinitions>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="320" />
        <ColumnDefinition Width="12" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>
</Grid>
```

Place common acquisition settings in the left column:

- `SelectedPointTool`
- `AxisXKeyText`
- `AxisYKeyText`
- `GridStepXText`
- `GridStepYText`
- `PlaneUnitText`
- `SelectedPlaneModel`
- `RunAutoPlaneCalibrationCommand`
- `CalculatePlaneCommand`
- `SavePlaneCommand`
- `ClearAutoPlaneSamplesCommand`

Move advanced settings into an `Expander`:

```xml
<Expander Header="高级运动设置"
          IsExpanded="{Binding IsAdvancedPlaneSettingsExpanded}">
    <UniformGrid Columns="2">
        <!-- MotionSpeedText, MotionAccelerationText, SettleDelayMsText, RansacThresholdText,
             UseEncoderPosition, ReturnToStartAfterAuto -->
    </UniformGrid>
</Expander>
```

- [ ] **Step 3: Place the plane preview and sample table together**

In the main plane area, keep the existing `ZoomableImageSurface` bound to:

- `SelectedAutoPlaneFrame`
- `AutoPlaneOverlays`
- `IsAutoPlanePreviewMissing`

Place `AutoPlaneSamples` below or beside the preview using the existing columns:

- `Index`
- `TargetX`
- `TargetY`
- `ActualX`
- `ActualY`
- `ImageX`
- `ImageY`
- `Status`
- `Message`

- [ ] **Step 4: Add plane quality result panel**

Replace the plain plane result text block with bindings to:

```xml
<TextBlock Text="{Binding PlaneQualitySummary.Label}" FontWeight="SemiBold" />
<TextBlock Text="{Binding PlaneQualitySummary.Details}" TextWrapping="Wrap" />
<TextBlock Text="{Binding PlaneQualitySummary.MaxErrorPointText}" TextWrapping="Wrap" />
<TextBox Text="{Binding PlaneMatrixText, Mode=OneWay}" IsReadOnly="True" TextWrapping="Wrap" />
```

Keep `PlaneResultText` available if desired, but the visible summary should prefer `PlaneQualitySummary`.

- [ ] **Step 5: Move camera intrinsic UI into the second tab**

Inside the `相机内参` tab, create a two-column layout:

- Left column: pattern settings and actions.
- Main column: preview, image detection table, result panel.

Preserve existing bindings:

- `ChessboardColumnsText`
- `ChessboardRowsText`
- `SquareSizeText`
- `CameraUnitText`
- `RunCameraDirectoryCalibrationCommand`
- `SaveCameraCommand`
- `ClearCameraImagesCommand`
- `SelectedChessboardFrame`
- `ChessboardOverlays`
- `ChessboardObservations`
- `CameraMatrixText`
- `DistortionText`

Use `CameraQualitySummary` for the visible summary:

```xml
<TextBlock Text="{Binding CameraQualitySummary.Label}" FontWeight="SemiBold" />
<TextBlock Text="{Binding CameraQualitySummary.Details}" TextWrapping="Wrap" />
```

- [ ] **Step 6: Build to catch XAML errors**

Run:

```powershell
dotnet build CVWork\VisionStation.Client\VisionStation.Client.csproj --no-restore
```

Expected: PASS.

- [ ] **Step 7: Commit if git is available**

Run:

```powershell
git status --short
```

Expected in this workspace: `fatal: not a git repository`. If run inside a git checkout, commit:

```powershell
git add CVWork\VisionStation.Vision.UI\Views\CalibrationView.xaml
git commit -m "feat: split calibration page into tabs"
```

---

### Task 4: Verify Regression Coverage

**Files:**
- Modify if needed: `CVWork/VisionStation.Vision.UI/ViewModels/CalibrationViewModel.cs`
- Modify if needed: `CVWork/VisionStation.Vision.UI/Views/CalibrationView.xaml`

- [ ] **Step 1: Run calibration algorithm tests**

Run:

```powershell
dotnet test CVWork\VisionStation.Vision.Tests\VisionStation.Vision.Tests.csproj --filter CalibrationServiceTests --no-restore
```

Expected: PASS with 5 tests passed.

- [ ] **Step 2: Run UI tests**

Run:

```powershell
dotnet test CVWork\VisionStation.Vision.UI.Tests\VisionStation.Vision.UI.Tests.csproj --no-restore
```

Expected: PASS.

- [ ] **Step 3: Build the client**

Run:

```powershell
dotnet build CVWork\VisionStation.Client\VisionStation.Client.csproj --no-restore
```

Expected: PASS.

- [ ] **Step 4: Manual smoke check**

Open the WPF app if the environment supports it and confirm:

- Calibration page opens with `平面标定` selected.
- `高级运动设置` is collapsed by default.
- Switching to `相机内参` does not clear plane samples or results.
- Switching back to `平面标定` preserves current in-memory values.
- Reload loads saved calibration from the current recipe.

- [ ] **Step 5: Commit if git is available**

Run:

```powershell
git status --short
```

Expected in this workspace: `fatal: not a git repository`. If run inside a git checkout, commit:

```powershell
git add CVWork\VisionStation.Vision.UI\ViewModels\CalibrationViewModel.cs CVWork\VisionStation.Vision.UI\Views\CalibrationView.xaml CVWork\VisionStation.Vision.UI.Tests\CalibrationViewModelTests.cs
git commit -m "test: verify calibration page redesign"
```
