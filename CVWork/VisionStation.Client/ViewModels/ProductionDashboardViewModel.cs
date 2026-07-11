using System.Collections.ObjectModel;
using System.ComponentModel;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Application;
using VisionStation.Client.Presentation;
using VisionStation.Client.Services;
using VisionStation.Domain;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.Services;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Client.ViewModels;

public sealed class ProductionDashboardViewModel : BindableBase
{
    private readonly ProductionCoordinator _coordinator;
    private readonly IInspectionExecution _inspectionExecution;
    private readonly IInspectionRecordRepository _records;
    private readonly IRecipeRepository _recipes;
    private readonly IAppLogService _log;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IVisionOverlayBuilder _overlayBuilder;
    private readonly ProductionDashboardLayoutService _layoutService;
    private readonly Dictionary<string, FlowDisplaySnapshot> _latestFlowResults = new(StringComparer.OrdinalIgnoreCase);
    private ProductionDashboardRecipeLayout _displayLayout = new();
    private ProductionSnapshot _productionSnapshot;

    private ImageFrame? _currentFrame;
    private string _lastOutcome = "READY";
    private string _lastOutcomeBrush = "#FF33D6A6";
    private string _lastBarcode = "-";
    private string _lastMessage = "Waiting";
    private string _lastResultSummary = "-";
    private string _cycleTimeText = "0 ms";
    private int _totalCount;
    private int _okCount;
    private int _ngCount;
    private string _yieldText = "100.0%";
    private bool _isBusy;
    private int _activeCommandCount;
    private bool _occupancyMessageVisible;
    private bool _suppressDisplayPanePersistence;
    private string _currentRecipeId = string.Empty;
    private int _displayPaneSeed;
    private int _displayColumnCount = 1;
    private string _flowPanelSubtitle = "加载视觉流程...";

    public ProductionDashboardViewModel(
        ProductionCoordinator coordinator,
        IInspectionExecution inspectionExecution,
        IInspectionRecordRepository records,
        IRecipeRepository recipes,
        IAppLogService log,
        IUiDispatcher uiDispatcher,
        IVisionOverlayBuilder overlayBuilder,
        ProductionDashboardLayoutService layoutService)
    {
        _coordinator = coordinator;
        _inspectionExecution = inspectionExecution;
        _productionSnapshot = coordinator.Snapshot;
        _records = records;
        _recipes = recipes;
        _log = log;
        _uiDispatcher = uiDispatcher;
        _overlayBuilder = overlayBuilder;
        _layoutService = layoutService;

        AddDisplayPaneCommand = new DelegateCommand(AddDisplayPane);
        RemoveDisplayPaneCommand = new DelegateCommand<InspectionDisplayPaneItem>(RemoveDisplayPane, CanRemoveDisplayPane);
        RunSingleCommand = new AsyncDelegateCommand(RunSingleAsync, CanRunSingle)
            .Catch(ReportCommandFailureSafely);
        StartCommand = new AsyncDelegateCommand(StartAsync, CanStart)
            .Catch(ReportCommandFailureSafely);
        StopCommand = new AsyncDelegateCommand(StopAsync, CanStop)
            .Catch(ReportCommandFailureSafely);

        foreach (var entry in _log.Recent(80).Reverse())
        {
            Logs.Add(ToLogItem(entry));
        }

        _log.LogWritten += (_, entry) => _uiDispatcher.Invoke(() => AddLog(entry));
        _coordinator.SnapshotChanged += (_, snapshot) => _uiDispatcher.Invoke(() =>
        {
            _productionSnapshot = snapshot;
            ApplySnapshot(snapshot);
            RefreshExecutionPresentation();
        });
        _coordinator.InspectionCompleted += (_, run) => _uiDispatcher.Invoke(() => ApplyRunResult(run));
        _inspectionExecution.Changed += (_, _) => _uiDispatcher.Invoke(RefreshExecutionPresentation);
        _layoutService.LayoutChanged += (_, _) => _ = ReloadDisplayLayoutAsync();

        ApplySnapshot(_productionSnapshot);
        RefreshExecutionPresentation();

        _ = LoadDisplayFlowsAsync();
        _ = LoadRecentAsync();
    }

