using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.Services;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Vision.UI.ViewModels;

public sealed class BlobAnalysisToolDialogViewModel : BindableBase
{
    private const string ThresholdModeRangeText = "灰度范围（Range）";
    private const string ThresholdModeOtsuText = "自动阈值（Otsu）";
    private const string ThresholdModeFixedText = "固定阈值（Fixed）";
    private const string ThresholdModeAdaptiveText = "自适应阈值（Adaptive）";
    private const string SelectionLargestText = "最大面积优先（Largest）";
    private const string SelectionSmallestText = "最小面积优先（Smallest）";
    private const string SelectionClosestCenterText = "最接近中心（ClosestCenter）";
    private const string SelectionTopLeftText = "从左上排序（TopLeft）";

    private readonly IReadOnlyList<RoiDefinition> _availableRois;
    private readonly Dictionary<string, string> _parameters;
    private readonly string _toolId;
    private readonly Recipe? _previewRecipe;
    private readonly IVisionPipeline _pipeline;
    private readonly IAppLogService _log;
    private bool _enabled;
    private bool _isBusy;
    private bool _isApplyingPreset;
    private bool _roiReferenceDirty;
    private bool _isRoiPlacementArmed;
    private string _name;
    private string _roiId;
    private string _roiPlacementHint = string.Empty;
    private string _selectedTabKey = "Home";
    private RoiShapeOptionItem _selectedRoiShape;
    private RoiEditorItem? _blobRoiEditor;
    private RoiEditorItem? _selectedEditableRoi;
    private ToolResult? _previewResult;
    private string _thresholdMode;
    private double _threshold;
    private double _grayMin;
    private double _grayMax;
    private string _polarity;
    private double _minArea;
    private double _maxArea;
    private double _minWidth;
    private double _maxWidth;
    private double _minHeight;
    private double _maxHeight;
    private double _minCircularity;
    private double _maxCircularity;
    private double _minAspectRatio;
    private double _maxAspectRatio;
    private int _adaptiveBlockSize;
    private double _adaptiveC;
    private int _minCount;
    private int _maxCount;
    private int _maxResults;
    private string _selection;
    private int _morphOpen;
    private int _morphClose;
    private bool _showRoi;
    private bool _showResultLabel;
    private bool _showCrosshair;
    private BlobPresetItem? _selectedPreset;
    private BlobDetailItem? _selectedBlobDetail;
    private string _statusText = "等待运行";
    private string _durationText = "0ms";
    private string _countText = "-";
    private string _bestAreaText = "-";
    private string _bestCenterText = "-";
    private string _bestCircularityText = "-";

