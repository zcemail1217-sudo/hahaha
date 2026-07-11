using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Client.Events;
using VisionStation.Client.Presentation;
using VisionStation.Client.Services;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision.UI.Services;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Client.ViewModels;

public sealed class RecipeManagementViewModel : BindableBase, INavigationAware
{
    private const int MaxTestRunLogCount = 300;
    private const int MaxRecipeRefreshAttempts = 2;
    private const int RecipeRefreshRetryDelayMs = 50;
    private const string UnsavedChangesKey = "recipe-management";

    private readonly IRecipeRepository _recipes;
    private readonly RuntimePaths _paths;
    private readonly IFlowEditorDialogService _flowEditorDialog;
    private readonly IAppLogService _log;
    private readonly IEventAggregator _events;
    private readonly IDeviceConfigurationRepository _configurationRepository;
    private readonly IInspectionExecution _inspectionExecution;
    private readonly ICommunicationChannelRuntime _communicationChannels;
    private readonly IInspectionRunControl _inspectionRunControl;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IUnsavedChangesService _unsavedChanges;
    private Recipe? _loadedRecipe;
    private RecipeListItem? _selectedRecipe;
    private RecipeFlowSummaryItem? _selectedFlow;
    private RecipeProductParameterItem? _selectedProductParameter;
    private RecipeProcessStepItem? _selectedProcessStep;
    private RecipeVariableItem? _selectedRecipeVariable;
    private RecipeMotionSequenceItem? _selectedMotionSequence;
    private RecipeVisionResultItem? _selectedVisionResult;
    private RecipePlcSignalItem? _selectedPlcSignal;
    private RecipeSignalMappingItem? _selectedSignalMapping;
    private string _recipeId = string.Empty;
    private string _recipeName = string.Empty;
    private string _productCode = string.Empty;
    private string _description = string.Empty;
    private string _cameraId = string.Empty;
    private string _cameraDisplayName = string.Empty;
    private string _exposureTimeUs = "8000";
    private string _gain = "1.0";
    private bool _hardwareTrigger;
    private bool _saveOkImages = true;
    private bool _saveNgImages = true;
    private string _retentionDays = "30";
    private string _maxStorageMegabytes = "20480";
    private string _statusText = "正在加载配方...";
    private string _testRunStateText = "未运行";
    private bool _isBusy;
    private bool _isCurrentRecipe;
    private bool _hasUnsavedChanges;
    private bool _isLoadingEditor;
    private bool _isTestRunning;
    private bool _isTestRunPaused;
    private bool _testRunResetRequested;
    private CancellationTokenSource? _testRunLifetimeCancellation;
    private CancellationTokenSource? _testRunAttemptCancellation;
    private Task _testRunLifetimeCancellationCompletion = Task.CompletedTask;
    private Task _testRunAttemptCancellationCompletion = Task.CompletedTask;
    private bool _inspectionExecutionSubscribed;
    private string? _pendingRecipeRefreshId;
    private long _recipeRefreshGeneration;
    private RecipeProcessStepItem? _activeRuntimeStep;
    private InspectionRunResult? _lastTestRun;

    private static readonly Regex StepStartedRegex = new(@"^开始步骤\s+(?<stepNo>\d+):", RegexOptions.Compiled);
    private static readonly Regex StepCompletedRegex = new(@"^完成步骤\s+(?<stepNo>\d+):.*?耗时\s+(?<duration>\d+)\s*ms", RegexOptions.Compiled);
    private static readonly Regex StepFailedRegex = new(@"^步骤失败\s+(?<stepNo>\d+):.*?耗时\s+(?<duration>\d+)\s*ms，错误\s+(?<error>.+)$", RegexOptions.Compiled);
    private static readonly Regex StepResultRegex = new(@"^步骤结果\s+(?<stepNo>\d+):.*?，(?<result>.+)$", RegexOptions.Compiled);
    private static readonly Regex WaitStartedRegex = new(@"^等待步骤\s+(?<stepNo>\d+):.*?，(?<detail>等待.+)$", RegexOptions.Compiled);
    private static readonly Regex WaitMatchedRegex = new(@"^等待完成\s+(?<stepNo>\d+):.*?，(?<result>收到.+)$", RegexOptions.Compiled);
    private static readonly Regex WaitIgnoredRegex = new(@"^等待步骤\s+(?<stepNo>\d+):.*?(?<result>收到.+?，未满足条件.+)$", RegexOptions.Compiled);

