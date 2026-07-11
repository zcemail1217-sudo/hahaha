using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.Services;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Vision.UI.ViewModels;

public sealed class VisionDebugViewModel : BindableBase
{
    private const string UnsavedChangesKey = "vision-flow";
    private static readonly string[] SupportedImageExtensions = [".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff"];
    private const string ProjectOutputSourcePrefix = "port:";
    private const double DefaultFlowCanvasWidth = 980;
    private const double DefaultFlowNodeX = 32;
    private const double FlowNodeWidth = 190;
    private const double FlowNodeColumnGap = 96;
    private const double FlowNodeRowStride = 108;
    private const double FlowNodeHeaderHeight = 28;
    private const double FlowPortRowHeight = 20;
    private const double FlowPortStartOffset = 39;
    private const double FlowPortEdgeOffset = 12;
    private const double FlowCanvasPadding = 12;

    private readonly IRecipeRepository _recipes;
    private readonly ICameraDevice _camera;
    private readonly IConfigurableCameraDevice _configurableCamera;
    private readonly IImageFrameFileService _imageFiles;
    private readonly IVisionPipeline _pipeline;
    private readonly IVisionOverlayBuilder _overlayBuilder;
    private readonly IToolParameterDialogService _toolParameterDialog;
    private readonly IFlowEditorDialogService _flowEditorDialog;
    private readonly IAppLogService _log;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IRegionManager _regionManager;
    private readonly IUnsavedChangesService _unsavedChanges;
    private readonly SemaphoreSlim _recipeLoadGate = new(1, 1);
    private readonly Task _initialization;
    private Recipe? _currentRecipe;
    private ImageFrame? _currentFrame;
    private ImageFrame? _displayedFlowFrame;
    private ImageFrame? _lastAcquiredFrame;
    private VisionToolItem? _selectedTool;
    private ToolboxTreeItem? _selectedToolboxItem;
    private FlowTreeItem? _selectedFlowItem;
    private VisionFlowItem? _selectedVisionFlow;
    private VisionFlowItem? _selectedImageFlow;
    private CancellationTokenSource? _continuousRunCts;
    private string _recipeName = "Loading";
    private string _flowName = "MainFlow";
    private string _statusText = "Ready";
    private string _debugOutcome = "READY";
    private string _debugOutcomeBrush = "#FF33D6A6";
    private string _debugBarcode = "-";
    private string _debugMessage = "等待调试";
    private string _debugFailureSummary = string.Empty;
    private string _debugCycleTimeText = "0 ms";
    private string _continuousRunText = "连续运行";
    private string _toolboxSearchText = string.Empty;
    private bool _isDebugBusy;
    private bool _isContinuousRunning;
    private bool _isToolboxPaneExpanded = true;
    private bool _isResultImagePaneExpanded = true;
    private bool _isToolOutputPaneExpanded = true;
    private string _toolOutputTitle = "未选择工具";
    private string _toolOutputStatus = "未选择";
    private string _toolOutputStatusBrush = "#FFA9B7C2";
    private string _toolOutputDuration = "-";
    private string _toolOutputMessage = "选择画布中的工具后显示输出";
    private bool _showSelectedToolMatchPoints;
    private string _selectedToolMatchPointTitle = "匹配点 0 个";
    private double _flowCanvasWidth = 920;
    private double _flowCanvasHeight = 420;
    private int _promptCount;
    private int _warningCount;
    private int _errorCount;
    private VisionToolKind _selectedToolKind = VisionToolKind.MeasureDistance;
    private bool _isLoadingFlow;
    private bool _hasUnsavedChanges;
    private string _activeFlowId = "main";
    private readonly Dictionary<string, ToolResult> _latestFlowToolResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FlowImageDisplayState> _flowImageStates = new(StringComparer.OrdinalIgnoreCase);
    private FlowResultImageItem? _selectedFlowResultImage;

    public VisionDebugViewModel(
        IRecipeRepository recipes,
        ICameraDevice camera,
        IConfigurableCameraDevice configurableCamera,
        IImageFrameFileService imageFiles,
        IVisionPipeline pipeline,
        IVisionOverlayBuilder overlayBuilder,
        IToolParameterDialogService toolParameterDialog,
        IFlowEditorDialogService flowEditorDialog,
        IAppLogService log,
        IUiDispatcher uiDispatcher,
        IRegionManager regionManager,
        IUnsavedChangesService unsavedChanges)
    {
        _recipes = recipes;
        _camera = camera;
        _configurableCamera = configurableCamera;
        _imageFiles = imageFiles;
        _pipeline = pipeline;
        _overlayBuilder = overlayBuilder;
        _toolParameterDialog = toolParameterDialog;
        _flowEditorDialog = flowEditorDialog;
        _log = log;
        _uiDispatcher = uiDispatcher;
        _regionManager = regionManager;
        _unsavedChanges = unsavedChanges;
        _log.LogWritten += OnAppLogWritten;

        RefreshImageCommand = new DelegateCommand(async () => await RefreshImageAsync());
        RunDebugCommand = new DelegateCommand(async () => await RunDebugAsync(), () => !IsDebugBusy)
            .ObservesProperty(() => IsDebugBusy);
        ContinuousRunCommand = new DelegateCommand(async () => await ToggleContinuousRunAsync());
        ClearResultCommand = new DelegateCommand(() => ClearDebugResults("已清空调试结果"));
        AddToolCommand = new DelegateCommand(AddTool);
        AddToolboxItemCommand = new DelegateCommand<object>(AddToolboxItem);
        EditToolCommand = new DelegateCommand<object>(async item => await EditToolAsync(item));
        SelectFlowNodeCommand = new DelegateCommand<object>(SelectFlowNode);
        MoveFlowNodeCommand = new DelegateCommand<FlowNodeMoveRequest>(MoveFlowNode);
        OpenFlowEditorCommand = new AsyncDelegateCommand<object>(OpenFlowEditorAsync);
        AutoLayoutFlowCommand = new DelegateCommand(AutoLayoutFlow);
        ConnectFlowPortsCommand = new DelegateCommand<CanvasFlowPortConnectionRequest>(ConnectFlowPorts, CanConnectFlowPorts);
        SelectFlowNodesCommand = new DelegateCommand<FlowNodeSelectionRequest>(ProcessFlowNodeSelection);
        ToggleToolboxPaneCommand = new DelegateCommand(() => IsToolboxPaneExpanded = !IsToolboxPaneExpanded);
        ToggleResultImagePaneCommand = new DelegateCommand(() => IsResultImagePaneExpanded = !IsResultImagePaneExpanded);
        ToggleToolOutputPaneCommand = new DelegateCommand(() => IsToolOutputPaneExpanded = !IsToolOutputPaneExpanded);
        DuplicateSelectedToolCommand = new DelegateCommand(DuplicateSelectedTool, () => SelectedTool is not null)
            .ObservesProperty(() => SelectedTool);
        DuplicateSelectedNodesCommand = new DelegateCommand(DuplicateSelectedNodes, () => SelectedFlowNodes.Count > 0)
            .ObservesProperty(() => SelectedFlowNodes.Count);
        DeleteSelectedToolCommand = new DelegateCommand(DeleteSelectedTool, () => SelectedTool is not null)
            .ObservesProperty(() => SelectedTool);
        MoveToolUpCommand = new DelegateCommand(() => MoveSelectedTool(-1), CanMoveSelectedToolUp)
            .ObservesProperty(() => SelectedTool);
        MoveToolDownCommand = new DelegateCommand(() => MoveSelectedTool(1), CanMoveSelectedToolDown)
            .ObservesProperty(() => SelectedTool);
        SaveRecipeCommand = new DelegateCommand(async () => await SaveRecipeAsync());
        OpenCalibrationCommand = new DelegateCommand(OpenCalibration);
        NewFlowCommand = new DelegateCommand(NewFlow);
        DuplicateFlowCommand = new DelegateCommand(DuplicateFlow, () => SelectedVisionFlow is not null)
            .ObservesProperty(() => SelectedVisionFlow);
        DeleteFlowCommand = new DelegateCommand(DeleteFlow, () => SelectedVisionFlow is not null && VisionFlows.Count > 1)
            .ObservesProperty(() => SelectedVisionFlow);

        RefreshVisibleToolboxCategories();
        _initialization = InitializeRecipeAsync();
    }

    public ObservableCollection<VisionFlowItem> VisionFlows { get; } = new();

    public ObservableCollection<VisionToolItem> Tools { get; } = new();

    private ObservableCollection<RoiEditorItem> _rois = new();

    public ObservableCollection<ToolResultItem> DebugToolResults { get; } = new();

    public ObservableCollection<VisionOverlayItem> Overlays { get; } = new();

    public ObservableCollection<VisionOverlayItem> DisplayedFlowOverlays { get; } = new();

    public ObservableCollection<RoiChoiceItem> RoiChoices { get; } = new();

    public ObservableCollection<ToolboxTreeItem> ToolboxCategories { get; } = CreatePaletteToolbox();

    public ObservableCollection<ToolboxTreeItem> VisibleToolboxCategories { get; } = new();

    public ObservableCollection<FlowTreeItem> FlowItems { get; } = new();

    public ObservableCollection<FlowNodeItem> FlowNodes { get; } = new();

    public ObservableCollection<FlowConnectionItem> FlowConnections { get; } = new();

    public ObservableCollection<FlowNodeItem> SelectedFlowNodes { get; } = new();

    public ObservableCollection<VisionDebugLogItem> DebugLogs { get; } = new();

    public ObservableCollection<FlowResultImageItem> FlowResultImages { get; } = new();

    public ObservableCollection<ToolOutputValueItem> SelectedToolOutputPorts { get; } = new();

    public ObservableCollection<ToolOutputValueItem> SelectedToolRawData { get; } = new();

    public ObservableCollection<MultiTargetMatchPointItem> SelectedToolMatchPoints { get; } = new();

    public IReadOnlyList<VisionToolKind> AvailableToolKinds { get; } = Enum.GetValues<VisionToolKind>()
        .Where(kind => kind is not VisionToolKind.TcpCommunication and not VisionToolKind.SerialCommunication)
        .ToArray();

    public DelegateCommand RefreshImageCommand { get; }

    public DelegateCommand RunDebugCommand { get; }

    public DelegateCommand ContinuousRunCommand { get; }

    public DelegateCommand ClearResultCommand { get; }

    public DelegateCommand AddToolCommand { get; }

    public DelegateCommand<object> AddToolboxItemCommand { get; }

    public DelegateCommand<object> EditToolCommand { get; }

    public DelegateCommand<object> SelectFlowNodeCommand { get; }

    public DelegateCommand<FlowNodeMoveRequest> MoveFlowNodeCommand { get; }

    public AsyncDelegateCommand<object> OpenFlowEditorCommand { get; }

    public DelegateCommand OpenCalibrationCommand { get; }

    public DelegateCommand AutoLayoutFlowCommand { get; }

    public DelegateCommand<CanvasFlowPortConnectionRequest> ConnectFlowPortsCommand { get; }

    public DelegateCommand<FlowNodeSelectionRequest> SelectFlowNodesCommand { get; }

    public DelegateCommand ToggleToolboxPaneCommand { get; }

    public DelegateCommand ToggleResultImagePaneCommand { get; }

    public DelegateCommand ToggleToolOutputPaneCommand { get; }

    public DelegateCommand DuplicateSelectedToolCommand { get; }

    public DelegateCommand DuplicateSelectedNodesCommand { get; }

    public DelegateCommand DeleteSelectedToolCommand { get; }

    public DelegateCommand MoveToolUpCommand { get; }

    public DelegateCommand MoveToolDownCommand { get; }

    public DelegateCommand SaveRecipeCommand { get; }

    public DelegateCommand NewFlowCommand { get; }

    public DelegateCommand DuplicateFlowCommand { get; }

    public DelegateCommand DeleteFlowCommand { get; }

    public string RecipeName
    {
        get => _recipeName;
        private set => SetProperty(ref _recipeName, value);
    }

    public string FlowName
    {
        get => _flowName;
        set
        {
            if (SetProperty(ref _flowName, value) && !_isLoadingFlow)
            {
                var activeItem = VisionFlows.FirstOrDefault(flow => string.Equals(flow.Id, _activeFlowId, StringComparison.OrdinalIgnoreCase));
                if (activeItem is not null && !string.IsNullOrWhiteSpace(value))
                {
                    activeItem.Name = value.Trim();
                    MarkDirty();
                }
            }
        }
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
            {
                _unsavedChanges.SetUnsaved(
                    UnsavedChangesKey,
                    "视觉流程",
                    value,
                    _ => SaveRecipeAsync(),
                    $"{RecipeName} / {FlowName}");
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DebugOutcome
    {
        get => _debugOutcome;
        private set => SetProperty(ref _debugOutcome, value);
    }

    public string DebugOutcomeBrush
    {
        get => _debugOutcomeBrush;
        private set => SetProperty(ref _debugOutcomeBrush, value);
    }

    public string DebugBarcode
    {
        get => _debugBarcode;
        private set => SetProperty(ref _debugBarcode, value);
    }

    public string DebugMessage
    {
        get => _debugMessage;
        private set => SetProperty(ref _debugMessage, value);
    }

    public string DebugFailureSummary
    {
        get => _debugFailureSummary;
        private set
        {
            if (SetProperty(ref _debugFailureSummary, value))
            {
                RaisePropertyChanged(nameof(HasDebugFailure));
            }
        }
    }

    public bool HasDebugFailure => !string.IsNullOrWhiteSpace(DebugFailureSummary);

    public string DebugCycleTimeText
    {
        get => _debugCycleTimeText;
        private set => SetProperty(ref _debugCycleTimeText, value);
    }

    public string ContinuousRunText
    {
        get => _continuousRunText;
        private set => SetProperty(ref _continuousRunText, value);
    }

    public bool IsDebugBusy
    {
        get => _isDebugBusy;
        private set => SetProperty(ref _isDebugBusy, value);
    }

    public bool IsContinuousRunning
    {
        get => _isContinuousRunning;
        private set => SetProperty(ref _isContinuousRunning, value);
    }

    public double FlowCanvasWidth
    {
        get => _flowCanvasWidth;
        private set => SetProperty(ref _flowCanvasWidth, value);
    }

    public double FlowCanvasHeight
    {
        get => _flowCanvasHeight;
        private set => SetProperty(ref _flowCanvasHeight, value);
    }

    public int PromptCount
    {
        get => _promptCount;
        private set => SetProperty(ref _promptCount, value);
    }

    public int WarningCount
    {
        get => _warningCount;
        private set => SetProperty(ref _warningCount, value);
    }

    public int ErrorCount
    {
        get => _errorCount;
        private set => SetProperty(ref _errorCount, value);
    }

    public VisionToolKind SelectedToolKind
    {
        get => _selectedToolKind;
        set => SetProperty(ref _selectedToolKind, value);
    }

    public string ToolboxSearchText
    {
        get => _toolboxSearchText;
        set
        {
            if (SetProperty(ref _toolboxSearchText, value))
            {
                RefreshVisibleToolboxCategories();
            }
        }
    }

    public bool IsToolboxPaneExpanded
    {
        get => _isToolboxPaneExpanded;
        set
        {
            if (SetProperty(ref _isToolboxPaneExpanded, value))
            {
                RaisePropertyChanged(nameof(ToolboxPaneToggleText));
            }
        }
    }

    public bool IsResultImagePaneExpanded
    {
        get => _isResultImagePaneExpanded;
        set
        {
            if (SetProperty(ref _isResultImagePaneExpanded, value))
            {
                RaisePropertyChanged(nameof(ResultImagePaneToggleText));
            }
        }
    }

    public bool IsToolOutputPaneExpanded
    {
        get => _isToolOutputPaneExpanded;
        set
        {
            if (SetProperty(ref _isToolOutputPaneExpanded, value))
            {
                RaisePropertyChanged(nameof(ToolOutputPaneToggleText));
            }
        }
    }

    public string ToolboxPaneToggleText => IsToolboxPaneExpanded ? "<" : ">";

    public string ResultImagePaneToggleText => IsResultImagePaneExpanded ? ">" : "<";

    public string ToolOutputPaneToggleText => IsToolOutputPaneExpanded ? "v" : "^";

    public string ToolOutputTitle
    {
        get => _toolOutputTitle;
        private set => SetProperty(ref _toolOutputTitle, value);
    }

    public string ToolOutputStatus
    {
        get => _toolOutputStatus;
        private set => SetProperty(ref _toolOutputStatus, value);
    }

    public string ToolOutputStatusBrush
    {
        get => _toolOutputStatusBrush;
        private set => SetProperty(ref _toolOutputStatusBrush, value);
    }

    public string ToolOutputDuration
    {
        get => _toolOutputDuration;
        private set => SetProperty(ref _toolOutputDuration, value);
    }

    public string ToolOutputMessage
    {
        get => _toolOutputMessage;
        private set => SetProperty(ref _toolOutputMessage, value);
    }

    public bool ShowSelectedToolMatchPoints
    {
        get => _showSelectedToolMatchPoints;
        private set => SetProperty(ref _showSelectedToolMatchPoints, value);
    }

    public string SelectedToolMatchPointTitle
    {
        get => _selectedToolMatchPointTitle;
        private set => SetProperty(ref _selectedToolMatchPointTitle, value);
    }

    public FlowResultImageItem? SelectedFlowResultImage
    {
        get => _selectedFlowResultImage;
        set => SetProperty(ref _selectedFlowResultImage, value);
    }

    public ImageFrame? CurrentFrame
    {
        get => _currentFrame;
        private set => SetProperty(ref _currentFrame, value);
    }

    public ImageFrame? DisplayedFlowFrame
    {
        get => _displayedFlowFrame;
        private set => SetProperty(ref _displayedFlowFrame, value);
    }

    public VisionToolItem? SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (SetProperty(ref _selectedTool, value))
            {
                MoveToolUpCommand.RaiseCanExecuteChanged();
                MoveToolDownCommand.RaiseCanExecuteChanged();
                RefreshSelectedToolOutput();
                RefreshFlowCanvas();
            }
        }
    }

    public ToolboxTreeItem? SelectedToolboxItem
    {
        get => _selectedToolboxItem;
        set
        {
            if (SetProperty(ref _selectedToolboxItem, value) && value?.Kind is { } kind)
            {
                SelectedToolKind = kind;
            }
        }
    }

    public FlowTreeItem? SelectedFlowItem
    {
        get => _selectedFlowItem;
        set
        {
            if (SetProperty(ref _selectedFlowItem, value) && value?.Tool is not null)
            {
                SelectedTool = value.Tool;
            }
        }
    }

    public VisionFlowItem? SelectedVisionFlow
    {
        get => _selectedVisionFlow;
        set
        {
            if (ReferenceEquals(_selectedVisionFlow, value))
            {
                return;
            }

            if (!_isLoadingFlow && _currentRecipe is not null && _selectedVisionFlow is not null)
            {
                _currentRecipe = BuildWorkingRecipe();
            }

            if (!SetProperty(ref _selectedVisionFlow, value))
            {
                return;
            }

            DuplicateFlowCommand.RaiseCanExecuteChanged();
            DeleteFlowCommand.RaiseCanExecuteChanged();

            if (value is null || _isLoadingFlow || _currentRecipe is null)
            {
                return;
            }

            LoadFlow(value.Id);
        }
    }

