using Prism.Mvvm;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using VisionStation.Vision.UI.Models;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.ViewModels;

public sealed record ToolResultItem(string Name, string Kind, string Outcome, string Duration, string Message);

public sealed record MultiTargetMatchPointItem(
    int Index,
    string X,
    string Y,
    string Angle,
    string Score,
    string Width,
    string Height,
    string Shape,
    string Radius)
{
    public string SizeText => $"{Width} x {Height}";

    public string RadiusText => string.IsNullOrWhiteSpace(Radius) || Radius == "0" ? "-" : Radius;
}

public sealed record ToolOutputValueItem(string Name, string Value, string DataType, string Source)
{
    public string Stroke => DataType switch
    {
        "Image" => "#FF33D6A6",
        "Pose" => "#FFFFC95A",
        "Point" or "Point[]" => "#FFFFC95A",
        "Roi" => "#FF7AD7FF",
        "Result" => "#FFBFA2FF",
        "Line" => "#FF8FD4FF",
        "Circle" => "#FF8FE8B9",
        "Number" => "#FFFFD27A",
        "Text" => "#FFD2E7FF",
        _ => "#FFA9B7C2"
    };

    public string SoftFill => DataType switch
    {
        "Image" => "#1533D6A6",
        "Pose" => "#18FFC95A",
        "Point" or "Point[]" => "#18FFC95A",
        "Roi" => "#147AD7FF",
        "Result" => "#16BFA2FF",
        "Line" => "#158FD4FF",
        "Circle" => "#158FE8B9",
        "Number" => "#18FFD27A",
        "Text" => "#15D2E7FF",
        _ => "#101D2A33"
    };
}

public sealed class ToolOutputOptionItem : BindableBase
{
    private bool _isEnabled;

    public ToolOutputOptionItem(string key, string name, string dataType, bool isEnabled)
    {
        Key = key;
        Name = name;
        DataType = dataType;
        _isEnabled = isEnabled;
    }

    public string Key { get; }

    public string Name { get; }

    public string DataType { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string Stroke => DataType switch
    {
        "Image" => "#FF33D6A6",
        "Pose" => "#FFFFC95A",
        "Point" or "Point[]" => "#FFFFC95A",
        "Roi" => "#FF7AD7FF",
        "Result" => "#FFBFA2FF",
        "Line" => "#FF8FD4FF",
        "Circle" => "#FF8FE8B9",
        "Number" => "#FFFFD27A",
        "Text" => "#FFD2E7FF",
        _ => "#FFA9B7C2"
    };

    public string SoftFill => DataType switch
    {
        "Image" => "#1533D6A6",
        "Pose" => "#18FFC95A",
        "Point" or "Point[]" => "#18FFC95A",
        "Roi" => "#147AD7FF",
        "Result" => "#16BFA2FF",
        "Line" => "#158FD4FF",
        "Circle" => "#158FE8B9",
        "Number" => "#18FFD27A",
        "Text" => "#15D2E7FF",
        _ => "#101D2A33"
    };
}

public sealed record ToolInputSourceOptionItem(string ToolId, string PortKey, string Name, string DataType)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(ToolId) || string.IsNullOrWhiteSpace(PortKey);

    public override string ToString()
    {
        return Name;
    }
}

public sealed class ToolInputBindingItem : BindableBase
{
    private ToolInputSourceOptionItem? _selectedOption;
    private string _targetPortKey;
    private string _targetName;
    private string _dataType;

    public ToolInputBindingItem(
        string targetPortKey,
        string targetName,
        string dataType,
        IEnumerable<ToolInputSourceOptionItem> options,
        ToolInputSourceOptionItem? selectedOption,
        bool isTypeEditable = false,
        bool canDelete = false,
        IEnumerable<ToolParameterChoiceItem>? dataTypeOptions = null)
    {
        _targetPortKey = targetPortKey;
        _targetName = targetName;
        _dataType = dataType;
        Options = new ObservableCollection<ToolInputSourceOptionItem>(options);
        DataTypeOptions = new ObservableCollection<ToolParameterChoiceItem>(dataTypeOptions ?? Array.Empty<ToolParameterChoiceItem>());
        IsTypeEditable = isTypeEditable;
        CanDelete = canDelete;
        _selectedOption = selectedOption ?? Options.FirstOrDefault();
    }

    public string TargetPortKey
    {
        get => _targetPortKey;
        private set => SetProperty(ref _targetPortKey, value);
    }

    public string TargetName
    {
        get => _targetName;
        private set => SetProperty(ref _targetName, value);
    }

    public string DataType
    {
        get => _dataType;
        set
        {
            if (SetProperty(ref _dataType, value))
            {
                RaisePropertyChanged(nameof(Stroke));
                RaisePropertyChanged(nameof(SoftFill));
            }
        }
    }

    public ObservableCollection<ToolInputSourceOptionItem> Options { get; }

    public ObservableCollection<ToolParameterChoiceItem> DataTypeOptions { get; }

    public bool IsTypeEditable { get; }

    public bool CanDelete { get; }

    public ToolInputSourceOptionItem? SelectedOption
    {
        get => _selectedOption;
        set => SetProperty(ref _selectedOption, value);
    }

    public void ReplaceOptions(IEnumerable<ToolInputSourceOptionItem> options, ToolInputSourceOptionItem? selectedOption = null)
    {
        Options.Clear();
        foreach (var option in options)
        {
            Options.Add(option);
        }

        SelectedOption = selectedOption ?? Options.FirstOrDefault();
    }

    public void RenameTarget(string targetPortKey, string targetName)
    {
        TargetPortKey = targetPortKey;
        TargetName = targetName;
    }

    public string Stroke => DataType switch
    {
        "Image" => "#FF33D6A6",
        "Pose" => "#FFFFC95A",
        "Point" or "Point[]" => "#FFFFC95A",
        "Line" => "#FF8FD4FF",
        "Circle" => "#FF8FE8B9",
        "Number" => "#FFFFD27A",
        "Result" => "#FFBFA2FF",
        _ => "#FFA9B7C2"
    };

    public string SoftFill => DataType switch
    {
        "Image" => "#1533D6A6",
        "Pose" => "#18FFC95A",
        "Point" or "Point[]" => "#18FFC95A",
        "Line" => "#158FD4FF",
        "Circle" => "#158FE8B9",
        "Number" => "#18FFD27A",
        "Result" => "#16BFA2FF",
        _ => "#101D2A33"
    };
}