    public RecipeManagementViewModel(
        IRecipeRepository recipes,
        RuntimePaths paths,
        IFlowEditorDialogService flowEditorDialog,
        IAppLogService log,
        IEventAggregator events,
        IDeviceConfigurationRepository configurationRepository,
        IInspectionExecution inspectionExecution,
        ICommunicationChannelRuntime communicationChannels,
        IInspectionRunControl inspectionRunControl,
        IUiDispatcher uiDispatcher,
        IUnsavedChangesService unsavedChanges)
    {
        _recipes = recipes;
        _paths = paths;
        _flowEditorDialog = flowEditorDialog;
        _log = log;
        _events = events;
        _configurationRepository = configurationRepository;
        _inspectionExecution = inspectionExecution;
        _communicationChannels = communicationChannels;
        _inspectionRunControl = inspectionRunControl;
        _uiDispatcher = uiDispatcher;
        _unsavedChanges = unsavedChanges;

        Recipes.CollectionChanged += (_, _) => RaiseCommandStates();
        AttachEditableCollection(ProductParameters);
        AttachEditableCollection(ProcessSteps);
        AttachEditableCollection(RecipeVariables);
        RecipeVariables.CollectionChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(RecipeVariableCount));
            RefreshVisionFlowResultPreviewItems();
        };
        AttachEditableCollection(MotionSequences);
        AttachEditableCollection(VisionResults);
        AttachEditableCollection(PlcSignals);
        AttachEditableCollection(SignalMappings);

        ReloadCommand = new DelegateCommand(async () => await LoadAsync(), CanReload);
        SaveCommand = new DelegateCommand(async () => await SaveAsync(), CanSave);
        SetCurrentRecipeCommand = new DelegateCommand(async () => await SetCurrentRecipeAsync(), CanSetCurrentRecipe);
        TestRunRecipeCommand = new AsyncDelegateCommand(TestRunRecipeAsync, CanTestRun)
            .EnableParallelExecution()
            .Catch(ReportTestRunFailureSafely);
        PauseTestRunCommand = new DelegateCommand(PauseTestRun, () => IsTestRunning && !IsTestRunPaused);
        ResetTestRunCommand = new DelegateCommand(ResetTestRun, () => !IsBusy);
        OpenFlowEditorCommand = new AsyncDelegateCommand(OpenFlowEditorAsync, CanOpenFlowEditor)
            .Catch(ReportOpenFlowEditorFailureSafely);
        NewRecipeCommand = new DelegateCommand(async () => await CreateRecipeAsync(), CanCreateRecipe);
        DuplicateRecipeCommand = new DelegateCommand(async () => await DuplicateRecipeAsync(), CanOpenFlowEditor);
        DeleteRecipeCommand = new DelegateCommand(async () => await DeleteRecipeAsync(), CanDeleteRecipe);
        AddProductParameterCommand = new DelegateCommand(AddProductParameter, CanEditRecipe);
        RemoveProductParameterCommand = new DelegateCommand(
            RemoveProductParameter,
            () => CanEditRecipe() && SelectedProductParameter is not null);
        UseVariableInProductParameterCommand = new DelegateCommand(UseVariableInProductParameter, CanUseVariableInProductParameter);
        UseVariableInProcessStepCommand = new DelegateCommand(UseVariableInProcessStep, CanUseVariableInProcessStep);
        AddProcessToolCommand = new DelegateCommand<object>(AddProcessTool, _ => CanEditRecipe());
        SelectProcessStepCommand = new DelegateCommand<object>(SelectProcessStep);
        RemoveProcessStepItemCommand = new DelegateCommand<object>(RemoveProcessStepItem, _ => CanEditRecipe());
        MoveProcessStepLeftCommand = new DelegateCommand<object>(item => MoveProcessStepItem(item, -1), _ => CanEditRecipe());
        MoveProcessStepRightCommand = new DelegateCommand<object>(item => MoveProcessStepItem(item, 1), _ => CanEditRecipe());
        AddProcessStepCommand = new DelegateCommand(AddProcessStep, CanEditRecipe);
        RemoveProcessStepCommand = new DelegateCommand(
            RemoveProcessStep,
            () => CanEditRecipe() && SelectedProcessStep is not null);
        MoveProcessStepUpCommand = new DelegateCommand(() => MoveProcessStep(-1), CanMoveProcessStepUp);
        MoveProcessStepDownCommand = new DelegateCommand(() => MoveProcessStep(1), CanMoveProcessStepDown);
        AddMotionSequenceCommand = new DelegateCommand(AddMotionSequence, CanEditRecipe);
        RemoveMotionSequenceCommand = new DelegateCommand(
            RemoveMotionSequence,
            () => CanEditRecipe() && SelectedMotionSequence is not null);
        AddVisionResultCommand = new DelegateCommand(AddVisionResult, CanEditRecipe);
        RemoveVisionResultCommand = new DelegateCommand(
            RemoveVisionResult,
            () => CanEditRecipe() && SelectedVisionResult is not null);
        AddPlcSignalCommand = new DelegateCommand(AddPlcSignal, CanEditRecipe);
        RemovePlcSignalCommand = new DelegateCommand(
            RemovePlcSignal,
            () => CanEditRecipe() && SelectedPlcSignal is not null);
        AddSignalMappingCommand = new DelegateCommand(AddSignalMapping, CanEditRecipe);
        RemoveSignalMappingCommand = new DelegateCommand(
            RemoveSignalMapping,
            () => CanEditRecipe() && SelectedSignalMapping is not null);
        _events.GetEvent<RecipeChangedEvent>().Subscribe(OnExternalRecipeChanged, ThreadOption.UIThread);
        _configurationRepository.ConfigurationSaved += OnConfigurationSaved;
        _log.LogWritten += OnAppLogWritten;

        SubscribeInspectionExecution();
        _ = LoadAsync();
    }

    public ObservableCollection<RecipeListItem> Recipes { get; } = new();

    public ObservableCollection<RecipeFlowSummaryItem> FlowSummaries { get; } = new();

    public ObservableCollection<RecipeProductParameterItem> ProductParameters { get; } = new();

    public ObservableCollection<RecipeVariableItem> RecipeVariables { get; } = new();

    public ObservableCollection<RecipeProcessStepItem> ProcessSteps { get; } = new();

    public ObservableCollection<RecipeChannelOption> TcpChannelOptions { get; } = new();

    public ObservableCollection<RecipeChannelOption> SerialChannelOptions { get; } = new();

    public ObservableCollection<LogLineItem> TestRunLogs { get; } = new();

    public ObservableCollection<RecipeMotionSequenceItem> MotionSequences { get; } = new();

    public ObservableCollection<RecipeVisionResultItem> VisionResults { get; } = new();

    public ObservableCollection<RecipeVisionResultSourceOption> VisionResultSourceOptions { get; } = new();

    public ObservableCollection<RecipeVisionFlowResultPreviewItem> VisionFlowResultPreviewItems { get; } = new();

    public bool HasVisionFlowResultPreviewItems => VisionFlowResultPreviewItems.Count > 0;

    public ObservableCollection<RecipePlcSignalItem> PlcSignals { get; } = new();

    public ObservableCollection<RecipeSignalMappingItem> SignalMappings { get; } = new();

    public IEnumerable<RecipeChannelOption> SelectedCommunicationChannelOptions
    {
        get
        {
            var source = SelectedProcessStep?.SignalSource;
            return string.Equals(source, "serial", StringComparison.OrdinalIgnoreCase)
                ? SerialChannelOptions
                : TcpChannelOptions;
        }
    }

    public DelegateCommand ReloadCommand { get; }

    public DelegateCommand SaveCommand { get; }

    public DelegateCommand SetCurrentRecipeCommand { get; }

    public AsyncDelegateCommand TestRunRecipeCommand { get; }

    public DelegateCommand PauseTestRunCommand { get; }

    public DelegateCommand ResetTestRunCommand { get; }

    public AsyncDelegateCommand OpenFlowEditorCommand { get; }

    public DelegateCommand NewRecipeCommand { get; }

    public DelegateCommand DuplicateRecipeCommand { get; }

    public DelegateCommand DeleteRecipeCommand { get; }

    public DelegateCommand AddProductParameterCommand { get; }

    public DelegateCommand RemoveProductParameterCommand { get; }

    public DelegateCommand UseVariableInProductParameterCommand { get; }

    public DelegateCommand UseVariableInProcessStepCommand { get; }

    public DelegateCommand<object> AddProcessToolCommand { get; }

    public DelegateCommand<object> SelectProcessStepCommand { get; }

    public DelegateCommand<object> RemoveProcessStepItemCommand { get; }

    public DelegateCommand<object> MoveProcessStepLeftCommand { get; }

    public DelegateCommand<object> MoveProcessStepRightCommand { get; }

    public DelegateCommand AddProcessStepCommand { get; }

    public DelegateCommand RemoveProcessStepCommand { get; }

    public DelegateCommand MoveProcessStepUpCommand { get; }

    public DelegateCommand MoveProcessStepDownCommand { get; }

    public DelegateCommand AddMotionSequenceCommand { get; }

    public DelegateCommand RemoveMotionSequenceCommand { get; }

    public DelegateCommand AddVisionResultCommand { get; }

    public DelegateCommand RemoveVisionResultCommand { get; }

    public DelegateCommand AddPlcSignalCommand { get; }

    public DelegateCommand RemovePlcSignalCommand { get; }

    public DelegateCommand AddSignalMappingCommand { get; }

    public DelegateCommand RemoveSignalMappingCommand { get; }

    public string RecipeDirectory => _paths.RecipeDirectory;

    public string RecipeId
    {
        get => _recipeId;
        private set => SetProperty(ref _recipeId, value);
    }

    public string RecipeName
    {
        get => _recipeName;
        set => SetEditorProperty(ref _recipeName, value);
    }

    public string ProductCode
    {
        get => _productCode;
        set => SetEditorProperty(ref _productCode, value);
    }

    public string Description
    {
        get => _description;
        set => SetEditorProperty(ref _description, value);
    }

    public string CameraId
    {
        get => _cameraId;
        set => SetEditorProperty(ref _cameraId, value);
    }

    public string CameraDisplayName
    {
        get => _cameraDisplayName;
        set => SetEditorProperty(ref _cameraDisplayName, value);
    }

    public string ExposureTimeUs
    {
        get => _exposureTimeUs;
        set => SetEditorProperty(ref _exposureTimeUs, value);
    }

    public string Gain
    {
        get => _gain;
        set => SetEditorProperty(ref _gain, value);
    }

    public bool HardwareTrigger
    {
        get => _hardwareTrigger;
        set => SetEditorProperty(ref _hardwareTrigger, value);
    }

    public bool SaveOkImages
    {
        get => _saveOkImages;
        set => SetEditorProperty(ref _saveOkImages, value);
    }

    public bool SaveNgImages
    {
        get => _saveNgImages;
        set => SetEditorProperty(ref _saveNgImages, value);
    }

    public string RetentionDays
    {
        get => _retentionDays;
        set => SetEditorProperty(ref _retentionDays, value);
    }

    public string MaxStorageMegabytes
    {
        get => _maxStorageMegabytes;
        set => SetEditorProperty(ref _maxStorageMegabytes, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string TestRunStateText
    {
        get => _testRunStateText;
        private set => SetProperty(ref _testRunStateText, value);
    }

    public string TestRunButtonText => IsTestRunPaused ? "继续流程" : "试运行流程";

    public bool IsTestRunning
    {
        get => _isTestRunning;
        private set
        {
            if (SetProperty(ref _isTestRunning, value))
            {
                RaisePropertyChanged(nameof(TestRunButtonText));
                RaisePropertyChanged(nameof(IsRecipeEditingEnabled));
                RaiseCommandStates();
            }
        }
    }

    public bool IsTestRunPaused
    {
        get => _isTestRunPaused;
        private set
        {
            if (SetProperty(ref _isTestRunPaused, value))
            {
                RaisePropertyChanged(nameof(TestRunButtonText));
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(IsRecipeEditingEnabled));
                RaiseCommandStates();
            }
        }
    }

    public bool IsRecipeEditingEnabled => !IsBusy && !IsTestRunning;

    public bool IsCurrentRecipe
    {
        get => _isCurrentRecipe;
        private set
        {
            if (SetProperty(ref _isCurrentRecipe, value))
            {
                RaisePropertyChanged(nameof(CurrentRecipeBadge));
                RaiseCommandStates();
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
                    "配方管理",
                    value,
                    _ => SaveAsync(),
                    RecipeName);
                RaisePropertyChanged(nameof(ChangeStateText));
                RaiseCommandStates();
            }
        }
    }

    public string CurrentRecipeBadge => IsCurrentRecipe ? "当前生产配方" : "候选配方";

    public string ChangeStateText => HasUnsavedChanges ? "有未保存修改" : "已保存";

    public IReadOnlyList<RecipeProcessToolboxItem> ProcessToolboxItems { get; } =
    [
        new(ProcessStepType.AxisMove, "运动控制", "轴运动、点位切换、动作命令", "#FF33D6A6"),
        new(ProcessStepType.WaitPlcSignal, "等待信号", "等待轴卡输入、PLC、TCP 或串口信号", "#FFFFC95A"),
        new(ProcessStepType.RunVisionFlow, "运行视觉", "调用当前产品绑定的视觉流程", "#FF3DDC97"),
        new(ProcessStepType.ReadVisionResult, "读取结果", "从视觉结果集中提取字段", "#FF7AD7FF"),
        new(ProcessStepType.WriteResultTable, "写结果表", "写入程序内部结果字段", "#FF5CE08A"),
        new(ProcessStepType.WritePlc, "写 PLC", "把结果或状态回写 PLC", "#FFFF8A65"),
        new(ProcessStepType.DeviceRead, "读设备", "从指定设备地址读取值", "#FF7AD7FF"),
        new(ProcessStepType.DeviceWrite, "写设备", "向指定设备地址写入值", "#FFFF8A65"),
        new(ProcessStepType.DeviceCommand, "设备命令", "调用指定设备的命令接口", "#FFBFA2FF"),
        new(ProcessStepType.StringProcess, "字符串处理", "解析 TCP、串口、PLC 或扫码枪收到的文本", "#FF6FD4FF"),
        new(ProcessStepType.Delay, "延时", "流程暂停指定毫秒", "#FFBFA2FF"),
        new(ProcessStepType.End, "结束", "显式结束当前运行流程", "#FFFF667A")
    ];

    public IReadOnlyList<RecipeProcessToolboxItem> RuntimeProcessToolboxItems { get; } =
    [
        new(ProcessStepType.AxisMove, "运动控制", "支持多轴一起运动，预留真实轴卡同步执行", "#FF33D6A6"),
        new(ProcessStepType.Delay, "延迟", "让当前流程暂停指定毫秒", "#FFBFA2FF"),
        new(ProcessStepType.RunVisionFlow, "视觉工具", "调用当前产品绑定的视觉流程并获取结果", "#FF3DDC97"),
        new(ProcessStepType.WaitPlcSignal, "等待信号", "轴卡输入、PLC、TCP、串口都可作为等待源", "#FFFFC95A", "wait-signal"),
        new(ProcessStepType.DeviceCommand, "TCP 通讯", "发送文本或等待 TCP 响应，可写入结果键", "#FF7AD7FF", "tcp"),
        new(ProcessStepType.DeviceCommand, "串口通讯", "用于扫码枪、仪表或下位机串口握手", "#FF8FD4FF", "serial"),
        new(ProcessStepType.WritePlc, "写 PLC", "把流程结果或状态写回 PLC", "#FFFF8A65"),
        new(ProcessStepType.DeviceRead, "读设备", "从 PLC 或其他仪器读取地址值", "#FF7AD7FF"),
        new(ProcessStepType.DeviceWrite, "写设备", "向 PLC 或其他仪器写入地址值", "#FFFF8A65"),
        new(ProcessStepType.DeviceCommand, "设备命令", "调用设备适配器暴露的命令", "#FFBFA2FF"),
        new(ProcessStepType.StringProcess, "字符串处理", "分割、截取、正则提取接收到的字符串", "#FF6FD4FF"),
        new(ProcessStepType.ResultJudge, "结果判定", "按视觉结果的上下限做 OK/NG 判定", "#FF6FE7C8"),
        new(ProcessStepType.WriteResultTable, "存入表格", "把结果写入当前生产记录表", "#FF5CE08A")
    ];

    public IReadOnlyList<string> StringProcessOperationOptions { get; } =
    [
        "分割",
        "正则提取",
        "截取",
        "替换",
        "去空格",
        "转大写",
        "转小写",
        "包含判断"
    ];

    public IReadOnlyList<string> VisionResultDataTypeOptions { get; } =
    [
        "VisionResult",
        "Image",
        "Point",
        "Pose",
        "Line",
        "Circle",
        "Number",
        "Text",
        "Result",
        "Roi",
        "Point[]",
        "Pose[]",
        "Number[]"
    ];

    public string FlowSummaryText => SelectedFlow is null
        ? "未选择运行流程"
        : $"{SelectedFlow.Name} / {SelectedFlow.ToolCount} 工具 / {SelectedFlow.RoiCount} ROI";

    public int RecipeVariableCount => RecipeVariables.Count;

    public string SelectedRecipeVariableReference => SelectedRecipeVariable?.ReferenceText ?? string.Empty;

    public RecipeListItem? SelectedRecipe
    {
        get => _selectedRecipe;
        set
        {
            if (IsTestRunning)
            {
                return;
            }

            if (!SetProperty(ref _selectedRecipe, value))
            {
                return;
            }

            AdvanceRecipePageEpoch();
            RaiseCommandStates();
            _ = LoadRecipeAsync(value?.Id);
        }
    }

    public RecipeFlowSummaryItem? SelectedFlow
    {
        get => _selectedFlow;
        set
        {
            if (IsTestRunning)
            {
                return;
            }

            if (SetProperty(ref _selectedFlow, value))
            {
                RaisePropertyChanged(nameof(FlowSummaryText));
                RefreshVisionResultSourceOptions(_loadedRecipe);
                MarkDirty("运行流程已切换，等待保存");
            }
        }
    }

    public RecipeProductParameterItem? SelectedProductParameter
    {
        get => _selectedProductParameter;
        set
        {
            if (SetProperty(ref _selectedProductParameter, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public RecipeVariableItem? SelectedRecipeVariable
    {
        get => _selectedRecipeVariable;
        set
        {
            if (SetProperty(ref _selectedRecipeVariable, value))
            {
                RaisePropertyChanged(nameof(SelectedRecipeVariableReference));
                RaiseCommandStates();
            }
        }
    }

    public RecipeProcessStepItem? SelectedProcessStep
    {
        get => _selectedProcessStep;
        set
        {
            if (SetProperty(ref _selectedProcessStep, value))
            {
                SelectedSignalMapping = value?.IsWaitSignalStep == true
                    ? SignalMappings.FirstOrDefault(item =>
                        string.Equals(item.Key, value.SignalId, StringComparison.OrdinalIgnoreCase))
                    : SelectedSignalMapping;
                RaisePropertyChanged(nameof(SelectedCommunicationChannelOptions));
                RefreshVisionFlowResultPreviewItems();
                RaiseCommandStates();
            }
        }
    }

    public RecipeMotionSequenceItem? SelectedMotionSequence
    {
        get => _selectedMotionSequence;
        set
        {
            if (SetProperty(ref _selectedMotionSequence, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public RecipeVisionResultItem? SelectedVisionResult
    {
        get => _selectedVisionResult;
        set
        {
            if (SetProperty(ref _selectedVisionResult, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public RecipePlcSignalItem? SelectedPlcSignal
    {
        get => _selectedPlcSignal;
        set
        {
            if (SetProperty(ref _selectedPlcSignal, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public RecipeSignalMappingItem? SelectedSignalMapping
    {
        get => _selectedSignalMapping;
        set
        {
            if (SetProperty(ref _selectedSignalMapping, value))
            {
                RaiseCommandStates();
            }
        }
    }

    private bool CanEditRecipe()
    {
        return !IsBusy && !IsTestRunning && _loadedRecipe is not null;
    }

    private bool CanUseVariableInProductParameter()
    {
        return CanEditRecipe() && SelectedRecipeVariable is not null && SelectedProductParameter is not null;
    }

    private bool CanUseVariableInProcessStep()
    {
        return CanEditRecipe() && SelectedRecipeVariable is not null && SelectedProcessStep is not null;
    }

    private bool CanSave()
    {
        return CanEditRecipe();
    }

    private bool CanSetCurrentRecipe()
    {
        return CanEditRecipe() && !IsCurrentRecipe;
    }

    private bool CanOpenFlowEditor()
    {
        return CanEditRecipe();
    }

    private bool CanDeleteRecipe()
    {
        return CanEditRecipe() && SelectedRecipe is not null && Recipes.Count > 1;
    }

    private bool CanMoveProcessStepUp()
    {
        return CanEditRecipe() && SelectedProcessStep is not null && ProcessSteps.IndexOf(SelectedProcessStep) > 0;
    }

    private bool CanMoveProcessStepDown()
    {
        return CanEditRecipe() && SelectedProcessStep is not null && ProcessSteps.IndexOf(SelectedProcessStep) >= 0 &&
               ProcessSteps.IndexOf(SelectedProcessStep) < ProcessSteps.Count - 1;
    }

    private bool CanReload() => !IsBusy && !IsTestRunning;

    private bool CanCreateRecipe() => !IsBusy && !IsTestRunning;

    private async Task LoadAsync(string? preferredRecipeId = null)
    {
        if (IsTestRunning)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshCommunicationChannelOptionsAsync();
            var currentRecipeId = await _recipes.GetCurrentRecipeIdAsync();
            var selectedId = preferredRecipeId ?? SelectedRecipe?.Id ?? currentRecipeId;
            var recipes = await _recipes.ListAsync();

            Recipes.Clear();
            foreach (var recipe in recipes.OrderBy(recipe => recipe.Name))
            {
                var activeFlow = recipe.GetActiveFlow();
                Recipes.Add(new RecipeListItem(
                    recipe.Id,
                    recipe.Name,
                    recipe.ProductCode,
                    recipe.EffectiveFlows.Count,
                    activeFlow.Tools.Count,
                    activeFlow.Rois.Count,
                    recipe.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
                    string.Equals(recipe.Id, currentRecipeId, StringComparison.OrdinalIgnoreCase)));
            }

            SelectedRecipe = Recipes.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                             ?? Recipes.FirstOrDefault(item => item.IsCurrent)
                             ?? Recipes.FirstOrDefault();

            StatusText = SelectedRecipe is null
                ? "未找到可用配方"
                : $"已加载 {Recipes.Count} 个配方，当前选中 {SelectedRecipe.Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnConfigurationSaved(object? sender, DeviceConfiguration configuration)
    {
        ApplyCommunicationChannelOptions(configuration);
    }

    private async Task RefreshCommunicationChannelOptionsAsync()
    {
        var configuration = await _configurationRepository.GetAsync();
        ApplyCommunicationChannelOptions(configuration);
    }

    private void ApplyCommunicationChannelOptions(DeviceConfiguration configuration)
    {
        ReplaceItems(TcpChannelOptions, configuration.SystemSettings.Communication.TcpChannels
            .Where(channel => channel.Enabled)
            .Select(channel => new RecipeChannelOption(
                channel.Key,
                string.IsNullOrWhiteSpace(channel.Name) ? channel.Key : $"{channel.Name} / {channel.Key}")));

        ReplaceItems(SerialChannelOptions, configuration.SystemSettings.Communication.SerialChannels
            .Where(channel => channel.Enabled)
            .Select(channel => new RecipeChannelOption(
                channel.Key,
                string.IsNullOrWhiteSpace(channel.Name) ? channel.Key : $"{channel.Name} / {channel.Key}")));

        RaisePropertyChanged(nameof(SelectedCommunicationChannelOptions));
    }

    private async Task LoadRecipeAsync(string? recipeId)
    {
        if (IsTestRunning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(recipeId))
        {
            ClearEditor();
            return;
        }

        IsBusy = true;
        try
        {
            if (HasUnsavedChanges && _loadedRecipe is not null && !string.Equals(_loadedRecipe.Id, recipeId, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warning("Recipe", $"Unsaved changes in {_loadedRecipe.Name} were discarded when switching recipes.");
            }

            var recipe = await _recipes.GetAsync(recipeId) ?? await _recipes.GetCurrentAsync();
            var currentRecipeId = await _recipes.GetCurrentRecipeIdAsync();
            ApplyRecipe(recipe.WithNormalizedFlows(), currentRecipeId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnExternalRecipeChanged(string recipeId)
    {
        if (_loadedRecipe is null ||
            string.IsNullOrWhiteSpace(recipeId) ||
            !string.Equals(_loadedRecipe.Id, recipeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _recipeRefreshGeneration);
        if (IsTestRunning)
        {
            _pendingRecipeRefreshId = recipeId;
            return;
        }

        StartRecipeRefresh(recipeId, generation, updateStatus: true);
    }

    private void RequestRecipeRefresh(string recipeId, bool updateStatus)
    {
        var generation = Interlocked.Increment(ref _recipeRefreshGeneration);
        StartRecipeRefresh(recipeId, generation, updateStatus);
    }

    private void StartRecipeRefresh(
        string recipeId,
        long generation,
        bool updateStatus)
    {
        _ = RefreshRecipeVariablesAsync(recipeId, generation, updateStatus);
    }

    private async Task RefreshRecipeVariablesAsync(
        string recipeId,
        long generation,
        bool updateStatus)
    {
        Recipe? recipe = null;
        for (var attempt = 1; attempt <= MaxRecipeRefreshAttempts; attempt++)
        {
            if (generation != Volatile.Read(ref _recipeRefreshGeneration))
            {
                return;
            }

            try
            {
                recipe = await _recipes
                    .GetAsync(recipeId)
                    .ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                if (attempt == MaxRecipeRefreshAttempts ||
                    generation != Volatile.Read(ref _recipeRefreshGeneration))
                {
                    LogWarningSafely($"配方参数刷新失败：{ex.Message}");
                    return;
                }
            }

            await Task.Delay(RecipeRefreshRetryDelayMs).ConfigureAwait(false);
        }

        try
        {
            if (recipe is null ||
                generation != Volatile.Read(ref _recipeRefreshGeneration))
            {
                return;
            }

            _uiDispatcher.Invoke(() =>
            {
                if (generation != Volatile.Read(ref _recipeRefreshGeneration))
                {
                    return;
                }

                if (IsTestRunning)
                {
                    _pendingRecipeRefreshId = recipeId;
                    return;
                }

                if (_loadedRecipe is null ||
                    !string.Equals(
                        _loadedRecipe.Id,
                        recipeId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                ApplyRecipeVariables(recipe, updateStatus);
            });
        }
        catch (Exception ex)
        {
            LogWarningSafely($"配方参数刷新失败：{ex.Message}");
        }
    }

    private void ApplyRecipeVariables(Recipe recipe, bool updateStatus)
    {
        _isLoadingEditor = true;
        try
        {
            ReplaceItems(RecipeVariables, recipe.Variables.Select(RecipeVariableItem.FromDefinition));
            NormalizeWaitStepsToVariables();
            SelectedRecipeVariable = RecipeVariables.FirstOrDefault(variable =>
                                         string.Equals(variable.Key, SelectedRecipeVariable?.Key, StringComparison.OrdinalIgnoreCase))
                                     ?? RecipeVariables.FirstOrDefault();
            _loadedRecipe = _loadedRecipe! with { Variables = recipe.Variables };
            RaisePropertyChanged(nameof(RecipeVariableCount));
            RefreshVisionFlowResultPreviewItems();
            if (updateStatus)
            {
                StatusText = $"参数已同步：{RecipeVariables.Count} 个参数可用于配方步骤。";
            }
        }
        finally
        {
            _isLoadingEditor = false;
            RaiseCommandStates();
        }
    }

    private void ApplyRecipe(Recipe recipe, string currentRecipeId)
    {
        AdvanceRecipePageEpoch();
        _isLoadingEditor = true;
        try
        {
            _loadedRecipe = recipe;
            _lastTestRun = null;
            RecipeId = recipe.Id;
            _recipeName = recipe.Name;
            RaisePropertyChanged(nameof(RecipeName));
            _productCode = recipe.ProductCode;
            RaisePropertyChanged(nameof(ProductCode));
            _description = recipe.Description;
            RaisePropertyChanged(nameof(Description));
            _cameraId = recipe.Camera.CameraId;
            RaisePropertyChanged(nameof(CameraId));
            _cameraDisplayName = recipe.Camera.DisplayName;
            RaisePropertyChanged(nameof(CameraDisplayName));
            _exposureTimeUs = recipe.Camera.ExposureTimeUs.ToString("0.###");
            RaisePropertyChanged(nameof(ExposureTimeUs));
            _gain = recipe.Camera.Gain.ToString("0.###");
            RaisePropertyChanged(nameof(Gain));
            _hardwareTrigger = recipe.Camera.HardwareTrigger;
            RaisePropertyChanged(nameof(HardwareTrigger));
            _saveOkImages = recipe.TracePolicy.SaveOkImages;
            RaisePropertyChanged(nameof(SaveOkImages));
            _saveNgImages = recipe.TracePolicy.SaveNgImages;
            RaisePropertyChanged(nameof(SaveNgImages));
            _retentionDays = recipe.TracePolicy.RetentionDays.ToString();
            RaisePropertyChanged(nameof(RetentionDays));
            _maxStorageMegabytes = recipe.TracePolicy.MaxStorageMegabytes.ToString();
            RaisePropertyChanged(nameof(MaxStorageMegabytes));

            FlowSummaries.Clear();
            foreach (var flow in recipe.EffectiveFlows)
            {
                FlowSummaries.Add(new RecipeFlowSummaryItem(
                    flow.Id,
                    flow.Name,
                    flow.Tools.Count,
                    flow.Rois.Count,
                    flow.UpdatedAt.ToString("yyyy-MM-dd HH:mm")));
            }

            _selectedFlow = FlowSummaries.FirstOrDefault(item => string.Equals(item.Id, recipe.CurrentFlowId, StringComparison.OrdinalIgnoreCase))
                            ?? FlowSummaries.FirstOrDefault();
            RaisePropertyChanged(nameof(SelectedFlow));
            RaisePropertyChanged(nameof(FlowSummaryText));

            ReplaceItems(ProductParameters, recipe.ProductParameters.Select(RecipeProductParameterItem.FromDefinition));
            ReplaceItems(RecipeVariables, recipe.Variables.Select(RecipeVariableItem.FromDefinition));
            ReplaceItems(ProcessSteps, NormalizeRecipeProcessSteps(recipe.ProcessSteps).Select(RecipeProcessStepItem.FromDefinition));
            NormalizeWaitStepsToVariables();
            ReplaceItems(MotionSequences, recipe.MotionSequences.Select(RecipeMotionSequenceItem.FromDefinition));
            ReplaceItems(VisionResults, recipe.VisionResults.Select(RecipeVisionResultItem.FromDefinition));
            ReplaceItems(PlcSignals, recipe.PlcSignals.Select(RecipePlcSignalItem.FromDefinition));
            ReplaceItems(SignalMappings, recipe.SignalMappings.Select(RecipeSignalMappingItem.FromDefinition));

            SelectedProductParameter = ProductParameters.FirstOrDefault();
            SelectedRecipeVariable = RecipeVariables.FirstOrDefault();
            SelectedProcessStep = ProcessSteps.FirstOrDefault();
            SelectedMotionSequence = MotionSequences.FirstOrDefault();
            SelectedVisionResult = VisionResults.FirstOrDefault();
            SelectedPlcSignal = PlcSignals.FirstOrDefault();
            SelectedSignalMapping = SignalMappings.FirstOrDefault(item =>
                string.Equals(item.Key, SelectedProcessStep?.SignalId, StringComparison.OrdinalIgnoreCase))
                ?? SignalMappings.FirstOrDefault();
            RefreshVisionResultSourceOptions(recipe);
            RefreshVisionFlowResultPreviewItems();

            IsCurrentRecipe = string.Equals(recipe.Id, currentRecipeId, StringComparison.OrdinalIgnoreCase);
            HasUnsavedChanges = false;
            StatusText = $"正在编辑 {recipe.Name}，运行流程 {SelectedFlow?.Name ?? "-"}";
        }
        finally
        {
            _isLoadingEditor = false;
        }
    }

    private void ClearEditor()
    {
        AdvanceRecipePageEpoch();
        _isLoadingEditor = true;
        try
        {
            _loadedRecipe = null;
            RecipeId = string.Empty;
            RecipeName = string.Empty;
            ProductCode = string.Empty;
            Description = string.Empty;
            CameraId = string.Empty;
            CameraDisplayName = string.Empty;
            ExposureTimeUs = "8000";
            Gain = "1.0";
            HardwareTrigger = false;
            SaveOkImages = true;
            SaveNgImages = true;
            RetentionDays = "30";
            MaxStorageMegabytes = "20480";
            FlowSummaries.Clear();
            ProductParameters.Clear();
            RecipeVariables.Clear();
            ProcessSteps.Clear();
            MotionSequences.Clear();
            VisionResults.Clear();
            VisionResultSourceOptions.Clear();
            VisionFlowResultPreviewItems.Clear();
            RaisePropertyChanged(nameof(HasVisionFlowResultPreviewItems));
            PlcSignals.Clear();
            SignalMappings.Clear();
            _lastTestRun = null;
            SelectedFlow = null;
            SelectedProductParameter = null;
            SelectedRecipeVariable = null;
            SelectedProcessStep = null;
            SelectedMotionSequence = null;
            SelectedVisionResult = null;
            SelectedPlcSignal = null;
            SelectedSignalMapping = null;
            IsCurrentRecipe = false;
            HasUnsavedChanges = false;
            StatusText = "请选择一个配方进行编辑";
        }
        finally
        {
            _isLoadingEditor = false;
        }
    }

    private async Task SaveAsync()
    {
        if (!CanSave())
        {
            return;
        }

        var recipe = await PersistSelectedRecipeAsync(setCurrentRecipe: IsCurrentRecipe, refreshList: true);
        if (recipe is null)
        {
            return;
        }

        StatusText = $"配方 {recipe.Name} 已保存";
        _log.Info("Recipe", $"Saved recipe {recipe.Name} ({recipe.ProductCode})");
    }

    private async Task SetCurrentRecipeAsync()
    {
        if (!CanSetCurrentRecipe())
        {
            return;
        }

        var recipe = await PersistSelectedRecipeAsync(setCurrentRecipe: true, refreshList: true);
        if (recipe is null)
        {
            return;
        }

        StatusText = $"当前生产配方已切换为 {recipe.Name}";
        _log.Info("Recipe", $"Current recipe switched to {recipe.Name}");
    }

    private async Task TestRunRecipeAsync()
    {
        if (IsTestRunPaused)
        {
            ResumeTestRun();
            return;
        }

        if (IsTestRunning || _loadedRecipe is null)
        {
            if (_loadedRecipe is null)
            {
                StatusText = "请先选择一个配方，再试运行流程";
                AddTestRunLog("WARN", "Recipe", StatusText);
                _log.Warning("Recipe", "Test run ignored because no recipe is selected.");
            }

            return;
        }

        var admission = _inspectionExecution.TryBegin(new InspectionRunIntent(
            InspectionRunModes.RecipeTest,
            nameof(RecipeManagementViewModel)));
        if (admission is RunAdmission.Rejected rejected)
        {
            StatusText = ProductionRunUiState.FormatRejection(rejected.Rejection);
            TestRunStateText = StatusText;
            RaiseCommandStates();
            return;
        }

        await using var session = ((RunAdmission.Acquired)admission).Session;
        using var lifetime = new CancellationTokenSource();
        _testRunLifetimeCancellation = lifetime;
        _testRunLifetimeCancellationCompletion = Task.CompletedTask;
        var recipeName = RecipeName;
        var runControlStarted = false;
        try
        {
            var runRecipeSnapshot = BuildRecipe();
            var attemptRecipeSnapshot = runRecipeSnapshot;
            recipeName = runRecipeSnapshot.Name;
            IsTestRunning = true;
            IsTestRunPaused = false;
            RaiseCommandStates();
            var restartRequested = false;
            do
            {
                restartRequested = false;
                _testRunResetRequested = false;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
                    lifetime.Token);
                _testRunAttemptCancellation = attempt;
                _testRunAttemptCancellationCompletion = Task.CompletedTask;
                _inspectionRunControl.BeginRun();
                runControlStarted = true;
                try
                {
                    _log.Info("Recipe", $"试运行：正在保存配方 {recipeName} 并启动流程");
                    var recipe = await PersistSelectedRecipeAsync(
                        setCurrentRecipe: true,
                        refreshList: false,
                        attempt.Token,
                        attemptRecipeSnapshot);
                    if (recipe is null)
                    {
                        StatusText = "试运行取消：当前配方保存失败";
                        _log.Warning("Recipe", StatusText);
                        return;
                    }

                    recipeName = recipe.Name;
                    ResetProcessStepRuntimeStates(prepareForRun: true);
                    StatusText = $"正在试运行 {recipe.Name}";
                    TestRunStateText = StatusText;
                    _log.Info("Recipe", $"Test run started for recipe {recipe.Name}");
                    await _communicationChannels.ConnectAsync(
                        CommunicationChannelConnectionPolicies.Production,
                        attempt.Token);
                    var run = await session.ExecuteAsync(new InspectionRequest
                    {
                        RecipeId = recipe.Id,
                        BatchId = DateTimeOffset.Now.ToString("yyyyMMdd"),
                        OperatorName = Environment.UserName,
                        TriggeredByPlc = false,
                        ProcessOnly = true
                    }, attempt.Token);
                    StatusText = $"试运行完成：{run.Result.Outcome}，耗时 {run.Result.CycleTime.TotalMilliseconds:0} ms";
                    TestRunStateText = StatusText;
                    AddTestRunResultSnapshot(run);
                    _log.Info("Recipe", $"Test run completed for recipe {recipe.Name}: {run.Result.Outcome}");
                }
                catch (InspectionRunResetException) when (
                    !lifetime.IsCancellationRequested)
                {
                    restartRequested = true;
                }
                catch (OperationCanceledException) when (
                    _testRunResetRequested && !lifetime.IsCancellationRequested)
                {
                    restartRequested = true;
                }
                finally
                {
                    var attemptCancellationCompletion =
                        _testRunAttemptCancellationCompletion;
                    _testRunAttemptCancellation = null;
                    await attemptCancellationCompletion;
                    if (lifetime.IsCancellationRequested)
                    {
                        await _testRunLifetimeCancellationCompletion;
                    }
                }

                if (restartRequested)
                {
                    attemptRecipeSnapshot = WithVariableValuesResetToDefaults(
                        runRecipeSnapshot);
                    IsTestRunPaused = false;
                    ResetRecipeVariablesToDefaults();
                    ResetProcessStepRuntimeStates(prepareForRun: true);
                    StatusText = "流程已复位，正在从第一步重新开始";
                    TestRunStateText = StatusText;
                    AddTestRunLog("INFO", "Recipe", StatusText);
                    _log.Info("Recipe", StatusText);
                }
            }
            while (restartRequested);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            StatusText = "配方试运行已取消";
            TestRunStateText = StatusText;
        }
        catch (Exception ex)
        {
            StatusText = $"试运行失败：{ex.Message}";
            TestRunStateText = StatusText;
            MarkActiveRuntimeStepFailed(ex.Message);
            LogErrorSafely($"Test run failed for recipe {recipeName}: {ex.Message}");
        }
        finally
        {
            Exception? endRunFailure = null;
            if (runControlStarted)
            {
                try
                {
                    _inspectionRunControl.EndRun();
                }
                catch (Exception ex)
                {
                    endRunFailure = ex;
                }
            }

            Exception? disconnectFailure = null;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _communicationChannels.DisconnectAsync(
                    CommunicationChannelConnectionPolicies.Production,
                    cleanup.Token);
            }
            catch (Exception ex)
            {
                disconnectFailure = ex;
            }

            await _testRunAttemptCancellationCompletion;
            await _testRunLifetimeCancellationCompletion;
            _testRunAttemptCancellation = null;
            _testRunLifetimeCancellation = null;
            _testRunResetRequested = false;
            try
            {
                IsTestRunPaused = false;
            }
            catch
            {
            }

            try
            {
                IsTestRunning = false;
            }
            catch
            {
            }

            var pendingRecipeRefreshId = _pendingRecipeRefreshId;
            _pendingRecipeRefreshId = null;
            try
            {
                RaiseCommandStates();
            }
            catch
            {
            }

            if (endRunFailure is not null)
            {
                LogWarningSafely($"试运行控制清理失败：{endRunFailure.Message}");
            }

            if (disconnectFailure is not null)
            {
                LogWarningSafely($"试运行通信清理失败：{disconnectFailure.Message}");
            }

            if (!string.IsNullOrWhiteSpace(pendingRecipeRefreshId))
            {
                RequestRecipeRefresh(pendingRecipeRefreshId, updateStatus: false);
            }
        }
    }

    private async Task OpenFlowEditorAsync()
    {
        if (!CanOpenFlowEditor())
        {
            return;
        }

        var recipe = await PersistSelectedRecipeAsync(setCurrentRecipe: true, refreshList: false);
        if (recipe is null)
        {
            return;
        }

        await _flowEditorDialog.ShowEditorAsync(recipe.Id);
        await LoadAsync(recipe.Id);
        StatusText = $"已打开 {recipe.Name} 的运行流程编辑器";
    }

    private void ReportOpenFlowEditorFailureSafely(Exception exception)
    {
        var message = $"打开流程编辑器失败：{exception.Message}";
        try
        {
            StatusText = message;
        }
        catch
        {
        }

        LogErrorSafely(message);
    }

    private void ReportTestRunFailureSafely(Exception exception)
    {
        var message = $"试运行失败：{exception.Message}";
        try
        {
            StatusText = message;
            TestRunStateText = message;
        }
        catch
        {
        }

        try
        {
            IsTestRunPaused = false;
            IsTestRunning = false;
            RaiseCommandStates();
        }
        catch
        {
        }

        LogErrorSafely(message);
    }

    private void LogErrorSafely(string message)
    {
        try
        {
            _log.Error("Recipe", message);
        }
        catch
        {
        }
    }

    private void LogWarningSafely(string message)
    {
        try
        {
            _log.Warning("Recipe", message);
        }
        catch
        {
        }
    }

    private void RequestAttemptCancellation()
    {
        _testRunAttemptCancellationCompletion = BeginCancellation(
            _testRunAttemptCancellation,
            _testRunAttemptCancellationCompletion,
            "试运行尝试取消失败");
    }

    private void RequestLifetimeCancellation()
    {
        _testRunLifetimeCancellationCompletion = BeginCancellation(
            _testRunLifetimeCancellation,
            _testRunLifetimeCancellationCompletion,
            "试运行页面取消失败");
    }

    private Task BeginCancellation(
        CancellationTokenSource? source,
        Task previousCompletion,
        string failurePrefix)
    {
        if (source is null)
        {
            return previousCompletion;
        }

        Task currentCompletion;
        try
        {
            currentCompletion = source.CancelAsync();
        }
        catch (Exception ex)
        {
            LogWarningSafely($"{failurePrefix}：{ex.Message}");
            return previousCompletion;
        }

        return ObserveCancellationAsync(
            previousCompletion,
            currentCompletion,
            failurePrefix);
    }

    private async Task ObserveCancellationAsync(
        Task previousCompletion,
        Task currentCompletion,
        string failurePrefix)
    {
        try
        {
            await previousCompletion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogWarningSafely($"{failurePrefix}：{ex.Message}");
        }

        try
        {
            await currentCompletion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogWarningSafely($"{failurePrefix}：{ex.Message}");
        }
    }

    private async Task CreateRecipeAsync()
    {
        if (!CanCreateRecipe())
        {
            return;
        }

        var recipeId = BuildRecipeId("recipe");
        var recipe = DefaultRecipeFactory.Create() with
        {
            Id = recipeId,
            Name = $"新产品-{DateTimeOffset.Now:HHmmss}",
            ProductCode = $"MODEL-{DateTimeOffset.Now:HHmmss}",
            UpdatedAt = DateTimeOffset.Now
        };

        await _recipes.SaveAsync(recipe);
        await LoadAsync(recipe.Id);
        StatusText = $"已创建新配方 {recipe.Name}";
    }

    private async Task DuplicateRecipeAsync()
    {
        if (!CanOpenFlowEditor())
        {
            return;
        }

        if (_loadedRecipe is null)
        {
            return;
        }

        var source = BuildRecipe();
        var copy = source with
        {
            Id = BuildRecipeId(source.ProductCode),
            Name = $"{source.Name}-副本",
            ProductCode = $"{source.ProductCode}-COPY",
            UpdatedAt = DateTimeOffset.Now
        };

        await _recipes.SaveAsync(copy);
        await LoadAsync(copy.Id);
        StatusText = $"已复制配方 {copy.Name}";
    }

    private async Task DeleteRecipeAsync()
    {
        if (!CanDeleteRecipe())
        {
            return;
        }

        var target = SelectedRecipe;
        if (target is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"确定删除配方“{target.Name}”（{target.ProductCode}）吗？",
            "删除配方",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            StatusText = "已取消删除配方";
            return;
        }

        Recipe? fallbackRecipe = null;
        IsBusy = true;
        try
        {
            var recipes = await _recipes.ListAsync();
            if (recipes.Count <= 1)
            {
                StatusText = "至少需要保留一个配方，当前配方不能删除";
                return;
            }

            fallbackRecipe = recipes
                .Where(recipe => !string.Equals(recipe.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(recipe => recipe.Name)
                .FirstOrDefault();

            if (fallbackRecipe is null)
            {
                StatusText = "没有可切换的备用配方，删除已取消";
                return;
            }

            await _recipes.DeleteAsync(target.Id);
            if (target.IsCurrent)
            {
                await _recipes.SetCurrentRecipeAsync(fallbackRecipe.Id);
            }

            _loadedRecipe = null;
            HasUnsavedChanges = false;
            _log.Warning("Recipe", $"Deleted recipe {target.Name} ({target.ProductCode})");
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync(fallbackRecipe.Id);
        StatusText = $"已删除配方 {target.Name}，当前选中 {fallbackRecipe.Name}";
    }

    private async Task<Recipe?> PersistSelectedRecipeAsync(
        bool setCurrentRecipe,
        bool refreshList,
        CancellationToken cancellationToken = default,
        Recipe? recipeSnapshot = null)
    {
        if (_loadedRecipe is null)
        {
            return null;
        }

        var recipe = recipeSnapshot ?? BuildRecipe();
        await _recipes.SaveAsync(recipe, cancellationToken);
        _events.GetEvent<RecipeChangedEvent>().Publish(recipe.Id);
        if (setCurrentRecipe)
        {
            await _recipes.SetCurrentRecipeAsync(recipe.Id, cancellationToken);
        }

        _loadedRecipe = recipe.WithNormalizedFlows();
        HasUnsavedChanges = false;

        if (refreshList)
        {
            await LoadAsync(recipe.Id);
        }
        else
        {
            var currentRecipeId = setCurrentRecipe
                ? recipe.Id
                : await _recipes.GetCurrentRecipeIdAsync(cancellationToken);
            ApplyRecipe(_loadedRecipe, currentRecipeId);
        }

        return _loadedRecipe;
    }

    private Recipe BuildRecipe()
    {
        var recipe = (_loadedRecipe ?? DefaultRecipeFactory.Create()).WithNormalizedFlows();
        return recipe with
        {
            Name = string.IsNullOrWhiteSpace(RecipeName) ? recipe.Name : RecipeName.Trim(),
            ProductCode = string.IsNullOrWhiteSpace(ProductCode) ? recipe.ProductCode : ProductCode.Trim(),
            Description = Description?.Trim() ?? string.Empty,
            Camera = recipe.Camera with
            {
                CameraId = string.IsNullOrWhiteSpace(CameraId) ? recipe.Camera.CameraId : CameraId.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(CameraDisplayName) ? recipe.Camera.DisplayName : CameraDisplayName.Trim(),
                ExposureTimeUs = ParseDouble(ExposureTimeUs, recipe.Camera.ExposureTimeUs),
                Gain = ParseDouble(Gain, recipe.Camera.Gain),
                HardwareTrigger = HardwareTrigger
            },
            ProductParameters = ProductParameters.Select(item => item.ToDefinition()).ToArray(),
            Variables = RecipeVariables.Select(item => item.ToDefinition()).ToArray(),
            CurrentFlowId = SelectedFlow?.Id ?? recipe.CurrentFlowId,
            ProcessSteps = ProcessSteps.Select((item, index) => item.ToDefinition(index + 1)).ToArray(),
            MotionSequences = MotionSequences.Select(item => item.ToDefinition()).ToArray(),
            VisionResults = VisionResults.Select(item => item.ToDefinition()).ToArray(),
            PlcSignals = PlcSignals.Select(item => item.ToDefinition()).ToArray(),
            SignalMappings = SignalMappings.Select(item => item.ToDefinition()).ToArray(),
            TracePolicy = recipe.TracePolicy with
            {
                SaveOkImages = SaveOkImages,
                SaveNgImages = SaveNgImages,
                RetentionDays = ParseInt(RetentionDays, recipe.TracePolicy.RetentionDays),
                MaxStorageMegabytes = ParseLong(MaxStorageMegabytes, recipe.TracePolicy.MaxStorageMegabytes)
            },
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private static Recipe WithVariableValuesResetToDefaults(Recipe recipe) =>
        recipe with
        {
            Variables = recipe.Variables
                .Select(variable => variable with
                {
                    CurrentValue = variable.DefaultValue
                })
                .ToArray()
        };

    private void AddProductParameter()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        var item = new RecipeProductParameterItem
        {
            Name = $"参数-{ProductParameters.Count + 1}",
            Value = string.Empty,
            Unit = string.Empty,
            Description = "新产品参数"
        };
        ProductParameters.Add(item);
        SelectedProductParameter = item;
    }

    private void RemoveProductParameter()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (SelectedProductParameter is null)
        {
            return;
        }

        ProductParameters.Remove(SelectedProductParameter);
        SelectedProductParameter = ProductParameters.FirstOrDefault();
    }

    private void UseVariableInProductParameter()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (SelectedRecipeVariable is null || SelectedProductParameter is null)
        {
            return;
        }

        SelectedProductParameter.Value = SelectedRecipeVariable.ReferenceText;
        if (string.IsNullOrWhiteSpace(SelectedProductParameter.Unit))
        {
            SelectedProductParameter.Unit = SelectedRecipeVariable.Unit;
        }

        if (string.IsNullOrWhiteSpace(SelectedProductParameter.Description))
        {
            SelectedProductParameter.Description = $"引用变量 {SelectedRecipeVariable.Key}";
        }

        StatusText = $"已把 {SelectedRecipeVariable.ReferenceText} 写入产品参数 {SelectedProductParameter.Name}";
        MarkDirty();
    }

    private void UseVariableInProcessStep()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (SelectedRecipeVariable is null || SelectedProcessStep is null)
        {
            return;
        }

        var token = SelectedRecipeVariable.ReferenceText;
        var step = SelectedProcessStep;
        if (step.StepType == ProcessStepType.WaitPlcSignal)
        {
            step.SignalId = SelectedRecipeVariable.Key;
            StatusText = $"等待步骤已绑定参数 {SelectedRecipeVariable.DisplayText}";
            MarkDirty();
            return;
        }

        if (step.StepType == ProcessStepType.AxisMove && string.IsNullOrWhiteSpace(step.Position))
        {
            step.Position = token;
        }
        else if (step.StepType == ProcessStepType.Delay && string.IsNullOrWhiteSpace(step.DelayMs))
        {
            step.DelayMs = token;
        }

        step.ParametersText = AppendParameterLine(step.ParametersText, SelectedRecipeVariable.Key, token);
        StatusText = $"已把 {token} 写入运行步骤 {step.Name}";
        MarkDirty();
    }

    private void AddProcessStep()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        var item = new RecipeProcessStepItem
        {
            StepNo = ProcessSteps.Count + 1,
            Name = $"步骤-{ProcessSteps.Count + 1:00}",
            StepType = ProcessStepType.Delay,
            DelayMs = "200",
            TimeoutMs = "3000",
            Description = "新流程步骤"
        };

        ProcessSteps.Add(item);
        RenumberProcessSteps();
        SelectedProcessStep = item;
    }

    private void RemoveProcessStep()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (SelectedProcessStep is null)
        {
            return;
        }

        var removeIndex = ProcessSteps.IndexOf(SelectedProcessStep);
        ProcessSteps.Remove(SelectedProcessStep);
        RenumberProcessSteps();
        SelectedProcessStep = removeIndex >= 0 && removeIndex < ProcessSteps.Count
            ? ProcessSteps[removeIndex]
            : ProcessSteps.LastOrDefault();
    }

    private void MoveProcessStep(int direction)
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (SelectedProcessStep is null)
        {
            return;
        }

        var currentIndex = ProcessSteps.IndexOf(SelectedProcessStep);
        if (currentIndex < 0)
        {
            return;
        }

        var targetIndex = currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= ProcessSteps.Count)
        {
            return;
        }

        ProcessSteps.Move(currentIndex, targetIndex);
        RenumberProcessSteps();
        SelectedProcessStep = ProcessSteps[targetIndex];
    }

    private void AddProcessTool(object? parameter)
    {
        if (!CanEditRecipe())
        {
            return;
        }

        var templateKey = string.Empty;
        ProcessStepType stepType;
        if (parameter is RecipeProcessToolboxItem toolboxItem)
        {
            stepType = toolboxItem.StepType;
            templateKey = toolboxItem.TemplateKey;
        }
        else if (parameter is ProcessStepType parameterStepType)
        {
            stepType = parameterStepType;
        }
        else
        {
            return;
        }

        var item = CreateProcessStepItem(stepType);
        ApplyProcessTemplate(item, templateKey);
        if (stepType == ProcessStepType.ResultJudge)
        {
            item.Name = $"结果判定-{ProcessSteps.Count + 1:00}";
            item.StepType = stepType;
            item.ResultKey = "MeasuredWidth";
            item.OutputTarget = "OverallResult";
            item.LowerLimit = "0";
            item.UpperLimit = "999999";
            item.Description = "根据视觉结果上下限判定当前产品 OK/NG";
        }

        if (stepType == ProcessStepType.AxisMove && string.IsNullOrWhiteSpace(item.AxisTargetsText))
        {
            item.AxisTargetsText = "AxisX,0,80,120";
        }

        if (stepType == ProcessStepType.WriteResultTable)
        {
            item.Name = $"存入表格-{ProcessSteps.Count + 1:00}";
            item.StepType = stepType;
            item.ResultKey = "MeasuredWidth";
            item.OutputTarget = "ResultTable.MeasuredWidth";
            item.Description = "把关键结果写入当前生产结果表";
        }

        if (stepType == ProcessStepType.RunVisionFlow)
        {
            item.Name = $"视觉工具-{ProcessSteps.Count + 1:00}";
            item.StepType = stepType;
        }

        if (stepType == ProcessStepType.Delay)
        {
            item.Name = $"延迟-{ProcessSteps.Count + 1:00}";
            item.StepType = stepType;
        }

        ProcessSteps.Add(item);
        RenumberProcessSteps();
        SelectedProcessStep = item;
        StatusText = $"{item.StepTypeText} 节点已加入运行流程";
    }

    private void SelectProcessStep(object? parameter)
    {
        if (parameter is RecipeProcessStepItem item)
        {
            SelectedProcessStep = item;
        }
    }

    private void RemoveProcessStepItem(object? parameter)
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (parameter is not RecipeProcessStepItem item)
        {
            return;
        }

        SelectedProcessStep = item;
        RemoveProcessStep();
    }

    private void MoveProcessStepItem(object? parameter, int direction)
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (parameter is not RecipeProcessStepItem item)
        {
            return;
        }

        SelectedProcessStep = item;
        MoveProcessStep(direction);
    }

    private void ApplyProcessTemplate(RecipeProcessStepItem item, string templateKey)
    {
        var stepNo = ProcessSteps.Count + 1;
        switch (templateKey)
        {
            case "tcp":
                item.Name = $"TCP通讯-{stepNo:00}";
                item.StepType = ProcessStepType.DeviceCommand;
                item.DeviceKey = ResolveDefaultTcpChannelKey();
                item.CommandName = "SendReceive";
                item.SignalId = item.DeviceKey;
                item.ResultKey = ResolveDefaultStringOutputKey();
                item.TimeoutMs = "3000";
                item.ParametersText = $"source=tcp\r\nchannelKey={item.DeviceKey}\r\npayload=\r\nexpected=OK\r\nmatch=Contains";
                item.Description = "通过 TCP 通道发送内容并等待响应，可把响应保存到参数";
                break;
            case "serial":
                item.Name = $"串口通讯-{stepNo:00}";
                item.StepType = ProcessStepType.DeviceCommand;
                item.DeviceKey = ResolveDefaultSerialChannelKey();
                item.CommandName = "SendReceive";
                item.SignalId = item.DeviceKey;
                item.ResultKey = ResolveDefaultStringOutputKey();
                item.TimeoutMs = "3000";
                item.ParametersText = $"source=serial\r\nchannelKey={item.DeviceKey}\r\npayload=\r\nexpected=OK\r\nmatch=Contains";
                item.Description = "通过串口通道发送内容并等待响应，可用于扫码枪、仪表或下位机握手";
                break;
        }
    }

    private void AddMotionSequence()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        var item = new RecipeMotionSequenceItem
        {
            Name = $"动作序列-{MotionSequences.Count + 1}",
            ControllerProfile = "Reserved",
            Description = "预留给轴卡说明书接入后的完整动作序列",
            Enabled = true,
            StepCount = 0
        };
        MotionSequences.Add(item);
        SelectedMotionSequence = item;
    }

    private void RemoveMotionSequence()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (SelectedMotionSequence is null)
        {
            return;
        }

        MotionSequences.Remove(SelectedMotionSequence);
        SelectedMotionSequence = MotionSequences.FirstOrDefault();
    }

    private void AddVisionResult()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        var source = VisionResultSourceOptions.FirstOrDefault();
        var item = new RecipeVisionResultItem
        {
            Name = $"结果项-{VisionResults.Count + 1}",
            FlowId = ResolveSelectedVisionResultFlowId(),
            SourceToolId = source?.SourceToolId ?? string.Empty,
            SourceKey = source?.SourceKey ?? string.Empty,
            DataType = source?.DataType ?? "Text",
            Description = "供 PLC 或外部二次开发调用"
        };
        VisionResults.Add(item);
        SelectedVisionResult = item;
    }

    private void RemoveVisionResult()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (SelectedVisionResult is null)
        {
            return;
        }

        VisionResults.Remove(SelectedVisionResult);
        SelectedVisionResult = VisionResults.FirstOrDefault();
    }

    private string ResolveSelectedVisionResultFlowId()
    {
        return SelectedFlow?.Id ?? _loadedRecipe?.CurrentFlowId ?? string.Empty;
    }

    private void RefreshVisionResultSourceOptions(Recipe? recipe)
    {
        VisionResultSourceOptions.Clear();
        if (recipe is null)
        {
            return;
        }

        var flow = ResolveVisionResultSourceFlow(recipe);
        if (flow is null)
        {
            return;
        }

        VisionResultSourceOptions.Add(new RecipeVisionResultSourceOption(
            "|ResultFrameId",
            string.Empty,
            "ResultFrameId",
            "最终结果图 / ResultFrameId (Image)",
            "Image"));

        foreach (var tool in flow.Tools)
        {
            if (tool.Kind == VisionToolKind.Result)
            {
                foreach (var input in CreateResultToolSourceOptions(flow, tool))
                {
                    VisionResultSourceOptions.Add(input);
                }

                continue;
            }

            foreach (var port in VisionToolCatalog.GetOutputPorts(tool.Kind))
            {
                var sourceKey = $"port:{port.Key}";
                VisionResultSourceOptions.Add(new RecipeVisionResultSourceOption(
                    $"{tool.Id}|{sourceKey}",
                    tool.Id,
                    sourceKey,
                    $"{tool.Name} / {port.Name} ({port.DataType})",
                    port.DataType));
            }
        }
    }

    private static IEnumerable<RecipeVisionResultSourceOption> CreateResultToolSourceOptions(
        VisionFlowDefinition flow,
        VisionToolDefinition resultTool)
    {
        foreach (var input in VisionToolCatalog.GetResultInputPorts(resultTool.Parameters))
        {
            var sourceToolId = resultTool.Parameters.GetValueOrDefault($"input:{input.Key}:toolId") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceToolId))
            {
                continue;
            }

            var sourcePortKey = resultTool.Parameters.GetValueOrDefault($"input:{input.Key}:portKey") ?? "ResultOutput";
            var sourceTool = flow.Tools.FirstOrDefault(tool => string.Equals(tool.Id, sourceToolId, StringComparison.OrdinalIgnoreCase));
            var sourcePort = sourceTool is null
                ? null
                : VisionToolCatalog.GetOutputPorts(sourceTool.Kind)
                    .FirstOrDefault(port => string.Equals(port.Key, sourcePortKey, StringComparison.OrdinalIgnoreCase));
            var sourceText = sourceTool is null
                ? sourceToolId
                : $"{sourceTool.Name}.{sourcePort?.Name ?? sourcePortKey}";

            yield return new RecipeVisionResultSourceOption(
                $"{resultTool.Id}|{input.Key}",
                resultTool.Id,
                input.Key,
                $"{resultTool.Name} / {input.Name} <- {sourceText} ({input.DataType})",
                input.DataType);
        }
    }

    private VisionFlowDefinition? ResolveVisionResultSourceFlow(Recipe recipe)
    {
        var flowId = ResolveSelectedVisionResultFlowId();
        return recipe.EffectiveFlows.FirstOrDefault(flow =>
                   string.Equals(flow.Id, flowId, StringComparison.OrdinalIgnoreCase))
               ?? recipe.EffectiveFlows.FirstOrDefault(flow =>
                   string.Equals(flow.Id, recipe.CurrentFlowId, StringComparison.OrdinalIgnoreCase))
               ?? recipe.EffectiveFlows.FirstOrDefault();
    }

    private void RefreshVisionFlowResultPreviewItems()
    {
        VisionFlowResultPreviewItems.Clear();
        var rows = VisionFlowResultPreviewBuilder.Build(
            SelectedProcessStep,
            ResolveProcessStepFlow(SelectedProcessStep),
            RecipeVariables,
            _lastTestRun?.RuntimeValues,
            _lastTestRun?.Result.ResultData);

        foreach (var row in rows)
        {
            VisionFlowResultPreviewItems.Add(row);
        }

        RaisePropertyChanged(nameof(HasVisionFlowResultPreviewItems));
    }

    private VisionFlowDefinition? ResolveProcessStepFlow(RecipeProcessStepItem? step)
    {
        if (_loadedRecipe is null || step is null)
        {
            return null;
        }

        return _loadedRecipe.EffectiveFlows.FirstOrDefault(flow =>
                   string.Equals(flow.Id, step.FlowId, StringComparison.OrdinalIgnoreCase))
               ?? _loadedRecipe.EffectiveFlows.FirstOrDefault(flow =>
                   string.Equals(flow.Id, _loadedRecipe.CurrentFlowId, StringComparison.OrdinalIgnoreCase))
               ?? _loadedRecipe.EffectiveFlows.FirstOrDefault();
    }

    private void AddPlcSignal()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        var item = new RecipePlcSignalItem
        {
            Name = $"PLC信号-{PlcSignals.Count + 1}",
            Direction = "Read",
            TriggerValue = "1",
            TimeoutMs = "3000",
            Blocking = true,
            Description = "等待或回写 PLC 信号"
        };
        PlcSignals.Add(item);
        SelectedPlcSignal = item;
    }

    private void RemovePlcSignal()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (SelectedPlcSignal is null)
        {
            return;
        }

        PlcSignals.Remove(SelectedPlcSignal);
        SelectedPlcSignal = PlcSignals.FirstOrDefault();
    }

    private void AddSignalMapping()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        var item = new RecipeSignalMappingItem
        {
            Key = $"Signal{SignalMappings.Count + 1}",
            Name = $"逻辑信号-{SignalMappings.Count + 1}",
            DataType = "bool",
            SourceType = "PLC",
            DeviceKey = "plc-main",
            Address = "StartButton",
            Enabled = true,
            Description = "等待流程继续的逻辑信号"
        };
        SignalMappings.Add(item);
        SelectedSignalMapping = item;
        if (SelectedProcessStep?.IsWaitSignalStep == true)
        {
            SelectedProcessStep.SignalId = item.Key;
        }
    }

    private void RemoveSignalMapping()
    {
        if (!CanEditRecipe())
        {
            return;
        }

        if (SelectedSignalMapping is null)
        {
            return;
        }

        var removed = SelectedSignalMapping;
        SignalMappings.Remove(removed);
        SelectedSignalMapping = SignalMappings.FirstOrDefault();
        if (SelectedProcessStep?.IsWaitSignalStep == true &&
            string.Equals(SelectedProcessStep.SignalId, removed.Key, StringComparison.OrdinalIgnoreCase))
        {
            SelectedProcessStep.SignalId = SelectedSignalMapping?.Key ?? string.Empty;
        }
    }

    private void AttachEditableCollection<T>(ObservableCollection<T> collection) where T : class
    {
        collection.CollectionChanged += OnEditableCollectionChanged;
        foreach (var item in collection.OfType<INotifyPropertyChanged>())
        {
            item.PropertyChanged += OnEditableItemChanged;
        }
    }

    private void OnEditableCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<INotifyPropertyChanged>())
            {
                item.PropertyChanged -= OnEditableItemChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<INotifyPropertyChanged>())
            {
                item.PropertyChanged += OnEditableItemChanged;
            }
        }

        MarkDirty();
        RaiseCommandStates();
    }

    private void OnEditableItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is RecipeSignalMappingItem signalMapping &&
            ReferenceEquals(signalMapping, SelectedSignalMapping) &&
            e.PropertyName == nameof(RecipeSignalMappingItem.Key) &&
            SelectedProcessStep?.IsWaitSignalStep == true)
        {
            SelectedProcessStep.SignalId = signalMapping.Key;
        }

        if (ReferenceEquals(sender, SelectedProcessStep) ||
            sender is RecipeVariableItem)
        {
            RefreshVisionFlowResultPreviewItems();
        }

        MarkDirty();
    }

    private void PauseTestRun()
    {
        if (!IsTestRunning || IsTestRunPaused)
        {
            return;
        }

        _inspectionRunControl.Pause();
        IsTestRunPaused = true;
        StatusText = "已请求暂停：当前步骤结束后停在流程位置";
        TestRunStateText = StatusText;
        AddTestRunLog("INFO", "Recipe", StatusText);
        _log.Info("Recipe", StatusText);
    }

    private void ResumeTestRun()
    {
        if (!IsTestRunPaused)
        {
            return;
        }

        _inspectionRunControl.Resume();
        IsTestRunPaused = false;
        StatusText = "流程继续运行";
        TestRunStateText = StatusText;
        AddTestRunLog("INFO", "Recipe", StatusText);
        _log.Info("Recipe", StatusText);
    }

    private void ResetTestRun()
    {
        if (IsTestRunning)
        {
            _testRunResetRequested = true;
            Exception? requestResetFailure = null;
            try
            {
                _inspectionRunControl.RequestReset();
            }
            catch (Exception ex)
            {
                requestResetFailure = ex;
            }
            finally
            {
                RequestAttemptCancellation();
            }

            if (requestResetFailure is not null)
            {
                LogWarningSafely($"试运行复位请求失败：{requestResetFailure.Message}");
            }

            return;
        }

        ResetRecipeVariablesToDefaults();
        ResetProcessStepRuntimeStates(prepareForRun: false);
        SelectedProcessStep = ProcessSteps.FirstOrDefault();
        IsTestRunPaused = false;
        StatusText = "流程已复位，变量已初始化";
        TestRunStateText = StatusText;
        AddTestRunLog("INFO", "Recipe", StatusText);
        _log.Info("Recipe", StatusText);
        RaiseCommandStates();
    }

    private void OnAppLogWritten(object? sender, AppLogEntry entry)
    {
        if (!IsTestRunning || !IsRecipeRunLog(entry.Source))
        {
            return;
        }

        _uiDispatcher.Invoke(() =>
        {
            var message = OperatorMessageLocalizer.LocalizeMessage(entry.Message);
            TestRunStateText = message;
            StatusText = message;
            UpdateProcessStepRuntimeFromLog(entry.Level, entry.Source, entry.Message);
            AddTestRunLog(entry.Level, entry.Source, message, entry.Timestamp);
        });
    }

    private void ResetProcessStepRuntimeStates(bool prepareForRun)
    {
        _activeRuntimeStep = null;
        foreach (var step in ProcessSteps)
        {
            step.ResetRuntimeState(prepareForRun);
        }
    }

    private void ResetRecipeVariablesToDefaults()
    {
        foreach (var variable in RecipeVariables)
        {
            variable.CurrentValue = variable.DefaultValue;
        }
    }

    private void UpdateProcessStepRuntimeFromLog(string level, string source, string message)
    {
        if (!string.Equals(source, "ProcessFlow", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (TryGetStepMatch(StepStartedRegex, message, out var startedStep, out _))
        {
            _activeRuntimeStep = startedStep;
            SelectedProcessStep = startedStep;
            startedStep.MarkRuntimeRunning("正在执行");
            return;
        }

        if (TryGetStepMatch(WaitStartedRegex, message, out var waitingStep, out var waitMatch))
        {
            _activeRuntimeStep = waitingStep;
            waitingStep.MarkRuntimeRunning(SimplifyRuntimeText(waitMatch.Groups["detail"].Value));
            return;
        }

        if (TryGetStepMatch(WaitIgnoredRegex, message, out var ignoredStep, out var ignoredMatch))
        {
            ignoredStep.MarkRuntimeResult(SimplifyRuntimeText(ignoredMatch.Groups["result"].Value));
            return;
        }

        if (TryGetStepMatch(WaitMatchedRegex, message, out var matchedStep, out var matchedMatch))
        {
            matchedStep.MarkRuntimeResult(SimplifyRuntimeText(matchedMatch.Groups["result"].Value));
            return;
        }

        if (TryGetStepMatch(StepResultRegex, message, out var resultStep, out var resultMatch))
        {
            resultStep.MarkRuntimeResult(SimplifyRuntimeText(resultMatch.Groups["result"].Value));
            return;
        }

        if (TryGetStepMatch(StepCompletedRegex, message, out var completedStep, out var completedMatch))
        {
            completedStep.MarkRuntimeSucceeded($"耗时 {completedMatch.Groups["duration"].Value} ms");
            if (ReferenceEquals(_activeRuntimeStep, completedStep))
            {
                _activeRuntimeStep = null;
            }

            return;
        }

        if (TryGetStepMatch(StepFailedRegex, message, out var failedStep, out var failedMatch))
        {
            failedStep.MarkRuntimeFailed(
                $"耗时 {failedMatch.Groups["duration"].Value} ms",
                SimplifyRuntimeText(failedMatch.Groups["error"].Value));
            if (ReferenceEquals(_activeRuntimeStep, failedStep))
            {
                _activeRuntimeStep = null;
            }
        }
    }

    private void MarkActiveRuntimeStepFailed(string message)
    {
        if (_activeRuntimeStep is null)
        {
            return;
        }

        _activeRuntimeStep.MarkRuntimeFailed(null, SimplifyRuntimeText(message));
        _activeRuntimeStep = null;
    }

    private bool TryGetStepMatch(Regex regex, string message, out RecipeProcessStepItem step, out Match match)
    {
        match = regex.Match(message);
        if (match.Success &&
            int.TryParse(match.Groups["stepNo"].Value, out var stepNo) &&
            (step = ProcessSteps.FirstOrDefault(item => item.StepNo == stepNo)!) is not null)
        {
            return true;
        }

        step = null!;
        return false;
    }

    private static string SimplifyRuntimeText(string text)
    {
        var normalized = OperatorMessageLocalizer.LocalizeMessage(text ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return normalized.Length <= 120 ? normalized : $"{normalized[..117]}...";
    }

    private void AddTestRunLog(string level, string source, string message, DateTimeOffset? timestamp = null)
    {
        BoundedCollection.InsertNewestFirst(TestRunLogs, new LogLineItem(
            (timestamp ?? DateTimeOffset.Now).ToString("HH:mm:ss"),
            level,
            OperatorMessageLocalizer.LocalizeSource(source),
            OperatorMessageLocalizer.LocalizeMessage(message)), MaxTestRunLogCount);
    }

    private void AddTestRunResultSnapshot(InspectionRunResult run)
    {
        _lastTestRun = run;
        RefreshVisionFlowResultPreviewItems();

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in run.Result.ResultData.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(30))
        {
            emitted.Add(pair.Key);
            AddTestRunLog("INFO", "Result", $"{pair.Key} = {pair.Value}");
        }

        foreach (var target in ProcessSteps
                     .Select(step => step.OutputTarget)
                     .Where(target => !string.IsNullOrWhiteSpace(target))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (emitted.Contains(target) || !run.RuntimeValues.TryGetValue(target, out var value))
            {
                continue;
            }

            emitted.Add(target);
            AddTestRunLog("INFO", "Runtime", $"{target} = {value}");
        }

        if (emitted.Count == 0)
        {
            AddTestRunLog("INFO", "Result", "No result data was produced.");
        }
    }

    private static bool IsRecipeRunLog(string source)
    {
        return string.Equals(source, "ProcessFlow", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(source, "Production", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(source, "Recipe", StringComparison.OrdinalIgnoreCase);
    }

    private bool SetEditorProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (IsTestRunning)
        {
            return false;
        }

        if (!SetProperty(ref storage, value, propertyName))
        {
            return false;
        }

        MarkDirty();
        return true;
    }

    private void MarkDirty(string? statusText = null)
    {
        if (_isLoadingEditor)
        {
            return;
        }

        HasUnsavedChanges = true;
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            StatusText = statusText;
        }
        else if (!string.IsNullOrWhiteSpace(RecipeName))
        {
            StatusText = $"{RecipeName} 有未保存修改";
        }
    }

    private void RaiseCommandStates()
    {
        SaveCommand.RaiseCanExecuteChanged();
        SetCurrentRecipeCommand.RaiseCanExecuteChanged();
        TestRunRecipeCommand.RaiseCanExecuteChanged();
        PauseTestRunCommand.RaiseCanExecuteChanged();
        ResetTestRunCommand.RaiseCanExecuteChanged();
        OpenFlowEditorCommand.RaiseCanExecuteChanged();
        NewRecipeCommand.RaiseCanExecuteChanged();
        DuplicateRecipeCommand.RaiseCanExecuteChanged();
        DeleteRecipeCommand.RaiseCanExecuteChanged();
        AddProductParameterCommand.RaiseCanExecuteChanged();
        RemoveProductParameterCommand.RaiseCanExecuteChanged();
        UseVariableInProductParameterCommand.RaiseCanExecuteChanged();
        UseVariableInProcessStepCommand.RaiseCanExecuteChanged();
        AddProcessToolCommand.RaiseCanExecuteChanged();
        AddProcessStepCommand.RaiseCanExecuteChanged();
        RemoveProcessStepCommand.RaiseCanExecuteChanged();
        MoveProcessStepUpCommand.RaiseCanExecuteChanged();
        MoveProcessStepDownCommand.RaiseCanExecuteChanged();
        AddMotionSequenceCommand.RaiseCanExecuteChanged();
        RemoveMotionSequenceCommand.RaiseCanExecuteChanged();
        AddVisionResultCommand.RaiseCanExecuteChanged();
        RemoveVisionResultCommand.RaiseCanExecuteChanged();
        AddPlcSignalCommand.RaiseCanExecuteChanged();
        RemovePlcSignalCommand.RaiseCanExecuteChanged();
        AddSignalMappingCommand.RaiseCanExecuteChanged();
        RemoveSignalMappingCommand.RaiseCanExecuteChanged();
    }

    private bool CanTestRun() =>
        !IsBusy &&
        (IsTestRunPaused ||
         (!IsTestRunning && _inspectionExecution.Current is null));

    private void SubscribeInspectionExecution()
    {
        if (_inspectionExecutionSubscribed)
        {
            return;
        }

        _inspectionExecution.Changed += OnInspectionExecutionChanged;
        _inspectionExecutionSubscribed = true;
        RaiseCommandStates();
    }

    private void UnsubscribeInspectionExecution()
    {
        if (!_inspectionExecutionSubscribed)
        {
            return;
        }

        _inspectionExecution.Changed -= OnInspectionExecutionChanged;
        _inspectionExecutionSubscribed = false;
    }

    private void OnInspectionExecutionChanged(
        object? sender,
        InspectionExecutionChangedEventArgs args)
    {
        _uiDispatcher.Invoke(RaiseCommandStates);
    }

    public void OnNavigatedTo(NavigationContext navigationContext)
    {
        SubscribeInspectionExecution();
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
        AdvanceRecipePageEpoch();
        try
        {
            RequestLifetimeCancellation();
        }
        finally
        {
            UnsubscribeInspectionExecution();
        }
    }

    private void AdvanceRecipePageEpoch() =>
        Interlocked.Increment(ref _recipeRefreshGeneration);

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static IEnumerable<ProcessStepDefinition> NormalizeRecipeProcessSteps(IEnumerable<ProcessStepDefinition> steps)
    {
        return steps
            .Where(step => step.StepType != ProcessStepType.AcquireImage)
            .OrderBy(step => step.StepNo)
            .Select((step, index) => step with { StepNo = index + 1 });
    }

    private void RenumberProcessSteps()
    {
        for (var index = 0; index < ProcessSteps.Count; index++)
        {
            ProcessSteps[index].StepNo = index + 1;
        }

        RaiseCommandStates();
    }

    private string ResolveDefaultWaitVariableKey()
    {
        return RecipeVariables.FirstOrDefault(variable =>
                   string.Equals(variable.DataType, "bool", StringComparison.OrdinalIgnoreCase) &&
                   (string.Equals(variable.Direction, "Input", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(variable.Direction, "InOut", StringComparison.OrdinalIgnoreCase)))?.Key
               ?? RecipeVariables.FirstOrDefault(variable =>
                   string.Equals(variable.Direction, "Input", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(variable.Direction, "InOut", StringComparison.OrdinalIgnoreCase))?.Key
               ?? RecipeVariables.FirstOrDefault()?.Key
               ?? "PlcReady";
    }

    private string ResolveDefaultStringInputKey()
    {
        return RecipeVariables.FirstOrDefault(variable =>
                   string.Equals(variable.DataType, "string", StringComparison.OrdinalIgnoreCase) &&
                   (string.Equals(variable.Direction, "Input", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(variable.Direction, "InOut", StringComparison.OrdinalIgnoreCase)))?.Key
               ?? RecipeVariables.FirstOrDefault(variable =>
                   string.Equals(variable.Direction, "Input", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(variable.Direction, "InOut", StringComparison.OrdinalIgnoreCase))?.Key
               ?? RecipeVariables.FirstOrDefault()?.Key
               ?? "RawText";
    }

    private string ResolveDefaultStringOutputKey()
    {
        return RecipeVariables.FirstOrDefault(variable =>
                   string.Equals(variable.DataType, "string", StringComparison.OrdinalIgnoreCase) &&
                   (string.Equals(variable.Direction, "Output", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(variable.Direction, "InOut", StringComparison.OrdinalIgnoreCase)))?.Key
               ?? RecipeVariables.FirstOrDefault(variable =>
                   string.Equals(variable.Direction, "Output", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(variable.Direction, "InOut", StringComparison.OrdinalIgnoreCase))?.Key
               ?? RecipeVariables.FirstOrDefault(variable =>
                   string.Equals(variable.DataType, "string", StringComparison.OrdinalIgnoreCase))?.Key
               ?? RecipeVariables.FirstOrDefault()?.Key
               ?? "ParsedText";
    }

    private string ResolveDefaultTcpChannelKey()
    {
        return TcpChannelOptions.FirstOrDefault()?.Key ?? "tcp-main";
    }

    private string ResolveDefaultSerialChannelKey()
    {
        return SerialChannelOptions.FirstOrDefault()?.Key ?? "serial-main";
    }

    private void NormalizeWaitStepsToVariables()
    {
        if (RecipeVariables.Count == 0)
        {
            return;
        }

        var variableKeys = RecipeVariables.Select(variable => variable.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var step in ProcessSteps.Where(step => step.IsWaitSignalStep))
        {
            if (variableKeys.Contains(step.SignalId))
            {
                continue;
            }

            step.SignalId = RecipeVariables.FirstOrDefault(variable =>
                                string.Equals(variable.Key, "PlcReady", StringComparison.OrdinalIgnoreCase))?.Key
                            ?? ResolveDefaultWaitVariableKey();
        }
    }

    private RecipeProcessStepItem CreateProcessStepItem(ProcessStepType stepType)
    {
        var stepNo = ProcessSteps.Count + 1;
        return stepType switch
        {
            ProcessStepType.AxisMove => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"运动控制-{stepNo:00}",
                StepType = stepType,
                DeviceKey = "motion-main",
                AxisKey = "AxisX",
                Position = "0",
                Speed = "80",
                Acceleration = "120",
                TimeoutMs = "5000",
                Description = "执行到指定点位或动作命令"
            },
            ProcessStepType.WaitPlcSignal => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"等待信号-{stepNo:00}",
                StepType = stepType,
                SignalId = ResolveDefaultWaitVariableKey(),
                ResultKey = string.Empty,
                TimeoutMs = "5000",
                ParametersText = "expected=1\r\nmatch=Equals\r\npollIntervalMs=50\r\ndebounceMs=100\r\nonTimeout=AlarmStop",
                Description = "等待 PLC、TCP 或串口信号满足条件后继续"
            },
            ProcessStepType.AcquireImage => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"采图-{stepNo:00}",
                StepType = stepType,
                Description = "触发当前相机采图"
            },
            ProcessStepType.RunVisionFlow => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"运行视觉-{stepNo:00}",
                StepType = stepType,
                FlowId = SelectedFlow?.Id ?? "main",
                Description = "调用当前产品绑定的视觉流程"
            },
            ProcessStepType.ReadVisionResult => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"读取结果-{stepNo:00}",
                StepType = stepType,
                ResultKey = "MeasuredWidth",
                Description = "从视觉结果集中读取指定字段"
            },
            ProcessStepType.WriteResultTable => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"写结果表-{stepNo:00}",
                StepType = stepType,
                ResultKey = "MeasuredWidth",
                OutputTarget = "ResultTable.Width",
                Description = "写入内部结果表字段"
            },
            ProcessStepType.WritePlc => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"写PLC-{stepNo:00}",
                StepType = stepType,
                DeviceKey = "plc-main",
                ResultKey = "OverallResult",
                OutputTarget = "D200",
                TimeoutMs = "1000",
                Description = "把结果或状态回写到 PLC"
            },
            ProcessStepType.DeviceRead => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"读设备-{stepNo:00}",
                StepType = stepType,
                DeviceKey = "plc-main",
                SignalId = "D100",
                ResultKey = "DeviceValue",
                TimeoutMs = "1000",
                Description = "从指定设备地址读取值并写入运行时结果"
            },
            ProcessStepType.DeviceWrite => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"写设备-{stepNo:00}",
                StepType = stepType,
                DeviceKey = "plc-main",
                ResultKey = "OverallResult",
                OutputTarget = "D200",
                TimeoutMs = "1000",
                Description = "把运行时值或常量写入指定设备地址"
            },
            ProcessStepType.DeviceCommand => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"设备命令-{stepNo:00}",
                StepType = stepType,
                DeviceKey = "plc-main",
                CommandName = "Read",
                SignalId = "D100",
                ResultKey = "DeviceCommandResult",
                TimeoutMs = "1000",
                Description = "调用设备适配器暴露的命令接口"
            },
            ProcessStepType.StringProcess => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"字符串处理-{stepNo:00}",
                StepType = stepType,
                ResultKey = ResolveDefaultStringInputKey(),
                OutputTarget = ResolveDefaultStringOutputKey(),
                CommandName = "Split",
                ParametersText = "separator=,\r\nindex=0\r\npattern=\r\ngroup=1\r\nstart=0\r\nlength=\r\noldValue=\r\nnewValue=",
                Description = "把接收到的字符串分割、截取或正则提取后写入输出参数"
            },
            ProcessStepType.End => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"结束-{stepNo:00}",
                StepType = stepType,
                Description = "显式结束当前运行流程"
            },
            _ => new RecipeProcessStepItem
            {
                StepNo = stepNo,
                Name = $"延时-{stepNo:00}",
                StepType = ProcessStepType.Delay,
                DelayMs = "200",
                TimeoutMs = "3000",
                Description = "在流程中等待指定毫秒"
            }
        };
    }

    private static string AppendParameterLine(string? existingText, string key, string value)
    {
        var lineKey = string.IsNullOrWhiteSpace(key) ? "Variable" : key.Trim();
        var line = $"{lineKey}={value}";
        if (string.IsNullOrWhiteSpace(existingText))
        {
            return line;
        }

        var lines = existingText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => !item.StartsWith($"{lineKey}=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        lines.Add(line);
        return string.Join(Environment.NewLine, lines);
    }

    private static double ParseDouble(string? text, double fallback)
    {
        return double.TryParse(text, out var value) ? value : fallback;
    }

    private static int ParseInt(string? text, int fallback)
    {
        return int.TryParse(text, out var value) ? value : fallback;
    }

    private static long ParseLong(string? text, long fallback)
    {
        return long.TryParse(text, out var value) ? value : fallback;
    }

    private static string BuildRecipeId(string? seed)
    {
        var source = string.IsNullOrWhiteSpace(seed) ? "recipe" : seed.Trim().ToLowerInvariant();
        var normalized = new string(source
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "recipe";
        }

        return $"{normalized}-{DateTimeOffset.Now:yyyyMMddHHmmss}";
    }
}

public sealed record RecipeChannelOption(string Key, string DisplayText)
{
    public override string ToString()
    {
        return DisplayText;
    }
}

public sealed record RecipeVisionResultSourceOption(
    string Address,
    string SourceToolId,
    string SourceKey,
    string DisplayText,
    string DataType);