    public VisionFlowItem? SelectedImageFlow
    {
        get => _selectedImageFlow;
        set
        {
            if (SetProperty(ref _selectedImageFlow, value))
            {
                RefreshDisplayedFlowImage();
            }
        }
    }

    private async Task LoadRecipeCoreAsync(
        string? recipeId,
        CancellationToken cancellationToken)
    {
        await _recipeLoadGate.WaitAsync(cancellationToken);
        try
        {
            var recipe = string.IsNullOrWhiteSpace(recipeId)
                ? await _recipes.GetCurrentAsync(cancellationToken)
                : await _recipes.GetAsync(recipeId, cancellationToken)
                    ?? await _recipes.GetCurrentAsync(cancellationToken);
            recipe = recipe.WithNormalizedFlows();
            _currentRecipe = recipe;
            _flowImageStates.Clear();
            var activeFlow = recipe.GetActiveFlow();

            _uiDispatcher.Invoke(() =>
            {
                RecipeName = recipe.Name;
                RefreshVisionFlowList(recipe, activeFlow.Id);
                LoadFlowDefinition(activeFlow);
                StatusText = $"Loaded flow {activeFlow.Name} / {_rois.Count} ROI / {Tools.Count} tools";
                ClearDebugResults();
                CurrentFrame = null;
                _lastAcquiredFrame = null;
                FlowResultImages.Clear();
                SelectedFlowResultImage = null;
                DisplayedFlowFrame = null;
                DisplayedFlowOverlays.Clear();
                DebugMessage = "No image loaded";
                HasUnsavedChanges = false;
                AddDebugLog("提示", $"配方加载完成：{recipe.Name} / {activeFlow.Name}");
            });
        }
        finally
        {
            _recipeLoadGate.Release();
        }
    }

    private async Task InitializeRecipeAsync()
    {
        try
        {
            await LoadRecipeCoreAsync(null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error("VisionDebug", $"Initial recipe load failed: {ex.Message}");
        }
    }

    /// <summary>等待首次配方加载完成，不重新投影当前编辑状态。</summary>
    public Task EnsureInitializedAsync(
        CancellationToken cancellationToken = default) =>
        _initialization.WaitAsync(cancellationToken);

    public async Task LoadRecipeAsync(
        string? recipeId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await LoadRecipeCoreAsync(recipeId, cancellationToken);
    }

    private async Task<ImageFrame?> RefreshImageAsync()
    {
        try
        {
            var frame = await LoadActiveFlowInputFrameAsync();
            _uiDispatcher.Invoke(() =>
            {
                _lastAcquiredFrame = frame;
                CurrentFrame = frame;
                FlowResultImages.Clear();
                SelectedFlowResultImage = null;
                StatusText = $"Image refreshed {frame.Width}x{frame.Height}";
                ClearDebugResults();
                SaveActiveFlowImageState();
                AddDebugLog("提示", $"图像刷新完成：{frame.Width}x{frame.Height} / {frame.Source}");
            });

            return frame;
        }
        catch (Exception ex)
        {
            _uiDispatcher.Invoke(() =>
            {
                StatusText = $"图像刷新失败：{ex.Message}";
                AddDebugLog("错误", StatusText);
            });
            _log.Error("VisionDebug", StatusText);
            return null;
        }
    }

    private async Task<ImageFrame> LoadActiveFlowInputFrameAsync()
    {
        var acquireTool = Tools.FirstOrDefault(tool => tool.Enabled && tool.Kind == VisionToolKind.AcquireImage);
        if (acquireTool is null)
        {
            return await GrabCameraFrameAsync();
        }

        var parameters = acquireTool.ToDefinition().Parameters;
        var source = parameters.GetValueOrDefault("source") ?? "Camera";
        ImageFrame frame;
        if (string.Equals(source, "File", StringComparison.OrdinalIgnoreCase))
        {
            var path = parameters.GetValueOrDefault("filePath");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException($"{acquireTool.Name} 使用文件采集，但还没有配置图像路径");
            }

            frame = await _imageFiles.LoadImageAsync(path);
        }
        else if (string.Equals(source, "Directory", StringComparison.OrdinalIgnoreCase))
        {
            frame = await LoadDirectoryInputFrameAsync(acquireTool, parameters);
        }
        else
        {
            frame = await GrabCameraFrameAsync(parameters);
        }

        return GetBool(parameters, "convertColorToGray", false) ? ToGray8(frame) : frame;
    }