public sealed record FlowResultImageItem(
    string ToolId,
    string DisplayName,
    ImageFrame? Frame,
    IReadOnlyList<VisionOverlayItem> Overlays)
{
    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed record LogLineItem(string Time, string Level, string Source, string Message)
{
    public string LevelText => Level switch
    {
        "Error" or "ERROR" => "错误",
        "Critical" or "CRITICAL" => "严重",
        "Warning" or "WARN" or "Warn" => "警告",
        "Info" or "INFO" => "信息",
        _ => Level
    };

    public string LevelBrush => Level switch
    {
        "Error" or "ERROR" or "Critical" or "CRITICAL" => "#FFFF667A",
        "Warning" or "WARN" or "Warn" => "#FFFFC95A",
        "Info" or "INFO" => "#FF7AD7FF",
        _ => "#FFA9B7C2"
    };
}

public sealed record ResultFieldItem(string Key, string Value);

public sealed record InspectionFlowOptionItem(string FlowId, string FlowName)
{
    public override string ToString()
    {
        return FlowName;
    }
}

public sealed class InspectionDisplayPaneItem : BindableBase
{
    private InspectionFlowOptionItem? _selectedFlow;
    private ImageFrame? _currentFrame;
    private bool _hasResult;
    private string _lastOutcome = "READY";
    private string _lastOutcomeBrush = "#FF33D6A6";
    private string _lastMessage = "等待流程结果";
    private string _lastBarcode = "-";

    public InspectionDisplayPaneItem(string title, InspectionFlowOptionItem? selectedFlow)
    {
        Title = title;
        _selectedFlow = selectedFlow;
        _lastMessage = selectedFlow is null ? "请选择视觉流程" : $"等待 {selectedFlow.FlowName} 结果";
    }

    public string Title { get; }

    public string BoundFlowId => SelectedFlow?.FlowId ?? string.Empty;

    public ObservableCollection<VisionOverlayItem> Overlays { get; } = new();

    public bool HasResult
    {
        get => _hasResult;
        set
        {
            SetProperty(ref _hasResult, value);
        }
    }

    public InspectionFlowOptionItem? SelectedFlow
    {
        get => _selectedFlow;
        set
        {
            if (SetProperty(ref _selectedFlow, value))
            {
                RaisePropertyChanged(nameof(BoundFlowId));
                LastMessage = value is null ? "请选择视觉流程" : $"等待 {value.FlowName} 结果";
            }
        }
    }

    public ImageFrame? CurrentFrame
    {
        get => _currentFrame;
        set => SetProperty(ref _currentFrame, value);
    }

    public string LastOutcome
    {
        get => _lastOutcome;
        set => SetProperty(ref _lastOutcome, value);
    }

    public string LastOutcomeBrush
    {
        get => _lastOutcomeBrush;
        set => SetProperty(ref _lastOutcomeBrush, value);
    }

    public string LastMessage
    {
        get => _lastMessage;
        set => SetProperty(ref _lastMessage, value);
    }

    public string LastBarcode
    {
        get => _lastBarcode;
        set => SetProperty(ref _lastBarcode, value);
    }
}

public sealed record AlarmToastItem(
    string Id,
    AlarmSeverity Severity,
    string Source,
    string Message,
    string Details,
    DateTimeOffset Timestamp)
{
    public string SeverityText => Severity switch
    {
        AlarmSeverity.Warning => "警告",
        AlarmSeverity.Error => "错误",
        AlarmSeverity.Critical => "严重",
        _ => "提示"
    };

    public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

    public string DialogTitle => Severity switch
    {
        AlarmSeverity.Warning => "报警提示",
        AlarmSeverity.Error => "设备/流程报警",
        AlarmSeverity.Critical => "严重报警",
        _ => "系统提示"
    };

    public string Icon => Severity switch
    {
        AlarmSeverity.Info => "\uE946",
        AlarmSeverity.Warning => "\uE7BA",
        AlarmSeverity.Error => "\uEA39",
        AlarmSeverity.Critical => "\uE783",
        _ => "\uE946"
    };

    public string AccentBrush => Severity switch
    {
        AlarmSeverity.Info => "#FF7AD7FF",
        AlarmSeverity.Warning => "#FFFFC95A",
        AlarmSeverity.Error => "#FFFF667A",
        AlarmSeverity.Critical => "#FFFF3B4F",
        _ => "#FF33D6A6"
    };

    public string SoftBrush => Severity switch
    {
        AlarmSeverity.Info => "#252B6F8C",
        AlarmSeverity.Warning => "#2DFFC95A",
        AlarmSeverity.Error => "#30FF667A",
        AlarmSeverity.Critical => "#40FF3B4F",
        _ => "#3333D6A6"
    };

    public bool RequiresAcknowledgement => Severity is AlarmSeverity.Error or AlarmSeverity.Critical;

    public Visibility HideButtonVisibility => RequiresAcknowledgement ? Visibility.Collapsed : Visibility.Visible;

    public string ActionText => RequiresAcknowledgement ? "确认报警" : "知道了";
}

public sealed record InspectionRecordItem(
    string Time,
    string RecipeName,
    string Outcome,
    string CycleTime,
    string Barcode,
    string ResultSummary,
    IReadOnlyList<ResultFieldItem> ResultFields,
    string OriginalImagePath,
    string ResultImagePath)
{
    public string OutcomeText => Outcome.ToUpperInvariant();

    public string OutcomeBrush => Outcome switch
    {
        "Ok" or "OK" => "#FF42E58E",
        "Ng" or "NG" => "#FFFF5C7A",
        "Error" or "ERROR" => "#FFFF3B4F",
        _ => "#FF33D6A6"
    };

    public string OutcomeSoftBrush => Outcome switch
    {
        "Ok" or "OK" => "#2042E58E",
        "Ng" or "NG" => "#26FF5C7A",
        "Error" or "ERROR" => "#32FF3B4F",
        _ => "#2633D6A6"
    };

    public string OutcomeIcon => Outcome switch
    {
        "Ok" or "OK" => "\uE73E",
        "Ng" or "NG" => "\uEA39",
        "Error" or "ERROR" => "\uE783",
        _ => "\uE946"
    };
}

public sealed record DeviceSnapshotItem(string Name, string State, string Message, string Time);

public sealed record RecipeSummaryItem(string Id, string Name, string ProductCode, int FlowCount, int ToolCount, int RoiCount, string UpdatedAt);

public sealed class RecipeListItem : BindableBase
{
    private string _name;
    private string _productCode;
    private bool _isCurrent;
    private string _updatedAt;

    public RecipeListItem(string id, string name, string productCode, int flowCount, int toolCount, int roiCount, string updatedAt, bool isCurrent)
    {
        Id = id;
        _name = name;
        _productCode = productCode;
        FlowCount = flowCount;
        ToolCount = toolCount;
        RoiCount = roiCount;
        _updatedAt = updatedAt;
        _isCurrent = isCurrent;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string ProductCode
    {
        get => _productCode;
        set => SetProperty(ref _productCode, value);
    }

    public int FlowCount { get; }

    public int ToolCount { get; }

    public int RoiCount { get; }

    public string UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}

public sealed record RecipeFlowSummaryItem(string Id, string Name, int ToolCount, int RoiCount, string UpdatedAt)
{
    public string DisplayText => $"{Name} / {ToolCount} 工具 / {RoiCount} ROI";

    public override string ToString()
    {
        return Name;
    }
}

public sealed class RecipeProductParameterItem : BindableBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "参数";
    private string _value = string.Empty;
    private string _unit = string.Empty;
    private string _description = string.Empty;
    private bool _editable = true;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool Editable
    {
        get => _editable;
        set => SetProperty(ref _editable, value);
    }

    public static RecipeProductParameterItem FromDefinition(ProductParameterDefinition definition)
    {
        return new RecipeProductParameterItem
        {
            Id = definition.Id,
            Name = definition.Name,
            Value = definition.Value,
            Unit = definition.Unit,
            Description = definition.Description,
            Editable = definition.Editable
        };
    }

    public ProductParameterDefinition ToDefinition()
    {
        return new ProductParameterDefinition
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Name = Name,
            Value = Value,
            Unit = Unit,
            Description = Description,
            Editable = Editable
        };
    }
}

public sealed class RecipeMotionSequenceItem : BindableBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "动作序列";
    private string _controllerProfile = "Reserved";
    private string _description = string.Empty;
    private bool _enabled = true;
    private int _stepCount;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string ControllerProfile
    {
        get => _controllerProfile;
        set => SetProperty(ref _controllerProfile, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public int StepCount
    {
        get => _stepCount;
        set => SetProperty(ref _stepCount, value);
    }

    public IReadOnlyList<MotionStepDefinition> Steps { get; init; } = Array.Empty<MotionStepDefinition>();

    public static RecipeMotionSequenceItem FromDefinition(MotionSequenceDefinition definition)
    {
        return new RecipeMotionSequenceItem
        {
            Id = definition.Id,
            Name = definition.Name,
            ControllerProfile = definition.ControllerProfile,
            Description = definition.Description,
            Enabled = definition.Enabled,
            StepCount = definition.Steps.Count,
            Steps = definition.Steps
        };
    }

    public MotionSequenceDefinition ToDefinition()
    {
        return new MotionSequenceDefinition
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Name = Name,
            ControllerProfile = ControllerProfile,
            Description = Description,
            Enabled = Enabled,
            Steps = Steps
        };
    }
}

public sealed class RecipeProcessStepItem : BindableBase
{
    public const string VisionResultBindingParameterPrefix = "resultBinding:";

