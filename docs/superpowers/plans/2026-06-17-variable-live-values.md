# Variable Live Values Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show visual-result snapshots and lightweight live device values in the variable center "current value" column without saving those transient values back into recipes.

**Architecture:** Keep recipe `CurrentValue` as the editable persisted initial value. Add a separate display/live value on `RecipeVariableItem`, feed it from inspection snapshots, communication frames, and a low-frequency device polling loop while the variable center is loaded.

**Tech Stack:** .NET 8, WPF, Prism, xUnit.

---

### Task 1: Live Display Model

**Files:**
- Modify: `VisionStation.Client/ViewModels/VariableCenterViewModel.cs`
- Test: `VisionStation.Vision.UI.Tests/VariableCenterLiveValueTests.cs`

- [ ] Add tests proving runtime snapshots update a variable display value by `Key`, `Name`, or `SourceBindingKey`.
- [ ] Add tests proving live display updates do not change persisted `CurrentValue`.
- [ ] Add `LiveValue`, `LiveValueUpdatedAt`, and `DisplayedCurrentValue` to `RecipeVariableItem`.

### Task 2: Snapshot And Frame Updates

**Files:**
- Modify: `VisionStation.Client/ViewModels/VariableCenterViewModel.cs`
- Modify: `VisionStation.Client/Views/VariableCenterView.xaml`

- [ ] Update `ApplyRuntimeSnapshot` to update matching `RecipeVariableItem` instances.
- [ ] Subscribe to `ICommunicationChannelRuntime.FrameReceived` and map latest TCP/Serial frames into matching variables.
- [ ] Bind the current-value column to `DisplayedCurrentValue` as read-only.

### Task 3: Lightweight Device Polling

**Files:**
- Modify: `VisionStation.Client/ViewModels/VariableCenterViewModel.cs`
- Modify: `VisionStation.Client/App.xaml.cs`

- [ ] Inject `ICommunicationChannelRuntime`, `IDeviceRuntime`, `IPlcClient`, and `IDigitalIoController`.
- [ ] Start a one-second polling loop when the view model loads.
- [ ] Poll only enabled visible recipe variables with PLC or IO sources.
- [ ] Stop work through cancellation when the view model is no longer usable.

### Task 4: Verification

**Commands:**
- `dotnet test VisionStation.Vision.UI.Tests/VisionStation.Vision.UI.Tests.csproj --no-restore`
- `dotnet build VisionStation.Client/VisionStation.Client.csproj --no-restore`
