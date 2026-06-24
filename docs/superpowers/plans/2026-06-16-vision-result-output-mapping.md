# Vision Result Output Mapping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the recipe step property editor expose typed visual result outputs so a vision flow can publish images, points, distances, angles, and other result values into runtime parameters.

**Architecture:** Reuse the existing `VisionResultDefinition` model as the persisted output mapping. The run-vision step editor will show this mapping beside flow id and timeout, while the application layer continues to resolve `SourceToolId` plus `SourceKey=port:<PortKey>` through `VisionResultResolver` and `VisionPortValueFormatter`.

**Tech Stack:** .NET 8, WPF, Prism, xUnit, existing `VisionStation.Domain`, `VisionStation.Application`, `VisionStation.Vision.UI`, and `VisionStation.Client`.

---

### Task 1: Runtime Port Formatting Coverage

**Files:**
- Test: `VisionStation.Application.Tests/VisionPortValueFormatterTests.cs`
- Modify: `VisionStation.Application/Inspection/VisionPortValueFormatter.cs`

- [ ] **Step 1: Write failing tests**

Add tests proving `ImageOutput`, point, distance, and angle ports format into receivable string values:

```csharp
[Theory]
[InlineData("ImageOutput", "processed-frame")]
[InlineData("AngleOutput", "45.5")]
[InlineData("MeasureValueOutput", "12.34")]
public void FormatPortValue_FormatsTypedOutputPorts(string portKey, string expected)
{
    var result = new ToolResult
    {
        Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["outputFrameId"] = "processed-frame",
            ["angle"] = "45.5",
            ["distance"] = "12.34"
        }
    };

    Assert.Equal(expected, VisionPortValueFormatter.FormatPortValue(result, portKey, null));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test VisionStation.Application.Tests/VisionStation.Application.Tests.csproj --filter VisionPortValueFormatterTests`

Expected before implementation: `ImageOutput` returns empty or the wrong image reference.

- [ ] **Step 3: Implement minimal formatter fix**

Update `ImageOutput` formatting to prefer `outputFrameId`, then fall back to `frameId`, `inputFrameId`, and `source`.

- [ ] **Step 4: Run tests**

Run: `dotnet test VisionStation.Application.Tests/VisionStation.Application.Tests.csproj --filter VisionPortValueFormatterTests`

Expected after implementation: all filtered tests pass.

### Task 2: Recipe Result Mapping UI Options

**Files:**
- Modify: `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/UiModels.cs`

- [ ] **Step 1: Add typed options**

Expose result data type options: `Image`, `Point`, `Pose`, `Line`, `Circle`, `Number`, `Text`, `Result`, `Roi`, `Point[]`, `Pose[]`, `Number[]`.

- [ ] **Step 2: Add source port options**

Build source options from the selected recipe flow tools with `VisionToolCatalog.GetOutputPorts(tool.Kind)`, storing `SourceToolId`, `SourceKey=port:<PortKey>`, `DataType`, and display text.

- [ ] **Step 3: Make add-result default contextual**

When the selected process step is a run-vision step, create new mappings with the selected step flow id and the first available source option.

### Task 3: Step Property Editor Surface

**Files:**
- Modify: `VisionStation.Client/Views/ProcessStepPropertyEditorView.xaml`
- Modify: `VisionStation.Client/Views/RecipeManagementView.xaml`

- [ ] **Step 1: Add mapping grid to run-vision block**

Show a compact `VisionResults` grid inside the visible run-vision block with columns for receive parameter name, type, source output, alias, PLC address, and description.

- [ ] **Step 2: Use combo boxes for type/source**

Use `VisionResultDataTypeOptions` for type and `VisionResultSourceOptions` for source output.

- [ ] **Step 3: Keep the existing global table consistent**

Use the same combo columns in the recipe-level result table so both entry points edit the same mapping data.

### Task 4: Verification

**Files:**
- Solution: `CVWork.sln`

- [ ] **Step 1: Run focused tests**

Run: `dotnet test VisionStation.Application.Tests/VisionStation.Application.Tests.csproj`

- [ ] **Step 2: Build client**

Run: `dotnet build VisionStation.Client/VisionStation.Client.csproj`

- [ ] **Step 3: Report status**

Report exact command results, any failures, and changed files.
