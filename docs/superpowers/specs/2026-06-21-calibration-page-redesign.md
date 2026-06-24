# Visual Calibration Page Redesign

## Context

The current visual calibration page combines camera intrinsic calibration, automatic nine-point plane calibration, and runtime coordinate-transform guidance on one screen. The implementation lives mainly in:

- `CVWork/VisionStation.Vision.UI/Views/CalibrationView.xaml`
- `CVWork/VisionStation.Vision.UI/ViewModels/CalibrationViewModel.cs`
- `CVWork/VisionStation.Vision/OpenCvCalibrationService.cs`
- `CVWork/VisionStation.Vision/Tools/CoordinateTransformTool.cs`

The user for this page is a vision/debug engineer, not a basic operator. The page should keep engineering detail available, but reduce first-glance complexity and make the most common task, nine-point plane calibration, faster to operate and diagnose.

## Goals

- Make plane calibration the default work area.
- Keep camera intrinsic calibration one click away.
- Preserve access to engineering data: sample rows, images, overlays, RMS, max error, inlier count, matrices, and failure reasons.
- Separate common controls from advanced tuning controls.
- Make calibration quality easier to judge before saving.
- Keep the current calibration data model and persistence path: results remain stored on the current recipe camera settings.

## Non-Goals

- Do not change the OpenCV calibration algorithms.
- Do not change recipe persistence format beyond any UI-driven fields already stored in `CameraCalibrationResult` and `PlaneCalibrationResult`.
- Do not add a new calibration database or global device calibration store.
- Do not redesign the full vision-debug page.
- Do not introduce a wizard that hides engineering data behind many next/back steps.

## Chosen Approach

Use a two-tab engineer workbench:

- `平面标定` is selected by default.
- `相机内参` is the second tab.

This keeps both workflows available while removing the current side-by-side competition. The page should feel like two focused tools under one calibration shell rather than one large mixed form.

## Page Shell

The top shell stays compact and global:

- Page title: `视觉标定`
- Current recipe: `配方名 / 产品码`
- Optional status summary: whether current recipe already has plane calibration and camera intrinsics.
- Actions: `打开视觉流程`, `重新载入`
- Main content: two tabs, `平面标定` and `相机内参`

Switching tabs must not clear current unsaved in-memory results. Reload still reloads the current recipe and existing saved calibration results.

## Plane Calibration Tab

Plane calibration is the primary workflow. It should be arranged as an engineer workbench with three logical areas.

### Acquisition Settings

Show the common settings near the primary action:

- Vision point tool selector
- X axis key
- Y axis key
- X step
- Y step
- Plane unit
- Model selector: `Affine`, `Homography`, `Auto`

Primary action:

- `跑九点并计算`

Secondary actions:

- `重新计算`
- `保存当前矩阵`
- `清空`

The model selector remains visible because vision engineers may intentionally compare affine and homography results during debug.

### Advanced Motion Settings

Move these controls into a collapsed `高级运动设置` panel:

- Speed
- Acceleration
- Settling delay
- RANSAC threshold
- Use encoder feedback
- Return to start after completion

Defaults remain the current values. Expanding/collapsing the panel must not reset values.

### Image Preview

The image preview should be larger and visually central:

- Show the selected sample frame.
- Overlay all valid sample points.
- Label points with sample index 1-9.
- Use distinct states: OK, warning/failure, selected.
- Selecting a sample row highlights the corresponding overlay point.

The preview should preserve the existing zoomable image behavior and overlay rendering infrastructure.

### Sample Table

Keep the sample table, but place it near the preview and make it read as a diagnostic artifact:

- Index
- Target X
- Target Y
- Actual X
- Actual Y
- Image X
- Image Y
- Status
- Message/failure reason

Rows with failed point extraction, failed motion, failed grab, or failed tool execution should be visually distinguishable.

### Result and Quality

Replace the current plain result block with a compact quality panel plus matrix details:

- Quality state: `可用`, `警告`, or `不建议保存`
- Model
- RMS error
- Max error
- Point count
- Inlier count
- Max-error point index, when derivable from `PlaneCalibrationResult.PointErrors`
- Matrix text

Quality thresholds use the existing logic:

- RMS <= 0.05: good/usable
- RMS <= 0.2: warning/usable with review
- RMS > 0.2: not recommended without investigation

Saving should still be allowed for engineers, but if quality is not recommended the UI should make the risk visible before save. A hard blocking dialog is not required for the first iteration.

## Camera Intrinsic Tab

Camera intrinsic calibration remains a focused secondary workflow.

### Pattern Settings

Show common controls:

- Inner corner columns
- Inner corner rows
- Square size
- Unit

Actions:

- `选择目录并计算`
- `保存内参`
- `清空`

### Image Preview

Show the selected chessboard image and detected corners:

- Keep the current zoomable preview.
- Overlay detected corners.
- Show a neutral empty state before directory selection.