    private string _id = Guid.NewGuid().ToString("N");
    private int _stepNo;
    private string _name = "流程步骤";
    private ProcessStepType _stepType = ProcessStepType.Delay;
    private bool _enabled = true;
    private string _deviceKey = string.Empty;
    private string _axisKey = string.Empty;
    private string _position = string.Empty;
    private string _speed = string.Empty;
    private string _acceleration = string.Empty;
    private string _axisTargetsText = string.Empty;
    private string _flowId = string.Empty;
    private string _signalId = string.Empty;
    private string _resultKey = string.Empty;
    private string _outputTarget = string.Empty;
    private string _commandName = string.Empty;
    private string _lowerLimit = string.Empty;
    private string _upperLimit = string.Empty;
    private string _delayMs = string.Empty;
    private string _timeoutMs = "3000";
    private string _parametersText = string.Empty;
    private string _description = string.Empty;
    private string _runtimeStatusText = "未运行";
    private string _runtimeDurationText = "耗时 -";
    private string _runtimeResultText = "结果 -";
    private string _runtimeStatusBrush = "#FFA9B7C2";

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public int StepNo
    {
        get => _stepNo;
        set => SetProperty(ref _stepNo, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ProcessStepType StepType
    {
        get => _stepType;
        set
        {
            if (SetProperty(ref _stepType, value))
            {
                RaisePropertyChanged(nameof(StepTypeText));
                RaisePropertyChanged(nameof(StepTypeHint));
                RaisePropertyChanged(nameof(AccentBrush));
                RaisePropertyChanged(nameof(DisplayStepTypeText));
                RaisePropertyChanged(nameof(DisplayStepTypeHint));
                RaisePropertyChanged(nameof(DisplayAccentBrush));
                RaiseToolPanelPropertiesChanged();
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string DeviceKey
    {
        get => _deviceKey;
        set
        {
            if (SetProperty(ref _deviceKey, value))
            {
                RaisePropertyChanged(nameof(WaitSignalChannelKey));
            }
        }
    }

    public string AxisKey
    {
        get => _axisKey;
        set => SetProperty(ref _axisKey, value);
    }

    public string Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    public string Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    public string Acceleration
    {
        get => _acceleration;
        set => SetProperty(ref _acceleration, value);
    }

    public string AxisTargetsText
    {
        get => _axisTargetsText;
        set => SetProperty(ref _axisTargetsText, value);
    }

    public string FlowId
    {
        get => _flowId;
        set => SetProperty(ref _flowId, value);
    }

    public string SignalId
    {
        get => _signalId;
        set
        {
            if (SetProperty(ref _signalId, value))
            {
                RaisePropertyChanged(nameof(WaitSignalAddressOrPayload));
                RaisePropertyChanged(nameof(ChannelKey));
            }
        }
    }

    public string ResultKey
    {
        get => _resultKey;
        set
        {
            if (SetProperty(ref _resultKey, value))
            {
                RaisePropertyChanged(nameof(StringInputKey));
            }
        }
    }

    public string OutputTarget
    {
        get => _outputTarget;
        set
        {
            if (SetProperty(ref _outputTarget, value))
            {
                RaisePropertyChanged(nameof(StringOutputKey));
            }
        }
    }

    public string CommandName
    {
        get => _commandName;
        set
        {
            if (SetProperty(ref _commandName, value))
            {
                RaisePropertyChanged(nameof(StringOperation));
            }
        }
    }

    public string LowerLimit
    {
        get => _lowerLimit;
        set => SetProperty(ref _lowerLimit, value);
    }

    public string UpperLimit
    {
        get => _upperLimit;
        set => SetProperty(ref _upperLimit, value);
    }

    public string DelayMs
    {
        get => _delayMs;
        set => SetProperty(ref _delayMs, value);
    }

    public string TimeoutMs
    {
        get => _timeoutMs;
        set => SetProperty(ref _timeoutMs, value);
    }

    public string ParametersText
    {
        get => _parametersText;
        set
        {
            if (SetProperty(ref _parametersText, value))
            {
                RaiseParameterPropertiesChanged();
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string RuntimeStatusText
    {
        get => _runtimeStatusText;
        set => SetProperty(ref _runtimeStatusText, value);
    }

    public string RuntimeDurationText
    {
        get => _runtimeDurationText;
        set => SetProperty(ref _runtimeDurationText, value);
    }

    public string RuntimeResultText
    {
        get => _runtimeResultText;
        set => SetProperty(ref _runtimeResultText, value);
    }

    public string RuntimeStatusBrush
    {
        get => _runtimeStatusBrush;
        set => SetProperty(ref _runtimeStatusBrush, value);
    }

    public void ResetRuntimeState(bool prepareForRun)
    {
        RuntimeStatusText = prepareForRun
            ? Enabled ? "待运行" : "已禁用"
            : "未运行";
        RuntimeDurationText = "耗时 -";
        RuntimeResultText = prepareForRun && !Enabled ? "跳过" : "结果 -";
        RuntimeStatusBrush = prepareForRun && Enabled ? "#FFA9B7C2" : "#FF87939D";
    }

    public void MarkRuntimeRunning(string? detail = null)
    {
        RuntimeStatusText = "运行中";
        RuntimeDurationText = "计时中";
        RuntimeResultText = string.IsNullOrWhiteSpace(detail) ? "正在执行" : detail.Trim();
        RuntimeStatusBrush = "#FF7AD7FF";
    }

    public void MarkRuntimeResult(string result)
    {
        if (!string.IsNullOrWhiteSpace(result))
        {
            RuntimeResultText = result.Trim();
        }
    }

    public void MarkRuntimeSucceeded(string? durationText = null, string? result = null)
    {
        RuntimeStatusText = "完成";
        if (!string.IsNullOrWhiteSpace(durationText))
        {
            RuntimeDurationText = durationText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            RuntimeResultText = result.Trim();
        }
        else if (string.IsNullOrWhiteSpace(RuntimeResultText) || RuntimeResultText == "正在执行" || RuntimeResultText == "结果 -")
        {
            RuntimeResultText = "执行完成";
        }

        RuntimeStatusBrush = "#FF3DDC97";
    }

    public void MarkRuntimeFailed(string? durationText, string error)
    {
        RuntimeStatusText = "失败";
        if (!string.IsNullOrWhiteSpace(durationText))
        {
            RuntimeDurationText = durationText.Trim();
        }

        RuntimeResultText = string.IsNullOrWhiteSpace(error) ? "执行失败" : error.Trim();
        RuntimeStatusBrush = "#FFFF667A";
    }

    public string DisplayStepTypeText => StepType switch
    {
        ProcessStepType.AxisMove => "运动控制",
        ProcessStepType.WaitPlcSignal => "等待信号",
        ProcessStepType.AcquireImage => "采图",
        ProcessStepType.RunVisionFlow => "视觉工具",
        ProcessStepType.ReadVisionResult => "读取结果",
        ProcessStepType.WriteResultTable => "存入表格",
        ProcessStepType.WritePlc => "写 PLC",
        ProcessStepType.DeviceRead => "读设备",
        ProcessStepType.DeviceWrite => "写设备",
        ProcessStepType.DeviceCommand => "设备命令",
        ProcessStepType.Delay => "延迟",
        ProcessStepType.End => "结束",
        ProcessStepType.StringProcess => "字符串处理",
        ProcessStepType.ResultJudge => "结果判定",
        _ => StepType.ToString()
    };

    public string DisplayStepTypeHint => StepType switch
    {
        ProcessStepType.AxisMove => "支持单轴和多轴同步运动，按产品流程执行动作",
        ProcessStepType.WaitPlcSignal => "等待轴卡输入、PLC、TCP、串口或仪表信号满足条件后继续执行",
        ProcessStepType.AcquireImage => "触发当前相机采图",
        ProcessStepType.RunVisionFlow => "调用当前产品绑定的视觉流程并收集结果",
        ProcessStepType.ReadVisionResult => "从视觉结果中读取一个值供后续节点使用",
        ProcessStepType.WriteResultTable => "把关键结果写入当前生产记录表",
        ProcessStepType.WritePlc => "把状态值或结果值写回 PLC 地址",
        ProcessStepType.DeviceRead => "读取 PLC、TCP、串口或轴卡 IO 参数",
        ProcessStepType.DeviceWrite => "写入 PLC、TCP、串口或运行参数",
        ProcessStepType.DeviceCommand => "向设备发送已配置的动作命令",
        ProcessStepType.Delay => "让当前流程暂停指定毫秒",
        ProcessStepType.End => "显式结束当前运行流程",
        ProcessStepType.StringProcess => "把接收到的字符串解析成后续流程可使用的参数",
        ProcessStepType.ResultJudge => "根据视觉结果的上下限做 OK/NG 判定",
        _ => "运行流程节点"
    };

    public string DisplayAccentBrush => StepType switch
    {
        ProcessStepType.AxisMove => "#FF33D6A6",
        ProcessStepType.WaitPlcSignal => "#FFFFC95A",
        ProcessStepType.AcquireImage => "#FF6FD4FF",
        ProcessStepType.RunVisionFlow => "#FF3DDC97",
        ProcessStepType.ReadVisionResult => "#FF7AD7FF",
        ProcessStepType.WriteResultTable => "#FF5CE08A",
        ProcessStepType.WritePlc => "#FFFF8A65",
        ProcessStepType.DeviceRead => "#FF7AD7FF",
        ProcessStepType.DeviceWrite => "#FF6FE7C8",
        ProcessStepType.DeviceCommand => "#FF8AA8FF",
        ProcessStepType.Delay => "#FFBFA2FF",
        ProcessStepType.End => "#FFFF667A",
        ProcessStepType.StringProcess => "#FF6FD4FF",
        ProcessStepType.ResultJudge => "#FF6FE7C8",
        _ => "#FFA9B7C2"
    };

    public string StepTypeText => StepType switch
    {
        ProcessStepType.AxisMove => "运动控制",
        ProcessStepType.WaitPlcSignal => "等待信号",
        ProcessStepType.AcquireImage => "采图",
        ProcessStepType.RunVisionFlow => "运行视觉",
        ProcessStepType.ReadVisionResult => "读取结果",
        ProcessStepType.WriteResultTable => "写结果表",
        ProcessStepType.WritePlc => "写 PLC",
        ProcessStepType.DeviceRead => "读设备",
        ProcessStepType.DeviceWrite => "写设备",
        ProcessStepType.DeviceCommand => "设备命令",
        ProcessStepType.StringProcess => "字符串处理",
        ProcessStepType.Delay => "延时",
        ProcessStepType.End => "结束",
        ProcessStepType.ResultJudge => "结果判定",
        _ => StepType.ToString()
    };

    public string StepTypeHint => StepType switch
    {
        ProcessStepType.AxisMove => "执行轴运动或动作命令",
        ProcessStepType.WaitPlcSignal => "等待指定设备信号达到触发条件",
        ProcessStepType.AcquireImage => "触发当前相机采图",
        ProcessStepType.RunVisionFlow => "调用当前产品绑定的视觉流程",
        ProcessStepType.ReadVisionResult => "从视觉结果集中取值",
        ProcessStepType.WriteResultTable => "写入内部结果表字段",
        ProcessStepType.WritePlc => "把结果或状态回写 PLC",
        ProcessStepType.DeviceRead => "读取设备或参数值",
        ProcessStepType.DeviceWrite => "写入设备或参数值",
        ProcessStepType.DeviceCommand => "执行设备命令",
        ProcessStepType.StringProcess => "解析接收到的字符串",
        ProcessStepType.Delay => "在流程中暂停指定毫秒",
        ProcessStepType.End => "显式结束当前运行流程",
        ProcessStepType.ResultJudge => "根据结果上下限判定 OK/NG",
        _ => "运行流程节点"
    };

    public string AccentBrush => StepType switch
    {
        ProcessStepType.AxisMove => "#FF33D6A6",
        ProcessStepType.WaitPlcSignal => "#FFFFC95A",
        ProcessStepType.AcquireImage => "#FF6FD4FF",
        ProcessStepType.RunVisionFlow => "#FF3DDC97",
        ProcessStepType.ReadVisionResult => "#FF7AD7FF",
        ProcessStepType.WriteResultTable => "#FF5CE08A",
        ProcessStepType.WritePlc => "#FFFF8A65",
        ProcessStepType.DeviceRead => "#FF7AD7FF",
        ProcessStepType.DeviceWrite => "#FF6FE7C8",
        ProcessStepType.DeviceCommand => "#FF8AA8FF",
        ProcessStepType.Delay => "#FFBFA2FF",
        ProcessStepType.End => "#FFFF667A",
        ProcessStepType.StringProcess => "#FF6FD4FF",
        ProcessStepType.ResultJudge => "#FF6FE7C8",
        _ => "#FFA9B7C2"
    };

    public bool IsWaitSignalStep => StepType == ProcessStepType.WaitPlcSignal;

    public bool IsAxisMoveStep => StepType == ProcessStepType.AxisMove;

    public bool IsAcquireImageStep => StepType == ProcessStepType.AcquireImage;

    public bool IsRunVisionFlowStep => StepType == ProcessStepType.RunVisionFlow;

    public bool ShowVisionFlowResultPanel => IsRunVisionFlowStep;

    public bool ShowAdvancedParametersText => !IsRunVisionFlowStep;

    public bool IsReadVisionResultStep => StepType == ProcessStepType.ReadVisionResult;

    public bool IsWriteResultTableStep => StepType == ProcessStepType.WriteResultTable;

    public bool IsWritePlcStep => StepType == ProcessStepType.WritePlc;

    public bool IsDeviceReadStep => StepType == ProcessStepType.DeviceRead;

    public bool IsDeviceWriteStep => StepType == ProcessStepType.DeviceWrite;

    public bool IsDeviceCommandStep => StepType == ProcessStepType.DeviceCommand;

    public bool IsGenericDeviceCommandStep => IsDeviceCommandStep && !IsCommunicationStep;

    public bool IsResultJudgeStep => StepType == ProcessStepType.ResultJudge;

    public bool IsStringProcessStep => StepType == ProcessStepType.StringProcess;

    public bool IsDelayStep => StepType == ProcessStepType.Delay;

    public bool IsEndStep => StepType == ProcessStepType.End;

    public bool IsCommunicationStep
    {
        get
        {
            var source = GetParameter("source", string.Empty);
            return (StepType == ProcessStepType.DeviceCommand || StepType == ProcessStepType.WaitPlcSignal) &&
                (string.Equals(source, "tcp", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(source, "serial", StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool IsSignalOrCommunicationStep => IsWaitSignalStep || IsCommunicationStep;

    public string SignalSource
    {
        get => GetParameter("source", "device");
        set
        {
            SetParameter("source", value);
            RaisePropertyChanged(nameof(IsCommunicationStep));
            RaisePropertyChanged(nameof(IsGenericDeviceCommandStep));
            RaisePropertyChanged(nameof(IsSignalOrCommunicationStep));
        }
    }

    public string WaitSignalSourceMode
    {
        get
        {
            var source = GetParameter("source", "device");
            if (string.Equals(source, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                return "TCP";
            }

            if (string.Equals(source, "serial", StringComparison.OrdinalIgnoreCase))
            {
                return "串口";
            }

            if (string.Equals(source, "digitalIo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "io", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "axisInput", StringComparison.OrdinalIgnoreCase))
            {
                return "轴卡 IO";
            }

            return "PLC";
        }
        set
        {
            var source = value?.Trim() switch
            {
                "TCP" => "tcp",
                "串口" => "serial",
                "轴卡 IO" => "digitalIo",
                _ => "device"
            };
            SetParameter("source", source);
            RaisePropertyChanged(nameof(WaitSignalSourceMode));
            RaisePropertyChanged(nameof(WaitSignalChannelLabel));
            RaisePropertyChanged(nameof(WaitSignalAddressLabel));
            RaisePropertyChanged(nameof(IsCommunicationStep));
            RaisePropertyChanged(nameof(IsGenericDeviceCommandStep));
            RaisePropertyChanged(nameof(IsSignalOrCommunicationStep));
        }
    }

    public string WaitSignalChannelLabel => WaitSignalSourceMode switch
    {
        "TCP" => "TCP 通道 Key",
        "串口" => "串口通道 Key",
        "轴卡 IO" => "IO 设备 Key",
        _ => "PLC 设备 Key"
    };

    public string WaitSignalAddressLabel => WaitSignalSourceMode switch
    {
        "TCP" => "发送内容",
        "串口" => "发送内容",
        "轴卡 IO" => "IO 点位",
        _ => "PLC 地址"
    };

    public string WaitSignalChannelKey
    {
        get => IsCommunicationStep ? GetParameter("channelKey", DeviceKey) : DeviceKey;
        set
        {
            if (IsCommunicationStep)
            {
                SetParameter("channelKey", value);
                if (SetProperty(ref _deviceKey, value ?? string.Empty, nameof(DeviceKey)))
                {
                    RaisePropertyChanged(nameof(WaitSignalChannelKey));
                }
            }
            else if (SetProperty(ref _deviceKey, value ?? string.Empty, nameof(DeviceKey)))
            {
                RaisePropertyChanged(nameof(WaitSignalChannelKey));
            }
        }
    }

    public string WaitSignalAddressOrPayload
    {
        get => IsCommunicationStep ? CommunicationPayload : SignalId;
        set
        {
            if (IsCommunicationStep)
            {
                CommunicationPayload = value;
            }
            else if (SetProperty(ref _signalId, value ?? string.Empty, nameof(SignalId)))
            {
                RaisePropertyChanged(nameof(WaitSignalAddressOrPayload));
            }
        }
    }

    public string ChannelKey
    {
        get => GetParameter("channelKey", SignalId);
        set => SetParameter("channelKey", value);
    }

    public string ExpectedValue
    {
        get => GetParameter("expected", StepType == ProcessStepType.DeviceCommand ? "OK" : "1");
        set => SetParameter("expected", value);
    }

    public string MatchMode
    {
        get => ToDisplayMatchMode(GetParameter("match", StepType == ProcessStepType.DeviceCommand ? "Contains" : "Equals"));
        set => SetParameter("match", ToStoredMatchMode(value));
    }

    public string PollIntervalMs
    {
        get => GetParameter("pollIntervalMs", "50");
        set => SetParameter("pollIntervalMs", value);
    }

    public string DebounceMs
    {
        get => GetParameter("debounceMs", "100");
        set => SetParameter("debounceMs", value);
    }

    public string TimeoutAction
    {
        get => GetParameter("onTimeout", "AlarmStop");
        set => SetParameter("onTimeout", value);
    }

    public string CommunicationPayload
    {
        get => GetParameter("payload", string.Empty);
        set => SetParameter("payload", value);
    }

    public string WaitResponse
    {
        get => GetParameter("waitResponse", "true");
        set => SetParameter("waitResponse", value);
    }

    public string StringInputKey
    {
        get => ResultKey;
        set => ResultKey = value;
    }

    public string StringOutputKey
    {
        get => OutputTarget;
        set => OutputTarget = value;
    }

    public string StringOperation
    {
        get => ToDisplayStringOperation(CommandName);
        set => CommandName = ToStoredStringOperation(value);
    }

    public string StringSeparator
    {
        get => GetParameter("separator", ",");
        set => SetParameter("separator", value);
    }

    public string StringIndex
    {
        get => GetParameter("index", "0");
        set => SetParameter("index", value);
    }

    public string StringPattern
    {
        get => GetParameter("pattern", string.Empty);
        set => SetParameter("pattern", value);
    }

    public string StringGroup
    {
        get => GetParameter("group", "1");
        set => SetParameter("group", value);
    }

    public string StringStart
    {
        get => GetParameter("start", "0");
        set => SetParameter("start", value);
    }

    public string StringLength
    {
        get => GetParameter("length", string.Empty);
        set => SetParameter("length", value);
    }

    public string StringOldValue
    {
        get => GetParameter("oldValue", string.Empty);
        set => SetParameter("oldValue", value);
    }

    public string StringNewValue
    {
        get => GetParameter("newValue", string.Empty);
        set => SetParameter("newValue", value);
    }

    public string GetVisionResultBinding(string resultToolId, string inputKey)
    {
        var key = BuildVisionResultBindingParameterKey(resultToolId, inputKey);
        return string.IsNullOrWhiteSpace(key) ? string.Empty : GetParameter(key, string.Empty);
    }

    public void SetVisionResultBinding(string resultToolId, string inputKey, string? variableKey)
    {
        var key = BuildVisionResultBindingParameterKey(resultToolId, inputKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        SetParameter(key, variableKey);
    }

    public static string BuildVisionResultBindingParameterKey(string resultToolId, string inputKey)
    {
        return string.IsNullOrWhiteSpace(resultToolId) || string.IsNullOrWhiteSpace(inputKey)
            ? string.Empty
            : $"{VisionResultBindingParameterPrefix}{resultToolId.Trim()}:{inputKey.Trim()}";
    }

    public static RecipeProcessStepItem FromDefinition(ProcessStepDefinition definition)
    {
        return new RecipeProcessStepItem
        {
            Id = definition.Id,
            StepNo = definition.StepNo,
            Name = definition.Name,
            StepType = definition.StepType,
            Enabled = definition.Enabled,
            DeviceKey = definition.DeviceKey,
            AxisKey = definition.AxisKey,
            Position = definition.Position.ToString("0.###", CultureInfo.InvariantCulture),
            Speed = definition.Speed.ToString("0.###", CultureInfo.InvariantCulture),
            Acceleration = definition.Acceleration.ToString("0.###", CultureInfo.InvariantCulture),
            AxisTargetsText = BuildAxisTargetsText(definition),
            FlowId = definition.FlowId,
            SignalId = definition.SignalId,
            ResultKey = definition.ResultKey,
            OutputTarget = definition.OutputTarget,
            CommandName = definition.CommandName,
            LowerLimit = definition.LowerLimit?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
            UpperLimit = definition.UpperLimit?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
            DelayMs = definition.DelayMs.ToString(CultureInfo.InvariantCulture),
            TimeoutMs = definition.TimeoutMs.ToString(CultureInfo.InvariantCulture),
            ParametersText = FormatParameters(definition.Parameters),
            Description = definition.Description
        };
    }

    public ProcessStepDefinition ToDefinition(int stepNo)
    {
        return new ProcessStepDefinition
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            StepNo = stepNo,
            Name = Name,
            StepType = StepType,
            Enabled = Enabled,
            DeviceKey = DeviceKey,
            AxisKey = AxisKey,
            Position = ParseDouble(Position),
            Speed = ParseDouble(Speed),
            Acceleration = ParseDouble(Acceleration),
            AxisTargets = ParseAxisTargets(AxisTargetsText),
            FlowId = FlowId,
            SignalId = SignalId,
            ResultKey = ResultKey,
            OutputTarget = OutputTarget,
            CommandName = CommandName,
            LowerLimit = ParseNullableDouble(LowerLimit),
            UpperLimit = ParseNullableDouble(UpperLimit),
            DelayMs = ParseInt(DelayMs),
            TimeoutMs = ParseInt(TimeoutMs, 3000),
            Parameters = ParseParameters(ParametersText),
            Description = Description
        };
    }

    private static string BuildAxisTargetsText(ProcessStepDefinition definition)
    {
        var targets = definition.AxisTargets.Count > 0
            ? definition.AxisTargets
            : string.IsNullOrWhiteSpace(definition.AxisKey)
                ? Array.Empty<AxisTargetDefinition>()
                :
                [
                    new AxisTargetDefinition
                    {
                        AxisKey = definition.AxisKey,
                        Position = definition.Position,
                        Speed = definition.Speed,
                        Acceleration = definition.Acceleration
                    }
                ];

        return string.Join(
            Environment.NewLine,
            targets.Select(target =>
                $"{target.AxisKey},{target.Position.ToString("0.###", CultureInfo.InvariantCulture)},{target.Speed.ToString("0.###", CultureInfo.InvariantCulture)},{target.Acceleration.ToString("0.###", CultureInfo.InvariantCulture)}"));
    }

    private static string FormatParameters(IReadOnlyDictionary<string, string> parameters)
    {
        return string.Join(
            Environment.NewLine,
            parameters
                .OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase)
                .Select(parameter => $"{parameter.Key}={parameter.Value}"));
    }

    private static Dictionary<string, string> ParseParameters(string? text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return parameters;
        }

        foreach (var line in text.Split(new[] { "\r\n", "\n", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            parameters[key] = line[(separatorIndex + 1)..].Trim();
        }

        return parameters;
    }

    private string GetParameter(string key, string fallback)
    {
        var parameters = ParseParameters(ParametersText);
        return parameters.TryGetValue(key, out var value) ? value : fallback;
    }

    private static string ToDisplayMatchMode(string value)
    {
        return value.Trim() switch
        {
            "Equals" or "Equal" or "==" => "等于",
            "NotEquals" or "NotEqual" or "!=" => "不等于",
            "Contains" => "包含",
            "Regex" => "正则",
            "GreaterThan" or ">" => "大于",
            "LessThan" or "<" => "小于",
            _ => value
        };
    }

    private static string ToStoredMatchMode(string? value)
    {
        return value?.Trim() switch
        {
            "Equals" or "Equal" or "==" => "等于",
            "NotEquals" or "NotEqual" or "!=" => "不等于",
            "Contains" => "包含",
            "Regex" => "正则",
            "GreaterThan" or ">" => "大于",
            "LessThan" or "<" => "小于",
            null => string.Empty,
            _ => value.Trim()
        };
    }

    private static string ToDisplayStringOperation(string? value)
    {
        return value?.Trim() switch
        {
            "Trim" => "去空格",
            "Split" => "分割",
            "Regex" or "RegexExtract" => "正则提取",
            "Substring" => "截取",
            "Replace" => "替换",
            "Upper" or "ToUpper" => "转大写",
            "Lower" or "ToLower" => "转小写",
            "Contains" => "包含判断",
            null or "" => "分割",
            _ => value.Trim()
        };
    }

    private static string ToStoredStringOperation(string? value)
    {
        return value?.Trim() switch
        {
            "去空格" or "Trim" => "Trim",
            "分割" or "Split" => "Split",
            "正则提取" or "Regex" or "RegexExtract" => "Regex",
            "截取" or "Substring" => "Substring",
            "替换" or "Replace" => "Replace",
            "转大写" or "Upper" or "ToUpper" => "Upper",
            "转小写" or "Lower" or "ToLower" => "Lower",
            "包含判断" or "Contains" => "Contains",
            null or "" => "Split",
            _ => value.Trim()
        };
    }

    private void SetParameter(string key, string? value)
    {
        var parameters = ParseParameters(ParametersText);
        if (string.IsNullOrWhiteSpace(value))
        {
            parameters.Remove(key);
        }
        else
        {
            parameters[key] = value.Trim();
        }

        var formatted = FormatParameters(parameters);
        if (SetProperty(ref _parametersText, formatted, nameof(ParametersText)))
        {
            RaiseParameterPropertiesChanged();
        }
    }

    private void RaiseParameterPropertiesChanged()
    {
        RaisePropertyChanged(nameof(SignalSource));
        RaisePropertyChanged(nameof(WaitSignalSourceMode));
        RaisePropertyChanged(nameof(WaitSignalChannelLabel));
        RaisePropertyChanged(nameof(WaitSignalAddressLabel));
        RaisePropertyChanged(nameof(WaitSignalChannelKey));
        RaisePropertyChanged(nameof(WaitSignalAddressOrPayload));
        RaisePropertyChanged(nameof(ChannelKey));
        RaisePropertyChanged(nameof(ExpectedValue));
        RaisePropertyChanged(nameof(MatchMode));
        RaisePropertyChanged(nameof(PollIntervalMs));
        RaisePropertyChanged(nameof(DebounceMs));
        RaisePropertyChanged(nameof(TimeoutAction));
        RaisePropertyChanged(nameof(CommunicationPayload));
        RaisePropertyChanged(nameof(WaitResponse));
        RaisePropertyChanged(nameof(StringOperation));
        RaisePropertyChanged(nameof(StringSeparator));
        RaisePropertyChanged(nameof(StringIndex));
        RaisePropertyChanged(nameof(StringPattern));
        RaisePropertyChanged(nameof(StringGroup));
        RaisePropertyChanged(nameof(StringStart));
        RaisePropertyChanged(nameof(StringLength));
        RaisePropertyChanged(nameof(StringOldValue));
        RaisePropertyChanged(nameof(StringNewValue));
        RaiseToolPanelPropertiesChanged();
    }

    private void RaiseToolPanelPropertiesChanged()
    {
        RaisePropertyChanged(nameof(IsWaitSignalStep));
        RaisePropertyChanged(nameof(IsAxisMoveStep));
        RaisePropertyChanged(nameof(IsAcquireImageStep));
        RaisePropertyChanged(nameof(IsRunVisionFlowStep));
        RaisePropertyChanged(nameof(ShowVisionFlowResultPanel));
        RaisePropertyChanged(nameof(ShowAdvancedParametersText));
        RaisePropertyChanged(nameof(IsReadVisionResultStep));
        RaisePropertyChanged(nameof(IsWriteResultTableStep));
        RaisePropertyChanged(nameof(IsWritePlcStep));
        RaisePropertyChanged(nameof(IsDeviceReadStep));
        RaisePropertyChanged(nameof(IsDeviceWriteStep));
        RaisePropertyChanged(nameof(IsDeviceCommandStep));
        RaisePropertyChanged(nameof(IsGenericDeviceCommandStep));
        RaisePropertyChanged(nameof(IsResultJudgeStep));
        RaisePropertyChanged(nameof(IsStringProcessStep));
        RaisePropertyChanged(nameof(IsDelayStep));
        RaisePropertyChanged(nameof(IsEndStep));
        RaisePropertyChanged(nameof(IsCommunicationStep));
        RaisePropertyChanged(nameof(IsSignalOrCommunicationStep));
    }

    private static AxisTargetDefinition[] ParseAxisTargets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<AxisTargetDefinition>();
        }

        return text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseAxisTargetLine)
            .Where(item => item is not null)
            .Cast<AxisTargetDefinition>()
            .ToArray();
    }

    private static AxisTargetDefinition? ParseAxisTargetLine(string line)
    {
        var parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        return new AxisTargetDefinition
        {
            AxisKey = parts[0],
            Position = parts.Length > 1 ? ParseDouble(parts[1]) : 0,
            Speed = parts.Length > 2 ? ParseDouble(parts[2]) : 100,
            Acceleration = parts.Length > 3 ? ParseDouble(parts[3]) : 100
        };
    }

    private static double ParseDouble(string? text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
               double.TryParse(text, out value)
            ? value
            : 0;
    }

    private static int ParseInt(string? text, int fallback = 0)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               int.TryParse(text, out value)
            ? value
            : fallback;
    }

    private static double? ParseNullableDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return ParseDouble(text);
    }
}

public sealed record RecipeProcessToolboxItem(
    ProcessStepType StepType,
    string Title,
    string Subtitle,
    string AccentBrush,
    string TemplateKey = "");

public sealed class RecipeVisionResultItem : BindableBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "结果项";
    private string _flowId = string.Empty;
    private string _sourceToolId = string.Empty;
    private string _sourceKey = string.Empty;
    private string _dataType = "string";
    private string _unit = string.Empty;
    private bool _participateInJudge;
    private string _externalAlias = string.Empty;
    private string _plcAddress = string.Empty;
    private string _description = string.Empty;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string FlowId
    {
        get => _flowId;
        set => SetProperty(ref _flowId, value);
    }

    public string SourceToolId
    {
        get => _sourceToolId;
        set
        {
            if (SetProperty(ref _sourceToolId, value))
            {
                RaisePropertyChanged(nameof(SourceAddress));
            }
        }
    }

    public string SourceKey
    {
        get => _sourceKey;
        set
        {
            if (SetProperty(ref _sourceKey, value))
            {
                RaisePropertyChanged(nameof(SourceAddress));
            }
        }
    }

    public string SourceAddress
    {
        get => string.IsNullOrWhiteSpace(SourceToolId) && string.IsNullOrWhiteSpace(SourceKey)
            ? string.Empty
            : $"{SourceToolId}|{SourceKey}";
        set
        {
            var parts = (value ?? string.Empty).Split('|', 2, StringSplitOptions.TrimEntries);
            SourceToolId = parts.Length > 0 ? parts[0] : string.Empty;
            SourceKey = parts.Length > 1 ? parts[1] : string.Empty;
            RaisePropertyChanged(nameof(SourceAddress));
        }
    }

    public string DataType
    {
        get => _dataType;
        set => SetProperty(ref _dataType, value);
    }

    public string Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, value);
    }

    public bool ParticipateInJudge
    {
        get => _participateInJudge;
        set => SetProperty(ref _participateInJudge, value);
    }

    public string ExternalAlias
    {
        get => _externalAlias;
        set => SetProperty(ref _externalAlias, value);
    }

    public string PlcAddress
    {
        get => _plcAddress;
        set => SetProperty(ref _plcAddress, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public static RecipeVisionResultItem FromDefinition(VisionResultDefinition definition)
    {
        return new RecipeVisionResultItem
        {
            Id = definition.Id,
            Name = definition.Name,
            FlowId = definition.FlowId,
            SourceToolId = definition.SourceToolId,
            SourceKey = definition.SourceKey,
            DataType = definition.DataType,
            Unit = definition.Unit,
            ParticipateInJudge = definition.ParticipateInJudge,
            ExternalAlias = definition.ExternalAlias,
            PlcAddress = definition.PlcAddress,
            Description = definition.Description
        };
    }

    public VisionResultDefinition ToDefinition()
    {
        return new VisionResultDefinition
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Name = Name,
            FlowId = FlowId,
            SourceToolId = SourceToolId,
            SourceKey = SourceKey,
            DataType = DataType,
            Unit = Unit,
            ParticipateInJudge = ParticipateInJudge,
            ExternalAlias = ExternalAlias,
            PlcAddress = PlcAddress,
            Description = Description
        };
    }
}

public sealed class RecipePlcSignalItem : BindableBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "PLC信号";
    private string _address = string.Empty;
    private string _direction = "Read";
    private string _triggerValue = "1";
    private string _timeoutMs = "3000";
    private bool _blocking = true;
    private string _description = string.Empty;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string Direction
    {
        get => _direction;
        set => SetProperty(ref _direction, value);
    }

    public string TriggerValue
    {
        get => _triggerValue;
        set => SetProperty(ref _triggerValue, value);
    }

    public string TimeoutMs
    {
        get => _timeoutMs;
        set => SetProperty(ref _timeoutMs, value);
    }

    public bool Blocking
    {
        get => _blocking;
        set => SetProperty(ref _blocking, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public static RecipePlcSignalItem FromDefinition(PlcSignalDefinition definition)
    {
        return new RecipePlcSignalItem
        {
            Id = definition.Id,
            Name = definition.Name,
            Address = definition.Address,
            Direction = definition.Direction,
            TriggerValue = definition.TriggerValue,
            TimeoutMs = definition.TimeoutMs.ToString(),
            Blocking = definition.Blocking,
            Description = definition.Description
        };
    }

    public PlcSignalDefinition ToDefinition()
    {
        return new PlcSignalDefinition
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Name = Name,
            Address = Address,
            Direction = Direction,
            TriggerValue = TriggerValue,
            TimeoutMs = int.TryParse(TimeoutMs, out var timeoutMs) ? timeoutMs : 3000,
            Blocking = Blocking,
            Description = Description
        };
    }
}

public sealed class RecipeSignalMappingItem : BindableBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _key = "Signal";
    private string _name = "逻辑信号";
    private string _dataType = "bool";
    private string _sourceType = "PLC";
    private string _deviceKey = string.Empty;
    private string _address = string.Empty;
    private string _channelKey = string.Empty;
    private string _requestText = string.Empty;
    private bool _enabled = true;
    private string _description = string.Empty;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value))
            {
                RaisePropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                RaisePropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string DataType
    {
        get => _dataType;
        set => SetProperty(ref _dataType, value);
    }

    public string SourceType
    {
        get => _sourceType;
        set
        {
            if (SetProperty(ref _sourceType, value))
            {
                RaisePropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string DeviceKey
    {
        get => _deviceKey;
        set => SetProperty(ref _deviceKey, value);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string ChannelKey
    {
        get => _channelKey;
        set => SetProperty(ref _channelKey, value);
    }

    public string RequestText
    {
        get => _requestText;
        set => SetProperty(ref _requestText, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string DisplayText => string.IsNullOrWhiteSpace(Name)
        ? $"{Key} ({SourceType})"
        : $"{Name} / {Key} ({SourceType})";

    public override string ToString()
    {
        return DisplayText;
    }

    public static RecipeSignalMappingItem FromDefinition(SignalMappingDefinition definition)
    {
        return new RecipeSignalMappingItem
        {
            Id = definition.Id,
            Key = definition.Key,
            Name = definition.Name,
            DataType = definition.DataType,
            SourceType = definition.SourceType,
            DeviceKey = definition.DeviceKey,
            Address = definition.Address,
            ChannelKey = definition.ChannelKey,
            RequestText = definition.RequestText,
            Enabled = definition.Enabled,
            Description = definition.Description
        };
    }

    public SignalMappingDefinition ToDefinition()
    {
        return new SignalMappingDefinition
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Key = string.IsNullOrWhiteSpace(Key) ? "Signal" : Key.Trim(),
            Name = string.IsNullOrWhiteSpace(Name) ? Key.Trim() : Name.Trim(),
            DataType = string.IsNullOrWhiteSpace(DataType) ? "bool" : DataType.Trim(),
            SourceType = string.IsNullOrWhiteSpace(SourceType) ? "PLC" : SourceType.Trim(),
            DeviceKey = DeviceKey?.Trim() ?? string.Empty,
            Address = Address?.Trim() ?? string.Empty,
            ChannelKey = ChannelKey?.Trim() ?? string.Empty,
            RequestText = RequestText ?? string.Empty,
            Enabled = Enabled,
            Description = Description?.Trim() ?? string.Empty
        };
    }
}

public sealed class VisionFlowItem : BindableBase
{
    private string _name;

    public VisionFlowItem(string id, string name, string description, DateTimeOffset updatedAt)
    {
        Id = id;
        _name = name;
        Description = description;
        UpdatedAt = updatedAt;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Description { get; }

    public DateTimeOffset UpdatedAt { get; }

    public IReadOnlyList<FlowConnectionOptionItem> ContextOptions { get; set; } = Array.Empty<FlowConnectionOptionItem>();

    public bool HasContextOptions => ContextOptions.Count > 0;
}

public sealed class ShellNavigationItem : BindableBase
{
    private bool _isSelected;

    public ShellNavigationItem(string key, string title, string icon)
    {
        Key = key;
        Title = title;
        Icon = icon;
    }

    public string Key { get; }

    public string Title { get; }

    public string Icon { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class VisionToolItem : BindableBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Vision Tool";
    private VisionToolKind _kind;
    private bool _enabled = true;
    private string _roiId = string.Empty;
    private string _parametersText = string.Empty;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public VisionToolKind Kind
    {
        get => _kind;
        set => SetProperty(ref _kind, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string RoiId
    {
        get => _roiId;
        set => SetProperty(ref _roiId, value);
    }

    public string ParametersText
    {
        get => _parametersText;
        set
        {
            if (SetProperty(ref _parametersText, value))
            {
                RaisePropertyChanged(nameof(ParametersPreview));
            }
        }
    }

    public string ParametersPreview => string.IsNullOrWhiteSpace(ParametersText)
        ? "-"
        : ParametersText.Replace(Environment.NewLine, "; ");

    public static VisionToolItem FromDefinition(VisionToolDefinition definition)
    {
        var parameters = new Dictionary<string, string>(definition.Parameters, StringComparer.OrdinalIgnoreCase);
        parameters.TryGetValue("roiId", out var roiId);
        parameters.Remove("roiId");

        return new VisionToolItem
        {
            Id = definition.Id,
            Name = definition.Name,
            Kind = definition.Kind,
            Enabled = definition.Enabled,
            RoiId = roiId ?? string.Empty,
            ParametersText = FormatParameters(parameters)
        };
    }

    public static VisionToolItem Create(VisionToolKind kind, int index)
    {
        return new VisionToolItem
        {
            Id = $"tool-{kind.ToString().ToLowerInvariant()}-{DateTimeOffset.UtcNow:HHmmssfff}",
            Name = $"{VisionToolCatalog.GetDefaultName(kind)}-{index}",
            Kind = kind,
            Enabled = true,
            ParametersText = FormatParameters(VisionToolCatalog.GetDefaultParameters(kind))
        };
    }

    public VisionToolDefinition ToDefinition()
    {
        var parameters = ParseParameters(ParametersText);
        if (!string.IsNullOrWhiteSpace(RoiId))
        {
            parameters["roiId"] = RoiId;
        }

        return new VisionToolDefinition
        {
            Id = Id,
            Name = Name,
            Kind = Kind,
            Enabled = Enabled,
            Parameters = parameters
        };
    }

    private static Dictionary<string, string> ParseParameters(string text)
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

    private static string FormatParameters(IReadOnlyDictionary<string, string> parameters)
    {
        return string.Join("; ", parameters.Select(pair => $"{pair.Key}={pair.Value}"));
    }

}

public sealed record RoiChoiceItem(string Id, string Name)
{
    public override string ToString()
    {
        return Name;
    }
}

public sealed record RoiShapeOptionItem(RoiShapeKind Kind, string Name)
{
    public override string ToString()
    {
        return Name;
    }
}

public sealed class ToolboxTreeItem : BindableBase
{
    private bool _isExpanded = true;

    public ToolboxTreeItem(
        string Name,
        string Icon,
        VisionToolKind? Kind = null,
        IReadOnlyList<ToolboxTreeItem>? Children = null,
        string? iconPath = null,
        bool isExpanded = true,
        string? measurementMode = null)
    {
        this.Name = Name;
        this.Icon = Icon;
        this.Kind = Kind;
        MeasurementMode = measurementMode;
        this.Children = Children ?? Array.Empty<ToolboxTreeItem>();
        _isExpanded = isExpanded;
        IconGeometry = Geometry.Parse(iconPath ?? ResolveIconPath(Name, Kind));
        IconGeometry.Freeze();
    }

    public string Name { get; }

    public string Icon { get; }

    public Geometry IconGeometry { get; }

    public VisionToolKind? Kind { get; }

    public string? MeasurementMode { get; }

    public IReadOnlyList<ToolboxTreeItem> Children { get; }

    public bool IsTool => Kind.HasValue;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                RaisePropertyChanged(nameof(IsChildrenVisible));
            }
        }
    }

    public bool IsChildrenVisible => IsExpanded && Children.Count > 0;

    public ToolboxTreeItem WithChildren(IReadOnlyList<ToolboxTreeItem> children)
    {
        return new ToolboxTreeItem(Name, Icon, Kind, children, IconGeometry.ToString(CultureInfo.InvariantCulture), true, MeasurementMode);
    }

    private static string ResolveIconPath(string name, VisionToolKind? kind)
    {
        return kind switch
        {
            VisionToolKind.AcquireImage => "M6,11 H11 L13,8 H19 L21,11 H26 V24 H6 Z M16,14 A4,4 0 1 1 16,22 A4,4 0 1 1 16,14 M9,13 H11",
            VisionToolKind.ImageProcess => "M6,8 H26 V24 H6 Z M9,11 H23 M9,16 H23 M9,21 H23 M12,5 V27 M20,5 V27",
            VisionToolKind.TemplateLocate => "M7,7 H21 V21 H7 Z M10,11 H18 M10,15 H18 M22,22 L27,27 M23,18 A5,5 0 1 1 23,28 A5,5 0 1 1 23,18",
            VisionToolKind.MultiTargetMatch => "M5,7 H13 V15 H5 Z M19,7 H27 V15 H19 Z M5,19 H13 V27 H5 Z M19,19 H27 V27 H19 Z M9,11 H23 M9,23 H23 M11,9 V25 M23,9 V25",
            VisionToolKind.CoordinateTransform => "M6,24 L14,16 L6,8 M14,16 H27 M22,11 L27,16 L22,21 M8,27 H18 M13,22 L18,27 L13,32",
            VisionToolKind.FindCircle => "M16,5 A11,11 0 1 1 16,27 A11,11 0 1 1 16,5 M16,12 A4,4 0 1 1 16,20 A4,4 0 1 1 16,12 M16,4 V8 M16,24 V28 M4,16 H8 M24,16 H28",
            VisionToolKind.FindLine => "M6,24 L26,8 M6,24 H10 M6,24 V20 M26,8 H22 M26,8 V12 M9,8 H23",
            VisionToolKind.MeasureDistance => "M6,18 H26 M8,14 L6,18 L8,22 M24,14 L26,18 L24,22 M12,10 L20,26",
            VisionToolKind.LineAngle => "M6,24 L26,8 M8,8 L24,24 M15,15 A5,5 0 0 1 20,16",
            VisionToolKind.LineIntersection => "M6,24 L26,8 M8,8 L24,24 M16,16 A3,3 0 1 1 16,22 A3,3 0 1 1 16,16",
            VisionToolKind.FitLineFromPoints => "M7,23 L25,9 M9,21 A2,2 0 1 1 9,25 A2,2 0 1 1 9,21 M23,7 A2,2 0 1 1 23,11 A2,2 0 1 1 23,7",
            VisionToolKind.TemplatePoint => "M7,7 H21 V21 H7 Z M24,24 A3,3 0 1 1 24,30 A3,3 0 1 1 24,24 M21,21 L24,24",
            VisionToolKind.CodeRead => "M5,5 H13 V13 H5 Z M8,8 H10 V10 H8 Z M19,5 H27 V13 H19 Z M22,8 H24 V10 H22 Z M5,19 H13 V27 H5 Z M8,22 H10 V24 H8 Z M18,18 H21 V21 H18 Z M24,18 H27 V21 H24 Z M18,24 H21 V27 H18 Z M23,23 H27 V27 H23 Z",
            VisionToolKind.Ocr => "M7,25 L13,7 L19,25 M9,20 H17 M23,8 V25 M21,8 H27",
            VisionToolKind.DefectDetect => "M5,25 H27 V7 H5 Z M8,21 L12,16 L16,20 L21,11 L25,16 M8,11 H12 V15 H8 Z",
            VisionToolKind.RoiMap => "M7,7 H25 V25 H7 Z M7,7 H12 M7,7 V12 M25,7 H20 M25,7 V12 M7,25 H12 M7,25 V20 M25,25 H20 M25,25 V20 M12,12 H20 V20 H12 Z",
            VisionToolKind.Judge => "M7,9 L10,12 L15,6 M18,9 H27 M7,17 L10,20 L15,14 M18,17 H27 M7,25 L10,28 L15,22 M18,25 H27",
            VisionToolKind.Result => "M6,7 H26 V25 H6 Z M10,12 H22 M10,17 H19 M10,22 H22 M22,10 L27,15 L22,20",
            _ => ResolveIconPathByName(name)
        };
    }

    private static string ResolveIconPathByName(string name)
    {
        if (name.Contains("延时", StringComparison.OrdinalIgnoreCase))
        {
            return "M16,4 A12,12 0 1 1 16,28 A12,12 0 1 1 16,4 M16,9 V17 L22,20";
        }

        if (name.Contains("点位", StringComparison.OrdinalIgnoreCase))
        {
            return "M9,22 H23 V10 H9 Z M12,19 H20 V13 H12 Z M23,10 L27,6 M27,6 V12 M27,6 H21";
        }

        if (name.Contains("单轴", StringComparison.OrdinalIgnoreCase))
        {
            return "M7,12 H25 M21,8 L25,12 L21,16 M25,20 H7 M11,16 L7,20 L11,24";
        }

        if (name.Contains("数据", StringComparison.OrdinalIgnoreCase))
        {
            return "M5,25 H27 V7 H5 Z M8,21 L12,17 L16,20 L21,12 L25,15 M9,10 V14 M13,10 V17 M17,10 V15 M21,10 V12";
        }

        if (name.Contains("脚本", StringComparison.OrdinalIgnoreCase) || name.Contains("预先", StringComparison.OrdinalIgnoreCase))
        {
            return "M16,5 V9 M16,23 V27 M5,16 H9 M23,16 H27 M8,8 L11,11 M21,21 L24,24 M24,8 L21,11 M11,21 L8,24 M16,11 A5,5 0 1 1 16,21 A5,5 0 1 1 16,11";
        }

        if (name.Contains("存储", StringComparison.OrdinalIgnoreCase))
        {
            return "M7,5 H23 L27,9 V27 H7 Z M11,5 V13 H21 V5 M11,22 H23 M11,25 H23";
        }

        if (name.Contains("显示", StringComparison.OrdinalIgnoreCase))
        {
            return "M5,7 H27 V25 H5 Z M8,21 L13,16 L17,20 L22,13 L26,21 M10,11 H14 V15 H10 Z";
        }

        if (name.Contains("点集", StringComparison.OrdinalIgnoreCase))
        {
            return "M7,16 H25 M16,7 V25 M8,8 H11 V11 H8 Z M21,8 H24 V11 H21 Z M8,21 H11 V24 H8 Z M21,21 H24 V24 H21 Z";
        }

        if (name.Contains("产品", StringComparison.OrdinalIgnoreCase))
        {
            return "M7,10 H21 V24 H7 Z M21,10 L26,6 M26,6 V12 M26,6 H20";
        }

        if (name.Contains("矩形", StringComparison.OrdinalIgnoreCase))
        {
            return "M7,7 H25 V25 H7 Z M11,11 H21 V21 H11 Z";
        }

        if (name.Contains("字符", StringComparison.OrdinalIgnoreCase))
        {
            return "M7,25 L13,7 L19,25 M9,20 H17 M23,8 V25 M21,8 H27";
        }

        if (name.Contains("综合", StringComparison.OrdinalIgnoreCase))
        {
            return "M7,9 L10,12 L15,6 M18,9 H27 M7,17 L10,20 L15,14 M18,17 H27 M7,25 L10,28 L15,22 M18,25 H27";
        }

        return "M7,7 H25 V25 H7 Z M11,11 H21 V21 H11 Z";
    }
}

public sealed record VisionDebugLogItem(string Time, string Level, string Message);

public sealed record FlowConnectionOptionItem(string Header, ICommand Command);

public sealed record FlowPortConnectionRequest(FlowTreeItem Source, FlowTreeItem Target);

public sealed record CanvasFlowPortConnectionRequest(FlowPortItem Source, FlowPortItem Target);

public sealed record FlowNodeMoveRequest(FlowNodeItem Node, double X, double Y, bool Commit);

public sealed record FlowNodeSelectionRequest(Rect Bounds, bool Commit, bool Clear = false, bool Single = false, bool Toggle = false);

public sealed class FlowNodeItem : BindableBase
{
    private double _x;
    private double _y;
    private ToolResult? _runResult;

    public required VisionToolItem Tool { get; init; }

    public required string Title { get; init; }

    public required string Icon { get; init; }

    public required double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    public required double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public required bool IsSelected { get; init; }

    public string PreflightIssue { get; init; } = string.Empty;

    public IReadOnlyList<FlowPortItem> Inputs { get; init; } = Array.Empty<FlowPortItem>();

    public IReadOnlyList<FlowPortItem> Outputs { get; init; } = Array.Empty<FlowPortItem>();

    public IReadOnlyList<FlowConnectionOptionItem> ContextOptions { get; init; } = Array.Empty<FlowConnectionOptionItem>();

    public bool ShowResultSourceSummaries => Tool.Kind == VisionToolKind.Result;

    public bool ShowOutputPorts => !ShowResultSourceSummaries;

    public bool HasContextOptions => ContextOptions.Count > 0;

    public bool HasRunResult => _runResult is not null;

    public bool HasFailure => _runResult?.Outcome is InspectionOutcome.Ng or InspectionOutcome.Error;

    public bool HasPreflightIssue => _runResult is null && !string.IsNullOrWhiteSpace(PreflightIssue);

    public string StatusText => _runResult?.Outcome switch
    {
        InspectionOutcome.Ok => "OK",
        InspectionOutcome.Ng => "NG",
        InspectionOutcome.Error => "ERROR",
        _ when HasPreflightIssue => "缺输入",
        _ => Tool.Enabled ? "Enabled" : "Disabled"
    };

    public string StatusBrush => _runResult?.Outcome switch
    {
        InspectionOutcome.Ok => "#FF42E58E",
        InspectionOutcome.Ng or InspectionOutcome.Error => "#FFFF5C7A",
        _ when HasPreflightIssue => "#FFFFC95A",
        _ => "#FFA9B7C2"
    };

    public string ResultSummary => _runResult is null
        ? string.IsNullOrWhiteSpace(PreflightIssue) ? $"{Title} {StatusText}" : $"{Title}：{PreflightIssue}"
        : $"{Title} {StatusText}：{_runResult.Message}";

    public void SetRunResult(ToolResult? result)
    {
        if (ReferenceEquals(_runResult, result))
        {
            return;
        }

        _runResult = result;
        RaisePropertyChanged(nameof(HasRunResult));
        RaisePropertyChanged(nameof(HasFailure));
        RaisePropertyChanged(nameof(HasPreflightIssue));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(StatusBrush));
        RaisePropertyChanged(nameof(ResultSummary));
    }
}

public sealed class FlowPortItem
{
    public required VisionToolItem OwnerTool { get; init; }

    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string DataType { get; init; }

    public required bool IsInput { get; init; }

    public required bool IsOutput { get; init; }

    public required bool IsConnected { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    public string SourceDisplayName { get; init; } = string.Empty;

    public IReadOnlyList<FlowConnectionOptionItem> ContextOptions { get; init; } = Array.Empty<FlowConnectionOptionItem>();

    public bool HasSourceDisplayName => !string.IsNullOrWhiteSpace(SourceDisplayName);

    public bool HasContextOptions => ContextOptions.Count > 0;

    public bool CanDrag => IsOutput;

    public string Stroke => DataType switch
    {
        "Image" => "#FF33D6A6",
        "Pose" => "#FFFFC95A",
        "Point" or "Point[]" => "#FFFFC95A",
        "Roi" => "#FF7AD7FF",
        "Result" => "#FFBFA2FF",
        "Line" => "#FF8FD4FF",
        "Circle" => "#FF8FE8B9",
        "Number" => "#FFFFD27A",
        "Text" => "#FFD2E7FF",
        _ => "#FFA9B7C2"
    };

    public string SoftFill => DataType switch
    {
        "Image" => "#1533D6A6",
        "Pose" => "#18FFC95A",
        "Point" or "Point[]" => "#18FFC95A",
        "Roi" => "#147AD7FF",
        "Result" => "#16BFA2FF",
        "Line" => "#158FD4FF",
        "Circle" => "#158FE8B9",
        "Number" => "#18FFD27A",
        "Text" => "#15D2E7FF",
        _ => "#101D2A33"
    };
}

public sealed class FlowConnectionItem
{
    public required FlowPortItem Source { get; init; }

    public required FlowPortItem Target { get; init; }

    public required Geometry Geometry { get; init; }

    public required string Label { get; init; }

    public required double LabelX { get; init; }

    public required double LabelY { get; init; }

    public required double TargetX { get; init; }

    public required double TargetY { get; init; }

    public string Stroke => Source.DataType switch
    {
        "Image" => "#FF33D6A6",
        "Pose" => "#FFFFC95A",
        "Point" or "Point[]" => "#FFFFC95A",
        "Roi" => "#FF7AD7FF",
        "Result" => "#FFBFA2FF",
        "Line" => "#FF8FD4FF",
        "Circle" => "#FF8FE8B9",
        "Number" => "#FFFFD27A",
        "Text" => "#FFD2E7FF",
        _ => "#FFA9B7C2"
    };
}

public sealed record FlowTreeItem(
    string Name,
    string Icon,
    VisionToolItem? Tool = null,
    IReadOnlyList<FlowTreeItem>? Children = null,
    bool IsConnector = false,
    bool IsInput = false,
    bool IsOutput = false,
    bool IsConnected = false,
    VisionToolItem? OwnerTool = null,
    string PortKey = "",
    IReadOnlyList<FlowConnectionOptionItem>? ContextOptions = null)
{
    public IReadOnlyList<FlowTreeItem> Children { get; init; } = Children ?? Array.Empty<FlowTreeItem>();

    public IReadOnlyList<FlowConnectionOptionItem> ContextOptions { get; init; } = ContextOptions ?? Array.Empty<FlowConnectionOptionItem>();

    public bool HasContextOptions => ContextOptions.Count > 0;

    public bool IsConnectedInput => IsInput && IsConnected;

    public bool IsConnectedOutput => IsOutput && IsConnected;

    public bool IsTool => Tool is not null;
}

public sealed record RoiItem(string Name, RoiShapeKind Shape, string Geometry);