    public BlobAnalysisToolDialogViewModel(
        VisionToolItem tool,
        IReadOnlyList<RoiDefinition> rois,
        string flowName,
        ImageFrame? currentFrame,
        Recipe? previewRecipe,
        IVisionPipeline pipeline,
        IAppLogService log)
    {
        _availableRois = rois;
        _previewRecipe = previewRecipe;
        _pipeline = pipeline;
        _log = log;
        _toolId = tool.Id;
        _parameters = ParseParameters(tool.ParametersText);
        _roiReferenceDirty = !HasValidRoiReferencePose(_parameters);
        _name = tool.Name;
        _enabled = tool.Enabled;
        _roiId = tool.RoiId;
        _selectedRoiShape = ToolRoiFactory.ShapeOptions[0];
        _thresholdMode = NormalizeThresholdMode(GetString(_parameters, "thresholdMode", "Range"));
        _threshold = Clamp(GetDouble(_parameters, "threshold", 128), 0, 255);
        _grayMin = Clamp(GetDouble(_parameters, "grayMin", GetDouble(_parameters, "grayLower", 0)), 0, 255);
        _grayMax = Clamp(GetDouble(_parameters, "grayMax", GetDouble(_parameters, "grayUpper", 80)), _grayMin, 255);
        _polarity = NormalizePolarity(GetString(_parameters, "polarity", "暗斑"));
        _minArea = Math.Max(0, GetDouble(_parameters, "minArea", 30));
        _maxArea = Math.Max(_minArea, GetDouble(_parameters, "maxArea", 1_000_000));
        _minWidth = Math.Max(0, GetDouble(_parameters, "minWidth", 0));
        _maxWidth = Math.Max(_minWidth, GetDouble(_parameters, "maxWidth", 1_000_000));
        _minHeight = Math.Max(0, GetDouble(_parameters, "minHeight", 0));
        _maxHeight = Math.Max(_minHeight, GetDouble(_parameters, "maxHeight", 1_000_000));
        _minCircularity = Clamp(GetDouble(_parameters, "minCircularity", 0), 0, 1);
        _maxCircularity = Clamp(GetDouble(_parameters, "maxCircularity", 1), _minCircularity, 1);
        _minAspectRatio = Math.Max(0, GetDouble(_parameters, "minAspectRatio", 0));
        _maxAspectRatio = Math.Max(_minAspectRatio, GetDouble(_parameters, "maxAspectRatio", 1_000_000));
        _adaptiveBlockSize = NormalizeAdaptiveBlockSize(GetInt(_parameters, "adaptiveBlockSize", 41));
        _adaptiveC = Clamp(GetDouble(_parameters, "adaptiveC", 5), -255, 255);
        _minCount = Math.Max(0, GetInt(_parameters, "minCount", 1));
        _maxCount = Math.Max(_minCount, GetInt(_parameters, "maxCount", 1_000_000));
        _maxResults = Math.Clamp(GetInt(_parameters, "maxResults", 128), 1, 10_000);
        _selection = NormalizeSelection(GetString(_parameters, "selection", "Largest"));
        _morphOpen = Math.Clamp(GetInt(_parameters, "morphOpen", 1), 0, 31);
        _morphClose = Math.Clamp(GetInt(_parameters, "morphClose", 0), 0, 31);
        _showRoi = GetBool(_parameters, "showRoi", true);
        _showResultLabel = GetBool(_parameters, "showResultLabel", false);
        _showCrosshair = GetBool(_parameters, "showCrosshair", true);

        WindowTitle = string.IsNullOrWhiteSpace(flowName)
            ? _name
            : $"{_name} [ {flowName}. {_name} ]";
        CurrentFrame = currentFrame;
        InputFrameInfo = currentFrame is null
            ? "输入图像：未连接，请在流程画布连接采图.输出图像"
            : $"输入图像：{currentFrame.Width}x{currentFrame.Height} / {currentFrame.Source}";
        if (currentFrame is null)
        {
            _statusText = "未连接输入图像";
        }

        CreatedRois = new ObservableCollection<RoiDefinition>();
        RemovedRoiIds = new ObservableCollection<string>();
        EditableRois = new ObservableCollection<RoiEditorItem>();
        RoiShapeOptions = new ObservableCollection<RoiShapeOptionItem>(ToolRoiFactory.ShapeOptions);
        ThresholdModes = new ObservableCollection<string>([ThresholdModeRangeText, ThresholdModeOtsuText, ThresholdModeFixedText, ThresholdModeAdaptiveText]);
        Polarities = new ObservableCollection<string>(["暗斑", "亮斑"]);
        ResultSelections = new ObservableCollection<string>([SelectionLargestText, SelectionSmallestText, SelectionClosestCenterText, SelectionTopLeftText]);
        Presets = new ObservableCollection<BlobPresetItem>(CreatePresets());
        _selectedPreset = Presets.FirstOrDefault();
        OutputOptions = new ObservableCollection<ToolOutputOptionItem>(CreateOutputOptions(_parameters));
        ResultItems = new ObservableCollection<BlobResultItem>();
        BlobDetails = new ObservableCollection<BlobDetailItem>();
        Tabs =
        [
            new BlobAnalysisTabItem("Home", "主页"),
            new BlobAnalysisTabItem("Result", "结果"),
            new BlobAnalysisTabItem("Output", "输出项"),
            new BlobAnalysisTabItem("Display", "显示")
        ];

        SelectTabCommand = new DelegateCommand<BlobAnalysisTabItem>(SelectTab);
        EditRoiCommand = new DelegateCommand(ArmRoiPlacement, () => !IsMissingInputImage);
        PlaceRoiCommand = new DelegateCommand<Point2D>(PlaceBlobRoi);
        RunToolCommand = new DelegateCommand(async () => await RunToolAsync(), () => !IsBusy && !IsMissingInputImage)
            .ObservesProperty(() => IsBusy);
        RunFlowCommand = new DelegateCommand(async () => await RunFlowAsync(), () => !IsBusy && !IsMissingInputImage)
            .ObservesProperty(() => IsBusy);
        ApplyPresetCommand = new DelegateCommand(ApplyPreset);
        CloseCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, true));
        CancelCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, false));

        SelectTab(Tabs[0]);
        LoadBlobRoiEditor();
        RefreshPreviewOverlays();
        RefreshResultItems();
    }

    public event EventHandler<bool>? CloseRequested;

    public string WindowTitle { get; }

    public ImageFrame? CurrentFrame { get; }

    public bool IsMissingInputImage => CurrentFrame is null;

    public string MissingInputImageText => "未连接输入图像。请把采图工具的输出图像连接到本工具的输入图像。";

    public string InputFrameInfo { get; }

    public ObservableCollection<RoiDefinition> CreatedRois { get; }

    public ObservableCollection<string> RemovedRoiIds { get; }

    public ObservableCollection<RoiEditorItem> EditableRois { get; }

    public ObservableCollection<VisionOverlayItem> PreviewOverlays { get; } = new();

    public ObservableCollection<RoiShapeOptionItem> RoiShapeOptions { get; }

    public ObservableCollection<string> ThresholdModes { get; }

    public ObservableCollection<string> Polarities { get; }

    public ObservableCollection<string> ResultSelections { get; }

    public ObservableCollection<BlobPresetItem> Presets { get; }

    public ObservableCollection<ToolOutputOptionItem> OutputOptions { get; }

    public ObservableCollection<BlobResultItem> ResultItems { get; }

    public ObservableCollection<BlobDetailItem> BlobDetails { get; }

    public ObservableCollection<BlobAnalysisTabItem> Tabs { get; }

    public DelegateCommand<BlobAnalysisTabItem> SelectTabCommand { get; }

    public DelegateCommand EditRoiCommand { get; }

    public DelegateCommand<Point2D> PlaceRoiCommand { get; }

    public DelegateCommand RunToolCommand { get; }

    public DelegateCommand RunFlowCommand { get; }

    public DelegateCommand ApplyPresetCommand { get; }

    public DelegateCommand CloseCommand { get; }

    public DelegateCommand CancelCommand { get; }

    public bool RunFlowRequested { get; private set; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public RoiShapeOptionItem SelectedRoiShape
    {
        get => _selectedRoiShape;
        set => SetProperty(ref _selectedRoiShape, value);
    }

    public RoiEditorItem? SelectedEditableRoi
    {
        get => _selectedEditableRoi;
        set => SetProperty(ref _selectedEditableRoi, value);
    }

    public bool IsRoiPlacementArmed
    {
        get => _isRoiPlacementArmed;
        private set => SetProperty(ref _isRoiPlacementArmed, value);
    }

    public string RoiPlacementHint
    {
        get => _roiPlacementHint;
        private set => SetProperty(ref _roiPlacementHint, value);
    }

    public string SelectedTabKey
    {
        get => _selectedTabKey;
        private set
        {
            if (SetProperty(ref _selectedTabKey, value))
            {
                RaisePropertyChanged(nameof(IsHomeTab));
                RaisePropertyChanged(nameof(IsResultTab));
                RaisePropertyChanged(nameof(IsOutputTab));
                RaisePropertyChanged(nameof(IsDisplayTab));
            }
        }
    }

    public bool IsHomeTab => SelectedTabKey == "Home";

    public bool IsResultTab => SelectedTabKey == "Result";

    public bool IsOutputTab => SelectedTabKey == "Output";

    public bool IsDisplayTab => SelectedTabKey == "Display";

    public string ThresholdMode
    {
        get => _thresholdMode;
        set
        {
            if (SetProperty(ref _thresholdMode, NormalizeThresholdMode(value)))
            {
                RaisePropertyChanged(nameof(ThresholdModeHint));
                RaisePropertyChanged(nameof(PolarityHint));
            }
        }
    }

    public double Threshold
    {
        get => _threshold;
        set => SetProperty(ref _threshold, Clamp(value, 0, 255));
    }

    public double GrayMin
    {
        get => _grayMin;
        set
        {
            if (SetProperty(ref _grayMin, Clamp(value, 0, 255)) && GrayMax < GrayMin)
            {
                GrayMax = GrayMin;
            }

            if (!_isApplyingPreset)
            {
                ThresholdMode = ThresholdModeRangeText;
            }
        }
    }

    public double GrayMax
    {
        get => _grayMax;
        set
        {
            if (SetProperty(ref _grayMax, Clamp(value, GrayMin, 255)) && !_isApplyingPreset)
            {
                ThresholdMode = ThresholdModeRangeText;
            }
        }
    }

    public string Polarity
    {
        get => _polarity;
        set
        {
            if (SetProperty(ref _polarity, NormalizePolarity(value)))
            {
                RaisePropertyChanged(nameof(PolarityHint));
            }
        }
    }

    public double MinArea
    {
        get => _minArea;
        set => SetProperty(ref _minArea, Math.Max(0, value));
    }

    public double MaxArea
    {
        get => _maxArea;
        set => SetProperty(ref _maxArea, Math.Max(MinArea, value));
    }

    public double MinWidth
    {
        get => _minWidth;
        set => SetProperty(ref _minWidth, Math.Max(0, value));
    }

    public double MaxWidth
    {
        get => _maxWidth;
        set => SetProperty(ref _maxWidth, Math.Max(MinWidth, value));
    }

    public double MinHeight
    {
        get => _minHeight;
        set => SetProperty(ref _minHeight, Math.Max(0, value));
    }

    public double MaxHeight
    {
        get => _maxHeight;
        set => SetProperty(ref _maxHeight, Math.Max(MinHeight, value));
    }

    public double MinCircularity
    {
        get => _minCircularity;
        set => SetProperty(ref _minCircularity, Clamp(value, 0, 1));
    }

    public double MaxCircularity
    {
        get => _maxCircularity;
        set => SetProperty(ref _maxCircularity, Clamp(value, MinCircularity, 1));
    }

    public double MinAspectRatio
    {
        get => _minAspectRatio;
        set => SetProperty(ref _minAspectRatio, Math.Max(0, value));
    }

    public double MaxAspectRatio
    {
        get => _maxAspectRatio;
        set => SetProperty(ref _maxAspectRatio, Math.Max(MinAspectRatio, value));
    }

    public int AdaptiveBlockSize
    {
        get => _adaptiveBlockSize;
        set => SetProperty(ref _adaptiveBlockSize, NormalizeAdaptiveBlockSize(value));
    }

    public double AdaptiveC
    {
        get => _adaptiveC;
        set => SetProperty(ref _adaptiveC, Clamp(value, -255, 255));
    }

    public int MinCount
    {
        get => _minCount;
        set => SetProperty(ref _minCount, Math.Max(0, value));
    }

    public int MaxCount
    {
        get => _maxCount;
        set => SetProperty(ref _maxCount, Math.Max(MinCount, value));
    }

    public int MaxResults
    {
        get => _maxResults;
        set => SetProperty(ref _maxResults, Math.Clamp(value, 1, 10_000));
    }

    public string Selection
    {
        get => _selection;
        set
        {
            if (SetProperty(ref _selection, NormalizeSelection(value)))
            {
                RaisePropertyChanged(nameof(SelectionHint));
            }
        }
    }

    public BlobPresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                RaisePropertyChanged(nameof(SelectedPresetDescription));
            }
        }
    }

    public string SelectedPresetDescription => SelectedPreset?.Description ?? "选择一个常用场景后应用参数，也可以继续手动微调。";

    public BlobDetailItem? SelectedBlobDetail
    {
        get => _selectedBlobDetail;
        set
        {
            if (SetProperty(ref _selectedBlobDetail, value))
            {
                RefreshPreviewOverlays();
            }
        }
    }

    public string ThresholdModeHint => NormalizeThresholdModeKey(ThresholdMode) switch
    {
        "Range" => "灰度范围：只把 ROI 内灰度位于下限和上限之间的像素当作目标，这是斑点分析的推荐用法。",
        "Fixed" => "固定阈值：灰度大于或小于阈值的像素会被当作目标，适合光照稳定的场景。",
        "Adaptive" => "自适应阈值：局部自动分割，适合亮度不均，但速度会慢一些。",
        _ => "自动阈值：软件根据当前 ROI 自动分割，通常先用这个做初调。"
    };

    public string PolarityHint => NormalizePolarity(Polarity) == "亮斑"
        ? NormalizeThresholdModeKey(ThresholdMode) == "Range"
            ? "灰度范围模式直接使用上下限，极性只影响切换到单阈值/自动阈值时的找亮找暗方向。"
            : "亮斑：找比背景更亮的目标，例如白点、反光点、亮色颗粒。"
        : NormalizeThresholdModeKey(ThresholdMode) == "Range"
            ? "灰度范围模式直接使用上下限，极性只影响切换到单阈值/自动阈值时的找亮找暗方向。"
            : "暗斑：找比背景更暗的目标，例如黑点、孔洞、油污、缺陷阴影。";

    public string SelectionHint => NormalizeSelectionKey(Selection) switch
    {
        "Smallest" => "结果选择：输出面积最小的斑点，适合找最小瑕疵。",
        "ClosestCenter" => "结果选择：输出最靠近搜索区域中心的斑点，适合固定位置检测。",
        "TopLeft" => "结果选择：按从左到右、从上到下排序，适合多目标编号。",
        _ => "结果选择：输出面积最大的斑点，适合找主目标或最大缺陷。"
    };

    public string FilterHint => "筛选：面积用于过滤大小，圆度越接近 1 越像圆，比例用于过滤过细或过扁的目标。";

    public string AdvancedThresholdHint => "自适应阈值：块大小必须是奇数，数值越大参考的局部范围越宽；修正 C 会从局部均值中扣除，常用 2-10。";

    public string SizeFilterHint => "宽高筛选：在面积之外限制外接矩形尺寸，适合排除细长划痕、边缘噪声或过大的背景块。";

    public string MorphologyHint => "形态学：开运算去小噪点，闭运算补小缺口；数值越大处理越强，建议从 0 或 1 开始。";

    public int MorphOpen
    {
        get => _morphOpen;
        set => SetProperty(ref _morphOpen, Math.Clamp(value, 0, 31));
    }

    public int MorphClose
    {
        get => _morphClose;
        set => SetProperty(ref _morphClose, Math.Clamp(value, 0, 31));
    }

    public bool ShowRoi
    {
        get => _showRoi;
        set
        {
            if (SetProperty(ref _showRoi, value))
            {
                RefreshPreviewOverlays();
            }
        }
    }

    public bool ShowResultLabel
    {
        get => _showResultLabel;
        set
        {
            if (SetProperty(ref _showResultLabel, value))
            {
                RefreshPreviewOverlays();
            }
        }
    }

    public bool ShowCrosshair
    {
        get => _showCrosshair;
        set => SetProperty(ref _showCrosshair, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string CountText
    {
        get => _countText;
        private set => SetProperty(ref _countText, value);
    }

    public string BestAreaText
    {
        get => _bestAreaText;
        private set => SetProperty(ref _bestAreaText, value);
    }

    public string BestCenterText
    {
        get => _bestCenterText;
        private set => SetProperty(ref _bestCenterText, value);
    }

    public string BestCircularityText
    {
        get => _bestCircularityText;
        private set => SetProperty(ref _bestCircularityText, value);
    }

    public async Task<bool> ApplyToAsync(VisionToolItem tool)
    {
        SyncBlobRoi();
        if (!await CaptureRoiReferencePoseIfNeededAsync())
        {
            return false;
        }

        tool.Name = string.IsNullOrWhiteSpace(Name) ? tool.Name : Name.Trim();
        tool.Kind = VisionToolKind.DefectDetect;
        tool.Enabled = Enabled;
        tool.RoiId = _roiId;
        tool.ParametersText = FormatParameters(BuildParameters(includeRoiId: false));
        return await Task.FromResult(true);
    }

    private void SelectTab(BlobAnalysisTabItem? tab)
    {
        if (tab is null)
        {
            return;
        }

        foreach (var candidate in Tabs)
        {
            candidate.IsSelected = ReferenceEquals(candidate, tab);
        }

        SelectedTabKey = tab.Key;
    }

    private void LoadBlobRoiEditor()
    {
        var definition = FindBlobRoi();
        if (definition is null)
        {
            return;
        }

        SetBlobRoiEditor(RoiEditorItem.FromDefinition(definition));
    }

    private RoiDefinition? FindBlobRoi()
    {
        return CreatedRois.FirstOrDefault(roi => string.Equals(roi.Id, _roiId, StringComparison.OrdinalIgnoreCase))
               ?? _availableRois.FirstOrDefault(roi => string.Equals(roi.Id, _roiId, StringComparison.OrdinalIgnoreCase));
    }

    private void ArmRoiPlacement()
    {
        if (CurrentFrame is null)
        {
            StatusText = "没有可用图像";
            return;
        }

        if (_blobRoiEditor is not null)
        {
            ShowBlobRoiEditor();
        }

        IsRoiPlacementArmed = true;
        RoiPlacementHint = _blobRoiEditor is null
            ? $"在图像上单击放置{SelectedRoiShape.Name}"
            : $"单击空白处重画{SelectedRoiShape.Name}，或拖动现有 ROI 调整";
        StatusText = RoiPlacementHint;
    }

    private void PlaceBlobRoi(Point2D point)
    {
        if (!IsRoiPlacementArmed || CurrentFrame is null)
        {
            return;
        }

        QueueRoiRemoval(_blobRoiEditor?.Id);
        var roi = ToolRoiFactory.CreateRoiAt(
            Name,
            VisionToolKind.DefectDetect,
            SelectedRoiShape.Kind,
            CurrentFrame,
            1,
            point) with
        {
            Name = $"{GetOwnedRoiName()} 搜索区域"
        };
        _roiId = roi.Id;
        UpsertCreatedRoi(roi);
        SetBlobRoiEditor(RoiEditorItem.FromDefinition(roi));
        _roiReferenceDirty = true;
        IsRoiPlacementArmed = false;
        RoiPlacementHint = string.Empty;
        StatusText = "斑点搜索区域已放置";
    }

    private void SetBlobRoiEditor(RoiEditorItem editor)
    {
        if (_blobRoiEditor is not null)
        {
            _blobRoiEditor.PropertyChanged -= OnBlobRoiPropertyChanged;
        }

        EditableRois.Clear();
        _blobRoiEditor = editor;
        _blobRoiEditor.PropertyChanged += OnBlobRoiPropertyChanged;
        EditableRois.Add(editor);
        SelectedEditableRoi = editor;
        ClearResultPreview();
    }

    private void ShowBlobRoiEditor()
    {
        if (_blobRoiEditor is null)
        {
            return;
        }

        if (!EditableRois.Contains(_blobRoiEditor))
        {
            EditableRois.Add(_blobRoiEditor);
        }

        SelectedEditableRoi = _blobRoiEditor;
    }

    private void SyncBlobRoi()
    {
        if (_blobRoiEditor is null)
        {
            return;
        }

        var definition = _blobRoiEditor.ToDefinition() with
        {
            Name = string.IsNullOrWhiteSpace(_blobRoiEditor.Name)
                ? $"{GetOwnedRoiName()} 搜索区域"
                : _blobRoiEditor.Name
        };
        _roiId = definition.Id;
        UpsertCreatedRoi(definition);
    }

    private void UpsertCreatedRoi(RoiDefinition definition)
    {
        var existing = CreatedRois.FirstOrDefault(roi => string.Equals(roi.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            CreatedRois.Add(definition);
            return;
        }

        CreatedRois[CreatedRois.IndexOf(existing)] = definition;
    }

    private void QueueRoiRemoval(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (!RemovedRoiIds.Any(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase)))
        {
            RemovedRoiIds.Add(id);
        }

        var created = CreatedRois.FirstOrDefault(roi => string.Equals(roi.Id, id, StringComparison.OrdinalIgnoreCase));
        if (created is not null)
        {
            CreatedRois.Remove(created);
        }
    }

    private async Task RunToolAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (CurrentFrame is null)
        {
            StatusText = "没有可用图像";
            return;
        }

        IsBusy = true;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            SyncBlobRoi();
            if (!await CaptureRoiReferencePoseIfNeededAsync())
            {
                return;
            }

            var definition = new VisionToolDefinition
            {
                Id = _toolId,
                Name = Name,
                Kind = VisionToolKind.DefectDetect,
                Enabled = Enabled,
                Parameters = BuildParameters(includeRoiId: true)
            };
            var recipe = BuildPreviewRecipe(definition);
            var pipelineResult = await _pipeline.ExecuteAsync(recipe, CurrentFrame);
            var result = pipelineResult.ToolResults.LastOrDefault(item => string.Equals(item.ToolId, _toolId, StringComparison.OrdinalIgnoreCase));
            if (result is null)
            {
                StatusText = "预览运行失败：流程没有返回斑点分析结果";
                return;
            }

            stopwatch.Stop();
            DurationText = $"{stopwatch.ElapsedMilliseconds}ms";
            StatusText = result.Message;
            _previewResult = result;
            UpdateResultSummary(result);
            RefreshPreviewOverlays();
            RefreshResultItems();
            SelectTab(Tabs.FirstOrDefault(tab => tab.Key == "Result"));
            _log.Info("VisionDebug", $"{Name} {result.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunFlowAsync()
    {
        await RunToolAsync();
        RunFlowRequested = true;
        CloseRequested?.Invoke(this, true);
    }

    private Recipe BuildPreviewRecipe(VisionToolDefinition definition)
    {
        var previewRecipe = _previewRecipe ?? new Recipe
        {
            Tools = [definition],
            Rois = CreatedRois.ToArray()
        };
        var activeFlow = previewRecipe.GetActiveFlow();
        var tools = activeFlow.Tools.ToList();
        var toolIndex = tools.FindIndex(item => string.Equals(item.Id, _toolId, StringComparison.OrdinalIgnoreCase));
        if (toolIndex >= 0)
        {
            tools[toolIndex] = definition;
        }
        else
        {
            tools.Add(definition);
        }

        var rois = activeFlow.Rois
            .Where(item => !CreatedRois.Any(created => string.Equals(created.Id, item.Id, StringComparison.OrdinalIgnoreCase)))
            .Concat(CreatedRois)
            .ToArray();
        return previewRecipe.WithActiveFlow(activeFlow with
        {
            Rois = rois,
            Tools = tools.ToArray(),
            UpdatedAt = DateTimeOffset.Now
        });
    }

    private async Task<bool> CaptureRoiReferencePoseIfNeededAsync()
    {
        if (!_roiReferenceDirty)
        {
            return true;
        }

        var sourceToolId = GetPositionInputSourceToolId();
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            RemoveRoiReferencePose();
            _roiReferenceDirty = false;
            return true;
        }

        var currentPose = await TryGetCurrentPositionInputPoseAsync();
        if (currentPose is null)
        {
            StatusText = "模板位置未就绪。请先运行模板定位，再保存或预览斑点 ROI。";
            return false;
        }

        SetRoiReferencePose(currentPose);
        _roiReferenceDirty = false;
        return true;
    }

    private async Task<Pose2D?> TryGetCurrentPositionInputPoseAsync()
    {
        if (CurrentFrame is null)
        {
            return null;
        }

        var sourceToolId = GetPositionInputSourceToolId();
        var recipe = BuildPositionSourcePreviewRecipe(sourceToolId);
        if (string.IsNullOrWhiteSpace(sourceToolId) || recipe is null)
        {
            return null;
        }

        var pipelineResult = await _pipeline.ExecuteAsync(recipe, CurrentFrame);
        var sourceResult = pipelineResult.ToolResults.LastOrDefault(result =>
            string.Equals(result.ToolId, sourceToolId, StringComparison.OrdinalIgnoreCase));
        if (sourceResult?.Outcome != InspectionOutcome.Ok ||
            !TryGetDouble(sourceResult.Data, "x", out var x) ||
            !TryGetDouble(sourceResult.Data, "y", out var y))
        {
            return null;
        }

        TryGetDouble(sourceResult.Data, "angle", out var angle);
        return new Pose2D(x, y, angle);
    }

    private Recipe? BuildPositionSourcePreviewRecipe(string sourceToolId)
    {
        if (_previewRecipe is null || string.IsNullOrWhiteSpace(sourceToolId))
        {
            return null;
        }

        var activeFlow = _previewRecipe.GetActiveFlow();
        var sourceIndex = activeFlow.Tools
            .ToList()
            .FindIndex(tool => string.Equals(tool.Id, sourceToolId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
        {
            return null;
        }

        return _previewRecipe.WithActiveFlow(activeFlow with
        {
            Tools = activeFlow.Tools.Take(sourceIndex + 1).ToArray(),
            UpdatedAt = DateTimeOffset.Now
        });
    }

    private string GetPositionInputSourceToolId()
    {
        return _parameters.GetValueOrDefault("input:PositionInput:toolId") ?? string.Empty;
    }

    private void SetRoiReferencePose(Pose2D pose)
    {
        _parameters["roiReferencePoseX"] = pose.X.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["roiReferencePoseY"] = pose.Y.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["roiReferencePoseAngle"] = pose.Angle.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["roiReferencePoseToolId"] = GetPositionInputSourceToolId();
    }

    private void RemoveRoiReferencePose()
    {
        _parameters.Remove("roiReferencePoseX");
        _parameters.Remove("roiReferencePoseY");
        _parameters.Remove("roiReferencePoseAngle");
        _parameters.Remove("roiReferencePoseToolId");
    }

    private static bool HasValidRoiReferencePose(IReadOnlyDictionary<string, string> parameters)
    {
        if (!TryGetRoiReferencePose(parameters, out _))
        {
            return false;
        }

        var sourceToolId = parameters.GetValueOrDefault("input:PositionInput:toolId") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return false;
        }

        var referenceToolId = parameters.GetValueOrDefault("roiReferencePoseToolId") ?? string.Empty;
        return string.IsNullOrWhiteSpace(referenceToolId) ||
               string.Equals(referenceToolId, sourceToolId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetRoiReferencePose(IReadOnlyDictionary<string, string> parameters, out Pose2D pose)
    {
        pose = new Pose2D(0, 0, 0);
        if (!TryGetDouble(parameters, "roiReferencePoseX", out var x) ||
            !TryGetDouble(parameters, "roiReferencePoseY", out var y))
        {
            return false;
        }

        TryGetDouble(parameters, "roiReferencePoseAngle", out var angle);
        pose = new Pose2D(x, y, angle);
        return true;
    }

    private Dictionary<string, string> BuildParameters(bool includeRoiId)
    {
        var parameters = new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["thresholdMode"] = NormalizeThresholdModeKey(ThresholdMode),
            ["threshold"] = Threshold.ToInvariant("0.###"),
            ["grayMin"] = GrayMin.ToInvariant("0.###"),
            ["grayMax"] = GrayMax.ToInvariant("0.###"),
            ["polarity"] = Polarity,
            ["minArea"] = MinArea.ToInvariant("0.###"),
            ["maxArea"] = MaxArea.ToInvariant("0.###"),
            ["minWidth"] = MinWidth.ToInvariant("0.###"),
            ["maxWidth"] = MaxWidth.ToInvariant("0.###"),
            ["minHeight"] = MinHeight.ToInvariant("0.###"),
            ["maxHeight"] = MaxHeight.ToInvariant("0.###"),
            ["minCircularity"] = MinCircularity.ToInvariant("0.###"),
            ["maxCircularity"] = MaxCircularity.ToInvariant("0.###"),
            ["minAspectRatio"] = MinAspectRatio.ToInvariant("0.###"),
            ["maxAspectRatio"] = MaxAspectRatio.ToInvariant("0.###"),
            ["adaptiveBlockSize"] = AdaptiveBlockSize.ToString(CultureInfo.InvariantCulture),
            ["adaptiveC"] = AdaptiveC.ToInvariant("0.###"),
            ["minCount"] = MinCount.ToString(CultureInfo.InvariantCulture),
            ["maxCount"] = MaxCount.ToString(CultureInfo.InvariantCulture),
            ["maxResults"] = MaxResults.ToString(CultureInfo.InvariantCulture),
            ["selection"] = NormalizeSelectionKey(Selection),
            ["morphOpen"] = MorphOpen.ToString(CultureInfo.InvariantCulture),
            ["morphClose"] = MorphClose.ToString(CultureInfo.InvariantCulture),
            ["showRoi"] = ShowRoi.ToString(),
            ["showResultLabel"] = ShowResultLabel.ToString(),
            ["showCrosshair"] = ShowCrosshair.ToString(),
            ["enabledOutputs"] = FormatEnabledOutputKeys()
        };

        if (includeRoiId && !string.IsNullOrWhiteSpace(_roiId))
        {
            parameters["roiId"] = _roiId;
        }
        else
        {
            parameters.Remove("roiId");
        }

        return parameters;
    }

    private void ApplyPreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        _isApplyingPreset = true;
        try
        {
            ThresholdMode = SelectedPreset.ThresholdMode;
            Threshold = SelectedPreset.Threshold;
            GrayMin = SelectedPreset.GrayMin;
            GrayMax = SelectedPreset.GrayMax;
            Polarity = SelectedPreset.Polarity;
            Selection = SelectedPreset.Selection;
            MinArea = SelectedPreset.MinArea;
            MaxArea = SelectedPreset.MaxArea;
            MinWidth = 0;
            MaxWidth = 1_000_000;
            MinHeight = 0;
            MaxHeight = 1_000_000;
            MinCircularity = SelectedPreset.MinCircularity;
            MaxCircularity = SelectedPreset.MaxCircularity;
            MinAspectRatio = SelectedPreset.MinAspectRatio;
            MaxAspectRatio = SelectedPreset.MaxAspectRatio;
            AdaptiveBlockSize = 41;
            AdaptiveC = 5;
            MinCount = SelectedPreset.MinCount;
            MaxCount = SelectedPreset.MaxCount;
            MaxResults = SelectedPreset.MaxResults;
            MorphOpen = SelectedPreset.MorphOpen;
            MorphClose = SelectedPreset.MorphClose;
        }
        finally
        {
            _isApplyingPreset = false;
        }

        StatusText = $"已应用 {SelectedPreset.Name} 参数";
        ClearResultPreview();
    }

    private void UpdateResultSummary(ToolResult result)
    {
        CountText = result.Data.GetValueOrDefault("count", "-");
        BestAreaText = result.Data.GetValueOrDefault("area", "-");
        BestCircularityText = result.Data.GetValueOrDefault("circularity", "-");
        var x = result.Data.GetValueOrDefault("x", "-");
        var y = result.Data.GetValueOrDefault("y", "-");
        BestCenterText = $"X:{x} Y:{y}";
    }

    private void RefreshResultItems()
    {
        var selectedIndex = SelectedBlobDetail?.Index;
        ResultItems.Clear();
        BlobDetails.Clear();
        if (_previewResult is null)
        {
            ResultItems.Add(new BlobResultItem("状态", StatusText));
            ResultItems.Add(new BlobResultItem("耗时", DurationText));
            SelectedBlobDetail = null;
            return;
        }

        ResultItems.Add(new BlobResultItem("状态", _previewResult.Outcome.ToString().ToUpperInvariant()));
        ResultItems.Add(new BlobResultItem("数量", _previewResult.Data.GetValueOrDefault("count", "-")));
        ResultItems.Add(new BlobResultItem("中心", BestCenterText));
        ResultItems.Add(new BlobResultItem("面积", BestAreaText));
        ResultItems.Add(new BlobResultItem("圆度", BestCircularityText));
        ResultItems.Add(new BlobResultItem("模式", _previewResult.Data.GetValueOrDefault("criteriaMode", "-")));
        ResultItems.Add(new BlobResultItem(
            "灰度范围",
            $"{_previewResult.Data.GetValueOrDefault("grayMin", "-")}-{_previewResult.Data.GetValueOrDefault("grayMax", "-")}"));
        ResultItems.Add(new BlobResultItem("面积标准", _previewResult.Data.GetValueOrDefault("criteriaArea", "-")));
        ResultItems.Add(new BlobResultItem("宽度标准", _previewResult.Data.GetValueOrDefault("criteriaWidth", "-")));
        ResultItems.Add(new BlobResultItem("高度标准", _previewResult.Data.GetValueOrDefault("criteriaHeight", "-")));
        ResultItems.Add(new BlobResultItem("圆度标准", _previewResult.Data.GetValueOrDefault("criteriaCircularity", "-")));
        ResultItems.Add(new BlobResultItem("比例标准", _previewResult.Data.GetValueOrDefault("criteriaAspectRatio", "-")));
        ResultItems.Add(new BlobResultItem("数量标准", _previewResult.Data.GetValueOrDefault("criteriaCount", "-")));
        ResultItems.Add(new BlobResultItem("自适应参数", _previewResult.Data.GetValueOrDefault("criteriaAdaptive", "-")));
        ResultItems.Add(new BlobResultItem("外接圆", FormatCircleText(_previewResult.Data)));
        ResultItems.Add(new BlobResultItem("外接矩形", FormatRectText(_previewResult.Data)));
        ResultItems.Add(new BlobResultItem("前景像素", _previewResult.Data.GetValueOrDefault("foregroundPixels", "-")));
        ResultItems.Add(new BlobResultItem("耗时", DurationText));

        var index = 0;
        foreach (var blob in BlobAnalysisResultCodec.ParseBlobs(_previewResult.Data.GetValueOrDefault("blobs")).Take(200))
        {
            index++;
            BlobDetails.Add(new BlobDetailItem(
                index.ToString(CultureInfo.InvariantCulture),
                $"X:{blob.X:0.#} Y:{blob.Y:0.#}",
                blob.Area.ToString("0.#", CultureInfo.InvariantCulture),
                blob.Circularity.ToString("0.###", CultureInfo.InvariantCulture),
                $"{blob.Width:0.#} x {blob.Height:0.#}",
                $"{blob.Left:0.#},{blob.Top:0.#},{blob.Width:0.#},{blob.Height:0.#}",
                $"X:{blob.CircleX:0.#} Y:{blob.CircleY:0.#} R:{blob.CircleRadius:0.#}",
                blob.AspectRatio.ToString("0.###", CultureInfo.InvariantCulture),
                blob.Perimeter.ToString("0.#", CultureInfo.InvariantCulture),
                blob.X,
                blob.Y,
                blob.Left,
                blob.Top,
                blob.Width,
                blob.Height));
        }

        SelectedBlobDetail = string.IsNullOrWhiteSpace(selectedIndex)
            ? null
            : BlobDetails.FirstOrDefault(item => string.Equals(item.Index, selectedIndex, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatCircleText(IReadOnlyDictionary<string, string> data)
    {
        return $"X:{data.GetValueOrDefault("circleX", "-")} Y:{data.GetValueOrDefault("circleY", "-")} R:{data.GetValueOrDefault("circleRadius", "-")}";
    }

    private static string FormatRectText(IReadOnlyDictionary<string, string> data)
    {
        return $"X:{data.GetValueOrDefault("left", "-")} Y:{data.GetValueOrDefault("top", "-")} W:{data.GetValueOrDefault("width", "-")} H:{data.GetValueOrDefault("height", "-")}";
    }

    private void RefreshPreviewOverlays()
    {
        PreviewOverlays.Clear();
        if (ShowRoi && CreatePreviewSearchRoiOverlay() is { } roiOverlay)
        {
            PreviewOverlays.Add(roiOverlay);
        }

        if (_previewResult is null)
        {
            return;
        }

        var state = _previewResult.Outcome == InspectionOutcome.Ok ? VisionOverlayState.Ok : VisionOverlayState.Ng;
        var centers = new List<Point2D>();
        var selectedIndex = GetSelectedBlobIndex();
        BlobAnalysisBlob? selectedBlob = null;
        var index = 0;
        foreach (var blob in BlobAnalysisResultCodec.ParseBlobs(_previewResult.Data.GetValueOrDefault("blobs")))
        {
            index++;
            if (blob.Contour.Count >= 3)
            {
                PreviewOverlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Polyline,
                    State = state,
                    Label = ShowResultLabel ? $"#{index}" : string.Empty,
                    Points = blob.Contour
                });
            }
            else
            {
                PreviewOverlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Rectangle,
                    State = state,
                    Label = ShowResultLabel ? $"#{index}" : string.Empty,
                    X = blob.Left,
                    Y = blob.Top,
                    Width = blob.Width,
                    Height = blob.Height
                });
            }

            centers.Add(new Point2D(blob.X, blob.Y));
            if (selectedIndex == index)
            {
                selectedBlob = blob;
            }
        }

        if (centers.Count > 0)
        {
            PreviewOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.XMarker,
                State = state,
                Points = centers
            });
        }

        if (selectedBlob is { } highlighted)
        {
            if (highlighted.Contour.Count >= 3)
            {
                PreviewOverlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Polyline,
                    State = VisionOverlayState.Warning,
                    Points = highlighted.Contour
                });
            }
            else
            {
                var margin = Math.Clamp(Math.Max(highlighted.Width, highlighted.Height) * 0.08, 2, 10);
                PreviewOverlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Rectangle,
                    State = VisionOverlayState.Warning,
                    X = highlighted.Left - margin,
                    Y = highlighted.Top - margin,
                    Width = highlighted.Width + margin * 2,
                    Height = highlighted.Height + margin * 2
                });
            }

            PreviewOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Cross,
                State = VisionOverlayState.Warning,
                Label = selectedIndex > 0 ? $"#{selectedIndex}" : string.Empty,
                X = highlighted.X,
                Y = highlighted.Y
            });
        }
    }

    private VisionOverlayItem? CreatePreviewSearchRoiOverlay()
    {
        if (_previewResult is not null &&
            TryCreateSearchRoiFromResult(_previewResult.Data, out var runtimeRoi))
        {
            return VisionOverlayItem.FromRoi(runtimeRoi, VisionOverlayState.Neutral) with { Label = string.Empty };
        }

        return _blobRoiEditor is null
            ? null
            : VisionOverlayItem.FromRoi(_blobRoiEditor.ToDefinition(), VisionOverlayState.Neutral) with { Label = string.Empty };
    }

    private static bool TryCreateSearchRoiFromResult(IReadOnlyDictionary<string, string> data, out RoiDefinition roi)
    {
        roi = new RoiDefinition();
        if (!data.TryGetValue("searchRoiShape", out var shapeText) ||
            !Enum.TryParse<RoiShapeKind>(shapeText, true, out var shape) ||
            !TryGetDouble(data, "searchRoiX", out var x) ||
            !TryGetDouble(data, "searchRoiY", out var y))
        {
            return false;
        }

        switch (shape)
        {
            case RoiShapeKind.Circle:
                if (!TryGetDouble(data, "searchRoiRadius", out var radius))
                {
                    return false;
                }

                roi = new RoiDefinition
                {
                    Name = "Runtime ROI",
                    Shape = RoiShapeKind.Circle,
                    X = x,
                    Y = y,
                    Radius = radius
                };
                return true;

            case RoiShapeKind.RotatedRectangle:
                if (!TryGetDouble(data, "searchRoiWidth", out var rotatedWidth) ||
                    !TryGetDouble(data, "searchRoiHeight", out var rotatedHeight))
                {
                    return false;
                }

                TryGetDouble(data, "searchRoiAngle", out var angle);
                roi = new RoiDefinition
                {
                    Name = "Runtime ROI",
                    Shape = RoiShapeKind.RotatedRectangle,
                    X = x,
                    Y = y,
                    Width = rotatedWidth,
                    Height = rotatedHeight,
                    Angle = angle
                };
                return true;

            default:
                if (!TryGetDouble(data, "searchRoiWidth", out var width) ||
                    !TryGetDouble(data, "searchRoiHeight", out var height))
                {
                    return false;
                }

                roi = new RoiDefinition
                {
                    Name = "Runtime ROI",
                    Shape = RoiShapeKind.Rectangle,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height
                };
                return true;
        }
    }

    private void ClearResultPreview()
    {
        _previewResult = null;
        CountText = "-";
        BestAreaText = "-";
        BestCenterText = "-";
        BestCircularityText = "-";
        RefreshPreviewOverlays();
        RefreshResultItems();
    }

    private void OnBlobRoiPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RoiEditorItem.X)
            or nameof(RoiEditorItem.Y)
            or nameof(RoiEditorItem.Width)
            or nameof(RoiEditorItem.Height)
            or nameof(RoiEditorItem.Angle)
            or nameof(RoiEditorItem.Radius)
            or nameof(RoiEditorItem.Shape)
            or nameof(RoiEditorItem.Points)
            or nameof(RoiEditorItem.Geometry))
        {
            _roiReferenceDirty = true;
            ClearResultPreview();
        }
    }

    private static IEnumerable<ToolOutputOptionItem> CreateOutputOptions(IReadOnlyDictionary<string, string> parameters)
    {
        var enabled = ParseEnabledOutputKeys(parameters.GetValueOrDefault("enabledOutputs"));
        return VisionToolCatalog.GetOutputPorts(VisionToolKind.DefectDetect)
            .Select(port => new ToolOutputOptionItem(port.Key, port.Name, port.DataType, enabled.Contains(port.Key)))
            .ToArray();
    }

    private static IEnumerable<BlobPresetItem> CreatePresets()
    {
        return
        [
            new BlobPresetItem(
                "gray-range",
                "灰度范围 / 上下限",
                "按 ROI 内灰度下限和上限直接分割目标。适合先量出目标灰度范围，再稳定检测符合条件的斑点。",
                ThresholdModeRangeText,
                128,
                0,
                80,
                "暗斑",
                SelectionLargestText,
                30,
                1000000,
                0,
                1,
                0,
                1000000,
                1,
                1000000,
                256,
                1,
                0),
            new BlobPresetItem(
                "dark-dots",
                "黑色圆点 / 孔洞",
                "适合检测白底上的黑色圆点、孔洞、暗色缺陷。默认找 0-95 的暗灰度目标，再用面积和圆度过滤误检。",
                ThresholdModeRangeText,
                128,
                0,
                95,
                "暗斑",
                SelectionClosestCenterText,
                30,
                5000,
                0.45,
                1,
                0,
                8,
                1,
                1000000,
                256,
                1,
                0),
            new BlobPresetItem(
                "bright-particles",
                "亮色颗粒 / 反光点",
                "适合检测暗底上的亮点、金属反光、白色颗粒。默认找 180-255 的高灰度目标。",
                ThresholdModeRangeText,
                128,
                180,
                255,
                "亮斑",
                SelectionLargestText,
                10,
                10000,
                0.2,
                1,
                0,
                20,
                1,
                1000000,
                256,
                1,
                0),
            new BlobPresetItem(
                "surface-defect",
                "一般缺陷 / 脏污",
                "适合找面积较大的污点、划痕块、异物。默认找偏暗区域，主要靠面积和数量判断。",
                ThresholdModeRangeText,
                128,
                0,
                140,
                "暗斑",
                SelectionLargestText,
                50,
                1000000,
                0,
                1,
                0,
                1000000,
                1,
                1000000,
                128,
                1,
                1),
            new BlobPresetItem(
                "manual-stable",
                "固定光源 / 手动阈值",
                "适合相机、光源、背景都很稳定的产线。手动改阈值后结果更可控。",
                ThresholdModeFixedText,
                128,
                0,
                80,
                "暗斑",
                SelectionLargestText,
                30,
                1000000,
                0,
                1,
                0,
                1000000,
                1,
                1000000,
                128,
                0,
                0)
        ];
    }

    private static HashSet<string> ParseEnabledOutputKeys(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return VisionToolCatalog.GetDefaultOutputKeys(VisionToolKind.DefectDetect);
        }

        var keys = text
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return keys.Count == 0 ? VisionToolCatalog.GetDefaultOutputKeys(VisionToolKind.DefectDetect) : keys;
    }

    private string FormatEnabledOutputKeys()
    {
        var keys = OutputOptions
            .Where(option => option.IsEnabled)
            .Select(option => option.Key)
            .ToArray();
        return keys.Length == 0 ? "ResultOutput" : string.Join(",", keys);
    }

    private static Dictionary<string, string> ParseParameters(string text)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in text.Split(["\r\n", "\n", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = segment[..index].Trim();
            var value = index == segment.Length - 1 ? string.Empty : segment[(index + 1)..].Trim();
            items[key] = value;
        }

        foreach (var item in VisionToolCatalog.GetDefaultParameters(VisionToolKind.DefectDetect))
        {
            items.TryAdd(item.Key, item.Value);
        }

        return items;
    }

    private static string FormatParameters(IReadOnlyDictionary<string, string> parameters)
    {
        return string.Join("; ", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
    }

    private string GetOwnedRoiName()
    {
        return string.IsNullOrWhiteSpace(Name) ? "斑点分析" : Name.Trim();
    }

    private static string GetString(IReadOnlyDictionary<string, string> parameters, string key, string fallback)
    {
        return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> parameters, string key, double fallback)
    {
        return parameters.TryGetValue(key, out var value) &&
               TryParseFlexibleDouble(value, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> parameters, string key, out double value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var text) && TryParseFlexibleDouble(text, out value);
    }

    private static int GetInt(IReadOnlyDictionary<string, string> parameters, string key, int fallback)
    {
        return parameters.TryGetValue(key, out var value) &&
               int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        return parameters.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Clamp(double.IsNaN(value) || double.IsInfinity(value) ? min : value, min, max);
    }

    private static int NormalizeAdaptiveBlockSize(int value)
    {
        var normalized = Math.Clamp(value, 3, 501);
        return normalized % 2 == 0 ? Math.Min(501, normalized + 1) : normalized;
    }

    private int GetSelectedBlobIndex()
    {
        return SelectedBlobDetail is not null &&
               int.TryParse(SelectedBlobDetail.Index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index
            : -1;
    }

    private static bool TryParseFlexibleDouble(string? text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(text) && text.Contains(','))
        {
            return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        value = 0;
        return false;
    }

    private static string NormalizeThresholdMode(string? value)
    {
        return NormalizeThresholdModeKey(value) switch
        {
            "Range" => ThresholdModeRangeText,
            "Fixed" => ThresholdModeFixedText,
            "Adaptive" => ThresholdModeAdaptiveText,
            _ => ThresholdModeOtsuText
        };
    }

    private static string NormalizeThresholdModeKey(string? value)
    {
        return value?.Trim() switch
        {
            string text when text.Contains("Range", StringComparison.OrdinalIgnoreCase) || text.Contains("灰度范围", StringComparison.OrdinalIgnoreCase) || text.Contains("上下限", StringComparison.OrdinalIgnoreCase) => "Range",
            string text when text.Contains("Fixed", StringComparison.OrdinalIgnoreCase) || text.Contains("固定", StringComparison.OrdinalIgnoreCase) => "Fixed",
            string text when text.Contains("Adaptive", StringComparison.OrdinalIgnoreCase) || text.Contains("自适应", StringComparison.OrdinalIgnoreCase) => "Adaptive",
            _ => "Otsu"
        };
    }

    private static string NormalizePolarity(string? value)
    {
        return value?.Trim() switch
        {
            "Bright" or "bright" or "亮斑" or "亮目标" => "亮斑",
            _ => "暗斑"
        };
    }

    private static string NormalizeSelection(string? value)
    {
        return NormalizeSelectionKey(value) switch
        {
            "Smallest" => SelectionSmallestText,
            "ClosestCenter" => SelectionClosestCenterText,
            "TopLeft" => SelectionTopLeftText,
            _ => SelectionLargestText
        };
    }

    private static string NormalizeSelectionKey(string? value)
    {
        return value?.Trim() switch
        {
            string text when text.Contains("Smallest", StringComparison.OrdinalIgnoreCase) || text.Contains("最小", StringComparison.OrdinalIgnoreCase) => "Smallest",
            string text when text.Contains("ClosestCenter", StringComparison.OrdinalIgnoreCase) || text.Contains("中心", StringComparison.OrdinalIgnoreCase) => "ClosestCenter",
            string text when text.Contains("TopLeft", StringComparison.OrdinalIgnoreCase) || text.Contains("左上", StringComparison.OrdinalIgnoreCase) => "TopLeft",
            _ => "Largest"
        };
    }
}

public sealed class BlobAnalysisTabItem : BindableBase
{
    private bool _isSelected;

    public BlobAnalysisTabItem(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }

    public string Title { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed record BlobResultItem(string Name, string Value);

public sealed record BlobDetailItem(
    string Index,
    string Center,
    string Area,
    string Circularity,
    string Size,
    string Rect,
    string Circle,
    string AspectRatio,
    string Perimeter,
    double X,
    double Y,
    double Left,
    double Top,
    double Width,
    double Height);

public sealed record BlobPresetItem(
    string Key,
    string Name,
    string Description,
    string ThresholdMode,
    double Threshold,
    double GrayMin,
    double GrayMax,
    string Polarity,
    string Selection,
    double MinArea,
    double MaxArea,
    double MinCircularity,
    double MaxCircularity,
    double MinAspectRatio,
    double MaxAspectRatio,
    int MinCount,
    int MaxCount,
    int MaxResults,
    int MorphOpen,
    int MorphClose);

internal static class BlobAnalysisNumberExtensions
{
    public static string ToInvariant(this double value, string format)
    {
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}