### Image Detection Table

Keep the image table:

- Image file
- Status
- Image size
- Corner count
- Message/failure reason

### Result

Show:

- RMS reprojection error
- Image size
- View count
- Camera matrix
- Distortion coefficients

Quality labels use the existing thresholds:

- RMS <= 0.5 px: good
- RMS <= 1.0 px: usable, add varied images if possible
- RMS > 1.0 px: high reprojection error

The UI should make clear that camera intrinsic calibration is saved to the current recipe, but it is not currently automatically applied by the runtime vision pipeline.

## Data Flow

The redesign should preserve the existing data flow:

1. `CalibrationViewModel` loads the current recipe through `IRecipeRepository.GetCurrentAsync`.
2. Existing `Camera.PlaneCalibration` and `Camera.CameraCalibration` are shown when present.
3. Plane calibration runs the existing automatic nine-point sequence:
   - Read starting X/Y axis status.
   - Generate a 3x3 sequence around the start position.
   - Move X and Y axes.
   - Grab a frame.
   - Execute the active recipe flow up to and including the selected point tool.
   - Extract image X/Y from the selected tool result.
   - Pair image coordinates with actual axis coordinates.
   - Calculate plane calibration through `OpenCvCalibrationService.CalibratePlane`.
4. Plane calibration saves by updating `recipe.Camera.PlaneCalibration`.
5. Camera intrinsic calibration loads images from a selected directory, detects chessboard corners, calculates camera calibration, and saves by updating `recipe.Camera.CameraCalibration`.
6. Runtime coordinate conversion continues to happen through `CoordinateTransformTool`.

## Component Shape

Prefer a modest decomposition rather than one very large XAML surface:

- Keep `CalibrationView` as the navigation target.
- Split visual sections into local UserControls only if it improves readability:
  - `PlaneCalibrationPanel`
  - `CameraIntrinsicCalibrationPanel`
  - `CalibrationQualitySummary`

The first implementation may keep one ViewModel, but the design should avoid making `CalibrationView.xaml` harder to maintain. If the implementation touches large blocks anyway, moving tab-specific markup into separate views is preferred.

## Error Handling

The page should keep per-row failure reasons rather than hiding them in a single status line.

Plane calibration failures to show inline:

- Missing selected point tool.
- Axis status read failure.
- Axis motion failure.
- Camera grab failure.
- Vision pipeline execution failure.
- Selected tool result not found.
- Selected tool returned NG.
- Image point fields not found.
- Not enough valid points for selected model.
- OpenCV calibration failure.

Camera calibration failures to show inline:

- Directory missing or canceled.
- Image load failure.
- Chessboard not found.
- Not enough valid images.
- Inconsistent image sizes.
- OpenCV calibration failure.

## Validation Rules

- Affine requires at least 3 valid point pairs.
- Homography requires at least 4 valid point pairs.
- Auto requires at least 3 valid point pairs and may choose affine when fewer than 4 points exist.
- Camera intrinsic calibration requires at least 3 valid chessboard detections.
- Pattern columns and rows must be at least 2.
- Square size must be greater than 0.
- Motion speed and acceleration must be greater than 0.

The current code already enforces many of these in service methods. The page should surface validation earlier where it improves engineer feedback.

## Testing

Keep existing algorithm tests:

- `CalibrationServiceTests.NinePointAffineCalibrationMapsImageToWorld`
- `CalibrationServiceTests.HomographyCalibrationMapsPerspectivePlane`
- `CalibrationServiceTests.CameraCalibrationProducesLowSyntheticReprojectionError`
- `CalibrationServiceTests.EstimatePoseReturnsLowReprojectionError`
- `CalibrationServiceTests.CoordinateTransformToolUsesCalibrationMatrixParameter`

Add focused UI/ViewModel tests where practical:

- Existing recipe calibration is loaded into the correct tab state.
- Plane quality summary maps RMS thresholds to the expected state text.
- Homography with only 3 valid point pairs shows a validation failure before calling the service.
- Selecting a sample row updates the selected preview frame and overlay state.
- Camera intrinsic results preserve matrix and distortion display text.

Manual verification:

- Build `VisionStation.Client`.
- Open the calibration page from the vision-debug page.
- Confirm default tab is `平面标定`.
- Run through both empty-state and loaded-recipe states.
- Verify tab switching does not clear in-memory results.

## Acceptance Criteria

- The calibration page opens with `平面标定` selected.
- Plane and camera workflows are separated into tabs.
- Plane common settings and primary action are visible without expanding advanced settings.
- Advanced motion settings are collapsed by default and retain edited values.
- Plane preview, sample table, and result quality are visible in one focused work area.
- Camera intrinsic controls, preview, image table, and result are visible in the camera tab.
- Existing calibration algorithms and persistence behavior remain unchanged.
- Existing `CalibrationServiceTests` continue to pass.