    public ObservableCollection<InspectionDisplayPaneItem> DisplayPanes { get; } = new();

    public ObservableCollection<InspectionFlowOptionItem> FlowOptions { get; } = new();

    public ObservableCollection<ToolResultItem> ToolResults { get; } = new();

    public ObservableCollection<VisionOverlayItem> Overlays { get; } = new();

    public ObservableCollection<ResultFieldItem> ResultFields { get; } = new();

    public ObservableCollection<InspectionRecordItem> RecentRecords { get; } = new();

    public ObservableCollection<LogLineItem> Logs { get; } = new();

    public DelegateCommand AddDisplayPaneCommand { get; }

    public DelegateCommand<InspectionDisplayPaneItem> RemoveDisplayPaneCommand { get; }

    public AsyncDelegateCommand RunSingleCommand { get; }

    public AsyncDelegateCommand StartCommand { get; }

    public AsyncDelegateCommand StopCommand { get; }

    public int DisplayColumnCount
    {
        get => _displayColumnCount;
        private set => SetProperty(ref _displayColumnCount, value);
    }

    public string FlowPanelSubtitle
    {
        get => _flowPanelSubtitle;
        private set => SetProperty(ref _flowPanelSubtitle, value);
    }

    public ImageFrame? CurrentFrame
    {
        get => _currentFrame;
        private set => SetProperty(ref _currentFrame, value);
    }

    public string LastOutcome
    {
        get => _lastOutcome;
        private set => SetProperty(ref _lastOutcome, value);
    }

    public string LastOutcomeBrush
    {
        get => _lastOutcomeBrush;
        private set => SetProperty(ref _lastOutcomeBrush, value);
    }

    public string LastBarcode
    {
        get => _lastBarcode;
        private set => SetProperty(ref _lastBarcode, value);
    }

    public string LastMessage
    {
        get => _lastMessage;
        private set => SetProperty(ref _lastMessage, value);
    }

    public string LastResultSummary
    {
        get => _lastResultSummary;
        private set => SetProperty(ref _lastResultSummary, value);
    }

