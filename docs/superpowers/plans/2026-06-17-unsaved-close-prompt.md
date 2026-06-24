# Unsaved Close Prompt Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Warn on application close when editable modules have unsaved changes, with Save and close, Close without saving, and Cancel choices.

**Architecture:** Add a shared unsaved-change registry in `VisionStation.Application` so Client, Vision UI, and Devices UI can register dirty state without circular references. The WPF shell asks the registry for pending items during `Closing`, shows a three-action dialog, and either saves all registered items, discards by continuing close, or cancels close.

**Tech Stack:** .NET 8, WPF, Prism/DryIoc, xUnit.

---

### Task 1: Shared Unsaved-Change Service

**Files:**
- Create: `VisionStation.Application/Presentation/UnsavedChangesService.cs`
- Test: `VisionStation.Application.Tests/UnsavedChangesServiceTests.cs`

- [ ] **Step 1: Write failing service tests**

Create tests for registering dirty modules, clearing them, saving all dirty modules, and preserving dirty state after save failure.

- [ ] **Step 2: Run the focused test**

Run: `dotnet test VisionStation.Application.Tests/VisionStation.Application.Tests.csproj --filter UnsavedChangesServiceTests`

Expected: fail because `IUnsavedChangesService` and `UnsavedChangesService` do not exist.

- [ ] **Step 3: Implement the service**

Add immutable `UnsavedChangeItem`, interface `IUnsavedChangesService`, and thread-safe implementation `UnsavedChangesService`.

- [ ] **Step 4: Re-run focused tests**

Run: `dotnet test VisionStation.Application.Tests/VisionStation.Application.Tests.csproj --filter UnsavedChangesServiceTests`

Expected: pass.

### Task 2: Close Dialog And Shell Guard

**Files:**
- Modify: `VisionStation.Client/App.xaml.cs`
- Modify: `VisionStation.Client/ViewModels/ShellWindowViewModel.cs`
- Modify: `VisionStation.Client/Views/ShellWindow.xaml.cs`
- Create: `VisionStation.Client/Views/UnsavedChangesDialog.xaml`
- Create: `VisionStation.Client/Views/UnsavedChangesDialog.xaml.cs`

- [ ] **Step 1: Register the service**

Register `IUnsavedChangesService` as a singleton in Prism.

- [ ] **Step 2: Add shell accessors**

Inject the service into `ShellWindowViewModel` and expose methods to list and save pending changes.

- [ ] **Step 3: Add modal dialog**

Create a compact WPF dialog with buttons: `保存并关闭`, `不保存关闭`, `取消`.

- [ ] **Step 4: Guard closing**

Handle `ShellWindow.Closing`; if pending items exist, show the dialog. Save all and re-close on save, re-close directly on discard, or keep the window open on cancel.

### Task 3: Module Dirty-State Integration

**Files:**
- Modify: `VisionStation.Client/ViewModels/RecipeManagementViewModel.cs`
- Modify: `VisionStation.Client/ViewModels/VariableCenterViewModel.cs`
- Modify: `VisionStation.Client/ViewModels/SystemSettingsViewModel.cs`
- Modify: `VisionStation.Devices.UI/ViewModels/DeviceConfigurationViewModel.cs`
- Modify: `VisionStation.Devices.UI/ViewModels/DeviceStatusViewModel.cs`
- Modify: `VisionStation.Vision.UI/ViewModels/VisionDebugViewModel.cs`

- [ ] **Step 1: Register existing dirty flags**

Wire recipe management and variable center `HasUnsavedChanges` setters to the shared service.

- [ ] **Step 2: Add dirty flags to settings/config pages**

Add `HasUnsavedChanges` plus a small `MarkDirty` helper to system settings and device configuration editors.

- [ ] **Step 3: Track vision edits**

Mark vision dirty for flow/tool add, copy, delete, move, enable toggles, and flow name edits; clear dirty after `SaveRecipeAsync` and `LoadRecipeAsync`.

### Task 4: Verification

**Files:**
- Solution-wide verification only.

- [ ] **Step 1: Run focused tests**

Run: `dotnet test VisionStation.Application.Tests/VisionStation.Application.Tests.csproj --filter UnsavedChangesServiceTests`

- [ ] **Step 2: Run affected UI tests**

Run: `dotnet test VisionStation.Vision.UI.Tests/VisionStation.Vision.UI.Tests.csproj`

- [ ] **Step 3: Build the client**

Run: `dotnet build VisionStation.Client/VisionStation.Client.csproj`

Expected: all commands exit with code 0.