    private async Task<ImageFrame> LoadDirectoryInputFrameAsync(
        VisionToolItem acquireTool,
        IReadOnlyDictionary<string, string> parameters)
    {
        var directory = parameters.GetValueOrDefault("directoryPath");
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"{acquireTool.Name} 使用目录采集，但还没有配置图像目录");
        }

        var path = Directory.EnumerateFiles(directory)
            .Where(file => SupportedImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException($"{acquireTool.Name} 的图像目录中没有可用图片", directory);
        }

        return await _imageFiles.LoadImageAsync(path);
    }

    private async Task<ImageFrame> GrabCameraFrameAsync(IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (parameters is not null)
        {
            await _configurableCamera.ApplyAcquisitionSettingsAsync(
                new CameraAcquisitionSettings
                {
                    DeviceId = parameters.GetValueOrDefault("device") ?? parameters.GetValueOrDefault("cameraSerial") ?? string.Empty,
                    ExposureTimeMs = GetExposureTimeMs(parameters, 0),
                    TriggerSource = parameters.GetValueOrDefault("triggerSource") ?? string.Empty,
                    HeartbeatTimeoutMs = (int)Math.Clamp(GetDouble(parameters, "heartbeatTimeoutMs", 3000), 1000, 60000),
                    ClearBufferBeforeTrigger = GetBool(parameters, "clearBufferBeforeTrigger", true)
                });
        }

        if (_camera.Snapshot.State != DeviceConnectionState.Connected)
        {
            await _camera.ConnectAsync();
        }

        return await _camera.GrabAsync();
    }

    private void ApplyAcquiredFrame(VisionToolItem tool, ImageFrame frame)
    {
        _lastAcquiredFrame = frame;
        CurrentFrame = frame;
        Overlays.Clear();
        FlowResultImages.Clear();
        SelectedFlowResultImage = null;
        DebugToolResults.Clear();
        DebugOutcome = "READY";
        DebugOutcomeBrush = "#FF33D6A6";
        DebugBarcode = "-";
        DebugCycleTimeText = "0 ms";
        DebugMessage = $"{tool.Name} output image {frame.Width}x{frame.Height}";
        ClearFlowNodeRunResults();
        StatusText = $"{tool.Name} output image {frame.Width}x{frame.Height}";
        DebugToolResults.Add(new ToolResultItem(
            tool.Name,
            tool.Kind.ToString(),
            "OK",
            "0 ms",
            $"{frame.Width}x{frame.Height} / {frame.Source}"));
        SaveActiveFlowImageState();
    }

    private ImageFrame? GetInputFrameFor(VisionToolItem tool)
    {
        if (tool.Kind == VisionToolKind.AcquireImage)
        {
            return CurrentFrame;
        }

        return ToolNeedsImageInput(tool.Kind) && !TryGetImageInputSource(tool, out _)
            ? null
            : _lastAcquiredFrame ?? CurrentFrame;
    }

    private async Task RunDebugAsync(VisionToolItem? stopAfterTool = null)
    {
        if (IsDebugBusy)
        {
            return;
        }

        IsDebugBusy = true;
        try
        {
            var recipe = stopAfterTool is null ? BuildWorkingRecipe() : BuildWorkingRecipeToTool(stopAfterTool);
            var frame = recipe.GetActiveFlow().Tools.Any(tool => tool.Enabled && tool.Kind == VisionToolKind.AcquireImage)
                ? await RefreshImageAsync()
                : CurrentFrame;
            if (frame is null)
            {
                frame = await RefreshImageAsync();
            }

            if (frame is null)
            {
                StatusText = "调试失败：没有可用图像";
                AddDebugLog("错误", StatusText);
                return;
            }

            StatusText = stopAfterTool is null
                ? "视觉工具链调试运行中"
                : $"运行到 {stopAfterTool.Name}";
            var stopwatch = Stopwatch.StartNew();
            var result = await _pipeline.ExecuteAsync(recipe, frame);
            stopwatch.Stop();

            _uiDispatcher.Invoke(() =>
            {
                CurrentFrame = result.ResultFrame;
                DebugOutcome = result.Outcome.ToString().ToUpperInvariant();
                DebugOutcomeBrush = result.Outcome == InspectionOutcome.Ok ? "#FF42E58E" : "#FFFF5C7A";
                DebugBarcode = string.IsNullOrWhiteSpace(result.Barcode) ? "-" : result.Barcode;
                DebugMessage = result.Message;
                DebugCycleTimeText = $"{stopwatch.Elapsed.TotalMilliseconds:0} ms";
                ApplyFlowNodeRunResults(result.ToolResults, result.Outcome, result.Message);

                DebugToolResults.Clear();
                foreach (var tool in result.ToolResults)
                {
                    DebugToolResults.Add(ToToolResultItem(tool));
                }

                Overlays.Clear();
                foreach (var overlay in CreateResultPreviewOverlays(_overlayBuilder.Build(recipe, result.ResultFrame, result.ToolResults, result.Outcome)))
                {
                    Overlays.Add(overlay);
                }

                UpdateFlowResultImages(recipe, result);
                SaveActiveFlowImageState();

                StatusText = HasDebugFailure
                    ? $"调试完成 {DebugOutcome} / {DebugCycleTimeText} / {DebugFailureSummary}"
                    : $"调试完成 {DebugOutcome} / {DebugCycleTimeText}";
                AddDebugLog(result.Outcome == InspectionOutcome.Ok ? "提示" : "警告", StatusText);
            });

            _log.Info("VisionDebug", $"{recipe.Name} debug {result.Outcome} {stopwatch.Elapsed.TotalMilliseconds:0}ms");
        }
        catch (Exception ex)
        {
            _uiDispatcher.Invoke(() =>
            {
                DebugOutcome = "ERROR";
                DebugOutcomeBrush = "#FFFF5C7A";
                DebugMessage = ex.Message;
                DebugFailureSummary = ex.Message;
                StatusText = "视觉调试异常";
                AddDebugLog("错误", ex.Message);
            });
            _log.Error("VisionDebug", ex.Message);
        }
        finally
        {
            IsDebugBusy = false;
        }
    }

    private async Task ToggleContinuousRunAsync()
    {
        if (IsContinuousRunning)
        {
            _continuousRunCts?.Cancel();
            return;
        }

        _continuousRunCts = new CancellationTokenSource();
        var token = _continuousRunCts.Token;
        IsContinuousRunning = true;
        ContinuousRunText = "停止运行";
        AddDebugLog("提示", "连续运行启动");

        try
        {
            while (!token.IsCancellationRequested)
            {
                await RunDebugAsync();
                await Task.Delay(800, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsContinuousRunning = false;
            ContinuousRunText = "连续运行";
            _continuousRunCts.Dispose();
            _continuousRunCts = null;
            AddDebugLog("提示", "连续运行停止");
        }
    }

    private async Task SaveRecipeAsync()
    {
        if (_currentRecipe is null)
        {
            return;
        }

        var recipe = BuildWorkingRecipe();

        await _recipes.SaveAsync(recipe);
        _currentRecipe = recipe;
        RefreshVisionFlowList(recipe, recipe.CurrentFlowId);
        HasUnsavedChanges = false;
        StatusText = $"Saved {FlowName}: {_rois.Count} ROI items and {Tools.Count} tools";
        AddDebugLog("提示", StatusText);
        _log.Info("Recipe", $"Vision recipe saved for {recipe.Name}/{FlowName}, roi {_rois.Count}, tools {Tools.Count}");
    }

    private Recipe BuildWorkingRecipe()
    {
        var source = (_currentRecipe ?? new Recipe()).WithNormalizedFlows();
        var flow = BuildWorkingFlow();
        return source.WithActiveFlow(flow);
    }

    private void MarkDirty()
    {
        if (_isLoadingFlow)
        {
            return;
        }

        HasUnsavedChanges = true;
    }

    private Recipe BuildWorkingRecipeToTool(VisionToolItem stopAfterTool)
    {
        var recipe = BuildWorkingRecipe();
        var activeFlow = recipe.GetActiveFlow();
        var stopIndex = Tools.IndexOf(stopAfterTool);
        if (stopIndex < 0)
        {
            return recipe;
        }

        var partialFlow = activeFlow with
        {
            Tools = activeFlow.Tools.Take(stopIndex + 1).ToArray(),
            UpdatedAt = DateTimeOffset.Now
        };
        return recipe.WithActiveFlow(partialFlow);
    }

    private VisionFlowDefinition BuildWorkingFlow()
    {
        var sourceFlow = _currentRecipe?.EffectiveFlows.FirstOrDefault(flow => string.Equals(flow.Id, _activeFlowId, StringComparison.OrdinalIgnoreCase))
                         ?? _currentRecipe?.GetActiveFlow();
        var flowId = string.IsNullOrWhiteSpace(_activeFlowId) ? sourceFlow?.Id ?? "main" : _activeFlowId;
        var flowName = string.IsNullOrWhiteSpace(FlowName) ? "MainFlow" : FlowName.Trim();
        return new VisionFlowDefinition
        {
            Id = flowId,
            Name = flowName,
            Description = sourceFlow?.Description ?? string.Empty,
            Rois = _rois.Select(roi => roi.ToDefinition()).ToArray(),
            Tools = Tools.Select(tool => tool.ToDefinition()).ToArray(),
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private void RefreshVisionFlowList(Recipe recipe, string selectedFlowId)
    {
        _isLoadingFlow = true;
        try
        {
            var selectedImageFlowId = SelectedImageFlow?.Id;
            VisionFlows.Clear();
            foreach (var flow in recipe.EffectiveFlows)
            {
                var item = new VisionFlowItem(flow.Id, flow.Name, flow.Description, flow.UpdatedAt);
                item.ContextOptions = CreateVisionFlowContextOptions(item);
                VisionFlows.Add(item);
            }

            SelectedVisionFlow = VisionFlows.FirstOrDefault(flow => string.Equals(flow.Id, selectedFlowId, StringComparison.OrdinalIgnoreCase))
                                 ?? VisionFlows.FirstOrDefault();
            SelectedImageFlow = VisionFlows.FirstOrDefault(flow => string.Equals(flow.Id, selectedImageFlowId, StringComparison.OrdinalIgnoreCase))
                                ?? SelectedVisionFlow;
        }
        finally
        {
            _isLoadingFlow = false;
            DuplicateFlowCommand.RaiseCanExecuteChanged();
            DeleteFlowCommand.RaiseCanExecuteChanged();
        }
    }

    private void LoadFlow(string flowId)
    {
        if (_currentRecipe is null)
        {
            return;
        }

        var flow = _currentRecipe.EffectiveFlows.FirstOrDefault(item => string.Equals(item.Id, flowId, StringComparison.OrdinalIgnoreCase))
                   ?? _currentRecipe.GetActiveFlow();
        if (!string.Equals(_activeFlowId, flow.Id, StringComparison.OrdinalIgnoreCase))
        {
            SaveActiveFlowImageState();
        }

        _currentRecipe = _currentRecipe with
        {
            CurrentFlowId = flow.Id,
            Rois = flow.Rois,
            Tools = flow.Tools
        };
        LoadFlowDefinition(flow);
        RestoreFlowImageState(flow.Id);
        StatusText = $"Switched to flow {flow.Name}";
        AddDebugLog("提示", StatusText);
    }

    private void LoadFlowDefinition(VisionFlowDefinition flow)
    {
        _isLoadingFlow = true;
        try
        {
            _activeFlowId = flow.Id;
            FlowName = flow.Name;
            Tools.Clear();
            foreach (var tool in flow.Tools)
            {
                Tools.Add(VisionToolItem.FromDefinition(tool));
            }

            DetachRoiItems();
            _rois.Clear();
            foreach (var roi in flow.Rois)
            {
                AddRoiItem(RoiEditorItem.FromDefinition(roi));
            }

            SelectedTool = Tools.FirstOrDefault();
            RefreshRoiChoices();
            RefreshFlowTree();
            ClearDebugResults();
        }
        finally
        {
            _isLoadingFlow = false;
        }
    }

    private void NewFlow()
    {
        if (_currentRecipe is null)
        {
            return;
        }

        SaveActiveFlowImageState();
        _currentRecipe = BuildWorkingRecipe();
        var flow = CreateStarterFlow($"流程-{VisionFlows.Count + 1}");
        _currentRecipe = _currentRecipe.WithActiveFlow(flow);
        RefreshVisionFlowList(_currentRecipe, flow.Id);
        LoadFlowDefinition(flow);
        RestoreFlowImageState(flow.Id);
        MarkDirty();
        StatusText = $"Created flow {flow.Name}";
        AddDebugLog("提示", StatusText);
    }

    private void DuplicateFlow()
    {
        if (_currentRecipe is null)
        {
            return;
        }

        SaveActiveFlowImageState();
        _currentRecipe = BuildWorkingRecipe();
        var source = BuildWorkingFlow();
        var flow = source with
        {
            Id = $"flow-copy-{DateTimeOffset.UtcNow:HHmmssfff}",
            Name = $"{source.Name}-Copy",
            UpdatedAt = DateTimeOffset.Now
        };
        _currentRecipe = _currentRecipe.WithActiveFlow(flow);
        RefreshVisionFlowList(_currentRecipe, flow.Id);
        LoadFlowDefinition(flow);
        RestoreFlowImageState(flow.Id);
        MarkDirty();
        StatusText = $"Duplicated flow {flow.Name}";
        AddDebugLog("提示", StatusText);
    }

    private void DeleteFlow()
    {
        if (_currentRecipe is null || SelectedVisionFlow is null || VisionFlows.Count <= 1)
        {
            return;
        }

        var removedId = SelectedVisionFlow.Id;
        _flowImageStates.Remove(removedId);
        var remainingFlows = _currentRecipe.EffectiveFlows
            .Where(flow => !string.Equals(flow.Id, removedId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var nextFlow = remainingFlows.First();
        _currentRecipe = _currentRecipe with
        {
            CurrentFlowId = nextFlow.Id,
            Flows = remainingFlows,
            Rois = nextFlow.Rois,
            Tools = nextFlow.Tools,
            UpdatedAt = DateTimeOffset.Now
        };
        RefreshVisionFlowList(_currentRecipe, nextFlow.Id);
        LoadFlowDefinition(nextFlow);
        RestoreFlowImageState(nextFlow.Id);
        MarkDirty();
        StatusText = "Deleted selected flow";
        AddDebugLog("提示", StatusText);
    }

    private IReadOnlyList<FlowConnectionOptionItem> CreateVisionFlowContextOptions(VisionFlowItem flow)
    {
        return
        [
            new FlowConnectionOptionItem(
                "运行流程",
                new DelegateCommand(async () => await RunVisionFlowAsync(flow), () => !IsDebugBusy)),
            new FlowConnectionOptionItem(
                "编辑流程",
                new AsyncDelegateCommand(async () => await OpenFlowEditorAsync(flow))),
            new FlowConnectionOptionItem(
                "复制流程",
                new DelegateCommand(() => RunSelectedFlowAction(flow, DuplicateFlow))),
            new FlowConnectionOptionItem(
                "删除流程",
                new DelegateCommand(() => RunSelectedFlowAction(flow, DeleteFlow), () => VisionFlows.Count > 1))
        ];
    }

    private async Task RunVisionFlowAsync(VisionFlowItem flow)
    {
        if (SelectExistingFlow(flow))
        {
            await RunDebugAsync();
        }
    }

    private void RunSelectedFlowAction(VisionFlowItem flow, Action action)
    {
        if (SelectExistingFlow(flow))
        {
            action();
        }
    }

    private bool SelectExistingFlow(VisionFlowItem flow)
    {
        var current = VisionFlows.FirstOrDefault(candidate => string.Equals(candidate.Id, flow.Id, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return false;
        }

        SelectedVisionFlow = current;
        return true;
    }

    private static VisionFlowDefinition CreateStarterFlow(string name)
    {
        return new VisionFlowDefinition
        {
            Id = $"flow-{DateTimeOffset.UtcNow:HHmmssfff}",
            Name = name,
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = $"tool-acquire-{DateTimeOffset.UtcNow:HHmmssfff}",
                    Name = "采图",
                    Kind = VisionToolKind.AcquireImage
                },
                new VisionToolDefinition
                {
                    Id = $"tool-judge-{DateTimeOffset.UtcNow:HHmmssfff}",
                    Name = "综合判定",
                    Kind = VisionToolKind.Judge
                }
            ],
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private void AddTool()
    {
        var kind = SelectedToolboxItem?.Kind ?? SelectedToolKind;
        var displayName = SelectedToolboxItem?.Kind == kind ? SelectedToolboxItem.Name : null;
        AddToolOfKind(kind, displayName);
    }

    private void AddToolboxItem(object? item)
    {
        if (item is not ToolboxTreeItem { Kind: { } kind })
        {
            return;
        }

        SelectedToolboxItem = (ToolboxTreeItem)item;
        AddToolOfKind(kind, SelectedToolboxItem.Name, SelectedToolboxItem.MeasurementMode);
    }

    private void RefreshVisibleToolboxCategories()
    {
        VisibleToolboxCategories.Clear();
        var filter = ToolboxSearchText?.Trim();

        foreach (var category in ToolboxCategories)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                VisibleToolboxCategories.Add(category);
                continue;
            }

            var categoryMatches = category.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
            var children = category.Children
                .Where(child => categoryMatches || child.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (children.Length > 0)
            {
                VisibleToolboxCategories.Add(category.WithChildren(children));
            }
            else if (category.IsTool && category.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                VisibleToolboxCategories.Add(category);
            }
        }
    }

    private void AddToolOfKind(VisionToolKind kind, string? displayName = null, string? measurementMode = null)
    {
        SelectedToolKind = kind;
        var tool = VisionToolItem.Create(kind, Tools.Count + 1);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            tool.Name = displayName.Trim();
        }

        if (kind == VisionToolKind.MeasureDistance && !string.IsNullOrWhiteSpace(measurementMode))
        {
            SetToolParameter(tool, "measurementMode", VisionToolCatalog.NormalizeMeasurementMode(measurementMode));
            var defaultOutputs = VisionToolCatalog.GetDefaultOutputKeys(kind, measurementMode);
            SetToolParameter(tool, "enabledOutputs", string.Join(",", defaultOutputs));
        }

        if (kind == VisionToolKind.ImageProcess && !string.IsNullOrWhiteSpace(displayName))
        {
            ApplyImageProcessPreset(tool, displayName);
        }

        Tools.Add(tool);
        SelectedTool = tool;
        RefreshFlowTree();
        ClearDebugResults();
        MarkDirty();
        StatusText = $"Added tool {tool.Name}";
        AddDebugLog("提示", StatusText);
    }

    private void ApplyImageProcessPreset(VisionToolItem tool, string displayName)
    {
        if (displayName.Contains("二值", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("Threshold", StringComparison.OrdinalIgnoreCase))
        {
            SetToolParameter(tool, "operation", "Threshold");
            SetToolParameter(tool, "thresholdMode", "Otsu");
            return;
        }

        if (displayName.Contains("滤波", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("降噪", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("Filter", StringComparison.OrdinalIgnoreCase))
        {
            SetToolParameter(tool, "operation", "Filter");
            SetToolParameter(tool, "filterType", "Gaussian");
            return;
        }

        if (displayName.Contains("形态", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("Morph", StringComparison.OrdinalIgnoreCase))
        {
            SetToolParameter(tool, "operation", "Morphology");
            SetToolParameter(tool, "morphType", "Open");
            return;
        }

        if (displayName.Contains("增强", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("Enhance", StringComparison.OrdinalIgnoreCase))
        {
            SetToolParameter(tool, "operation", "Enhance");
            SetToolParameter(tool, "enhanceType", "BrightnessContrast");
            return;
        }

        if (displayName.Contains("几何", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("变换", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("Geometry", StringComparison.OrdinalIgnoreCase))
        {
            SetToolParameter(tool, "operation", "Geometry");
            SetToolParameter(tool, "geometryType", "Flip");
        }
    }

    private async Task EditToolAsync(object? item)
    {
        var tool = item switch
        {
            VisionToolItem visionTool => visionTool,
            FlowTreeItem flowItem => flowItem.Tool,
            FlowNodeItem flowNode => flowNode.Tool,
            FlowPortItem flowPort => flowPort.OwnerTool,
            null => SelectedTool,
            _ => null
        };
        if (tool is null)
        {
            return;
        }

        SelectedTool = tool;
        var result = _toolParameterDialog.EditTool(
            tool,
            RoiChoices.ToArray(),
            _rois.Select(roi => roi.ToDefinition()).ToArray(),
            AvailableToolKinds,
            FlowName,
            frame => _uiDispatcher.Invoke(() => ApplyAcquiredFrame(tool, frame)),
            GetInputFrameFor(tool),
            BuildWorkingRecipeToTool(tool));
        if (!result.Accepted)
        {
            return;
        }

        ApplyRemovedRois(result.RemovedRoiIds);
        ApplyCreatedRois(result.CreatedRois);
        EnsureConnectedSourceOutputsAreEnabled(tool);
        ClearDebugResults();
        if (result.OutputFrame is not null)
        {
            ApplyAcquiredFrame(tool, result.OutputFrame);
        }

        RefreshFlowTree();
        StatusText = $"Configured tool {tool.Name}";
        AddDebugLog("提示", StatusText);
        await SaveRecipeAsync();
        if (result.RunFlowRequested)
        {
            await RunDebugAsync();
        }
    }

    private void SelectFlowNode(object? item)
    {
        var tool = item switch
        {
            VisionToolItem visionTool => visionTool,
            FlowNodeItem flowNode => flowNode.Tool,
            FlowPortItem flowPort => flowPort.OwnerTool,
            _ => null
        };

        if (tool is null)
        {
            return;
        }

        SelectedTool = tool;
        RefreshFlowTree();
    }

    private async Task OpenFlowEditorAsync(object? item)
    {
        if (item is VisionFlowItem flow &&
            !string.Equals(flow.Id, _activeFlowId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedVisionFlow = flow;
        }

        if (_currentRecipe is not null)
        {
            await _flowEditorDialog.ShowEditorAsync();
        }
    }

    private void OpenCalibration()
    {
        _regionManager.RequestNavigate(RegionNames.MainRegion, NavigationKeys.Calibration);
    }

    private void ProcessFlowNodeSelection(FlowNodeSelectionRequest? request)
    {
        if (request is null)
        {
            return;
        }

        if (!request.Commit)
        {
            return;
        }

        if (request.Clear)
        {
            SelectedFlowNodes.Clear();
            return;
        }

        if (request.Single)
        {
            SelectedFlowNodes.Clear();
            var singleNode = FlowNodes.FirstOrDefault(node =>
            {
                var nodeRect = new Rect(node.X, node.Y, node.Width, node.Height);
                return nodeRect.IntersectsWith(request.Bounds);
            });

            if (singleNode is not null)
            {
                SelectedFlowNodes.Add(singleNode);
            }

            return;
        }

        if (request.Toggle)
        {
            var toggleNode = FlowNodes.FirstOrDefault(node =>
            {
                var nodeRect = new Rect(node.X, node.Y, node.Width, node.Height);
                return nodeRect.IntersectsWith(request.Bounds);
            });

            if (toggleNode is null)
            {
                return;
            }

            if (SelectedFlowNodes.Contains(toggleNode))
            {
                SelectedFlowNodes.Remove(toggleNode);
            }
            else
            {
                SelectedFlowNodes.Add(toggleNode);
            }

            return;
        }

        // Box selection
        SelectedFlowNodes.Clear();
        foreach (var node in FlowNodes)
        {
            var nodeRect = new Rect(node.X, node.Y, node.Width, node.Height);
            if (nodeRect.IntersectsWith(request.Bounds))
            {
                SelectedFlowNodes.Add(node);
            }
        }
    }

    private void MoveFlowNode(FlowNodeMoveRequest? request)
    {
        if (request?.Node.Tool is not { } tool)
        {
            return;
        }

        var x = Math.Max(8, request.X);
        var y = Math.Max(8, request.Y);
        request.Node.X = x;
        request.Node.Y = y;

        if (!request.Commit)
        {
            UpdateConnectionsForDraggedNode(request.Node);
            return;
        }

        SetToolParameter(tool, "canvasX", FormatCanvasCoordinate(x));
        SetToolParameter(tool, "canvasY", FormatCanvasCoordinate(y));

        if (!ReferenceEquals(SelectedTool, tool))
        {
            SelectedTool = tool;
        }
        else
        {
            RefreshFlowCanvas(tool.Id);
        }

        if (request.Commit)
        {
            ClearDebugResults();
            StatusText = $"Moved tool {tool.Name}";
            AddDebugLog("提示", StatusText);
            _ = SaveRecipeAsync();
        }
    }

    private void RefreshFlowConnectionsFromNodes()
    {
        if (FlowNodes.Count == 0)
        {
            return;
        }

        var portMap = new Dictionary<string, FlowPortItem>(StringComparer.OrdinalIgnoreCase);
        var nodeBounds = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        var maxRight = FlowCanvasWidth;
        var maxBottom = FlowCanvasHeight;

        foreach (var node in FlowNodes)
        {
            nodeBounds[node.Tool.Id] = new Rect(node.X, node.Y, node.Width, node.Height);
            maxRight = Math.Max(maxRight, node.X + node.Width + 180);
            maxBottom = Math.Max(maxBottom, node.Y + node.Height + 48);

            foreach (var item in CreateLiveCanvasPorts(node, isInput: true))
            {
                portMap[GetPortMapKey(item.OwnerTool.Id, item.Key)] = item;
            }

            foreach (var item in CreateLiveCanvasPorts(node, isInput: false))
            {
                portMap[GetPortMapKey(item.OwnerTool.Id, item.Key)] = item;
            }
        }

        FlowConnections.Clear();
        foreach (var targetTool in Tools)
        {
            foreach (var targetDefinition in GetInputPortDefinitions(targetTool))
            {
                var sourceToolId = GetConnectionSourceToolId(targetTool, targetDefinition.Key);
                if (string.IsNullOrWhiteSpace(sourceToolId))
                {
                    continue;
                }

                var sourcePortKey = GetConnectionSourcePortKey(targetTool, targetDefinition.Key);
                if (!portMap.TryGetValue(GetPortMapKey(sourceToolId, sourcePortKey), out var sourcePort) ||
                    !portMap.TryGetValue(GetPortMapKey(targetTool.Id, targetDefinition.Key), out var targetPort))
                {
                    continue;
                }

                var connection = CreateCanvasConnection(sourcePort, targetPort, nodeBounds);
                FlowConnections.Add(connection);
                maxRight = Math.Max(maxRight, connection.Geometry.Bounds.Right + 120);
                maxBottom = Math.Max(maxBottom, connection.Geometry.Bounds.Bottom + 48);
            }
        }

        FlowCanvasWidth = Math.Max(DefaultFlowCanvasWidth, maxRight + FlowCanvasPadding + 48);
        FlowCanvasHeight = Math.Max(560, maxBottom);
    }

    private void UpdateConnectionsForDraggedNode(FlowNodeItem draggedNode)
    {
        if (FlowNodes.Count == 0 || FlowConnections.Count == 0)
        {
            return;
        }

        var draggedToolId = draggedNode.Tool.Id;
        var nodeBounds = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in FlowNodes)
        {
            nodeBounds[node.Tool.Id] = new Rect(node.X, node.Y, node.Width, node.Height);
        }

        // Collect port positions for all nodes, computing dragged-node port coords from current node position
        var portPositions = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in FlowNodes)
        {
            var isDragged = ReferenceEquals(node, draggedNode);
            foreach (var port in node.Inputs)
            {
                var x = isDragged ? draggedNode.X + 14 : port.X;
                var y = isDragged ? draggedNode.Y + FlowPortStartOffset + GetPortIndex(node, port, isInput: true) * FlowPortRowHeight : port.Y;
                portPositions[GetPortMapKey(port.OwnerTool.Id, port.Key)] = new Point(x, y);
            }
            foreach (var port in node.Outputs)
            {
                var x = isDragged ? draggedNode.X + FlowNodeWidth - FlowPortEdgeOffset : port.X;
                var y = isDragged ? draggedNode.Y + FlowPortStartOffset + GetPortIndex(node, port, isInput: false) * FlowPortRowHeight : port.Y;
                portPositions[GetPortMapKey(port.OwnerTool.Id, port.Key)] = new Point(x, y);
            }
        }

        for (var connIndex = 0; connIndex < FlowConnections.Count; connIndex++)
        {
            var connection = FlowConnections[connIndex];
            if (!string.Equals(connection.Source.OwnerTool.Id, draggedToolId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(connection.Target.OwnerTool.Id, draggedToolId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceKey = GetPortMapKey(connection.Source.OwnerTool.Id, connection.Source.Key);
            var targetKey = GetPortMapKey(connection.Target.OwnerTool.Id, connection.Target.Key);
            if (!portPositions.TryGetValue(sourceKey, out var sourcePos) ||
                !portPositions.TryGetValue(targetKey, out var targetPos))
            {
                continue;
            }

            var route = CreateConnectionRouteFromPoints(
                sourcePos, targetPos, nodeBounds,
                connection.Source.OwnerTool.Id, connection.Target.OwnerTool.Id);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(route[0], false, false);
                for (var i = 1; i < route.Count; i++)
                {
                    context.LineTo(route[i], true, false);
                }
            }
            geometry.Freeze();

            var labelPoint = GetConnectionLabelPoint(route, nodeBounds.Values);
            var targetEnd = route[^1];
            FlowConnections[connIndex] = new FlowConnectionItem
            {
                Source = connection.Source,
                Target = connection.Target,
                Geometry = geometry,
                Label = connection.Label,
                LabelX = Math.Max(4, labelPoint.X - 56),
                LabelY = Math.Max(4, labelPoint.Y - 22),
                TargetX = targetEnd.X,
                TargetY = targetEnd.Y
            };
        }
    }

    private static int GetPortIndex(FlowNodeItem node, FlowPortItem port, bool isInput)
    {
        var list = isInput ? node.Inputs : node.Outputs;
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], port))
            {
                return i;
            }
        }
        return 0;
    }

    private static IReadOnlyList<Point> CreateConnectionRouteFromPoints(
        Point source, Point target, IReadOnlyDictionary<string, Rect> nodeBounds,
        string sourceToolId, string targetToolId)
    {
        const double stub = 18;
        const double margin = 42;
        var sourceNode = nodeBounds.TryGetValue(sourceToolId, out var srcBounds) ? srcBounds : (Rect?)null;
        var targetNode = nodeBounds.TryGetValue(targetToolId, out var tgtBounds) ? tgtBounds : (Rect?)null;
        var sourceExitX = (sourceNode?.Right ?? source.X) + stub;
        var targetEntryX = Math.Max(8, (targetNode?.Left ?? target.X) - stub);
        var sourceExit = new Point(sourceExitX, source.Y);
        var targetEntry = new Point(targetEntryX, target.Y);
        var topY = Math.Max(8, Math.Min(sourceNode?.Top ?? source.Y, targetNode?.Top ?? target.Y) - margin);
        var maxRouteY = Math.Max(source.Y, target.Y);
        var candidates = new List<IReadOnlyList<Point>>();

        var yStart = Math.Min(topY, Math.Min(source.Y, target.Y));
        var yEnd = maxRouteY;
        foreach (var routeY in GenerateRouteYLevels(yStart, yEnd, nodeBounds.Values, margin).Append(topY).Distinct())
        {
            candidates.Add(NormalizeRoute(
                source,
                sourceExit,
                new Point(sourceExitX, routeY),
                new Point(targetEntryX, routeY),
                targetEntry,
                target));
        }

        if (sourceExitX < targetEntryX)
        {
            var middleX = sourceExitX + (targetEntryX - sourceExitX) / 2;
            candidates.Add(NormalizeRoute(
                source,
                sourceExit,
                new Point(middleX, source.Y),
                new Point(middleX, target.Y),
                targetEntry,
                target));
        }

        var rightLaneX = GetRightDetourX(nodeBounds.Values, Math.Max(sourceExitX, targetEntryX), margin);
        foreach (var routeY in GenerateRouteYLevels(yStart, yEnd, nodeBounds.Values, margin).Distinct())
        {
            candidates.Add(NormalizeRoute(
                source,
                sourceExit,
                new Point(rightLaneX, source.Y),
                new Point(rightLaneX, routeY),
                new Point(targetEntryX, routeY),
                targetEntry,
                target));
        }

        return ChooseBestConnectionRoute(candidates, nodeBounds.Values);
    }

    private IEnumerable<FlowPortItem> CreateLiveCanvasPorts(FlowNodeItem node, bool isInput)
    {
        var definitions = isInput
            ? GetInputPortDefinitions(node.Tool)
            : GetOutputPortDefinitions(node.Tool);
        var index = 0;
        foreach (var definition in definitions)
        {
            yield return CreateCanvasPort(
                node.Tool,
                definition,
                isInput,
                isOutput: !isInput,
                isConnected: false,
                x: isInput ? node.X + FlowPortEdgeOffset : node.X + node.Width - FlowPortEdgeOffset,
                y: node.Y + FlowPortStartOffset + index * FlowPortRowHeight,
                contextOptions: Array.Empty<FlowConnectionOptionItem>());
            index++;
        }
    }

    private void AutoLayoutFlow()
    {
        if (Tools.Count == 0)
        {
            return;
        }

        var columnCount = GetAutoLayoutColumnCount();
        for (var toolIndex = 0; toolIndex < Tools.Count; toolIndex++)
        {
            var position = GetDefaultFlowNodePosition(toolIndex, columnCount);
            SetToolParameter(Tools[toolIndex], "canvasX", FormatCanvasCoordinate(position.X));
            SetToolParameter(Tools[toolIndex], "canvasY", FormatCanvasCoordinate(position.Y));
        }

        RefreshFlowTree();
        ClearDebugResults();
        StatusText = "Flow canvas arranged";
        AddDebugLog("提示", StatusText);
        _ = SaveRecipeAsync();
    }

    private void ApplyCreatedRois(IReadOnlyList<RoiDefinition>? createdRois)
    {
        if (createdRois is null || createdRois.Count == 0)
        {
            return;
        }

        RoiEditorItem? lastCreated = null;
        foreach (var definition in createdRois)
        {
            var existing = _rois.FirstOrDefault(roi => string.Equals(roi.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Name = definition.Name;
                existing.Shape = definition.Shape;
                existing.X = definition.X;
                existing.Y = definition.Y;
                existing.Width = definition.Width;
                existing.Height = definition.Height;
                existing.Angle = definition.Angle;
                existing.Radius = definition.Radius;
                existing.Points = definition.Points;
                lastCreated = existing;
                continue;
            }

            lastCreated = RoiEditorItem.FromDefinition(definition);
            AddRoiItem(lastCreated);
        }

        RefreshRoiChoices();
    }

    private void ApplyRemovedRois(IReadOnlyList<string>? roiIds)
    {
        if (roiIds is null || roiIds.Count == 0)
        {
            return;
        }

        var boundRoiIds = Tools
            .Select(tool => tool.RoiId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var id in roiIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (boundRoiIds.Contains(id))
            {
                continue;
            }

            var roi = _rois.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (roi is null)
            {
                continue;
            }

            _rois.Remove(roi);
            changed = true;
        }

        if (changed)
        {
            RefreshRoiChoices();
        }
    }

    private void DuplicateSelectedTool()
    {
        if (SelectedTool is null)
        {
            return;
        }

        var definition = SelectedTool.ToDefinition() with
        {
            Id = $"tool-copy-{DateTimeOffset.UtcNow:HHmmssfff}",
            Name = $"{SelectedTool.Name}-Copy"
        };

        var copy = VisionToolItem.FromDefinition(definition);
        SetToolParameter(copy, "canvasX", FormatCanvasCoordinate(GetToolCanvasCoordinate(SelectedTool, "canvasX", DefaultFlowNodeX) + 36));
        SetToolParameter(copy, "canvasY", FormatCanvasCoordinate(GetToolCanvasCoordinate(SelectedTool, "canvasY", FlowCanvasPadding) + 36));
        var index = Tools.IndexOf(SelectedTool);
        Tools.Insert(index + 1, copy);
        SelectedTool = copy;
        RefreshFlowTree();
        ClearDebugResults();
        MarkDirty();
        StatusText = $"Duplicated tool {copy.Name}";
        AddDebugLog("提示", StatusText);
    }

    private void DuplicateSelectedNodes()
    {
        if (SelectedFlowNodes.Count == 0)
        {
            return;
        }

        var offsetX = 36.0;
        var offsetY = 36.0;
        var copiedCount = SelectedFlowNodes.Count;
        var copies = new List<(VisionToolItem Copy, int InsertAfterIndex)>();

        foreach (var flowNode in SelectedFlowNodes)
        {
            if (flowNode.Tool is null)
            {
                continue;
            }

            var definition = flowNode.Tool.ToDefinition() with
            {
                Id = $"tool-copy-{DateTimeOffset.UtcNow:HHmmssfff}-{Guid.NewGuid():N}",
                Name = $"{flowNode.Tool.Name}-Copy"
            };

            var copy = VisionToolItem.FromDefinition(definition);
            var nodeX = flowNode.X + offsetX;
            var nodeY = flowNode.Y + offsetY;
            SetToolParameter(copy, "canvasX", FormatCanvasCoordinate(nodeX));
            SetToolParameter(copy, "canvasY", FormatCanvasCoordinate(nodeY));

            var index = Tools.IndexOf(flowNode.Tool);
            copies.Add((copy, index));
        }

        // Insert in reverse order to preserve insertion positions
        foreach (var (copy, index) in copies.OrderByDescending(c => c.InsertAfterIndex))
        {
            if (index >= 0)
            {
                Tools.Insert(index + 1, copy);
            }
            else
            {
                Tools.Add(copy);
            }
        }

        var lastCopy = copies.Count > 0 ? copies[^1].Copy : null;
        SelectedFlowNodes.Clear();
        if (lastCopy is not null)
        {
            SelectedTool = lastCopy;
        }

        RefreshFlowTree();
        ClearDebugResults();
        MarkDirty();
        StatusText = $"Copied {copiedCount} node(s)";
        AddDebugLog("提示", StatusText);
    }

    private void DeleteSelectedTool()
    {
        if (SelectedTool is null)
        {
            return;
        }

        var index = Tools.IndexOf(SelectedTool);
        var removed = SelectedTool;
        Tools.Remove(removed);
        SelectedTool = Tools.Count == 0 ? null : Tools[Math.Clamp(index, 0, Tools.Count - 1)];
        RefreshFlowTree();
        RaiseToolMoveCanExecuteChanged();
        ClearDebugResults();
        MarkDirty();
        StatusText = $"Deleted tool {removed.Name}";
        AddDebugLog("提示", StatusText);
    }

    private void MoveSelectedTool(int offset)
    {
        if (SelectedTool is null)
        {
            return;
        }

        var oldIndex = Tools.IndexOf(SelectedTool);
        var newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Tools.Count)
        {
            return;
        }

        Tools.Move(oldIndex, newIndex);
        RefreshFlowTree();
        RaiseToolMoveCanExecuteChanged();
        ClearDebugResults();
        MarkDirty();
        StatusText = $"Moved tool {SelectedTool.Name}";
    }

    private IReadOnlyList<FlowConnectionOptionItem> CreateFlowNodeContextOptions(VisionToolItem tool)
    {
        return
        [
            new FlowConnectionOptionItem(
                tool.Enabled ? "禁用工具" : "启用工具",
                new DelegateCommand(() => ToggleToolEnabled(tool))),
            new FlowConnectionOptionItem(
                "编辑参数",
                new DelegateCommand(async () => await EditToolAsync(tool))),
            new FlowConnectionOptionItem(
                "复制",
                new DelegateCommand(() => RunSelectedToolAction(tool, DuplicateSelectedTool))),
            new FlowConnectionOptionItem(
                "删除",
                new DelegateCommand(() => RunSelectedToolAction(tool, DeleteSelectedTool))),
            new FlowConnectionOptionItem(
                "上移",
                new DelegateCommand(() => RunSelectedToolAction(tool, () => MoveSelectedTool(-1)), () => CanMoveTool(tool, -1))),
            new FlowConnectionOptionItem(
                "下移",
                new DelegateCommand(() => RunSelectedToolAction(tool, () => MoveSelectedTool(1)), () => CanMoveTool(tool, 1))),
            new FlowConnectionOptionItem(
                "运行到此步",
                new DelegateCommand(async () => await RunDebugToToolAsync(tool), () => !IsDebugBusy))
        ];
    }

    private async Task RunDebugToToolAsync(VisionToolItem tool)
    {
        if (SelectExistingTool(tool))
        {
            await RunDebugAsync(tool);
        }
    }

    private void ToggleToolEnabled(VisionToolItem tool)
    {
        if (!SelectExistingTool(tool))
        {
            return;
        }

        tool.Enabled = !tool.Enabled;
        RefreshFlowTree();
        ClearDebugResults();
        MarkDirty();
        StatusText = $"{tool.Name} {(tool.Enabled ? "已启用" : "已禁用")}";
        AddDebugLog("提示", StatusText);
    }

    private void RunSelectedToolAction(VisionToolItem tool, Action action)
    {
        if (SelectExistingTool(tool))
        {
            action();
        }
    }

    private bool SelectExistingTool(VisionToolItem tool)
    {
        var current = Tools.FirstOrDefault(candidate => string.Equals(candidate.Id, tool.Id, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return false;
        }

        SelectedTool = current;
        return true;
    }

    private bool CanMoveTool(VisionToolItem tool, int offset)
    {
        var index = Tools.IndexOf(tool);
        var targetIndex = index + offset;
        return index >= 0 && targetIndex >= 0 && targetIndex < Tools.Count;
    }

    private bool CanMoveSelectedToolUp()
    {
        return SelectedTool is not null && Tools.IndexOf(SelectedTool) > 0;
    }

    private bool CanMoveSelectedToolDown()
    {
        return SelectedTool is not null && Tools.IndexOf(SelectedTool) >= 0 && Tools.IndexOf(SelectedTool) < Tools.Count - 1;
    }

    private void RaiseToolMoveCanExecuteChanged()
    {
        MoveToolUpCommand.RaiseCanExecuteChanged();
        MoveToolDownCommand.RaiseCanExecuteChanged();
    }

    private void RefreshFlowTree()
    {
        var selectedToolId = SelectedTool?.Id;
        FlowItems.Clear();

        for (var i = 0; i < Tools.Count; i++)
        {
            FlowItems.Add(CreateFlowToolNode(Tools[i], i));
        }

        RefreshFlowCanvas(selectedToolId);
        SelectedFlowItem = FlowItems.FirstOrDefault(item => item.Tool?.Id == selectedToolId) ?? FlowItems.FirstOrDefault();
    }

    private void RefreshFlowCanvas(string? selectedToolId = null)
    {
        selectedToolId ??= SelectedTool?.Id;
        FlowNodes.Clear();
        FlowConnections.Clear();

        var portMap = new Dictionary<string, FlowPortItem>(StringComparer.OrdinalIgnoreCase);
        var nodeBounds = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        var autoColumnCount = GetAutoLayoutColumnCount();
        var maxRight = DefaultFlowCanvasWidth;
        var maxBottom = 0.0;

        for (var toolIndex = 0; toolIndex < Tools.Count; toolIndex++)
        {
            var tool = Tools[toolIndex];
            var inputDefinitions = GetInputPortDefinitions(tool).ToArray();
            var outputDefinitions = GetOutputPortDefinitions(tool).ToArray();
            var rowCount = Math.Max(1, Math.Max(inputDefinitions.Length, outputDefinitions.Length));
            var nodeHeight = FlowNodeHeaderHeight + rowCount * FlowPortRowHeight + FlowCanvasPadding;
            var defaultPosition = GetDefaultFlowNodePosition(toolIndex, autoColumnCount);
            var nodeX = GetToolCanvasCoordinate(tool, "canvasX", defaultPosition.X);
            var nodeY = GetToolCanvasCoordinate(tool, "canvasY", defaultPosition.Y);

            var inputs = inputDefinitions
                .Select((definition, index) =>
                {
                    var port = CreateCanvasPort(
                        tool,
                        definition,
                        isInput: true,
                        isOutput: false,
                        isConnected: !string.IsNullOrWhiteSpace(GetConnectionSourceToolId(tool, definition.Key)),
                        x: nodeX + FlowPortEdgeOffset,
                        y: nodeY + FlowPortStartOffset + index * FlowPortRowHeight,
                        contextOptions: CreatePortInputContextOptions(tool, definition),
                        sourceDisplayName: tool.Kind == VisionToolKind.Result
                            ? FlowCanvasSourceDisplayBuilder.BuildInputSourceDisplayName(tool, definition.Key, Tools)
                            : string.Empty);
                    portMap[GetPortMapKey(tool.Id, definition.Key)] = port;
                    return port;
                })
                .ToArray();

            var outputs = outputDefinitions
                .Select((definition, index) =>
                {
                    var isProjectOutput = IsProjectOutput(tool, definition.Key);
                    var port = CreateCanvasPort(
                        tool,
                        definition,
                        isInput: false,
                        isOutput: true,
                        isConnected: isProjectOutput || FindPortOutputTargets(tool, definition.Key).Any(),
                        x: nodeX + FlowNodeWidth - FlowPortEdgeOffset,
                        y: nodeY + FlowPortStartOffset + index * FlowPortRowHeight,
                        contextOptions: CreatePortOutputContextOptions(tool, definition, isProjectOutput));
                    portMap[GetPortMapKey(tool.Id, definition.Key)] = port;
                    return port;
                })
                .ToArray();

            FlowNodes.Add(new FlowNodeItem
            {
                Tool = tool,
                Title = $"{toolIndex + 1:00}. {tool.Name}",
                Icon = GetToolIcon(tool.Kind),
                X = nodeX,
                Y = nodeY,
                Width = FlowNodeWidth,
                Height = nodeHeight,
                IsSelected = string.Equals(tool.Id, selectedToolId, StringComparison.OrdinalIgnoreCase),
                PreflightIssue = GetFlowNodePreflightIssue(tool),
                Inputs = inputs,
                Outputs = outputs,
                ContextOptions = CreateFlowNodeContextOptions(tool)
            });
            ApplyLatestToolResult(FlowNodes[^1]);

            nodeBounds[tool.Id] = new Rect(nodeX, nodeY, FlowNodeWidth, nodeHeight);
            maxRight = Math.Max(maxRight, nodeX + FlowNodeWidth + 180);
            maxBottom = Math.Max(maxBottom, nodeY + nodeHeight);
        }

        foreach (var targetTool in Tools)
        {
            foreach (var targetDefinition in GetInputPortDefinitions(targetTool))
            {
                var sourceToolId = GetConnectionSourceToolId(targetTool, targetDefinition.Key);
                if (string.IsNullOrWhiteSpace(sourceToolId))
                {
                    continue;
                }

                var sourcePortKey = GetConnectionSourcePortKey(targetTool, targetDefinition.Key);
                if (!portMap.TryGetValue(GetPortMapKey(sourceToolId, sourcePortKey), out var sourcePort) ||
                    !portMap.TryGetValue(GetPortMapKey(targetTool.Id, targetDefinition.Key), out var targetPort))
                {
                    continue;
                }

                var connection = CreateCanvasConnection(sourcePort, targetPort, nodeBounds);
                FlowConnections.Add(connection);
                maxRight = Math.Max(maxRight, connection.Geometry.Bounds.Right + 120);
                maxBottom = Math.Max(maxBottom, connection.Geometry.Bounds.Bottom + 48);
            }
        }

        FlowCanvasWidth = Math.Max(DefaultFlowCanvasWidth, maxRight + FlowCanvasPadding + 48);
        FlowCanvasHeight = Math.Max(560, maxBottom + FlowCanvasPadding);
    }

    private FlowPortItem CreateCanvasPort(
        VisionToolItem ownerTool,
        FlowPortDefinition definition,
        bool isInput,
        bool isOutput,
        bool isConnected,
        double x,
        double y,
        IReadOnlyList<FlowConnectionOptionItem> contextOptions,
        string sourceDisplayName = "")
    {
        return new FlowPortItem
        {
            OwnerTool = ownerTool,
            Key = definition.Key,
            Name = definition.Name,
            DataType = definition.DataType,
            IsInput = isInput,
            IsOutput = isOutput,
            IsConnected = isConnected,
            X = x,
            Y = y,
            SourceDisplayName = sourceDisplayName,
            ContextOptions = contextOptions
        };
    }

    private FlowConnectionItem CreateCanvasConnection(
        FlowPortItem sourcePort,
        FlowPortItem targetPort,
        IReadOnlyDictionary<string, Rect> nodeBounds)
    {
        var routePoints = CreateConnectionRoute(sourcePort, targetPort, nodeBounds);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(routePoints[0], false, false);
            for (var index = 1; index < routePoints.Count; index++)
            {
                context.LineTo(routePoints[index], true, false);
            }
        }

        geometry.Freeze();
        var labelPoint = GetConnectionLabelPoint(routePoints, nodeBounds.Values);

        var targetEnd = routePoints[^1];
        return new FlowConnectionItem
        {
            Source = sourcePort,
            Target = targetPort,
            Geometry = geometry,
            Label = $"{sourcePort.Name} -> {targetPort.Name}",
            LabelX = Math.Max(4, labelPoint.X - 56),
            LabelY = Math.Max(4, labelPoint.Y - 22),
            TargetX = targetEnd.X,
            TargetY = targetEnd.Y
        };
    }

    private static IReadOnlyList<Point> CreateConnectionRoute(
        FlowPortItem sourcePort,
        FlowPortItem targetPort,
        IReadOnlyDictionary<string, Rect> nodeBounds)
    {
        const double stub = 18;
        const double margin = 42;
        var source = new Point(sourcePort.X, sourcePort.Y);
        var target = new Point(targetPort.X, targetPort.Y);
        var sourceNode = GetPortOwnerBounds(sourcePort, nodeBounds);
        var targetNode = GetPortOwnerBounds(targetPort, nodeBounds);
        var sourceExitX = sourceNode.Right + stub;
        var targetEntryX = Math.Max(8, targetNode.Left - stub);
        var sourceExit = new Point(sourceExitX, source.Y);
        var targetEntry = new Point(targetEntryX, target.Y);
        var topY = Math.Max(8, Math.Min(sourceNode.Top, targetNode.Top) - margin);
        var maxRouteY = Math.Max(source.Y, target.Y);
        var candidates = new List<IReadOnlyList<Point>>();

        // Dense vertical grid from topY down to max(source.Y, target.Y) — never go below nodes
        var yStart = Math.Min(topY, Math.Min(source.Y, target.Y));
        var yEnd = maxRouteY;
        foreach (var routeY in GenerateRouteYLevels(yStart, yEnd, nodeBounds.Values, margin).Append(topY).Distinct())
        {
            candidates.Add(NormalizeRoute(
                source,
                sourceExit,
                new Point(sourceExitX, routeY),
                new Point(targetEntryX, routeY),
                targetEntry,
                target));
        }

        if (sourceExitX < targetEntryX)
        {
            var middleX = sourceExitX + (targetEntryX - sourceExitX) / 2;
            candidates.Add(NormalizeRoute(
                source,
                sourceExit,
                new Point(middleX, source.Y),
                new Point(middleX, target.Y),
                targetEntry,
                target));
        }

        var rightLaneX = GetRightDetourX(nodeBounds.Values, Math.Max(sourceExitX, targetEntryX), margin);
        foreach (var routeY in GenerateRouteYLevels(yStart, yEnd, nodeBounds.Values, margin).Distinct())
        {
            candidates.Add(NormalizeRoute(
                source,
                sourceExit,
                new Point(rightLaneX, source.Y),
                new Point(rightLaneX, routeY),
                new Point(targetEntryX, routeY),
                targetEntry,
                target));
        }

        return ChooseBestConnectionRoute(candidates, nodeBounds.Values);
    }

    private static Rect GetPortOwnerBounds(FlowPortItem port, IReadOnlyDictionary<string, Rect> nodeBounds)
    {
        return nodeBounds.TryGetValue(port.OwnerTool.Id, out var bounds)
            ? bounds
            : new Rect(Math.Max(0, port.X - FlowNodeWidth), Math.Max(0, port.Y - FlowNodeHeaderHeight), FlowNodeWidth, FlowNodeHeaderHeight + FlowPortRowHeight);
    }

    private static Rect InflateRouteObstacle(Rect obstacle)
    {
        obstacle.Inflate(14, 10);
        return obstacle;
    }

    private static double GetRightDetourX(IEnumerable<Rect> obstacles, double fallbackX, double margin)
    {
        return obstacles
            .Select(obstacle => obstacle.Right)
            .DefaultIfEmpty(fallbackX)
            .Max() + margin;
    }

    private static IReadOnlyList<Point> NormalizeRoute(params Point[] points)
    {
        var normalized = new List<Point>();
        foreach (var point in points)
        {
            AddRoutePoint(normalized, point);
        }

        for (var index = normalized.Count - 2; index > 0; index--)
        {
            var previous = normalized[index - 1];
            var current = normalized[index];
            var next = normalized[index + 1];
            if ((AreClose(previous.X, current.X) && AreClose(current.X, next.X)) ||
                (AreClose(previous.Y, current.Y) && AreClose(current.Y, next.Y)))
            {
                normalized.RemoveAt(index);
            }
        }

        return normalized;
    }

    private static bool IsRouteClear(IReadOnlyList<Point> points, IEnumerable<Rect> obstacles)
    {
        var inflatedObstacles = obstacles.Select(InflateRouteObstacle).ToArray();
        for (var index = 1; index < points.Count; index++)
        {
            if (index == 1 || index == points.Count - 1)
            {
                continue;
            }

            if (inflatedObstacles.Any(obstacle => SegmentIntersects(points[index - 1], points[index], obstacle)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SegmentIntersects(Point start, Point end, Rect obstacle)
    {
        if (AreClose(start.X, end.X))
        {
            var minY = Math.Min(start.Y, end.Y);
            var maxY = Math.Max(start.Y, end.Y);
            return start.X > obstacle.Left &&
                   start.X < obstacle.Right &&
                   maxY > obstacle.Top &&
                   minY < obstacle.Bottom;
        }

        if (AreClose(start.Y, end.Y))
        {
            var minX = Math.Min(start.X, end.X);
            var maxX = Math.Max(start.X, end.X);
            return start.Y > obstacle.Top &&
                   start.Y < obstacle.Bottom &&
                   maxX > obstacle.Left &&
                   minX < obstacle.Right;
        }

        return false;
    }

    private static IReadOnlyList<Point> ChooseBestConnectionRoute(IEnumerable<IReadOnlyList<Point>> candidates, IEnumerable<Rect> obstacles)
    {
        var obstacleArray = obstacles.ToArray();
        return candidates
            .OrderBy(candidate => GetRoutePenalty(candidate, obstacleArray))
            .ThenBy(GetPolylineLength)
            .First();
    }

    private static double GetRoutePenalty(IReadOnlyList<Point> points, IReadOnlyList<Rect> obstacles)
    {
        var inflatedObstacles = obstacles.Select(InflateRouteObstacle).ToArray();
        var crossings = 0;
        for (var index = 1; index < points.Count; index++)
        {
            if (index == 1 || index == points.Count - 1)
            {
                continue;
            }

            crossings += inflatedObstacles.Count(obstacle => SegmentIntersects(points[index - 1], points[index], obstacle));
        }

        var bendPenalty = Math.Max(0, points.Count - 2) * 18;
        return crossings * 100_000 + GetPolylineLength(points) + bendPenalty;
    }

    private static IEnumerable<double> GenerateRouteYLevels(double yStart, double yEnd, IEnumerable<Rect> obstacles, double margin)
    {
        const double step = 18;
        var low = Math.Min(yStart, yEnd);
        var high = Math.Max(yStart, yEnd);
        // Include source/target Y exactly plus dense grid between
        yield return yStart;
        yield return yEnd;
        foreach (var obstacle in obstacles)
        {
            yield return Math.Max(8, obstacle.Top - margin);
            yield return obstacle.Bottom + margin;
        }

        for (var y = low + step; y < high; y += step)
        {
            yield return y;
        }
    }

    private static double GetPolylineLength(IReadOnlyList<Point> points)
    {
        var length = 0.0;
        for (var index = 1; index < points.Count; index++)
        {
            length += (points[index] - points[index - 1]).Length;
        }

        return length;
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.01;
    }

    private static void AddRoutePoint(ICollection<Point> points, Point point)
    {
        if (points.LastOrDefault() == point)
        {
            return;
        }

        points.Add(point);
    }

    private static Point GetPolylineMidpoint(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return default;
        }

        var total = 0.0;
        for (var index = 1; index < points.Count; index++)
        {
            total += (points[index] - points[index - 1]).Length;
        }

        var remaining = total / 2;
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            var length = (end - start).Length;
            if (remaining <= length)
            {
                var ratio = length <= 0 ? 0 : remaining / length;
                return new Point(start.X + (end.X - start.X) * ratio, start.Y + (end.Y - start.Y) * ratio);
            }

            remaining -= length;
        }

        return points[^1];
    }

    private static Point GetConnectionLabelPoint(IReadOnlyList<Point> points, IEnumerable<Rect> obstacles)
    {
        if (points.Count == 0)
        {
            return default;
        }

        var inflatedObstacles = obstacles.Select(InflateRouteObstacle).ToArray();
        var bestPoint = GetPolylineMidpoint(points);
        var bestLength = 0.0;

        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            var length = (end - start).Length;
            if (length < 42)
            {
                continue;
            }

            var midpoint = new Point(start.X + (end.X - start.X) / 2, start.Y + (end.Y - start.Y) / 2);
            if (inflatedObstacles.Any(obstacle => obstacle.Contains(midpoint)))
            {
                continue;
            }

            if (length > bestLength)
            {
                bestLength = length;
                bestPoint = midpoint;
            }
        }

        return bestPoint;
    }

    private IReadOnlyList<FlowConnectionOptionItem> CreatePortInputContextOptions(VisionToolItem targetTool, FlowPortDefinition targetPort)
    {
        var options = new List<FlowConnectionOptionItem>();
        foreach (var source in FindCompatibleSourcePorts(targetTool, targetPort))
        {
            var capturedSourceTool = source.Tool;
            var capturedSourcePort = source.Port;
            options.Add(new FlowConnectionOptionItem(
                $"连接到 {capturedSourceTool.Name}.{capturedSourcePort.Name}",
                new DelegateCommand(() => ConnectPortInput(targetTool, targetPort.Key, capturedSourceTool, capturedSourcePort.Key))));
        }

        if (!string.IsNullOrWhiteSpace(GetConnectionSourceToolId(targetTool, targetPort.Key)))
        {
            options.Add(new FlowConnectionOptionItem(
                $"断开 {targetPort.Name}",
                new DelegateCommand(() => DisconnectPortInput(targetTool, targetPort.Key))));
        }

        return options;
    }

    private IReadOnlyList<FlowConnectionOptionItem> CreatePortOutputContextOptions(
        VisionToolItem sourceTool,
        FlowPortDefinition sourcePort,
        bool isProjectOutput)
    {
        var options = new List<FlowConnectionOptionItem>
        {
            new(
                isProjectOutput ? "更新工程输出" : "设为工程输出",
                new DelegateCommand(() => ExportProjectOutput(sourceTool, sourcePort)))
        };

        if (isProjectOutput)
        {
            options.Add(new FlowConnectionOptionItem(
                "取消工程输出",
                new DelegateCommand(() => RemoveProjectOutput(sourceTool, sourcePort.Key))));
        }

        return options;
    }

    private IEnumerable<(VisionToolItem Tool, FlowPortDefinition Port)> FindCompatibleSourcePorts(
        VisionToolItem targetTool,
        FlowPortDefinition targetPort)
    {
        return Tools
            .Where(tool => !ReferenceEquals(tool, targetTool) && !ToolDependsOn(tool, targetTool))
            .SelectMany(tool => GetAllOutputPortDefinitions(tool.Kind)
                .Where(port => string.Equals(port.DataType, targetPort.DataType, StringComparison.OrdinalIgnoreCase))
                .Select(port => (tool, port)));
    }

    private IEnumerable<VisionToolItem> FindPortOutputTargets(VisionToolItem sourceTool, string sourcePortKey)
    {
        return Tools.Where(targetTool =>
            !ReferenceEquals(sourceTool, targetTool) &&
            GetInputPortDefinitions(targetTool).Any(targetPort =>
                string.Equals(GetConnectionSourceToolId(targetTool, targetPort.Key), sourceTool.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetConnectionSourcePortKey(targetTool, targetPort.Key), sourcePortKey, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsProjectOutput(VisionToolItem sourceTool, string sourcePortKey)
    {
        return GetProjectOutput(sourceTool, sourcePortKey) is not null;
    }

    private string GetOutputPortDisplayName(VisionToolItem sourceTool, FlowPortDefinition sourcePort)
    {
        var projectOutput = GetProjectOutput(sourceTool, sourcePort.Key);
        if (projectOutput is null)
        {
            return sourcePort.Name;
        }

        var alias = string.IsNullOrWhiteSpace(projectOutput.ExternalAlias)
            ? projectOutput.Name
            : projectOutput.ExternalAlias;
        return $"{sourcePort.Name}（工程输出：{alias}）";
    }

    private VisionResultDefinition? GetProjectOutput(VisionToolItem sourceTool, string sourcePortKey)
    {
        if (_currentRecipe is null || string.IsNullOrWhiteSpace(_activeFlowId))
        {
            return null;
        }

        var sourceKey = BuildProjectOutputSourceKey(sourcePortKey);
        return _currentRecipe.VisionResults.FirstOrDefault(result =>
            string.Equals(result.FlowId, _activeFlowId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(result.SourceToolId, sourceTool.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(result.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase));
    }

    private void ExportProjectOutput(VisionToolItem sourceTool, FlowPortDefinition sourcePort)
    {
        if (!SelectExistingTool(sourceTool))
        {
            return;
        }

        var recipe = BuildWorkingRecipe();
        var sourceKey = BuildProjectOutputSourceKey(sourcePort.Key);
        var outputs = recipe.VisionResults.ToList();
        var existingIndex = outputs.FindIndex(result =>
            string.Equals(result.FlowId, _activeFlowId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(result.SourceToolId, sourceTool.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(result.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase));
        var existing = existingIndex >= 0 ? outputs[existingIndex] : null;
        var alias = string.IsNullOrWhiteSpace(existing?.ExternalAlias)
            ? BuildProjectOutputAlias(_activeFlowId, sourceTool.Id, sourcePort.Key)
            : existing.ExternalAlias;
        var name = string.IsNullOrWhiteSpace(existing?.Name)
            ? BuildProjectOutputName(sourceTool, sourcePort)
            : existing.Name;

        var definition = new VisionResultDefinition
        {
            Id = existing?.Id ?? BuildProjectOutputId(_activeFlowId, sourceTool.Id, sourcePort.Key),
            Name = name,
            FlowId = _activeFlowId,
            SourceToolId = sourceTool.Id,
            SourceKey = sourceKey,
            DataType = sourcePort.DataType,
            Unit = existing?.Unit ?? string.Empty,
            ParticipateInJudge = existing?.ParticipateInJudge ?? false,
            ExternalAlias = alias,
            PlcAddress = existing?.PlcAddress ?? string.Empty,
            Description = existing?.Description ?? $"工程输出：{sourceTool.Name}.{sourcePort.Name}"
        };

        if (existingIndex >= 0)
        {
            outputs[existingIndex] = definition;
        }
        else
        {
            outputs.Add(definition);
        }

        _currentRecipe = recipe with
        {
            VisionResults = outputs,
            UpdatedAt = DateTimeOffset.Now
        };
        RefreshFlowTree();
        RefreshSelectedToolOutput();
        StatusText = $"{sourceTool.Name}.{sourcePort.Name} 已设为工程输出：{alias}";
        AddDebugLog("提示", StatusText);
        _ = SaveRecipeAsync();
    }

    private void RemoveProjectOutput(VisionToolItem sourceTool, string sourcePortKey)
    {
        var recipe = BuildWorkingRecipe();
        var sourceKey = BuildProjectOutputSourceKey(sourcePortKey);
        var outputs = recipe.VisionResults
            .Where(result =>
                !string.Equals(result.FlowId, _activeFlowId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.SourceToolId, sourceTool.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (outputs.Length == recipe.VisionResults.Count)
        {
            return;
        }

        _currentRecipe = recipe with
        {
            VisionResults = outputs,
            UpdatedAt = DateTimeOffset.Now
        };
        RefreshFlowTree();
        RefreshSelectedToolOutput();
        StatusText = $"{sourceTool.Name}.{sourcePortKey} 已取消工程输出";
        AddDebugLog("提示", StatusText);
        _ = SaveRecipeAsync();
    }

    private static string GetPortMapKey(string toolId, string portKey)
    {
        return $"{toolId}:{portKey}";
    }

    private static string BuildProjectOutputSourceKey(string portKey)
    {
        return $"{ProjectOutputSourcePrefix}{portKey}";
    }

    private static string BuildProjectOutputId(string flowId, string toolId, string portKey)
    {
        return $"output-{BuildStableToken(flowId)}-{BuildStableToken(toolId)}-{BuildStableToken(portKey)}";
    }

    private static string BuildProjectOutputAlias(string flowId, string toolId, string portKey)
    {
        return $"FlowOutput_{BuildStableToken(flowId)}_{BuildStableToken(toolId)}_{BuildStableToken(portKey)}";
    }

    private static string BuildProjectOutputName(VisionToolItem sourceTool, FlowPortDefinition sourcePort)
    {
        return $"工程输出-{sourceTool.Name}-{sourcePort.Name}";
    }

    private static string BuildStableToken(string value)
    {
        var chars = value
            .Select(character => IsAsciiLetterOrDigit(character) ? character : '_')
            .ToArray();
        var token = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(token) ? "value" : token;
    }

    private static bool IsAsciiLetterOrDigit(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9';
    }

    private static IReadOnlyList<FlowPortDefinition> ToFlowPortDefinitions(IEnumerable<CatalogPortDefinition> ports)
    {
        return ports
            .Select(port => new FlowPortDefinition(port.Key, port.Name, port.DataType))
            .ToArray();
    }

    private sealed record FlowPortDefinition(string Key, string Name, string DataType);

    private FlowTreeItem CreateFlowToolNode(VisionToolItem tool, int index)
    {
        return new FlowTreeItem($"{index + 1:00}. {tool.Name}", GetToolIcon(tool.Kind), tool, CreateFlowChildren(tool));
    }

    private IReadOnlyList<FlowTreeItem> CreateFlowChildren(VisionToolItem tool)
    {
        var connectionChildren = CreateExplicitConnectionChildren(tool);
        if (UseExplicitFlowConnectionsOnly())
        {
            return connectionChildren;
        }

        return tool.Kind switch
        {
            VisionToolKind.AcquireImage =>
            [
                OutputNode("输出图像")
            ],
            VisionToolKind.TemplateLocate =>
            [
                InputNode($"输入图像（<- {FindPreviousImageSource(tool)} -> 输出图像）"),
                OutputNode("位置"),
                OutputNode("训练原点")
            ],
            VisionToolKind.MultiTargetMatch =>
            [
                InputNode("输入图像"),
                OutputNode("数量"),
                OutputNode("最佳位置"),
                OutputNode("OK/NG")
            ],
            VisionToolKind.RoiMap =>
            [
                InputNode("输入位置"),
                OutputNode("映射ROI")
            ],
            VisionToolKind.FindLine =>
            [
                InputNode("输入图像"),
                InputNode("输入位置"),
                InputNode($"输入ROI（{GetRoiDisplayName(tool.RoiId)}）"),
                OutputNode("直线"),
                OutputNode("OK/NG")
            ],
            VisionToolKind.FindCircle =>
            [
                InputNode("输入图像"),
                InputNode("输入位置"),
                InputNode($"输入ROI（{GetRoiDisplayName(tool.RoiId)}）"),
                OutputNode("圆"),
                OutputNode("OK/NG")
            ],
            VisionToolKind.MeasureDistance =>
            [
                InputNode("输入图像"),
                InputNode($"输入ROI（{GetRoiDisplayName(tool.RoiId)}）"),
                OutputNode("测量值"),
                OutputNode("OK/NG")
            ],
            VisionToolKind.CodeRead =>
            [
                InputNode("输入图像"),
                InputNode($"输入ROI（{GetRoiDisplayName(tool.RoiId)}）"),
                OutputNode("条码")
            ],
            VisionToolKind.Ocr =>
            [
                InputNode("输入图像"),
                InputNode($"输入ROI（{GetRoiDisplayName(tool.RoiId)}）"),
                OutputNode("字符")
            ],
            VisionToolKind.DefectDetect =>
            [
                InputNode("输入图像"),
                InputNode($"输入ROI（{GetRoiDisplayName(tool.RoiId)}）"),
                OutputNode("数量"),
                OutputNode("中心"),
                OutputNode("OK/NG")
            ],
            VisionToolKind.Judge =>
            [
                InputNode("输入工具结果"),
                OutputNode("综合判定")
            ],
            VisionToolKind.Result =>
                GetInputPortDefinitions(tool)
                    .Select(port => InputNode(port.Name))
                    .ToArray(),
            _ =>
            [
                OutputNode("输出结果")
            ]
        };
    }

    private static bool UseExplicitFlowConnectionsOnly()
    {
        return true;
    }

    private static bool UseOrderedFlowPortLayout()
    {
        return true;
    }

    private IReadOnlyList<FlowTreeItem> CreateExplicitConnectionChildren(VisionToolItem tool)
    {
        var children = new List<FlowTreeItem>();
        if (UseOrderedFlowPortLayout())
        {
            if (ToolNeedsImageInput(tool.Kind))
            {
                children.Add(TryGetImageInputSource(tool, out var connectedSourceTool)
                    ? ConnectedImageInputNode(tool, $"{connectedSourceTool.Name}.输出图像 -> 输入图像")
                    : ImageInputPortNode(tool, "输入图像"));
            }

            if (ToolRoiFactory.RequiresRoi(tool.Kind) && !string.IsNullOrWhiteSpace(tool.RoiId))
            {
                children.Add(ParameterNode($"ROI: {GetRoiDisplayName(tool.RoiId)}"));
            }

            if (ToolHasImageOutput(tool.Kind))
            {
                children.Add(ImageOutputPortNode(tool, "输出图像"));
            }

            foreach (var output in GetToolOutputPorts(tool))
            {
                children.Add(OutputNode(output));
            }

            return children;
        }

        AddVisiblePorts(tool, children);

        foreach (var targetTool in FindImageOutputTargets(tool))
        {
            children.Add(ConnectedImageOutputNode(tool, $"输出图像 -> {targetTool.Name}.输入图像"));
        }

        if (TryGetImageInputSource(tool, out var sourceTool))
        {
            children.Add(ConnectedImageInputNode(tool, $"输入图像 <- {sourceTool.Name}.输出图像"));
        }

        if (ToolRoiFactory.RequiresRoi(tool.Kind) && !string.IsNullOrWhiteSpace(tool.RoiId))
        {
            children.Add(ParameterNode($"ROI: {GetRoiDisplayName(tool.RoiId)}"));
        }

        return children;
    }

    private void AddVisiblePorts(VisionToolItem tool, ICollection<FlowTreeItem> children)
    {
        if (ToolHasImageOutput(tool.Kind) && !FindImageOutputTargets(tool).Any())
        {
            children.Add(ImageOutputPortNode(tool, "输出图像"));
        }

        if (ToolNeedsImageInput(tool.Kind) && !TryGetImageInputSource(tool, out _))
        {
            children.Add(ImageInputPortNode(tool, "输入图像"));
        }

        foreach (var output in GetToolOutputPorts(tool))
        {
            children.Add(OutputNode(output));
        }
    }

    private static bool ToolNeedsImageInput(VisionToolKind kind)
    {
        return kind is VisionToolKind.ImageProcess
            or VisionToolKind.TemplateLocate
            or VisionToolKind.MultiTargetMatch
            or VisionToolKind.FindLine
            or VisionToolKind.FindCircle
            or VisionToolKind.CodeRead
            or VisionToolKind.Ocr
            or VisionToolKind.DefectDetect;
    }

    private string GetFlowNodePreflightIssue(VisionToolItem tool)
    {
        if (!tool.Enabled)
        {
            return string.Empty;
        }

        var missingInputs = GetInputPortDefinitions(tool)
            .Where(definition => IsRequiredInputPort(tool, definition.Key))
            .Where(definition => string.IsNullOrWhiteSpace(GetConnectionSourceToolId(tool, definition.Key)))
            .Select(definition => definition.Name)
            .ToArray();
        if (missingInputs.Length > 0)
        {
            return missingInputs.Length == 1
                ? $"未连接{missingInputs[0]}"
                : $"未连接必需输入：{string.Join("、", missingInputs)}";
        }

        return string.Empty;
    }

    private static bool IsRequiredInputPort(VisionToolItem tool, string portKey)
    {
        if (string.Equals(portKey, "ImageInput", StringComparison.OrdinalIgnoreCase))
        {
            return ToolNeedsImageInput(tool.Kind);
        }

        if (tool.Kind == VisionToolKind.MeasureDistance)
        {
            return portKey is "PointInput" or "Point1Input" or "Point2Input" or "LineInput" or "Line1Input" or "Line2Input";
        }

        if (tool.Kind is VisionToolKind.LineAngle or VisionToolKind.LineIntersection)
        {
            return portKey is "Line1Input" or "Line2Input";
        }

        if (tool.Kind == VisionToolKind.FitLineFromPoints)
        {
            return portKey is "Point1Input" or "Point2Input";
        }

        if (tool.Kind == VisionToolKind.TemplatePoint)
        {
            return string.Equals(portKey, "PositionInput", StringComparison.OrdinalIgnoreCase);
        }

        if (tool.Kind == VisionToolKind.RoiMap)
        {
            return string.Equals(portKey, "PositionInput", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static IReadOnlyList<FlowPortDefinition> GetInputPortDefinitions(VisionToolItem tool)
    {
        if (tool.Kind == VisionToolKind.Result)
        {
            return ToFlowPortDefinitions(VisionToolCatalog.GetResultInputPorts(ParseToolParameters(tool.ParametersText)));
        }

        return ToFlowPortDefinitions(VisionToolCatalog.GetInputPorts(tool.Kind, GetMeasurementMode(tool)));
    }

    private static IReadOnlyList<FlowPortDefinition> GetInputPortDefinitions(VisionToolKind kind)
    {
        return ToFlowPortDefinitions(VisionToolCatalog.GetInputPorts(kind));
    }

    private static IReadOnlyList<FlowPortDefinition> GetOutputPortDefinitions(VisionToolItem tool)
    {
        var definitions = GetAllOutputPortDefinitions(tool.Kind);
        var enabledKeys = GetEnabledOutputKeys(tool, definitions);
        return definitions
            .Where(definition => enabledKeys.Contains(definition.Key))
            .ToArray();
    }

    private static IReadOnlyList<FlowPortDefinition> GetAllOutputPortDefinitions(VisionToolKind kind)
    {
        return ToFlowPortDefinitions(VisionToolCatalog.GetOutputPorts(kind));
    }

    private static HashSet<string> GetEnabledOutputKeys(VisionToolItem tool, IReadOnlyList<FlowPortDefinition> definitions)
    {
        var validKeys = definitions
            .Select(definition => definition.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = GetToolParameter(tool, "enabledOutputs");
        if (string.IsNullOrWhiteSpace(text))
        {
            return GetDefaultOutputKeys(tool.Kind, definitions, GetMeasurementMode(tool));
        }

        var keys = text
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(validKeys.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddRequiredGeometryOutputKeys(tool.Kind, keys);

        return keys.Count == 0
            ? GetDefaultOutputKeys(tool.Kind, definitions, GetMeasurementMode(tool))
            : keys;
    }

    private static HashSet<string> GetDefaultOutputKeys(
        VisionToolKind kind,
        IReadOnlyList<FlowPortDefinition> definitions,
        string? measurementMode = null)
    {
        var keys = VisionToolCatalog.GetDefaultOutputKeys(kind, measurementMode);
        return keys.Count == 0
            ? definitions.Select(definition => definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : keys;
    }

    private static void AddRequiredGeometryOutputKeys(VisionToolKind kind, ISet<string> keys)
    {
        switch (kind)
        {
            case VisionToolKind.MultiTargetMatch:
                keys.Add("PositionOutput");
                keys.Add("OriginOutput");
                break;
            case VisionToolKind.FindLine:
                keys.Add("MidPointOutput");
                break;
            case VisionToolKind.FindCircle:
                keys.Add("CenterOutput");
                break;
            case VisionToolKind.FitLineFromPoints:
                keys.Add("MidPointOutput");
                break;
            case VisionToolKind.LineIntersection:
            case VisionToolKind.TemplatePoint:
                keys.Add("PointOutput");
                break;
        }
    }

    private static string GetMeasurementMode(VisionToolItem tool)
    {
        return NormalizeMeasurementMode(GetToolParameter(tool, "measurementMode") ?? GuessMeasurementMode(tool.Name));
    }

    private static string GuessMeasurementMode(string name)
    {
        if (name.Contains("点线", StringComparison.OrdinalIgnoreCase))
        {
            return "PointLine";
        }

        if (name.Contains("线线", StringComparison.OrdinalIgnoreCase))
        {
            return "LineLine";
        }

        if (name.Contains("点点", StringComparison.OrdinalIgnoreCase))
        {
            return "PointPoint";
        }

        return "Simulated";
    }

    private static string NormalizeMeasurementMode(string value)
    {
        return VisionToolCatalog.NormalizeMeasurementMode(value);
    }

    private static IReadOnlyList<string> GetToolOutputPorts(VisionToolItem tool)
    {
        return GetOutputPortDefinitions(tool)
            .Where(definition => !string.Equals(definition.Key, "ImageOutput", StringComparison.OrdinalIgnoreCase))
            .Select(definition => definition.Name)
            .ToArray();
    }

    private static IReadOnlyList<FlowPortDefinition> GetOutputPortDefinitions(VisionToolKind kind)
    {
        return ToFlowPortDefinitions(VisionToolCatalog.GetOutputPorts(kind));
    }

    private static IReadOnlyList<string> GetToolOutputPorts(VisionToolKind kind)
    {
        return VisionToolCatalog.GetOutputPorts(kind)
            .Where(port => !string.Equals(port.Key, "ImageOutput", StringComparison.OrdinalIgnoreCase))
            .Where(port => VisionToolCatalog.GetDefaultOutputKeys(kind).Contains(port.Key))
            .Select(port => port.Name)
            .ToArray();
    }

    private IReadOnlyList<FlowConnectionOptionItem> CreateImageInputContextOptions(VisionToolItem targetTool)
    {
        var options = new List<FlowConnectionOptionItem>();
        foreach (var sourceTool in FindImageSourceTools(targetTool))
        {
            var capturedSource = sourceTool;
            options.Add(new FlowConnectionOptionItem(
                $"连接到 {capturedSource.Name}.输出图像",
                new DelegateCommand(() => ConnectImageInput(targetTool, capturedSource))));
        }

        if (!string.IsNullOrWhiteSpace(GetImageInputSourceToolId(targetTool)))
        {
            options.Add(new FlowConnectionOptionItem(
                "断开图像输入",
                new DelegateCommand(() => DisconnectImageInput(targetTool))));
        }

        return options;
    }

    private IEnumerable<VisionToolItem> FindImageSourceTools(VisionToolItem targetTool)
    {
        var targetIndex = Tools.IndexOf(targetTool);
        return Tools.Where(tool =>
            !ReferenceEquals(tool, targetTool) &&
            ToolHasImageOutput(tool.Kind) &&
            Tools.IndexOf(tool) >= 0 &&
            Tools.IndexOf(tool) < targetIndex);
    }

    private static bool ToolHasImageOutput(VisionToolKind kind)
    {
        return kind is VisionToolKind.AcquireImage or VisionToolKind.ImageProcess;
    }

    private bool CanConnectFlowPorts(CanvasFlowPortConnectionRequest? request)
    {
        if (request is null ||
            !request.Source.IsOutput ||
            !request.Target.IsInput ||
            ReferenceEquals(request.Source.OwnerTool, request.Target.OwnerTool) ||
            !string.Equals(request.Source.DataType, request.Target.DataType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourceIndex = Tools.IndexOf(request.Source.OwnerTool);
        var targetIndex = Tools.IndexOf(request.Target.OwnerTool);
        return sourceIndex >= 0 &&
               targetIndex >= 0 &&
               sourceIndex != targetIndex &&
               !ToolDependsOn(request.Source.OwnerTool, request.Target.OwnerTool);
    }

    private void ConnectFlowPorts(CanvasFlowPortConnectionRequest? request)
    {
        if (!CanConnectFlowPorts(request))
        {
            return;
        }

        ConnectPortInput(request!.Target.OwnerTool, request.Target.Key, request.Source.OwnerTool, request.Source.Key);
    }

    private void ConnectPortInput(VisionToolItem targetTool, string targetPortKey, VisionToolItem sourceTool, string sourcePortKey)
    {
        var targetPort = GetInputPortDefinitions(targetTool).FirstOrDefault(port => string.Equals(port.Key, targetPortKey, StringComparison.OrdinalIgnoreCase));
        var sourcePort = GetAllOutputPortDefinitions(sourceTool.Kind).FirstOrDefault(port => string.Equals(port.Key, sourcePortKey, StringComparison.OrdinalIgnoreCase));
        if (targetPort is null || sourcePort is null ||
            !string.Equals(targetPort.DataType, sourcePort.DataType, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "端口类型不匹配，无法连接";
            AddDebugLog("警告", StatusText);
            return;
        }

        var sourceIndex = Tools.IndexOf(sourceTool);
        var targetIndex = Tools.IndexOf(targetTool);
        if (sourceIndex < 0 || targetIndex < 0 || ReferenceEquals(sourceTool, targetTool))
        {
            StatusText = "输出必须连接到后面的工具输入";
            AddDebugLog("警告", StatusText);
            return;
        }

        if (ToolDependsOn(sourceTool, targetTool))
        {
            StatusText = "连接会形成循环依赖，无法连接";
            AddDebugLog("警告", StatusText);
            return;
        }

        var reordered = false;
        if (sourceIndex > targetIndex)
        {
            Tools.Move(sourceIndex, targetIndex);
            reordered = true;
        }

        SetPortConnection(targetTool, targetPortKey, sourceTool.Id, sourcePortKey);
        EnsureOutputPortEnabled(sourceTool, sourcePortKey);
        SelectedTool = targetTool;
        RefreshFlowTree();
        ClearDebugResults();
        StatusText = reordered
            ? $"{sourceTool.Name}已提前，{targetTool.Name}.{targetPort.Name} connected to {sourceTool.Name}.{sourcePort.Name}"
            : $"{targetTool.Name}.{targetPort.Name} connected to {sourceTool.Name}.{sourcePort.Name}";
        AddDebugLog("提示", StatusText);
        _ = SaveRecipeAsync();
    }

    private void DisconnectPortInput(VisionToolItem targetTool, string targetPortKey)
    {
        SetPortConnection(targetTool, targetPortKey, null, null);
        SelectedTool = targetTool;
        RefreshFlowTree();
        ClearDebugResults();
        StatusText = $"{targetTool.Name} input disconnected";
        AddDebugLog("提示", StatusText);
        _ = SaveRecipeAsync();
    }

    private void ConnectImageInput(VisionToolItem targetTool, VisionToolItem sourceTool)
    {
        if (!ToolNeedsImageInput(targetTool.Kind) || !ToolHasImageOutput(sourceTool.Kind) || ReferenceEquals(targetTool, sourceTool))
        {
            return;
        }

        var sourceIndex = Tools.IndexOf(sourceTool);
        var targetIndex = Tools.IndexOf(targetTool);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex >= targetIndex)
        {
            StatusText = "图像输出必须连接到后面的工具输入";
            AddDebugLog("警告", StatusText);
            return;
        }

        SetToolParameter(targetTool, "inputImageToolId", sourceTool.Id);
        SelectedTool = targetTool;
        RefreshFlowTree();
        ClearDebugResults();
        StatusText = $"{targetTool.Name} input image connected to {sourceTool.Name}";
        AddDebugLog("提示", StatusText);
        _ = SaveRecipeAsync();
    }

    private void DisconnectImageInput(VisionToolItem targetTool)
    {
        SetToolParameter(targetTool, "inputImageToolId", null);
        SetToolParameter(targetTool, "inputImageSourceToolId", null);
        SelectedTool = targetTool;
        RefreshFlowTree();
        ClearDebugResults();
        StatusText = $"{targetTool.Name} input image disconnected";
        AddDebugLog("提示", StatusText);
        _ = SaveRecipeAsync();
    }

    private IEnumerable<VisionToolItem> FindImageOutputTargets(VisionToolItem sourceTool)
    {
        return Tools.Where(targetTool =>
            !ReferenceEquals(targetTool, sourceTool) &&
            string.Equals(GetImageInputSourceToolId(targetTool), sourceTool.Id, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetImageInputSource(VisionToolItem targetTool, out VisionToolItem sourceTool)
    {
        var sourceToolId = GetImageInputSourceToolId(targetTool);
        sourceTool = Tools.FirstOrDefault(tool => string.Equals(tool.Id, sourceToolId, StringComparison.OrdinalIgnoreCase))!;
        return sourceTool is not null;
    }

    private static string GetImageInputSourceToolId(VisionToolItem tool)
    {
        return GetToolParameter(tool, "inputImageToolId")
               ?? GetToolParameter(tool, "inputImageSourceToolId")
               ?? GetToolParameter(tool, "input:ImageInput:toolId")
               ?? string.Empty;
    }

    private static string GetConnectionSourceToolId(VisionToolItem targetTool, string targetPortKey)
    {
        if (string.Equals(targetPortKey, "ImageInput", StringComparison.OrdinalIgnoreCase))
        {
            return GetImageInputSourceToolId(targetTool);
        }

        return GetToolParameter(targetTool, GetConnectionToolParameterKey(targetPortKey)) ?? string.Empty;
    }

    private static string GetConnectionSourcePortKey(VisionToolItem targetTool, string targetPortKey)
    {
        return GetToolParameter(targetTool, GetConnectionPortParameterKey(targetPortKey))
               ?? GetDefaultSourcePortKey(targetPortKey);
    }

    private static void SetPortConnection(VisionToolItem targetTool, string targetPortKey, string? sourceToolId, string? sourcePortKey)
    {
        SetToolParameter(targetTool, GetConnectionToolParameterKey(targetPortKey), sourceToolId);
        SetToolParameter(targetTool, GetConnectionPortParameterKey(targetPortKey), sourcePortKey);

        if (string.Equals(targetPortKey, "ImageInput", StringComparison.OrdinalIgnoreCase))
        {
            SetToolParameter(targetTool, "inputImageToolId", sourceToolId);
            SetToolParameter(targetTool, "inputImageSourceToolId", null);
        }

        if (string.Equals(targetPortKey, "PositionInput", StringComparison.OrdinalIgnoreCase))
        {
            SetToolParameter(targetTool, "positionInputToolId", null);
            SetToolParameter(targetTool, "positionInputPortKey", null);
        }
    }

    private void EnsureConnectedSourceOutputsAreEnabled(VisionToolItem targetTool)
    {
        foreach (var inputPort in GetInputPortDefinitions(targetTool))
        {
            var sourceToolId = GetConnectionSourceToolId(targetTool, inputPort.Key);
            if (string.IsNullOrWhiteSpace(sourceToolId))
            {
                continue;
            }

            var sourceTool = Tools.FirstOrDefault(tool => string.Equals(tool.Id, sourceToolId, StringComparison.OrdinalIgnoreCase));
            if (sourceTool is null)
            {
                continue;
            }

            EnsureOutputPortEnabled(sourceTool, GetConnectionSourcePortKey(targetTool, inputPort.Key));
        }
    }

    private static void EnsureOutputPortEnabled(VisionToolItem tool, string outputPortKey)
    {
        if (string.IsNullOrWhiteSpace(outputPortKey))
        {
            return;
        }

        var definitions = GetAllOutputPortDefinitions(tool.Kind);
        var validKeys = definitions
            .Select(definition => definition.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!validKeys.Contains(outputPortKey))
        {
            return;
        }

        var enabled = GetEnabledOutputKeys(tool, definitions);
        if (!enabled.Add(outputPortKey))
        {
            return;
        }

        var orderedKeys = definitions
            .Select(definition => definition.Key)
            .Where(enabled.Contains)
            .ToArray();
        SetToolParameter(tool, "enabledOutputs", string.Join(",", orderedKeys));
    }

    private bool ToolDependsOn(VisionToolItem tool, VisionToolItem dependency)
    {
        if (ReferenceEquals(tool, dependency))
        {
            return true;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ToolDependsOn(tool, dependency.Id, visited);
    }

    private bool ToolDependsOn(VisionToolItem tool, string dependencyToolId, ISet<string> visited)
    {
        if (!visited.Add(tool.Id))
        {
            return false;
        }

        foreach (var inputPort in GetInputPortDefinitions(tool))
        {
            var sourceToolId = GetConnectionSourceToolId(tool, inputPort.Key);
            if (string.IsNullOrWhiteSpace(sourceToolId))
            {
                continue;
            }

            if (string.Equals(sourceToolId, dependencyToolId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var sourceTool = Tools.FirstOrDefault(candidate => string.Equals(candidate.Id, sourceToolId, StringComparison.OrdinalIgnoreCase));
            if (sourceTool is not null && ToolDependsOn(sourceTool, dependencyToolId, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetConnectionToolParameterKey(string targetPortKey)
    {
        return $"input:{targetPortKey}:toolId";
    }

    private static string GetConnectionPortParameterKey(string targetPortKey)
    {
        return $"input:{targetPortKey}:portKey";
    }

    private static string GetDefaultSourcePortKey(string targetPortKey)
    {
        return targetPortKey switch
        {
            "ImageInput" => "ImageOutput",
            "PositionInput" => "PositionOutput",
            "ResultInput" => "ResultOutput",
            _ when targetPortKey.StartsWith("ResultInput", StringComparison.OrdinalIgnoreCase) => "ResultOutput",
            "PointInput" or "Point1Input" or "Point2Input" => "CenterOutput",
            "LineInput" or "Line1Input" or "Line2Input" => "LineOutput",
            _ => string.Empty
        };
    }

    private static double GetToolCanvasCoordinate(VisionToolItem tool, string key, double defaultValue)
    {
        var valueText = GetToolParameter(tool, key);
        return double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : defaultValue;
    }

    private int GetAutoLayoutColumnCount()
    {
        return Tools.Count > 5 ? 2 : 1;
    }

    private static Point GetDefaultFlowNodePosition(int toolIndex, int columnCount)
    {
        var safeColumnCount = Math.Max(1, columnCount);
        var column = toolIndex % safeColumnCount;
        var row = toolIndex / safeColumnCount;
        return new Point(
            DefaultFlowNodeX + column * (FlowNodeWidth + FlowNodeColumnGap),
            FlowCanvasPadding + row * FlowNodeRowStride);
    }

    private static string FormatCanvasCoordinate(double value)
    {
        return Math.Round(value).ToString(CultureInfo.InvariantCulture);
    }

    private static string? GetToolParameter(VisionToolItem tool, string key)
    {
        foreach (var segment in tool.ParametersText.Split(["\r\n", "\n", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var candidateKey = segment[..index].Trim();
            if (string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return index == segment.Length - 1 ? string.Empty : segment[(index + 1)..].Trim();
            }
        }

        return null;
    }

    private static void SetToolParameter(VisionToolItem tool, string key, string? value)
    {
        var parameters = ParseToolParameters(tool.ParametersText);
        if (string.IsNullOrWhiteSpace(value))
        {
            parameters.Remove(key);
        }
        else
        {
            parameters[key] = value;
        }

        tool.ParametersText = string.Join("; ", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
    }

    private static Dictionary<string, string> ParseToolParameters(string text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in text.Split(["\r\n", "\n", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0 || index == segment.Length - 1)
            {
                continue;
            }

            parameters[segment[..index].Trim()] = segment[(index + 1)..].Trim();
        }

        return parameters;
    }

    private string FindPreviousImageSource(VisionToolItem currentTool)
    {
        var currentIndex = Tools.IndexOf(currentTool);
        if (currentIndex <= 0)
        {
            return "图像";
        }

        return Tools
            .Take(currentIndex)
            .Reverse()
            .FirstOrDefault(tool => ToolHasImageOutput(tool.Kind))?.Name ?? "图像";
    }

    private string GetRoiDisplayName(string roiId)
    {
        if (string.IsNullOrWhiteSpace(roiId))
        {
            return "未绑定";
        }

        return _rois.FirstOrDefault(roi => string.Equals(roi.Id, roiId, StringComparison.OrdinalIgnoreCase))?.Name ?? roiId;
    }

    private static FlowTreeItem InputNode(string name)
    {
        var isImageInput = name.Contains("图像", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("图像", StringComparison.OrdinalIgnoreCase);
        return new FlowTreeItem($"<-- {name}", "\uE8AB", IsConnector: isImageInput, IsInput: true);
    }

    private static FlowTreeItem InputPortNode(string name)
    {
        return new FlowTreeItem($"<-- {name}", "\uE8AB", IsInput: true);
    }

    private FlowTreeItem ImageInputPortNode(VisionToolItem ownerTool, string name)
    {
        return new FlowTreeItem(
            $"<-- {name}",
            "\uE8AB",
            IsInput: true,
            OwnerTool: ownerTool,
            PortKey: "ImageInput",
            ContextOptions: CreateImageInputContextOptions(ownerTool));
    }

    private static FlowTreeItem ImageOutputPortNode(VisionToolItem ownerTool, string name)
    {
        return new FlowTreeItem(
            $"--> {name}",
            "\uE8A7",
            IsOutput: true,
            OwnerTool: ownerTool,
            PortKey: "ImageOutput");
    }

    private FlowTreeItem ConnectedImageInputNode(VisionToolItem ownerTool, string name)
    {
        return new FlowTreeItem(
            name,
            "\uE8AB",
            IsConnector: false,
            IsInput: true,
            IsConnected: true,
            OwnerTool: ownerTool,
            PortKey: "ImageInput",
            ContextOptions: CreateImageInputContextOptions(ownerTool));
    }

    private static FlowTreeItem ConnectedImageOutputNode(VisionToolItem ownerTool, string name)
    {
        return new FlowTreeItem(
            $"--> {name}",
            "\uE8A7",
            IsOutput: true,
            IsConnected: true,
            OwnerTool: ownerTool,
            PortKey: "ImageOutput");
    }

    private static FlowTreeItem OutputNode(string name)
    {
        return new FlowTreeItem($"--> {name}", "\uE8A7", IsOutput: true);
    }

    private static FlowTreeItem ConnectedOutputNode(string name)
    {
        return new FlowTreeItem($"--> {name}", "\uE8A7", IsOutput: true, IsConnected: true);
    }

    private static FlowTreeItem ConnectionNode(string name)
    {
        return new FlowTreeItem($"<-- {name}", "\uE8AB", IsConnector: true, IsInput: true);
    }

    private static FlowTreeItem ParameterNode(string name)
    {
        return new FlowTreeItem(name, "\uE713");
    }

    private static string GetToolIcon(VisionToolKind kind)
    {
        return kind switch
        {
            VisionToolKind.AcquireImage => "\uE722",
            VisionToolKind.ImageProcess => "\uE70F",
            VisionToolKind.TemplateLocate => "\uE8D4",
            VisionToolKind.MultiTargetMatch => "\uE8B3",
            VisionToolKind.CoordinateTransform => "\uE7B7",
            VisionToolKind.RoiMap => "\uE707",
            VisionToolKind.FindLine => "\uE8C1",
            VisionToolKind.FindCircle => "\uEA3A",
            VisionToolKind.MeasureDistance => "\uE8C1",
            VisionToolKind.LineAngle => "\uE8C1",
            VisionToolKind.LineIntersection => "\uE8C1",
            VisionToolKind.FitLineFromPoints => "\uE8C1",
            VisionToolKind.TemplatePoint => "\uE721",
            VisionToolKind.CodeRead => "\uE8EF",
            VisionToolKind.Ocr => "\uE8D2",
            VisionToolKind.DefectDetect => "\uEA39",
            VisionToolKind.Judge => "\uE9D5",
            VisionToolKind.Result => "\uE9D2",
            _ => "\uE713"
        };
    }

    private void AddRoiItem(RoiEditorItem roi)
    {
        roi.PropertyChanged += OnRoiPropertyChanged;
        _rois.Add(roi);
    }

    private void DetachRoiItems()
    {
        foreach (var roi in _rois)
        {
            roi.PropertyChanged -= OnRoiPropertyChanged;
        }
    }

    private void OnRoiPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RoiEditorItem.Name) or nameof(RoiEditorItem.Id))
        {
            RefreshRoiChoices();
            RefreshFlowTree();
        }
    }

    private void RefreshRoiChoices()
    {
        RoiChoices.Clear();
        RoiChoices.Add(new RoiChoiceItem(string.Empty, "不绑定"));
        foreach (var roi in _rois)
        {
            RoiChoices.Add(new RoiChoiceItem(roi.Id, roi.Name));
        }
    }

    private void ClearDebugResults(string? statusText = null)
    {
        DebugToolResults.Clear();
        Overlays.Clear();
        DebugOutcome = "READY";
        DebugOutcomeBrush = "#FF33D6A6";
        DebugBarcode = "-";
        DebugMessage = "等待调试";
        ClearFlowNodeRunResults();
        DebugCycleTimeText = "0 ms";

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            StatusText = statusText;
            AddDebugLog("提示", statusText);
        }
    }

    private void SaveActiveFlowImageState()
    {
        if (string.IsNullOrWhiteSpace(_activeFlowId))
        {
            return;
        }

        StoreFlowImageState(
            _activeFlowId,
            new FlowImageDisplayState(
                CloneImageFrame(CurrentFrame),
                CloneImageFrame(_lastAcquiredFrame),
                Overlays.ToArray(),
                FlowResultImages.Select(CloneFlowResultImageItem).ToArray(),
                SelectedFlowResultImage?.ToolId));
    }

    private void RestoreFlowImageState(string flowId)
    {
        if (!_flowImageStates.TryGetValue(flowId, out var state))
        {
            _lastAcquiredFrame = null;
            CurrentFrame = null;
            Overlays.Clear();
            FlowResultImages.Clear();
            SelectedFlowResultImage = null;
            RefreshDisplayedFlowImage();
            return;
        }

        _lastAcquiredFrame = CloneImageFrame(state.LastAcquiredFrame);
        CurrentFrame = CloneImageFrame(state.CurrentFrame);

        Overlays.Clear();
        foreach (var overlay in state.Overlays)
        {
            Overlays.Add(overlay);
        }

        FlowResultImages.Clear();
        foreach (var image in state.ResultImages)
        {
            FlowResultImages.Add(CloneFlowResultImageItem(image));
        }

        SelectedFlowResultImage = string.IsNullOrWhiteSpace(state.SelectedResultToolId)
            ? FlowResultImages.FirstOrDefault()
            : FlowResultImages.FirstOrDefault(image => string.Equals(image.ToolId, state.SelectedResultToolId, StringComparison.OrdinalIgnoreCase))
              ?? FlowResultImages.FirstOrDefault();
        RefreshDisplayedFlowImage();
    }

    private void StoreFlowImageState(string flowId, FlowImageDisplayState state)
    {
        _flowImageStates[flowId] = state;
        if (SelectedImageFlow is not null &&
            string.Equals(SelectedImageFlow.Id, flowId, StringComparison.OrdinalIgnoreCase))
        {
            RefreshDisplayedFlowImage();
        }
    }

    private void RefreshDisplayedFlowImage()
    {
        if (SelectedImageFlow is null ||
            !_flowImageStates.TryGetValue(SelectedImageFlow.Id, out var state))
        {
            DisplayedFlowFrame = null;
            DisplayedFlowOverlays.Clear();
            return;
        }

        DisplayedFlowFrame = CloneImageFrame(state.CurrentFrame);
        DisplayedFlowOverlays.Clear();
        foreach (var overlay in state.Overlays)
        {
            DisplayedFlowOverlays.Add(overlay);
        }
    }

    private static FlowResultImageItem CloneFlowResultImageItem(FlowResultImageItem item)
    {
        return item with
        {
            Frame = CloneImageFrame(item.Frame),
            Overlays = item.Overlays.ToArray()
        };
    }

    private static ImageFrame? CloneImageFrame(ImageFrame? frame)
    {
        return frame is null
            ? null
            : frame with { Pixels = (byte[])frame.Pixels.Clone() };
    }

    private sealed record FlowImageDisplayState(
        ImageFrame? CurrentFrame,
        ImageFrame? LastAcquiredFrame,
        IReadOnlyList<VisionOverlayItem> Overlays,
        IReadOnlyList<FlowResultImageItem> ResultImages,
        string? SelectedResultToolId);

    private void UpdateFlowResultImages(Recipe recipe, VisionPipelineResult result)
    {
        FlowResultImages.Clear();

        var finalOverlays = CreateResultPreviewOverlays(_overlayBuilder.Build(recipe, result.ResultFrame, result.ToolResults, result.Outcome));
        FlowResultImages.Add(new FlowResultImageItem("final", "最终结果", result.ResultFrame, finalOverlays));

        for (var index = 0; index < result.ToolResults.Count; index++)
        {
            var toolResult = result.ToolResults[index];
            var frame = result.ToolFrames?.GetValueOrDefault(toolResult.ToolId) ?? result.ResultFrame;
            var overlays = CreateResultPreviewOverlays(_overlayBuilder.Build(recipe, frame, [toolResult], toolResult.Outcome));
            FlowResultImages.Add(new FlowResultImageItem(
                toolResult.ToolId,
                $"{index + 1:00}. {toolResult.ToolName}",
                frame,
                overlays));
        }

        SelectedFlowResultImage = FlowResultImages.FirstOrDefault();
    }

    private void RefreshSelectedToolOutput()
    {
        SelectedToolOutputPorts.Clear();
        SelectedToolRawData.Clear();
        ClearSelectedToolMatchPoints();

        if (SelectedTool is null)
        {
            ToolOutputTitle = "未选择工具";
            ToolOutputStatus = "未选择";
            ToolOutputStatusBrush = "#FFA9B7C2";
            ToolOutputDuration = "-";
            ToolOutputMessage = "选择画布中的工具后显示输出";
            return;
        }

        ToolOutputTitle = SelectedTool.Name;
        if (!_latestFlowToolResults.TryGetValue(SelectedTool.Id, out var result))
        {
            ToolOutputStatus = SelectedTool.Enabled ? "未运行" : "已禁用";
            ToolOutputStatusBrush = SelectedTool.Enabled ? "#FFA9B7C2" : "#FF6F7D88";
            ToolOutputDuration = "-";
            ToolOutputMessage = SelectedTool.Enabled ? "运行当前流程后显示该工具输出" : "该工具当前未启用";

            foreach (var port in GetOutputPortDefinitions(SelectedTool))
            {
                SelectedToolOutputPorts.Add(new ToolOutputValueItem(GetOutputPortDisplayName(SelectedTool, port), "未运行", port.DataType, "输出端口"));
            }

            return;
        }

        ToolOutputStatus = result.Outcome.ToString().ToUpperInvariant();
        ToolOutputStatusBrush = result.Outcome switch
        {
            InspectionOutcome.Ok => "#FF42E58E",
            InspectionOutcome.Ng or InspectionOutcome.Error => "#FFFF5C7A",
            _ => "#FFA9B7C2"
        };
        ToolOutputDuration = $"{result.Duration.TotalMilliseconds:0.0} ms";
        ToolOutputMessage = string.IsNullOrWhiteSpace(result.Message) ? "-" : result.Message;

        foreach (var port in GetOutputPortDefinitions(SelectedTool))
        {
            SelectedToolOutputPorts.Add(new ToolOutputValueItem(
                GetOutputPortDisplayName(SelectedTool, port),
                FormatOutputPortValue(result, port),
                port.DataType,
                "输出端口"));
        }

        foreach (var pair in result.Data.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            SelectedToolRawData.Add(new ToolOutputValueItem(
                pair.Key,
                CompactOutputValue(pair.Value),
                GuessDataType(pair.Key, pair.Value),
                "原始数据"));
        }

        SetSelectedToolMatchPoints(result);
    }

    private void SetSelectedToolMatchPoints(ToolResult result)
    {
        if (result.Kind != VisionToolKind.MultiTargetMatch)
        {
            return;
        }

        foreach (var point in ParseMultiTargetMatchPoints(result.Data.GetValueOrDefault("matches")))
        {
            SelectedToolMatchPoints.Add(point);
        }

        SelectedToolMatchPointTitle = $"匹配点 {SelectedToolMatchPoints.Count} 个";
        ShowSelectedToolMatchPoints = true;
    }

    private void ClearSelectedToolMatchPoints()
    {
        SelectedToolMatchPoints.Clear();
        SelectedToolMatchPointTitle = "匹配点 0 个";
        ShowSelectedToolMatchPoints = false;
    }

    private static string FormatOutputPortValue(ToolResult result, FlowPortDefinition port)
    {
        if (port.Key.EndsWith("ResultOutput", StringComparison.OrdinalIgnoreCase) ||
            port.DataType.Equals("Result", StringComparison.OrdinalIgnoreCase))
        {
            return result.Outcome.ToString().ToUpperInvariant();
        }

        return result.Kind switch
        {
            VisionToolKind.AcquireImage when port.Key == "ImageOutput" =>
                JoinAvailable("图像", GetData(result, "source"), GetData(result, "frameId")),
            VisionToolKind.TemplateLocate when port.Key is "PositionOutput" or "OriginOutput" =>
                FormatPose(result),
            VisionToolKind.TemplateLocate when port.Key == "ScoreOutput" =>
                GetData(result, "score", "-"),
            VisionToolKind.TemplateLocate when port.Key == "XOutput" =>
                GetData(result, "x", "-"),
            VisionToolKind.TemplateLocate when port.Key == "YOutput" =>
                GetData(result, "y", "-"),
            VisionToolKind.TemplateLocate when port.Key == "AngleOutput" =>
                GetData(result, "angle", "-"),
            VisionToolKind.MultiTargetMatch when port.Key == "CountOutput" =>
                GetData(result, "count", "0"),
            VisionToolKind.MultiTargetMatch when port.Key is "PositionOutput" or "OriginOutput" or "BestPositionOutput" =>
                FormatPose(result),
            VisionToolKind.MultiTargetMatch when port.Key == "ScoreOutput" =>
                GetData(result, "score", GetData(result, "bestScore", "-")),
            VisionToolKind.MultiTargetMatch when port.Key == "XOutput" =>
                GetData(result, "x", "-"),
            VisionToolKind.MultiTargetMatch when port.Key == "YOutput" =>
                GetData(result, "y", "-"),
            VisionToolKind.MultiTargetMatch when port.Key == "AngleOutput" =>
                GetData(result, "angle", "-"),
            VisionToolKind.MultiTargetMatch when port.Key == "AllPositionsOutput" =>
                FormatMultiMatchPositions(result),
            VisionToolKind.MultiTargetMatch when port.Key == "ScoresOutput" =>
                FormatMultiMatchScores(result),
            VisionToolKind.CoordinateTransform when port.Key == "PointOutput" =>
                FormatPoint(result, "worldX", "worldY"),
            VisionToolKind.CoordinateTransform when port.Key == "PositionOutput" =>
                $"X={GetData(result, "worldX", "-")}  Y={GetData(result, "worldY", "-")}  A={GetData(result, "worldAngle", "-")}",
            VisionToolKind.CoordinateTransform when port.Key == "XOutput" =>
                GetData(result, "worldX", "-"),
            VisionToolKind.CoordinateTransform when port.Key == "YOutput" =>
                GetData(result, "worldY", "-"),
            VisionToolKind.CoordinateTransform when port.Key == "AngleOutput" =>
                GetData(result, "worldAngle", "-"),
            VisionToolKind.RoiMap when port.Key == "RoiOutput" =>
                $"{GetData(result, "roiCount", "0")} 个 ROI",
            VisionToolKind.FindLine when port.Key == "MidPointOutput" =>
                FormatPoint(result, "midX", "midY"),
            VisionToolKind.FindLine when port.Key == "LineOutput" =>
                FormatLine(result),
            VisionToolKind.FindCircle when port.Key == "CenterOutput" =>
                FormatPoint(result, "x", "y"),
            VisionToolKind.FindCircle when port.Key == "RadiusOutput" =>
                GetData(result, "radius", "-"),
            VisionToolKind.FindCircle when port.Key == "CircleOutput" =>
                FormatCircle(result),
            VisionToolKind.MeasureDistance when port.Key == "DeviationOutput" =>
                $"{GetData(result, "deviation", "-")} {GetData(result, "unit")}".Trim(),
            VisionToolKind.MeasureDistance when port.Key == "AbsDeviationOutput" =>
                $"{GetData(result, "absDeviation", "-")} {GetData(result, "unit")}".Trim(),
            VisionToolKind.MeasureDistance when port.Key == "MarginOutput" =>
                $"{GetData(result, "margin", "-")} {GetData(result, "unit")}".Trim(),
            VisionToolKind.MeasureDistance when port.Key == "NominalOutput" =>
                $"{GetData(result, "nominal", "-")} {GetData(result, "unit")}".Trim(),
            VisionToolKind.MeasureDistance when port.Key == "LowerLimitOutput" =>
                $"{GetData(result, "lower", "-")} {GetData(result, "unit")}".Trim(),
            VisionToolKind.MeasureDistance when port.Key == "UpperLimitOutput" =>
                $"{GetData(result, "upper", "-")} {GetData(result, "unit")}".Trim(),
            VisionToolKind.MeasureDistance when port.Key == "MeasureValueOutput" =>
                $"{GetData(result, "value", "-")} {GetData(result, "unit")}".Trim(),
            VisionToolKind.MeasureDistance when port.Key == "FootPointOutput" =>
                FormatPoint(result, "footX", "footY"),
            VisionToolKind.LineAngle when port.Key is "AngleOutput" or "MeasureValueOutput" =>
                $"{GetData(result, "angle", "-")} {GetData(result, "unit")}".Trim(),
            VisionToolKind.LineIntersection when port.Key == "PointOutput" =>
                FormatPoint(result, "x", "y"),
            VisionToolKind.LineIntersection when port.Key == "XOutput" =>
                GetData(result, "x", "-"),
            VisionToolKind.LineIntersection when port.Key == "YOutput" =>
                GetData(result, "y", "-"),
            VisionToolKind.FitLineFromPoints when port.Key == "LineOutput" =>
                FormatLine(result),
            VisionToolKind.FitLineFromPoints when port.Key == "MidPointOutput" =>
                FormatPoint(result, "midX", "midY"),
            VisionToolKind.FitLineFromPoints when port.Key == "AngleOutput" =>
                GetData(result, "angle", "-"),
            VisionToolKind.FitLineFromPoints when port.Key == "LengthOutput" =>
                GetData(result, "length", "-"),
            VisionToolKind.TemplatePoint when port.Key == "PointOutput" =>
                FormatPoint(result, "x", "y"),
            VisionToolKind.TemplatePoint when port.Key == "XOutput" =>
                GetData(result, "x", "-"),
            VisionToolKind.TemplatePoint when port.Key == "YOutput" =>
                GetData(result, "y", "-"),
            VisionToolKind.CodeRead when port.Key == "CodeOutput" =>
                GetData(result, "code", "-"),
            VisionToolKind.Ocr when port.Key == "TextOutput" =>
                GetData(result, "text", "-"),
            VisionToolKind.DefectDetect when port.Key == "CountOutput" =>
                GetData(result, "count", "0"),
            VisionToolKind.DefectDetect when port.Key == "BestCenterOutput" =>
                FormatPoint(result, "x", "y"),
            VisionToolKind.DefectDetect when port.Key == "AllCentersOutput" =>
                FormatBlobCenters(result),
            VisionToolKind.DefectDetect when port.Key == "BestAreaOutput" =>
                GetData(result, "area", "-"),
            VisionToolKind.DefectDetect when port.Key == "BestRectOutput" =>
                FormatBlobBounds(result),
            VisionToolKind.DefectDetect when port.Key == "BestCircleOutput" =>
                FormatBlobCircle(result),
            VisionToolKind.DefectDetect when port.Key == "BestWidthOutput" =>
                GetData(result, "width", "-"),
            VisionToolKind.DefectDetect when port.Key == "BestHeightOutput" =>
                GetData(result, "height", "-"),
            VisionToolKind.DefectDetect when port.Key == "BestAspectRatioOutput" =>
                GetData(result, "aspectRatio", "-"),
            VisionToolKind.DefectDetect when port.Key == "BestPerimeterOutput" =>
                GetData(result, "perimeter", "-"),
            VisionToolKind.DefectDetect when port.Key == "BestCircularityOutput" =>
                GetData(result, "circularity", "-"),
            VisionToolKind.DefectDetect when port.Key == "BestContourOutput" =>
                FormatBlobContour(result),
            VisionToolKind.Judge when port.Key == "OverallResultOutput" =>
                result.Outcome.ToString().ToUpperInvariant(),
            _ => GetData(result, port.Key, result.Outcome.ToString().ToUpperInvariant())
        };
    }

    private static string FormatPose(ToolResult result)
    {
        var x = GetData(result, "x", "-");
        var y = GetData(result, "y", "-");
        var angle = GetData(result, "angle", "-");
        return $"X={x}  Y={y}  A={angle}";
    }

    private static string FormatMultiMatchPositions(ToolResult result)
    {
        var matches = GetData(result, "matches", string.Empty);
        if (string.IsNullOrWhiteSpace(matches))
        {
            return "-";
        }

        return string.Join("; ", matches
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(5)
            .Select((item, index) =>
            {
                var parts = item.Split(',', StringSplitOptions.TrimEntries);
                return parts.Length >= 3
                    ? $"#{index + 1} X={parts[0]} Y={parts[1]} A={parts[2]}"
                    : $"#{index + 1} {item}";
            }));
    }

    private static string FormatMultiMatchScores(ToolResult result)
    {
        var matches = GetData(result, "matches", string.Empty);
        if (string.IsNullOrWhiteSpace(matches))
        {
            return "-";
        }

        return string.Join(", ", matches
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(12)
            .Select((item, index) =>
            {
                var parts = item.Split(',', StringSplitOptions.TrimEntries);
                return parts.Length >= 4 ? $"#{index + 1}:{parts[3]}" : $"#{index + 1}:-";
            }));
    }

    private static IReadOnlyList<MultiTargetMatchPointItem> ParseMultiTargetMatchPoints(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<MultiTargetMatchPointItem>();
        }

        var points = new List<MultiTargetMatchPointItem>();
        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 6)
            {
                continue;
            }

            var radius = parts.Length >= 8 ? FormatMatchNumber(parts[7], "0.###") : "-";
            points.Add(new MultiTargetMatchPointItem(
                points.Count + 1,
                FormatMatchNumber(parts[0], "0.###"),
                FormatMatchNumber(parts[1], "0.###"),
                FormatMatchNumber(parts[2], "0.###"),
                FormatMatchNumber(parts[3], "0.000"),
                FormatMatchNumber(parts[4], "0.###"),
                FormatMatchNumber(parts[5], "0.###"),
                parts.Length >= 7 && !string.IsNullOrWhiteSpace(parts[6]) ? parts[6] : "Rectangle",
                radius));
        }

        return points;
    }

    private static string FormatMatchNumber(string? text, string format)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "-";
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value.ToString(format, CultureInfo.InvariantCulture)
            : text;
    }

    private static string FormatLine(ToolResult result)
    {
        var x1 = GetData(result, "x1", "-");
        var y1 = GetData(result, "y1", "-");
        var x2 = GetData(result, "x2", "-");
        var y2 = GetData(result, "y2", "-");
        var angle = GetData(result, "angle", "-");
        return $"X1={x1} Y1={y1}  X2={x2} Y2={y2}  A={angle}";
    }

    private static string FormatPoint(ToolResult result, string xKey, string yKey)
    {
        var x = GetData(result, xKey, "-");
        var y = GetData(result, yKey, "-");
        return $"X={x}  Y={y}";
    }

    private static string FormatCircle(ToolResult result)
    {
        var x = GetData(result, "x", "-");
        var y = GetData(result, "y", "-");
        var radius = GetData(result, "radius", "-");
        return $"X={x}  Y={y}  R={radius}";
    }

    private static string FormatBlobCenters(ToolResult result)
    {
        var blobs = GetData(result, "blobs", string.Empty);
        if (string.IsNullOrWhiteSpace(blobs))
        {
            return "-";
        }

        return string.Join("; ", blobs
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .Select((item, index) =>
            {
                var parts = item.Split(',', StringSplitOptions.TrimEntries);
                return parts.Length >= 2 ? $"#{index + 1} X={parts[0]} Y={parts[1]}" : $"#{index + 1} {item}";
            }));
    }

    private static string FormatBlobBounds(ToolResult result)
    {
        var left = GetData(result, "left", "-");
        var top = GetData(result, "top", "-");
        var width = GetData(result, "width", "-");
        var height = GetData(result, "height", "-");
        return $"X={left} Y={top} W={width} H={height}";
    }

    private static string FormatBlobCircle(ToolResult result)
    {
        var x = GetData(result, "circleX", "-");
        var y = GetData(result, "circleY", "-");
        var radius = GetData(result, "circleRadius", "-");
        return $"X={x} Y={y} R={radius}";
    }

    private static string FormatBlobContour(ToolResult result)
    {
        var blobs = GetData(result, "blobs", string.Empty);
        if (string.IsNullOrWhiteSpace(blobs))
        {
            return "-";
        }

        var first = blobs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var parts = first?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is null || parts.Length < 14)
        {
            return "-";
        }

        var count = parts[13].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return $"{count} 点";
    }

    private static string GetData(ToolResult result, string key, string fallback = "")
    {
        return result.Data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string JoinAvailable(params string[] values)
    {
        return string.Join(" / ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string CompactOutputValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        const int maxLength = 180;
        return value.Length <= maxLength ? value : $"{value[..maxLength]}...";
    }

    private static string GuessDataType(string key, string value)
    {
        if (key.Contains("frame", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("image", StringComparison.OrdinalIgnoreCase))
        {
            return "Image";
        }

        if (key.Contains("mid", StringComparison.OrdinalIgnoreCase))
        {
            return "Point";
        }

        if (key.Contains("x", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("y", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("angle", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("pose", StringComparison.OrdinalIgnoreCase))
        {
            return "Pose";
        }

        if (key.Contains("score", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("count", StringComparison.OrdinalIgnoreCase) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return "Number";
        }

        return "Text";
    }

    private static IReadOnlyList<VisionOverlayItem> CreateResultPreviewOverlays(IReadOnlyList<VisionOverlayItem> overlays)
    {
        return overlays
            .Where(overlay => overlay.Kind != VisionOverlayKind.DirectionAxis)
            .Where(overlay => overlay.State != VisionOverlayState.Neutral)
            .Select(overlay => overlay with { Label = string.Empty })
            .ToArray();
    }

    private void AddDebugLog(string level, string message)
    {
        DebugLogs.Insert(0, new VisionDebugLogItem(DateTimeOffset.Now.ToString("HH:mm:ss"), level, message));
        while (DebugLogs.Count > 200)
        {
            DebugLogs.RemoveAt(DebugLogs.Count - 1);
        }

        switch (level)
        {
            case "错误":
                ErrorCount++;
                break;
            case "警告":
                WarningCount++;
                break;
            default:
                PromptCount++;
                break;
        }
    }

    private void OnAppLogWritten(object? sender, AppLogEntry entry)
    {
        if (!string.Equals(entry.Source, "VisionDebug", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _uiDispatcher.Invoke(() =>
        {
            var level = entry.Level switch
            {
                "Error" => "错误",
                "Warning" => "警告",
                _ => "提示"
            };
            AddDebugLog(level, entry.Message);
        });
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        return parameters.TryGetValue(key, out var value) && bool.TryParse(value, out var result)
            ? result
            : fallback;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> parameters, string key, double fallback)
    {
        return parameters.TryGetValue(key, out var value) && double.TryParse(value, out var result)
            ? result
            : fallback;
    }

    private static double GetExposureTimeMs(IReadOnlyDictionary<string, string> parameters, double fallback)
    {
        if (parameters.TryGetValue("exposureUs", out var exposureUs) && double.TryParse(exposureUs, out var us))
        {
            return us / 1000.0;
        }

        return GetDouble(parameters, "exposureMs", fallback);
    }

    private static ImageFrame ToGray8(ImageFrame frame)
    {
        if (frame.Format == PixelFormatKind.Gray8)
        {
            return frame;
        }

        var pixels = new byte[frame.Width * frame.Height];
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var targetOffset = y * frame.Width + x;
                if (frame.Format == PixelFormatKind.Bgr24)
                {
                    var sourceOffset = y * frame.Stride + x * 3;
                    pixels[targetOffset] = ToGray(frame.Pixels[sourceOffset], frame.Pixels[sourceOffset + 1], frame.Pixels[sourceOffset + 2]);
                }
                else
                {
                    var sourceOffset = y * frame.Stride + x * 4;
                    pixels[targetOffset] = ToGray(frame.Pixels[sourceOffset], frame.Pixels[sourceOffset + 1], frame.Pixels[sourceOffset + 2]);
                }
            }
        }

        return frame with
        {
            Format = PixelFormatKind.Gray8,
            Stride = frame.Width,
            Pixels = pixels
        };
    }

    private static byte ToGray(byte b, byte g, byte r)
    {
        return (byte)Math.Clamp((int)(r * 0.299 + g * 0.587 + b * 0.114), 0, 255);
    }

    private static ToolResultItem ToToolResultItem(ToolResult tool)
    {
        return new ToolResultItem(
            tool.ToolName,
            tool.Kind.ToString(),
            tool.Outcome.ToString(),
            $"{tool.Duration.TotalMilliseconds:0.0} ms",
            tool.Message);
    }

    private void ApplyFlowNodeRunResults(
        IReadOnlyList<ToolResult> toolResults,
        InspectionOutcome outcome,
        string fallbackMessage)
    {
        _latestFlowToolResults.Clear();
        foreach (var toolResult in toolResults)
        {
            _latestFlowToolResults[toolResult.ToolId] = toolResult;
        }

        foreach (var node in FlowNodes)
        {
            ApplyLatestToolResult(node);
        }

        RefreshSelectedToolOutput();

        var failures = toolResults
            .Where(tool => tool.Outcome is InspectionOutcome.Ng or InspectionOutcome.Error)
            .ToArray();
        if (failures.Length == 0)
        {
            DebugFailureSummary = outcome is InspectionOutcome.Ng or InspectionOutcome.Error
                ? fallbackMessage
                : string.Empty;
            return;
        }

        var firstFailure = failures[0];
        var toolIndex = Tools
            .Select((tool, index) => new { tool, index })
            .FirstOrDefault(item => string.Equals(item.tool.Id, firstFailure.ToolId, StringComparison.OrdinalIgnoreCase))
            ?.index;
        var step = toolIndex.HasValue ? $"{toolIndex.Value + 1:00}. " : string.Empty;
        var remainder = failures.Length > 1 ? $"，另有 {failures.Length - 1} 个异常工具" : string.Empty;
        DebugFailureSummary = $"{step}{firstFailure.ToolName}：{firstFailure.Message}{remainder}";
    }

    private void ApplyLatestToolResult(FlowNodeItem node)
    {
        node.SetRunResult(_latestFlowToolResults.GetValueOrDefault(node.Tool.Id));
    }

    private void ClearFlowNodeRunResults()
    {
        _latestFlowToolResults.Clear();
        DebugFailureSummary = string.Empty;
        foreach (var node in FlowNodes)
        {
            node.SetRunResult(null);
        }

        RefreshSelectedToolOutput();
    }

    private static ObservableCollection<ToolboxTreeItem> CreatePaletteToolbox()
    {
        return new ObservableCollection<ToolboxTreeItem>(
            VisionToolCatalog.GetToolboxCategories().Select(CreateToolboxTreeItem));
    }

    private static ToolboxTreeItem CreateToolboxTreeItem(ToolboxCatalogItem item)
    {
        var children = item.Children?.Select(CreateToolboxTreeItem).ToArray();
        return new ToolboxTreeItem(item.Name, item.Icon, item.Kind, children, measurementMode: item.MeasurementMode);
    }

    private static ObservableCollection<ToolboxTreeItem> CreateToolbox()
    {
        return
        [
            new ToolboxTreeItem("图像采集", "\uE722", Children:
            [
                new ToolboxTreeItem("SDK_Halcon", "\uE8B9", VisionToolKind.AcquireImage),
                new ToolboxTreeItem("SDK_巴斯勒", "\uE8B9", VisionToolKind.AcquireImage),
                new ToolboxTreeItem("SDK_海康威视", "\uE8B9", VisionToolKind.AcquireImage),
                new ToolboxTreeItem("SDK_欧姆龙", "\uE8B9", VisionToolKind.AcquireImage),
                new ToolboxTreeItem("SDK_大恒相机", "\uE8B9", VisionToolKind.AcquireImage)
            ]),
            new ToolboxTreeItem("图像相关", "\uE8A9"),
            new ToolboxTreeItem("匹配", "\uE8D4", Children:
            [
                new ToolboxTreeItem("模板匹配", "\uE8D4", VisionToolKind.TemplateLocate),
                new ToolboxTreeItem("多目标匹配", "\uE8B3", VisionToolKind.MultiTargetMatch)
            ]),
            new ToolboxTreeItem("标定", "\uE7B7"),
            new ToolboxTreeItem("定位引导", "\uE707", Children:
            [
                new ToolboxTreeItem("ROI映射", "\uE707", VisionToolKind.RoiMap)
            ]),
            new ToolboxTreeItem("查找与拟合", "\uE721", Children:
            [
                new ToolboxTreeItem("找线", "\uE8C1", VisionToolKind.FindLine),
                new ToolboxTreeItem("找圆", "\uEA3A", VisionToolKind.FindCircle),
                new ToolboxTreeItem("尺寸测量", "\uE8C1", VisionToolKind.MeasureDistance)
            ]),
            new ToolboxTreeItem("图像分割", "\uE9D9", Children:
            [
                new ToolboxTreeItem("缺陷检测", "\uEA39", VisionToolKind.DefectDetect)
            ]),
            new ToolboxTreeItem("创建", "\uE710"),
            new ToolboxTreeItem("几何", "\uE8C1", Children:
            [
                new ToolboxTreeItem("距离测量", "\uE8C1", VisionToolKind.MeasureDistance)
            ]),
            new ToolboxTreeItem("2D检测", "\uE9D2", Children:
            [
                new ToolboxTreeItem("外观检测", "\uE9D2", VisionToolKind.DefectDetect)
            ]),
            new ToolboxTreeItem("区域", "\uE8A9"),
            new ToolboxTreeItem("轮廓", "\uE9D9"),
            new ToolboxTreeItem("识别", "\uE8EF", Children:
            [
                new ToolboxTreeItem("条码识别", "\uE8EF", VisionToolKind.CodeRead),
                new ToolboxTreeItem("字符识别", "\uE8D2", VisionToolKind.Ocr)
            ]),
            new ToolboxTreeItem("通讯", "\uE968"),
            new ToolboxTreeItem("逻辑", "\uE9D5", Children:
            [
                new ToolboxTreeItem("综合判定", "\uE9D5", VisionToolKind.Judge)
            ]),
            new ToolboxTreeItem("系统", "\uE713"),
            new ToolboxTreeItem("运算", "\uE8EE"),
            new ToolboxTreeItem("光源", "\uE781"),
            new ToolboxTreeItem("其它工具", "\uE9D9"),
            new ToolboxTreeItem("输出", "\uE8E5", VisionToolKind.Judge)
        ];
    }
}