    public string CycleTimeText
    {
        get => _cycleTimeText;
        private set => SetProperty(ref _cycleTimeText, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public int OkCount
    {
        get => _okCount;
        private set => SetProperty(ref _okCount, value);
    }

    public int NgCount
    {
        get => _ngCount;
        private set => SetProperty(ref _ngCount, value);
    }

    public string YieldText
    {
        get => _yieldText;
        private set => SetProperty(ref _yieldText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    private Task RunSingleAsync()
    {
        return ExecuteProductionCommandAsync(async () =>
        {
            var result = await _coordinator.RunSingleAsync();
            return new ProductionCommandResult(result.Disposition, result.Rejection);
        });
    }

    private Task StartAsync()
    {
        return ExecuteProductionCommandAsync(() => _coordinator.StartAsync());
    }

    private Task StopAsync()
    {
        return ExecuteProductionCommandAsync(() => _coordinator.StopAsync());
    }

    private async Task ExecuteProductionCommandAsync(Func<Task<ProductionCommandResult>> execute)
    {
        Interlocked.Increment(ref _activeCommandCount);
        Exception? reportedFailure = null;
        try
        {
            if (!TryInvokeUi(RefreshCommandActivity, out var refreshFailure))
            {
                reportedFailure = refreshFailure;
                ReportCommandFailureSafely(refreshFailure!);
                return;
            }

            try
            {
                var result = await execute();
                if (!TryInvokeUi(() => ApplyCommandResult(result), out var resultFailure))
                {
                    reportedFailure = resultFailure;
                    ReportCommandFailureSafely(resultFailure!);
                }
            }
            catch (Exception ex)
            {
                reportedFailure = ex;
                ReportCommandFailureSafely(ex);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeCommandCount);
            if (!TryInvokeUi(RefreshCommandActivity, out var refreshFailure))
            {
                if (reportedFailure is null)
                {
                    ReportCommandFailureSafely(refreshFailure!);
                }
                else
                {
                    LogErrorSafely($"生产命令状态刷新失败：{refreshFailure!.Message}");
                }
            }
        }
    }

    private bool CanRunSingle()
    {
        return CurrentUiState.CanRunSingle;
    }

    private bool CanStart()
    {
        return CurrentUiState.CanStart;
    }

    private bool CanStop()
    {
        return CurrentUiState.CanStop;
    }

    private ProductionRunUiState CurrentUiState => ProductionRunUiState.Create(
        _productionSnapshot.State,
        _inspectionExecution.Current,
        _productionSnapshot.ActiveSessionId,
        Volatile.Read(ref _activeCommandCount) > 0);

    private void RefreshCommandActivity()
    {
        IsBusy = Volatile.Read(ref _activeCommandCount) > 0;
        RaiseProductionCommandCanExecuteChanged();
    }

    private void RefreshExecutionPresentation()
    {
        var current = _inspectionExecution.Current;
        var uiState = ProductionRunUiState.Create(
            _productionSnapshot.State,
            current,
            _productionSnapshot.ActiveSessionId,
            Volatile.Read(ref _activeCommandCount) > 0);

        if (uiState.IsExternallyOccupied)
        {
            LastMessage = uiState.OccupancyText;
            _occupancyMessageVisible = true;
        }
        else if (_occupancyMessageVisible)
        {
            LastMessage = current is null
                ? "检测执行占用已释放"
                : $"{current.Intent.Mode.DisplayName}已取得检测执行权";
            _occupancyMessageVisible = false;
        }

        RaiseProductionCommandCanExecuteChanged();
    }

    private void RaiseProductionCommandCanExecuteChanged()
    {
        RunSingleCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    private void ApplyCommandResult(ProductionCommandResult result)
    {
        if (result.Disposition == ProductionCommandDisposition.Rejected)
        {
            LastMessage = result.Rejection is null
                ? "生产检测正在申请执行权"
                : ProductionRunUiState.FormatRejection(result.Rejection);
        }
        else if (result.Disposition == ProductionCommandDisposition.Canceled)
        {
            LastMessage = "生产检测已取消";
        }
    }

    private void ReportCommandFailureSafely(Exception exception)
    {
        var message = $"生产命令失败：{exception.Message}";
        InvokeUiSafely(() => LastMessage = message);
        LogErrorSafely(message);
    }

    private bool TryInvokeUi(Action action, out Exception? failure)
    {
        try
        {
            _uiDispatcher.Invoke(action);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    private void InvokeUiSafely(Action action)
    {
        _ = TryInvokeUi(action, out _);
    }

    private void LogErrorSafely(string message)
    {
        try
        {
            _log.Error("Production", message);
        }
        catch
        {
        }
    }

    private async Task LoadDisplayFlowsAsync()
    {
        var recipe = await _recipes.GetCurrentAsync();
        var layout = _layoutService.LoadRecipeLayout(recipe.Id);
        _uiDispatcher.Invoke(() =>
        {
            _displayLayout = layout;
            RefreshFlowOptions(recipe, restoreSavedLayout: true);
        });
    }

    private async Task ReloadDisplayLayoutAsync()
    {
        var recipe = await _recipes.GetCurrentAsync();
        var layout = _layoutService.LoadRecipeLayout(recipe.Id);
        _uiDispatcher.Invoke(() =>
        {
            _displayLayout = layout;
            RefreshFlowOptions(recipe, restoreSavedLayout: true);
        });
    }

    private async Task LoadRecentAsync()
    {
        var records = await _records.RecentAsync(12);
        _uiDispatcher.Invoke(() =>
        {
            RecentRecords.Clear();
            foreach (var record in records)
            {
                RecentRecords.Add(ToRecordItem(record));
            }
        });
    }

    private void AddDisplayPane()
    {
        AddDisplayPane(ResolveDefaultFlowOption(), persistLayout: true);
    }

    private void AddDisplayPane(InspectionFlowOptionItem? selectedFlow, bool persistLayout)
    {
        var pane = new InspectionDisplayPaneItem($"窗口 {_displayPaneSeed + 1}", selectedFlow);
        _displayPaneSeed++;
        pane.PropertyChanged += OnDisplayPanePropertyChanged;
        DisplayPanes.Add(pane);
        UpdateDisplayPaneLayout();
        ApplyCachedFlowResult(pane);
        if (persistLayout)
        {
            SaveDisplayLayout();
        }
    }

    private bool CanRemoveDisplayPane(InspectionDisplayPaneItem? pane)
    {
        return pane is not null && DisplayPanes.Count > 1;
    }

    private void RemoveDisplayPane(InspectionDisplayPaneItem? pane)
    {
        if (pane is null || DisplayPanes.Count <= 1)
        {
            return;
        }

        pane.PropertyChanged -= OnDisplayPanePropertyChanged;
        DisplayPanes.Remove(pane);
        UpdateDisplayPaneLayout();
        SaveDisplayLayout();
    }

    private void OnDisplayPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectionDisplayPaneItem.SelectedFlow) &&
            sender is InspectionDisplayPaneItem pane)
        {
            ApplyCachedFlowResult(pane);
            SaveDisplayLayout();
        }
    }

    private InspectionFlowOptionItem? ResolveDefaultFlowOption()
    {
        return FlowOptions.FirstOrDefault(option => DisplayPanes.All(pane =>
                   !string.Equals(pane.BoundFlowId, option.FlowId, StringComparison.OrdinalIgnoreCase)))
               ?? FlowOptions.FirstOrDefault();
    }

    private void RefreshFlowOptions(Recipe recipe, bool restoreSavedLayout = false)
    {
        var normalized = recipe.WithNormalizedFlows();
        var recipeChanged = !string.Equals(_currentRecipeId, normalized.Id, StringComparison.OrdinalIgnoreCase);
        _currentRecipeId = normalized.Id;
        var selectedFlowIds = DisplayPanes.ToDictionary(pane => pane, pane => pane.BoundFlowId);

        FlowOptions.Clear();
        foreach (var flow in normalized.EffectiveFlows)
        {
            FlowOptions.Add(new InspectionFlowOptionItem(flow.Id, flow.Name));
        }

        if ((restoreSavedLayout || recipeChanged) && TryRestoreDisplayPanesFromLayout(normalized.Id))
        {
            return;
        }

        foreach (var pane in DisplayPanes)
        {
            var selectedFlowId = selectedFlowIds.GetValueOrDefault(pane);
            pane.SelectedFlow = FlowOptions.FirstOrDefault(option =>
                                    string.Equals(option.FlowId, selectedFlowId, StringComparison.OrdinalIgnoreCase))
                                ?? FlowOptions.FirstOrDefault();
        }

        if (DisplayPanes.Count == 0)
        {
            AddDisplayPane(ResolveDefaultFlowOption(), persistLayout: false);
            SaveDisplayLayout();
        }
        else
        {
            UpdateDisplayPaneLayout();
        }
    }

    private bool TryRestoreDisplayPanesFromLayout(string recipeId)
    {
        if (_displayLayout.Panes.Count == 0)
        {
            return false;
        }

        _suppressDisplayPanePersistence = true;
        try
        {
            foreach (var pane in DisplayPanes)
            {
                pane.PropertyChanged -= OnDisplayPanePropertyChanged;
            }

            DisplayPanes.Clear();
            _displayPaneSeed = 0;

            foreach (var savedPane in _displayLayout.Panes)
            {
                var selectedFlow = ResolveFlowOption(savedPane.FlowId) ?? ResolveDefaultFlowOption();
                AddDisplayPane(selectedFlow, persistLayout: false);
            }
        }
        finally
        {
            _suppressDisplayPanePersistence = false;
        }

        UpdateDisplayPaneLayout();
        return DisplayPanes.Count > 0;
    }

    private InspectionFlowOptionItem? ResolveFlowOption(string? flowId)
    {
        return FlowOptions.FirstOrDefault(option =>
            string.Equals(option.FlowId, flowId, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateDisplayPaneLayout()
    {
        DisplayColumnCount = DisplayPanes.Count <= 1 ? 1 : 2;
        FlowPanelSubtitle = $"{DisplayPanes.Count} 个窗口 / {FlowOptions.Count} 个流程";
        RemoveDisplayPaneCommand.RaiseCanExecuteChanged();
    }

    private void SaveDisplayLayout()
    {
        if (_suppressDisplayPanePersistence || string.IsNullOrWhiteSpace(_currentRecipeId))
        {
            return;
        }

        var snapshot = new ProductionDashboardRecipeLayout
        {
            Panes = DisplayPanes
                .Select(pane => new ProductionDashboardPaneLayout { FlowId = pane.BoundFlowId })
                .ToList()
        };

        try
        {
            _displayLayout = snapshot.Clone();
            _layoutService.SaveRecipeLayout(_currentRecipeId, snapshot);
        }
        catch (Exception ex)
        {
            _log.Warning("Production", $"主页面检测窗口布局保存失败：{ex.Message}");
        }
    }

    private void ApplyRunResult(InspectionRunResult run)
    {
        RefreshFlowOptions(run.Recipe);

        CurrentFrame = run.ResultFrame;
        LastOutcome = ToOutcomeText(run.Result.Outcome);
        LastOutcomeBrush = ResolveOutcomeBrush(run.Result.Outcome);
        LastBarcode = string.IsNullOrWhiteSpace(run.Result.Barcode) ? "-" : run.Result.Barcode;
        LastMessage = run.Result.Message;
        LastResultSummary = BuildResultSummary(run.Result.ResultData);
        CycleTimeText = $"{run.Result.CycleTime.TotalMilliseconds:0} ms";

        ToolResults.Clear();
        Overlays.Clear();
        ResultFields.Clear();
        foreach (var tool in run.Result.ToolResults)
        {
            ToolResults.Add(new ToolResultItem(
                tool.ToolName,
                tool.Kind.ToString(),
                tool.Outcome.ToString(),
                $"{tool.Duration.TotalMilliseconds:0.0} ms",
                tool.Message));
        }

        foreach (var item in ToResultFieldItems(run.Result.ResultData))
        {
            ResultFields.Add(item);
        }

        foreach (var overlay in CreateResultPreviewOverlays(_overlayBuilder.Build(run.Recipe, run.ResultFrame, run.Result.ToolResults, run.Result.Outcome)))
        {
            Overlays.Add(overlay);
        }

        CacheFlowResults(run);
        foreach (var pane in DisplayPanes)
        {
            ApplyCachedFlowResult(pane);
        }

        RecentRecords.Insert(0, ToRecordItem(run.Result));
        while (RecentRecords.Count > 12)
        {
            RecentRecords.RemoveAt(RecentRecords.Count - 1);
        }
    }

    private void CacheFlowResults(InspectionRunResult run)
    {
        var flowResults = run.FlowResults.Count > 0
            ? run.FlowResults
            :
            [
                new FlowRunResult(
                    run.Recipe.CurrentFlowId,
                    run.Recipe.GetActiveFlow().Name,
                    run.ResultFrame,
                    run.Result.ToolResults,
                    run.Result.Outcome,
                    run.Result.Barcode,
                    run.Result.Message)
            ];

        foreach (var flowResult in flowResults)
        {
            var flowRecipe = ResolveFlowRecipe(run.Recipe, flowResult.FlowId);
            var overlays = CreateResultPreviewOverlays(_overlayBuilder.Build(
                flowRecipe,
                flowResult.ResultFrame,
                flowResult.ToolResults,
                flowResult.Outcome));

            _latestFlowResults[flowResult.FlowId] = new FlowDisplaySnapshot(
                flowResult.ResultFrame,
                overlays,
                ToOutcomeText(flowResult.Outcome),
                ResolveOutcomeBrush(flowResult.Outcome),
                string.IsNullOrWhiteSpace(flowResult.Message) ? "流程完成" : flowResult.Message,
                string.IsNullOrWhiteSpace(flowResult.Barcode) ? "-" : flowResult.Barcode);
        }
    }

    private void ApplyCachedFlowResult(InspectionDisplayPaneItem pane)
    {
        if (pane.SelectedFlow is null)
        {
            ClearDisplayPane(pane, "请选择视觉流程");
            return;
        }

        if (!_latestFlowResults.TryGetValue(pane.SelectedFlow.FlowId, out var snapshot))
        {
            ClearDisplayPane(pane, $"等待 {pane.SelectedFlow.FlowName} 结果");
            return;
        }

        pane.CurrentFrame = snapshot.Frame;
        pane.HasResult = true;
        pane.LastOutcome = snapshot.OutcomeText;
        pane.LastOutcomeBrush = snapshot.OutcomeBrush;
        pane.LastMessage = snapshot.Message;
        pane.LastBarcode = snapshot.Barcode;

        pane.Overlays.Clear();
        foreach (var overlay in snapshot.Overlays)
        {
            pane.Overlays.Add(overlay);
        }
    }

    private static void ClearDisplayPane(InspectionDisplayPaneItem pane, string message)
    {
        pane.CurrentFrame = null;
        pane.HasResult = false;
        pane.LastOutcome = "READY";
        pane.LastOutcomeBrush = "#FF33D6A6";
        pane.LastMessage = message;
        pane.LastBarcode = "-";
        pane.Overlays.Clear();
    }

    private static IReadOnlyList<VisionOverlayItem> CreateResultPreviewOverlays(IReadOnlyList<VisionOverlayItem> overlays)
    {
        return overlays
            .Where(overlay => overlay.Kind != VisionOverlayKind.DirectionAxis)
            .Where(overlay => overlay.State != VisionOverlayState.Neutral)
            .Select(overlay => overlay with { Label = string.Empty })
            .ToArray();
    }

    private static Recipe ResolveFlowRecipe(Recipe recipe, string flowId)
    {
        var normalized = recipe.WithNormalizedFlows();
        var flow = normalized.EffectiveFlows.FirstOrDefault(item =>
            string.Equals(item.Id, flowId, StringComparison.OrdinalIgnoreCase));

        return flow is null ? normalized : normalized.WithActiveFlow(flow);
    }

    private void ApplySnapshot(ProductionSnapshot snapshot)
    {
        TotalCount = snapshot.TotalCount;
        OkCount = snapshot.OkCount;
        NgCount = snapshot.NgCount;
        YieldText = $"{snapshot.YieldRate:0.0}%";
        CycleTimeText = $"{snapshot.LastCycleTime.TotalMilliseconds:0} ms";
    }

    private void AddLog(AppLogEntry entry)
    {
        Logs.Insert(0, ToLogItem(entry));
        while (Logs.Count > 80)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    private static InspectionRecordItem ToRecordItem(InspectionResult record)
    {
        return new InspectionRecordItem(
            record.Timestamp.ToString("HH:mm:ss"),
            record.RecipeName,
            record.Outcome.ToString(),
            $"{record.CycleTime.TotalMilliseconds:0} ms",
            string.IsNullOrWhiteSpace(record.Barcode) ? "-" : record.Barcode,
            BuildResultSummary(record.ResultData),
            ToResultFieldItems(record.ResultData),
            record.OriginalImagePath,
            record.ResultImagePath);
    }

    private static string BuildResultSummary(IReadOnlyDictionary<string, string> resultData)
    {
        if (resultData.Count == 0)
        {
            return "-";
        }

        return string.Join(" | ", resultData.Take(4).Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static IReadOnlyList<ResultFieldItem> ToResultFieldItems(IReadOnlyDictionary<string, string> resultData)
    {
        return resultData
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ResultFieldItem(pair.Key, pair.Value))
            .ToArray();
    }

    private static LogLineItem ToLogItem(AppLogEntry entry)
    {
        return new LogLineItem(
            entry.Timestamp.ToString("HH:mm:ss"),
            entry.Level,
            OperatorMessageLocalizer.LocalizeSource(entry.Source),
            OperatorMessageLocalizer.LocalizeMessage(entry.Message));
    }

    private static string ToOutcomeText(InspectionOutcome outcome)
    {
        return outcome switch
        {
            InspectionOutcome.Ok => "OK",
            InspectionOutcome.Ng => "NG",
            InspectionOutcome.Error => "ERROR",
            _ => outcome.ToString().ToUpperInvariant()
        };
    }

    private static string ResolveOutcomeBrush(InspectionOutcome outcome)
    {
        return outcome == InspectionOutcome.Ok ? "#FF42E58E" : "#FFFF5C7A";
    }

    private sealed record FlowDisplaySnapshot(
        ImageFrame Frame,
        IReadOnlyList<VisionOverlayItem> Overlays,
        string OutcomeText,
        string OutcomeBrush,
        string Message,
        string Barcode);
}
