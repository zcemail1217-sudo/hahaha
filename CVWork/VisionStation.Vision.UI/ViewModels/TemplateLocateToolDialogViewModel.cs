using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VisionStation.Vision.UI.Models;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Vision.UI.Services;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;

namespace VisionStation.Vision.UI.ViewModels;

public sealed class TemplateLocateToolDialogViewModel : BindableBase
{
    private const string OpenCvMatchModeStateKey = "opencv.matchMode";
    private const string OpenCvMultiMatchModeStateKey = "opencv.multiMatchMode";

    private readonly IAppLogService _log;
    private readonly RuntimePaths _paths;
    private readonly IReadOnlyList<RoiDefinition> _availableRois;
    private readonly ITemplateMatchingService _matchingService;
    private Dictionary<string, string> _parameters;
    private readonly Recipe? _previewRecipe;
    private readonly IVisionPipeline? _pipeline;
    private readonly string _toolId;
    private readonly VisionToolKind _toolKind;
    private bool _enabled;
    private string _name;
    private string _roiId;
    private RoiShapeOptionItem _selectedRoiShape;
    private RoiShapeOptionItem _selectedTemplateRoiShape;
    private RoiEditorItem? _searchRoiEditor;
    private RoiEditorItem? _templateRoiEditor;
    private readonly List<RoiEditorItem> _templateMaskEditors = new();
    private RoiEditorItem? _selectedEditableRoi;
    private RoiPlacementTarget _pendingRoiPlacement = RoiPlacementTarget.None;
    private string _roiPlacementHint = string.Empty;
    private double _minScore;
    private int _pyramidLevels;
    private double _angleStart;
    private double _angleExtent;
    private string _polarity;
    private string _selectedMatchMode = "Shape";
    private string _selectedTabKey = "Template";
    private string _selectedTemplateMode = "Add";
    private string _selectedTemplateShape = "矩形";
    private double _contrast;
    private bool _autoContrast;
    private int _matchCount;
    private bool _showTemplateRegion;
    private bool _showSearchRegion;
    private bool _showCrosshair;
    private bool _useSubPixel;
    private string _templatePreviewImagePng = string.Empty;
    private string _templatePreviewEdgeOverlayPng = string.Empty;
    private bool _hasMatchResult;
    private double _matchX;
    private double _matchY;
    private double _matchAngle;
    private double _matchScale = 1;
    private TemplateMatchResult? _lastTemplateMatch;
    private IReadOnlyList<MultiTargetMatchCandidate> _multiMatches = Array.Empty<MultiTargetMatchCandidate>();
    private readonly ObservableCollection<MultiTargetMatchPointItem> _multiTargetResultPoints = new();
    private readonly Dictionary<MultiTargetMatchPointItem, MultiTargetMatchCandidate> _multiTargetCandidates =
        new(ReferenceEqualityComparer.Instance);
    private MultiTargetMatchPointItem? _selectedMultiTargetResultPoint;
    private RoiDefinition? _mappedSearchRoi;
    private bool _roiReferenceDirty;
    private VisionOverlayState _matchState = VisionOverlayState.Neutral;
    private bool _isBusy;
    private string _scoreText = "-";
    private string _poseText = "-";
    private string _durationText = "0ms";
    private string _statusText = "等待运行";
    private TemplateMatchingEngine _selectedEngine;
    private TemplateMatchingPreset? _selectedPreset;
    private bool _isAdvancedParametersExpanded;
    private bool _requiresRelearn;
    private bool _applyingPreset;
    private bool _halconLearnedSuccessfullyInSession;
    private bool _rejectLoadedHalconMode;
    private string _openCvMatchModeState = "Shape";
    private string _openCvMultiMatchModeState = "Shape";
    private string _halconAngleStartDeg = string.Empty;
    private string _halconAngleExtentDeg = string.Empty;
    private string _halconScaleMin = string.Empty;
    private string _halconScaleMax = string.Empty;
    private string _halconCandidateMinScore = string.Empty;
    private string _halconOuterCoverageMin = string.Empty;
    private string _halconInnerCoverageMin = string.Empty;
    private string _halconEdgeTolerancePx = string.Empty;
    private string _halconPolarityAgreementMin = string.Empty;
    private string _halconCandidateMaxOverlap = string.Empty;
    private string _halconMaxOverlap = string.Empty;
    private string _halconGreediness = string.Empty;
    private string _halconSubPixel = string.Empty;
    private string _halconNumLevels = string.Empty;
    private string _halconOperatorTimeoutMs = string.Empty;
    private string _halconCandidateLimit = string.Empty;
    private string _halconExpectedCount = string.Empty;

    public TemplateLocateToolDialogViewModel(
        VisionToolItem tool,
        IReadOnlyList<RoiChoiceItem> roiChoices,
        IReadOnlyList<RoiDefinition> rois,
        string flowName,
        ImageFrame? currentFrame,
        RuntimePaths paths,
        IAppLogService log,
        Recipe? previewRecipe,
        IVisionPipeline? pipeline,
        ITemplateMatchingService matchingService)
    {
        _log = log;
        _paths = paths;
        _matchingService = matchingService ?? throw new ArgumentNullException(nameof(matchingService));
        _previewRecipe = previewRecipe;
        _pipeline = pipeline;
        _enabled = tool.Enabled;
        _availableRois = rois;
        _parameters = ParseParameters(tool.ParametersText);
        _toolId = tool.Id;
        _toolKind = tool.Kind == VisionToolKind.MultiTargetMatch ? VisionToolKind.MultiTargetMatch : VisionToolKind.TemplateLocate;
        var parameters = _parameters;
        _selectedEngine = ResolveSelectedEngine(parameters);
        EngineOptions = new ReadOnlyCollection<TemplateMatchingEngine>(
            [TemplateMatchingEngine.ManagedNcc, TemplateMatchingEngine.OpenCv, TemplateMatchingEngine.Halcon]);
        PresetOptions = new ReadOnlyCollection<TemplateMatchingPreset>(
            [TemplateMatchingPreset.Strict, TemplateMatchingPreset.Balanced, TemplateMatchingPreset.HighRecall]);
        LoadHalconEditorValues(parameters);
        _selectedPreset = DetectPreset();
        var positionSourceToolId = GetPositionInputSourceToolId();
        _roiReferenceDirty = _toolKind == VisionToolKind.MultiTargetMatch &&
                             ReadRoiReferencePose(parameters, positionSourceToolId).Status != PoseReadStatus.Success;

        _name = tool.Name;
        _roiId = tool.RoiId;
        _selectedRoiShape = ToolRoiFactory.ShapeOptions[0];
        _selectedTemplateRoiShape = ToolRoiFactory.ShapeOptions[0];
        _minScore = GetDouble(parameters, "minScore", 0.85);
        _pyramidLevels = Math.Clamp(GetInt(parameters, "pyramidLevels", 3), 1, 8);
        _angleStart = GetDouble(parameters, "angleStart", -45);
        _angleExtent = GetDouble(parameters, "angleExtent", 90);
        if (_selectedEngine == TemplateMatchingEngine.OpenCv &&
            Math.Abs(_angleStart + 10) < 0.001 &&
            Math.Abs(_angleExtent - 20) < 0.001)
        {
            _angleStart = -45;
            _angleExtent = 90;
        }

        _polarity = GetString(parameters, "polarity", "use_polarity");
        _openCvMatchModeState = GetString(
            parameters,
            OpenCvMatchModeStateKey,
            GetString(parameters, TemplateMatchingParameterCatalog.MatchMode, "Shape"));
        _openCvMultiMatchModeState = GetString(
            parameters,
            OpenCvMultiMatchModeStateKey,
            GetString(parameters, "multiMatchMode", _openCvMatchModeState));
        _selectedMatchMode = NormalizeMatchMode(
            _toolKind == VisionToolKind.MultiTargetMatch
                ? _openCvMultiMatchModeState
                : _openCvMatchModeState);
        _rejectLoadedHalconMode = _selectedEngine == TemplateMatchingEngine.Halcon &&
                                  !string.Equals(
                                      GetString(parameters, TemplateMatchingParameterCatalog.MatchMode, "Shape").Trim(),
                                      "Shape",
                                      StringComparison.OrdinalIgnoreCase);
        _contrast = Math.Clamp(GetDouble(parameters, "contrast", 30), 0, 255);
        _autoContrast = GetBool(parameters, "autoContrast", false);
        _matchCount = Math.Clamp(
            GetInt(parameters, "matchCount", _toolKind == VisionToolKind.MultiTargetMatch ? 128 : 1),
            1,
            GetMaxMatchCount());
        _selectedTemplateShape = GetString(parameters, "templateShape", "矩形");
        _showTemplateRegion = GetBool(parameters, "showTemplateRegion", false);
        if (HasLearnedTemplateModel(parameters))
        {
            _showTemplateRegion = false;
        }
        _showSearchRegion = GetBool(parameters, "showSearchRegion", true);
        _showCrosshair = GetBool(parameters, "showCrosshair", true);
        _useSubPixel = GetBool(parameters, "useSubPixel", true);
        _templatePreviewImagePng = GetString(parameters, "templateImagePng", string.Empty);
        _templatePreviewEdgeOverlayPng = GetString(parameters, "templateEdgeOverlayPng", string.Empty);

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

        RoiChoices = new ObservableCollection<RoiChoiceItem>(roiChoices);
        RoiShapeOptions = new ObservableCollection<RoiShapeOptionItem>(ToolRoiFactory.ShapeOptions);
        MatchModes = new ObservableCollection<TemplateMatchModeItem>
        {
            new("Shape", "形状匹配", "边缘/轮廓匹配，适合工业定位和旋转场景"),
            new("GrayNcc", "灰度NCC", "归一化相关，适合纹理稳定、光照变化较小的模板"),
            new("GrayCcorr", "灰度相关", "强相关，支持模板掩膜，适合背景结构稳定的目标"),
            new("GraySqDiff", "灰度差异", "平方差匹配，适合精确灰度模板和背景稳定场景"),
            new("FeatureOrb", "特征点ORB", "特征点匹配，适合旋转、局部遮挡和较大位移")
        };
        if (_toolKind == VisionToolKind.MultiTargetMatch)
        {
            MatchModes.Clear();
            MatchModes.Add(new TemplateMatchModeItem("Shape", "形状多目标", "基于边缘轮廓在搜索区域内查找多个相同目标"));
            MatchModes.Add(new TemplateMatchModeItem("CircularBlob", "圆形目标", "学习模板中的圆心、半径等几何属性，适合圆孔、圆点、圆环目标"));
            MatchModes.Add(new TemplateMatchModeItem("GrayNcc", "灰度多目标", "基于灰度相关在搜索区域内查找多个相似目标"));
            if (_selectedMatchMode is not ("Shape" or "CircularBlob" or "GrayNcc"))
            {
                _selectedMatchMode = "Shape";
            }
        }
        CreatedRois = new ObservableCollection<RoiDefinition>();
        RemovedRoiIds = new ObservableCollection<string>();
        EditableRois = new ObservableCollection<RoiEditorItem>();
        MultiTargetResultPoints = new ReadOnlyObservableCollection<MultiTargetMatchPointItem>(
            _multiTargetResultPoints);
        OutputOptions = new ObservableCollection<ToolOutputOptionItem>(CreateOutputOptions(_toolKind, _parameters));
        Polarities = new ObservableCollection<string>
        {
            "use_polarity",
            "ignore_global_polarity",
            "ignore_local_polarity"
        };
        Tabs =
        [
            new TemplateLocateTabItem("Template", "模板", "\uE8D4"),
            new TemplateLocateTabItem("Search", "搜索区域", "\uE721"),
            new TemplateLocateTabItem("Parameters", "参数", "\uE713"),
            new TemplateLocateTabItem("Display", "显示", "\uE7F4"),
            new TemplateLocateTabItem("Result", "结果", "\uE9D5")
        ];
        Tabs.Insert(Math.Max(0, Tabs.Count - 1), new TemplateLocateTabItem("Output", "输出项", "\uE8AB"));
        SelectTab(Tabs[0]);
        TemplateShapes = new ObservableCollection<TemplateShapeItem>
        {
            new("矩形"),
            new("仿矩"),
            new("圆"),
            new("椭圆"),
            new("任意")
        };
        SelectTemplateShape(TemplateShapes.FirstOrDefault(shape => shape.Name == _selectedTemplateShape) ?? TemplateShapes[0]);

        SelectTabCommand = new DelegateCommand<TemplateLocateTabItem>(SelectTab);
        SelectTemplateModeCommand = new DelegateCommand<string>(SelectTemplateMode);
        SelectTemplateShapeCommand = new DelegateCommand<TemplateShapeItem>(SelectTemplateShape);
        CreateRoiCommand = new DelegateCommand(CreateRoi, () => !IsMissingInputImage);
        CreateTemplateRoiCommand = new DelegateCommand(CreateTemplateRoi, () => !IsMissingInputImage);
        CreateTemplateMaskRoiCommand = new DelegateCommand(CreateTemplateMaskRoi, () => !IsMissingInputImage);
        PlaceRoiCommand = new DelegateCommand<Point2D>(PlacePendingRoi);
        LearnTemplateCommand = new AsyncDelegateCommand(LearnTemplateAsync, () => !IsBusy && !IsMissingInputImage)
            .ObservesProperty(() => IsBusy);
        ResetTemplateCommand = new DelegateCommand(ResetTemplate, () => !IsBusy)
            .ObservesProperty(() => IsBusy);
        SetStandardCommand = new DelegateCommand(SetStandard);
        RunToolCommand = new AsyncDelegateCommand(RunToolAsync, () => !IsBusy && !IsMissingInputImage)
            .ObservesProperty(() => IsBusy);
        RunFlowCommand = new AsyncDelegateCommand(RunFlowAsync, () => !IsBusy && !IsMissingInputImage)
            .ObservesProperty(() => IsBusy);
        ConfirmCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, true));
        CancelCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, false));
        CloseCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, true));

        LoadSearchRoiEditor();
        LoadTemplateRoiEditor();
        LoadTemplateMaskEditors();
        RefreshPreviewOverlays();
    }

    public event EventHandler<bool>? CloseRequested;

    public string WindowTitle { get; }

    public ImageFrame? CurrentFrame { get; }

    public bool IsMissingInputImage => CurrentFrame is null;

    public string MissingInputImageText => "未连接输入图像。请把采图工具的输出图像连接到本工具的输入图像。";

    public string InputFrameInfo { get; }

    public IReadOnlyDictionary<string, string> PendingParameters =>
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase));

    public IReadOnlyList<TemplateMatchingEngine> EngineOptions { get; }

    public IReadOnlyList<TemplateMatchingPreset> PresetOptions { get; }

    public TemplateMatchingEngine SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (SetProperty(ref _selectedEngine, value))
            {
                _rejectLoadedHalconMode = false;
                RaisePropertyChanged(nameof(IsHalconEngine));
                _selectedPreset = DetectPreset();
                RaisePropertyChanged(nameof(SelectedPreset));
            }
        }
    }

    public bool IsHalconEngine => SelectedEngine == TemplateMatchingEngine.Halcon;

    public bool IsAdvancedParametersExpanded
    {
        get => _isAdvancedParametersExpanded;
        set => SetProperty(ref _isAdvancedParametersExpanded, value);
    }

    public TemplateMatchingPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is null)
            {
                SetProperty(ref _selectedPreset, null);
                return;
            }

            ApplyPreset(value.Value);
        }
    }

    public bool RequiresRelearn
    {
        get => _requiresRelearn;
        private set => SetProperty(ref _requiresRelearn, value);
    }

    public string HalconAngleStartDeg
    {
        get => _halconAngleStartDeg;
        set => SetHalconEditorValue(ref _halconAngleStartDeg, value, requiresRelearn: true);
    }

    public string HalconAngleExtentDeg
    {
        get => _halconAngleExtentDeg;
        set => SetHalconEditorValue(ref _halconAngleExtentDeg, value, requiresRelearn: true);
    }

    public string HalconScaleMin
    {
        get => _halconScaleMin;
        set => SetHalconEditorValue(ref _halconScaleMin, value, requiresRelearn: true);
    }

    public string HalconScaleMax
    {
        get => _halconScaleMax;
        set => SetHalconEditorValue(ref _halconScaleMax, value, requiresRelearn: true);
    }

    public string HalconCandidateMinScore
    {
        get => _halconCandidateMinScore;
        set => SetHalconEditorValue(ref _halconCandidateMinScore, value);
    }

    public string HalconOuterCoverageMin
    {
        get => _halconOuterCoverageMin;
        set => SetHalconEditorValue(ref _halconOuterCoverageMin, value);
    }

    public string HalconInnerCoverageMin
    {
        get => _halconInnerCoverageMin;
        set => SetHalconEditorValue(ref _halconInnerCoverageMin, value);
    }

    public string HalconEdgeTolerancePx
    {
        get => _halconEdgeTolerancePx;
        set => SetHalconEditorValue(ref _halconEdgeTolerancePx, value);
    }

    public string HalconPolarityAgreementMin
    {
        get => _halconPolarityAgreementMin;
        set => SetHalconEditorValue(ref _halconPolarityAgreementMin, value);
    }

    public string HalconCandidateMaxOverlap
    {
        get => _halconCandidateMaxOverlap;
        set => SetHalconEditorValue(ref _halconCandidateMaxOverlap, value);
    }

    public string HalconMaxOverlap
    {
        get => _halconMaxOverlap;
        set => SetHalconEditorValue(ref _halconMaxOverlap, value);
    }

    public string HalconGreediness
    {
        get => _halconGreediness;
        set => SetHalconEditorValue(ref _halconGreediness, value);
    }

    public string HalconSubPixel
    {
        get => _halconSubPixel;
        set => SetHalconEditorValue(ref _halconSubPixel, value);
    }

    public string HalconNumLevels
    {
        get => _halconNumLevels;
        set => SetHalconEditorValue(ref _halconNumLevels, value, requiresRelearn: true);
    }

    public string HalconOperatorTimeoutMs
    {
        get => _halconOperatorTimeoutMs;
        set => SetHalconEditorValue(ref _halconOperatorTimeoutMs, value);
    }

    public string HalconCandidateLimit
    {
        get => _halconCandidateLimit;
        set => SetHalconEditorValue(ref _halconCandidateLimit, value);
    }

    public string HalconExpectedCount
    {
        get => _halconExpectedCount;
        set => SetHalconEditorValue(ref _halconExpectedCount, value);
    }

    public string TemplatePreviewImagePng
    {
        get => _templatePreviewImagePng;
        private set => SetProperty(ref _templatePreviewImagePng, value);
    }

    public string TemplatePreviewEdgeOverlayPng
    {
        get => _templatePreviewEdgeOverlayPng;
        private set => SetProperty(ref _templatePreviewEdgeOverlayPng, value);
    }

    public ObservableCollection<RoiChoiceItem> RoiChoices { get; }

    public ObservableCollection<RoiShapeOptionItem> RoiShapeOptions { get; }

    public ObservableCollection<TemplateMatchModeItem> MatchModes { get; }

    public ObservableCollection<RoiDefinition> CreatedRois { get; }

    public ObservableCollection<string> RemovedRoiIds { get; }

    public ObservableCollection<RoiEditorItem> EditableRois { get; }

    public ObservableCollection<VisionOverlayItem> PreviewOverlays { get; } = new();

    public ObservableCollection<string> Polarities { get; }

    public ObservableCollection<ToolOutputOptionItem> OutputOptions { get; }

    public ReadOnlyObservableCollection<MultiTargetMatchPointItem> MultiTargetResultPoints { get; }

    public MultiTargetMatchPointItem? SelectedMultiTargetResultPoint
    {
        get => _selectedMultiTargetResultPoint;
        set
        {
            var authoritativeSelection = value is not null && _multiTargetCandidates.ContainsKey(value)
                ? value
                : null;
            if (SetProperty(ref _selectedMultiTargetResultPoint, authoritativeSelection))
            {
                ApplySelectedMultiTargetResultPoint();
                RefreshPreviewOverlays();
            }
        }
    }

    public ObservableCollection<TemplateLocateTabItem> Tabs { get; }

    public ObservableCollection<TemplateShapeItem> TemplateShapes { get; }

    public DelegateCommand<TemplateLocateTabItem> SelectTabCommand { get; }

    public DelegateCommand<string> SelectTemplateModeCommand { get; }

    public DelegateCommand<TemplateShapeItem> SelectTemplateShapeCommand { get; }

    public DelegateCommand CreateRoiCommand { get; }

    public DelegateCommand CreateTemplateRoiCommand { get; }

    public DelegateCommand CreateTemplateMaskRoiCommand { get; }

    public DelegateCommand<Point2D> PlaceRoiCommand { get; }

    public AsyncDelegateCommand LearnTemplateCommand { get; }

    public DelegateCommand ResetTemplateCommand { get; }

    public DelegateCommand SetStandardCommand { get; }

    public AsyncDelegateCommand RunToolCommand { get; }

    public AsyncDelegateCommand RunFlowCommand { get; }

    public DelegateCommand ConfirmCommand { get; }

    public DelegateCommand CancelCommand { get; }

    public DelegateCommand CloseCommand { get; }

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

    public string RoiId
    {
        get => _roiId;
        set
        {
            if (SetProperty(ref _roiId, value))
            {
                _mappedSearchRoi = null;
                LoadSearchRoiEditor();
                RefreshPreviewOverlays();
            }
        }
    }

    public RoiShapeOptionItem SelectedRoiShape
    {
        get => _selectedRoiShape;
        set => SetProperty(ref _selectedRoiShape, value);
    }

    public RoiShapeOptionItem SelectedTemplateRoiShape
    {
        get => _selectedTemplateRoiShape;
        set => SetProperty(ref _selectedTemplateRoiShape, value);
    }

    public RoiEditorItem? SelectedEditableRoi
    {
        get => _selectedEditableRoi;
        set => SetProperty(ref _selectedEditableRoi, value);
    }

    public bool IsRoiPlacementArmed => _pendingRoiPlacement != RoiPlacementTarget.None;

    public string RoiPlacementHint
    {
        get => _roiPlacementHint;
        private set => SetProperty(ref _roiPlacementHint, value);
    }

    public double MinScore
    {
        get => _minScore;
        set => SetProperty(ref _minScore, Math.Clamp(value, 0, 1));
    }

    public int PyramidLevels
    {
        get => _pyramidLevels;
        set => SetProperty(ref _pyramidLevels, Math.Clamp(value, 1, 8));
    }

    public double AngleStart
    {
        get => _angleStart;
        set => SetProperty(ref _angleStart, value);
    }

    public double AngleExtent
    {
        get => _angleExtent;
        set => SetProperty(ref _angleExtent, value);
    }

    public string Polarity
    {
        get => _polarity;
        set => SetProperty(ref _polarity, value);
    }

    public string SelectedMatchMode
    {
        get => _selectedMatchMode;
        set
        {
            if (SetProperty(ref _selectedMatchMode, NormalizeMatchMode(value)))
            {
                if (_toolKind == VisionToolKind.MultiTargetMatch)
                {
                    _openCvMultiMatchModeState = _selectedMatchMode;
                }
                else
                {
                    _openCvMatchModeState = _selectedMatchMode;
                }

                StatusText = $"匹配方法：{GetMatchModeName(_selectedMatchMode)}";
            }
        }
    }

    public string SelectedTabKey
    {
        get => _selectedTabKey;
        private set
        {
            if (SetProperty(ref _selectedTabKey, value))
            {
                RaisePropertyChanged(nameof(IsTemplateTab));
                RaisePropertyChanged(nameof(IsSearchTab));
                RaisePropertyChanged(nameof(IsParametersTab));
                RaisePropertyChanged(nameof(IsDisplayTab));
                RaisePropertyChanged(nameof(IsOutputTab));
                RaisePropertyChanged(nameof(IsResultTab));
            }
        }
    }

    public bool IsTemplateTab => SelectedTabKey == "Template";

    public bool IsSearchTab => SelectedTabKey == "Search";

    public bool IsParametersTab => SelectedTabKey == "Parameters";

    public bool IsDisplayTab => SelectedTabKey == "Display";

    public bool IsOutputTab => SelectedTabKey == "Output";

    public bool IsResultTab => SelectedTabKey == "Result";

    public bool IsMultiTargetTool => _toolKind == VisionToolKind.MultiTargetMatch;

    public bool HasMultiTargetResultPoints => MultiTargetResultPoints.Count > 0;

    public string MultiTargetResultSummary => $"匹配点 {MultiTargetResultPoints.Count} 个";

    public string SelectedTemplateMode
    {
        get => _selectedTemplateMode;
        private set
        {
            if (SetProperty(ref _selectedTemplateMode, value))
            {
                RaisePropertyChanged(nameof(IsAddTemplateMode));
                RaisePropertyChanged(nameof(IsRemoveTemplateMode));
            }
        }
    }

    public bool IsAddTemplateMode => SelectedTemplateMode == "Add";

    public bool IsRemoveTemplateMode => SelectedTemplateMode == "Remove";

    public string SelectedTemplateShape
    {
        get => _selectedTemplateShape;
        private set
        {
            if (SetProperty(ref _selectedTemplateShape, value))
            {
                RefreshPreviewOverlays();
            }
        }
    }

    public double Contrast
    {
        get => _contrast;
        set
        {
            if (SetProperty(ref _contrast, Math.Clamp(value, 0, 255)))
            {
                RaisePropertyChanged(nameof(ContrastDisplayText));
            }
        }
    }

    public bool AutoContrast
    {
        get => _autoContrast;
        set
        {
            if (SetProperty(ref _autoContrast, value))
            {
                RaisePropertyChanged(nameof(IsManualContrastEnabled));
                RaisePropertyChanged(nameof(ContrastDisplayText));
                RaisePropertyChanged(nameof(EdgeContrastHint));
            }
        }
    }

    public bool IsManualContrastEnabled => !AutoContrast;

    public string ContrastDisplayText => AutoContrast ? "自动" : Contrast.ToString("0");

    public string EdgeContrastHint => AutoContrast
        ? "自动分析模板灰度和边缘阈值，绿色表示学习到的模板轮廓"
        : "阈值越高，保留越明显的边缘；绿色表示学习到的模板轮廓";

    public int MatchCount
    {
        get => _matchCount;
        set => SetProperty(ref _matchCount, Math.Clamp(value, 1, GetMaxMatchCount()));
    }

    private int GetMaxMatchCount()
    {
        return _toolKind == VisionToolKind.MultiTargetMatch ? 256 : 32;
    }

    public bool ShowTemplateRegion
    {
        get => _showTemplateRegion;
        set
        {
            if (SetProperty(ref _showTemplateRegion, value))
            {
                RefreshPreviewOverlays();
            }
        }
    }

    public bool ShowSearchRegion
    {
        get => _showSearchRegion;
        set
        {
            if (SetProperty(ref _showSearchRegion, value))
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

    public bool UseSubPixel
    {
        get => _useSubPixel;
        set => SetProperty(ref _useSubPixel, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string ScoreText
    {
        get => _scoreText;
        private set => SetProperty(ref _scoreText, value);
    }

    public string PoseText
    {
        get => _poseText;
        private set => SetProperty(ref _poseText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool ApplyTo(VisionToolItem tool)
    {
        SyncEditorRois();
        Dictionary<string, string> parameters;
        try
        {
            parameters = BuildCurrentParameters();
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            StatusText = FormatDiagnostic(exception);
            return false;
        }

        tool.Name = string.IsNullOrWhiteSpace(Name) ? tool.Name : Name.Trim();
        tool.Kind = _toolKind;
        tool.Enabled = Enabled;
        tool.RoiId = RoiId ?? string.Empty;
        tool.ParametersText = FormatParameters(parameters);
        _parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        RaisePropertyChanged(nameof(PendingParameters));
        return true;
    }

    public async Task<bool> PrepareToCloseAsync()
    {
        SyncEditorRois();
        var capture = await CaptureRoiReferencePoseIfNeededAsync();
        if (!capture.IsSuccess)
        {
            return false;
        }

        try
        {
            _ = BuildCurrentParameters();
            return true;
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            StatusText = FormatDiagnostic(exception);
            return false;
        }
    }

    private void CreateRoi()
    {
        ArmRoiPlacement(RoiPlacementTarget.Search);
    }

    private void CreateTemplateRoi()
    {
        SelectedTemplateMode = "Add";
        ArmRoiPlacement(RoiPlacementTarget.Template);
    }

    private void CreateTemplateMaskRoi()
    {
        SelectedTemplateMode = "Remove";
        EnsureTemplateMaskEditorsLoaded();
        ArmRoiPlacement(RoiPlacementTarget.TemplateMask);
    }

    private void PlacePendingRoi(Point2D point)
    {
        if (_pendingRoiPlacement == RoiPlacementTarget.None)
        {
            return;
        }

        if (CurrentFrame is null)
        {
            StatusText = "No input image. Run the acquisition tool before placing ROI.";
            ClearRoiPlacement();
            return;
        }

        if (_pendingRoiPlacement == RoiPlacementTarget.Template)
        {
            PlaceTemplateRoi(point);
            return;
        }

        if (_pendingRoiPlacement == RoiPlacementTarget.TemplateMask)
        {
            PlaceTemplateMaskRoi(point);
            return;
        }

        PlaceSearchRoi(point);
    }

    private void PlaceSearchRoi(Point2D point)
    {
        QueueReplacedSearchRois();
        var roi = ToolRoiFactory.CreateRoiAt(Name, _toolKind, SelectedRoiShape.Kind, CurrentFrame, 1, point) with
        {
            Name = GetSearchRoiName()
        };
        _mappedSearchRoi = null;
        _roiReferenceDirty = _toolKind == VisionToolKind.MultiTargetMatch;
        UpsertCreatedRoi(roi);
        RoiChoices.Add(new RoiChoiceItem(roi.Id, roi.Name));
        RoiId = roi.Id;
        ShowSearchRegion = true;
        StatusText = $"Placed search ROI {roi.Name}. Drag handles to adjust it.";
        ClearRoiPlacement();
    }

    private void QueueReplacedSearchRois()
    {
        var replacementIds = RoiChoices
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Id) &&
                             (string.Equals(choice.Id, RoiId, StringComparison.OrdinalIgnoreCase) || IsOwnedSearchRoiName(choice.Name)))
            .Select(choice => choice.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var id in replacementIds)
        {
            QueueRoiRemoval(id);
        }
    }

    private void QueueRoiRemoval(string id)
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

        var choices = RoiChoices
            .Where(choice => string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var choice in choices)
        {
            RoiChoices.Remove(choice);
        }
    }

    private bool IsOwnedSearchRoiName(string name)
    {
        var baseName = GetSearchRoiName();
        if (string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!name.StartsWith($"{baseName} ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = name[(baseName.Length + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsDigit);
    }

    private string GetSearchRoiName()
    {
        var toolName = string.IsNullOrWhiteSpace(Name) ? _toolKind.ToString() : Name.Trim();
        return $"{toolName} ROI";
    }

    private void PlaceTemplateRoi(Point2D point)
    {
        var templateRoi = ToolRoiFactory.CreateRoiAt(Name, _toolKind, SelectedTemplateRoiShape.Kind, CurrentFrame, 1, point) with
        {
            Id = "template-roi",
            Name = "Template ROI"
        };

        SetTemplateRoiEditor(RoiEditorItem.FromDefinition(templateRoi));
        ShowTemplateRegion = true;
        StatusText = "Placed template ROI. Drag handles to adjust it, then learn template.";
        ClearRoiPlacement();
    }

    private void PlaceTemplateMaskRoi(Point2D point)
    {
        var index = _templateMaskEditors.Count + 1;
        var maskRoi = ToolRoiFactory.CreateRoiAt(Name, _toolKind, SelectedTemplateRoiShape.Kind, CurrentFrame, index, point) with
        {
            Id = $"template-mask-roi-{Guid.NewGuid():N}",
            Name = $"模板掩膜 {index}"
        };

        AddTemplateMaskEditor(RoiEditorItem.FromDefinition(maskRoi));
        ShowTemplateRegion = true;
        StatusText = "已放置模板掩膜 ROI。掩膜区域会排除不参与匹配的模板部分，请学习模板。";
        ClearRoiPlacement();
    }

    private void ArmRoiPlacement(RoiPlacementTarget target)
    {
        if (CurrentFrame is null)
        {
            StatusText = "No input image. Run the acquisition tool before placing ROI.";
            return;
        }

        _pendingRoiPlacement = target;
        RoiPlacementHint = IsPolygonPlacement(target)
            ? "按住左键拖拽绘制任意 ROI，松开完成。"
            : target switch
            {
                RoiPlacementTarget.Template => "Left-click the image to place template ROI.",
                RoiPlacementTarget.TemplateMask => "Left-click the image to place template mask ROI.",
                _ => "Left-click the image to place search ROI."
            };
        StatusText = RoiPlacementHint;
        RaisePropertyChanged(nameof(IsRoiPlacementArmed));
    }

    private bool IsPolygonPlacement(RoiPlacementTarget target)
    {
        return target switch
        {
            RoiPlacementTarget.Template or RoiPlacementTarget.TemplateMask => SelectedTemplateRoiShape.Kind == RoiShapeKind.Polygon,
            RoiPlacementTarget.Search => SelectedRoiShape.Kind == RoiShapeKind.Polygon,
            _ => false
        };
    }

    private void ClearRoiPlacement()
    {
        _pendingRoiPlacement = RoiPlacementTarget.None;
        RoiPlacementHint = string.Empty;
        RaisePropertyChanged(nameof(IsRoiPlacementArmed));
    }

    private void SelectTab(TemplateLocateTabItem tab)
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

    private void SelectTemplateMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        SelectedTemplateMode = mode;
        if (mode == "Remove")
        {
            ArmRoiPlacement(RoiPlacementTarget.TemplateMask);
            StatusText = "模板扣除模式：在图像上点击添加掩膜 ROI。";
            return;
        }

        ArmRoiPlacement(RoiPlacementTarget.Template);
        StatusText = "模板添加模式：在图像上点击放置模板 ROI。";
    }

    private void SelectTemplateShape(TemplateShapeItem shape)
    {
        if (shape is null)
        {
            return;
        }

        foreach (var candidate in TemplateShapes)
        {
            candidate.IsSelected = ReferenceEquals(candidate, shape);
        }

        SelectedTemplateShape = shape.Name;
        SelectedTemplateRoiShape = shape.Name switch
        {
            "仿矩" => RoiShapeOptions.First(option => option.Kind == RoiShapeKind.RotatedRectangle),
            "圆" or "椭圆" => RoiShapeOptions.First(option => option.Kind == RoiShapeKind.Circle),
            "任意" => RoiShapeOptions.First(option => option.Kind == RoiShapeKind.Polygon),
            _ => RoiShapeOptions.First(option => option.Kind == RoiShapeKind.Rectangle)
        };
        StatusText = $"模板区域类型：{shape.Name}";
    }

    private async Task LearnTemplateAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var learned = false;
        try
        {
            learned = await TryLearnTemplateAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }

        if (learned)
        {
            await RunToolAsync(cancellationToken);
        }
    }

    private async Task<bool> TryLearnTemplateAsync(CancellationToken cancellationToken)
    {
        if (CurrentFrame is null)
        {
            StatusText = "No input image to learn template.";
            _log.Warning("VisionDebug", $"{Name} template learn failed: no input image");
            return false;
        }

        if (_templateRoiEditor is null && !TryGetTemplateRoiGeometry(out _, out _, out _, out _))
        {
            StatusText = "Please place a template ROI before learning.";
            _log.Warning("VisionDebug", $"{Name} template learn failed: template ROI is not placed");
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyncEditorRois();
            if (!TryGetTemplateRoiDefinition(out var templateRoi))
            {
                StatusText = "Please place a template ROI before learning.";
                return false;
            }

            var persisted = BuildCurrentParameters();
            var requestParameters = BuildActiveRequestParameters(persisted, out _);
            EnsureStableOwnerForHalcon();
            var result = await _matchingService.LearnAsync(
                new TemplateLearningRequest(
                    CreateModelOwner(),
                    CurrentFrame,
                    templateRoi,
                    FindSelectedRoi(),
                    requestParameters),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                StatusText = result.Diagnostic is null
                    ? result.Message
                    : FormatDiagnostic(result.Diagnostic);
                return false;
            }

            var merged = new Dictionary<string, string>(persisted, StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in result.Parameters)
            {
                merged[parameter.Key] = parameter.Value;
            }

            var standardCreated = result.Engine != TemplateMatchingEngine.Halcon &&
                                  SetLearnedStandardPose(merged, result.Parameters);
            var previewImage = GetString(merged, "templateImagePng", string.Empty);
            var previewEdges = GetString(merged, "templateEdgeOverlayPng", string.Empty);

            // Save template pixels to a resource file for persistent model storage
            if (result.Engine != TemplateMatchingEngine.Halcon &&
                merged.TryGetValue("templatePixels", out var pixelsBase64) &&
                !string.IsNullOrWhiteSpace(pixelsBase64) &&
                merged.ContainsKey("templateWidth") &&
                merged.ContainsKey("templateHeight"))
            {
                try
                {
                    var resourceDir = Path.Combine(
                        _paths.TemplateResourceDirectory,
                        RuntimePaths.SanitizePathSegment(_toolId));
                    Directory.CreateDirectory(resourceDir);
                    var filePath = Path.Combine(resourceDir, "template.bin");
                    File.WriteAllBytes(filePath, Convert.FromBase64String(pixelsBase64));
                    merged["modelPath"] = filePath;
                    merged["modelVersion"] = "1.0";
                }
                catch (Exception ex)
                {
                    _log.Warning("VisionDebug", $"{Name} failed to persist template model file: {ex.Message}");
                }
            }

            if (result.Engine != TemplateMatchingEngine.Halcon && !HasLearnedTemplateModel(merged))
            {
                StatusText = "Template learn failed: model data was not generated.";
                _log.Warning("VisionDebug", $"{Name} template learn failed: model data was not generated");
                return false;
            }

            _parameters = merged;
            if (result.Engine == TemplateMatchingEngine.Halcon)
            {
                _halconLearnedSuccessfullyInSession = true;
                RequiresRelearn = false;
            }
            TemplatePreviewImagePng = previewImage;
            TemplatePreviewEdgeOverlayPng = previewEdges;
            RaisePropertyChanged(nameof(PendingParameters));
            StatusText = standardCreated
                ? $"{result.Message}; standard pose updated."
                : result.Message;
            _log.Info("VisionDebug", $"{Name} {result.Message}");
            HideTemplateRoiEditor();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            StatusText = FormatDiagnostic(exception);
            _log.Warning("VisionDebug", $"{Name} template learn configuration failed: {exception.Code}");
            return false;
        }
        catch (Exception ex)
        {
            StatusText = $"Template learn failed: {ex.Message}";
            _log.Error("VisionDebug", $"{Name} template learn failed: {ex.Message}");
            return false;
        }
    }

    private void ResetTemplate()
    {
        ScoreText = "-";
        PoseText = "-";
        DurationText = "0ms";
        _hasMatchResult = false;
        _lastTemplateMatch = null;
        _multiMatches = Array.Empty<MultiTargetMatchCandidate>();
        ClearMultiTargetResults();
        TemplatePreviewImagePng = string.Empty;
        TemplatePreviewEdgeOverlayPng = string.Empty;
        RemoveTemplateModelParameters();
        RemoveEditor(_templateRoiEditor);
        _templateRoiEditor = null;
        RemoveTemplateMaskEditors();
        SelectedEditableRoi = _searchRoiEditor;
        RefreshPreviewOverlays();
        StatusText = "模板数据已清除";
    }

    private void SetStandard()
    {
        if (SelectedEngine == TemplateMatchingEngine.Halcon)
        {
            StatusText = "HALCON 标准位姿由模型元数据管理；请重新学习模板生成新模型。";
            return;
        }

        if (!_hasMatchResult)
        {
            StatusText = "请先匹配模板，再设置标准";
            return;
        }

        _parameters["standardX"] = _matchX.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["standardY"] = _matchY.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["standardAngle"] = _matchAngle.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["standardScale"] = _matchScale.ToString("R", CultureInfo.InvariantCulture);
        StatusText = $"已设置当前匹配结果为标准：{PoseText}";
    }

    private async Task RunToolAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        if (CurrentFrame is null)
        {
            StatusText = "没有可用图像，请先运行采图工具";
            _log.Warning("VisionDebug", $"{Name} 模板定位失败：没有可用图像");
            return;
        }

        await RunTemplateMatcherAsync(cancellationToken);

    }

    private async Task RunTemplateMatcherAsync(CancellationToken cancellationToken)
    {
        if (CurrentFrame is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lastTemplateMatch = null;
            ClearMultiTargetResults();
            _mappedSearchRoi = null;
            RefreshPreviewOverlays();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            SyncEditorRois();
            var frame = CurrentFrame!;
            var configuredReference = ReadConfiguredReferencePose();
            if (configuredReference.Status == PoseReadStatus.Invalid)
            {
                ClearFailedRunResult(configuredReference.ErrorMessage);
                return;
            }

            var capture = await CaptureRoiReferencePoseIfNeededAsync();
            if (!capture.IsSuccess)
            {
                ClearFailedRunResult(StatusText);
                return;
            }

            if (_toolKind == VisionToolKind.MultiTargetMatch && capture.CurrentPose is not null)
            {
                configuredReference = capture.CurrentPose;
            }

            var mapping = await GetMappedSearchRoiAsync(
                FindSelectedRoi(),
                configuredReference,
                capture.CurrentPose);
            if (!mapping.IsSuccess)
            {
                ClearFailedRunResult(mapping.ErrorMessage);
                return;
            }

            var roi = mapping.Roi;
            _mappedSearchRoi = roi;
            var currentParameters = BuildCurrentParameters();
            if (!HasActiveLearnedTemplateModel(currentParameters) &&
                _templateRoiEditor is not null &&
                !await TryLearnTemplateAsync(cancellationToken))
            {
                return;
            }

            var persisted = BuildCurrentParameters();
            var parameters = BuildActiveRequestParameters(persisted, out var expectedCount);
            EnsureStableOwnerForHalcon();
            var batch = await _matchingService.MatchAsync(
                new TemplateMatchingRequest(
                    CreateModelOwner(),
                    frame,
                    roi,
                    parameters,
                    GetCardinality(),
                    expectedCount),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_toolKind == VisionToolKind.MultiTargetMatch)
            {
                _lastTemplateMatch = null;
                stopwatch.Stop();

                var multiMatch = TemplateMatchResultProjector.ToMulti(batch);
                SetMultiTargetResults(multiMatch.Matches);
                DurationText = $"{stopwatch.ElapsedMilliseconds}ms";
                _matchState = batch.Outcome == InspectionOutcome.Ok ? VisionOverlayState.Ok : VisionOverlayState.Ng;
                if (MultiTargetResultPoints.FirstOrDefault() is { } bestPoint)
                {
                    SelectedMultiTargetResultPoint = bestPoint;
                    PoseText = $"Count:{_multiMatches.Count} Best " +
                               $"X:{bestPoint.Pose.X:0.0} Y:{bestPoint.Pose.Y:0.0} A:{bestPoint.Pose.Angle:0.00}";
                }
                else
                {
                    ScoreText = "0.000";
                    PoseText = $"Count:{_multiMatches.Count}";
                    _matchX = 0;
                    _matchY = 0;
                    _matchAngle = 0;
                    _matchScale = 1;
                }

                RefreshPreviewOverlays();
                SelectTab(Tabs.First(tab => tab.Key == "Result"));
                StatusText = batch.Diagnostic is null ? batch.Message : FormatDiagnostic(batch.Diagnostic);
                _log.Info("VisionDebug", $"{Name} {batch.Message} best={ScoreText} {PoseText}");
                return;
            }

            var match = TemplateMatchResultProjector.ToSingle(batch);
            stopwatch.Stop();

            _lastTemplateMatch = match;
            ClearMultiTargetResults();
            ScoreText = match.Score.ToString("0.000", CultureInfo.InvariantCulture);
            PoseText = $"X:{match.Pose.X:0.0} Y:{match.Pose.Y:0.0} A:{match.Pose.Angle:0.00}";
            DurationText = $"{stopwatch.ElapsedMilliseconds}ms";
            _matchX = match.Pose.X;
            _matchY = match.Pose.Y;
            _matchAngle = match.Pose.Angle;
            _matchScale = match.Pose.Scale;
            _matchState = match.Outcome == InspectionOutcome.Ok ? VisionOverlayState.Ok : VisionOverlayState.Ng;
            _hasMatchResult = match.HasMatch;
            var standardCreated = batch.Engine != TemplateMatchingEngine.Halcon && TrySetInitialStandard(match);
            RefreshPreviewOverlays();
            SelectTab(Tabs.First(tab => tab.Key == "Result"));
            StatusText = batch.Diagnostic is not null
                ? FormatDiagnostic(batch.Diagnostic)
                : standardCreated
                    ? $"{match.Message}；已建立标准位姿"
                    : match.Message;
            _log.Info("VisionDebug", $"{Name} {match.Message} score={ScoreText} pose={PoseText}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            ClearFailedRunResult(FormatDiagnostic(exception));
        }
        catch (Exception exception)
        {
            ClearFailedRunResult($"Template match failed: {exception.Message}");
            _log.Error("VisionDebug", $"{Name} template match failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TrySetInitialStandard(TemplateMatchResult match)
    {
        if (!match.HasMatch ||
            match.Outcome != InspectionOutcome.Ok ||
            _parameters.ContainsKey("standardX") ||
            _parameters.ContainsKey("standardY"))
        {
            return false;
        }

        _parameters["standardX"] = match.Pose.X.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["standardY"] = match.Pose.Y.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["standardAngle"] = match.Pose.Angle.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["standardScale"] = match.Pose.Scale.ToString("R", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool SetLearnedStandardPose(
        IDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> learned)
    {
        if (!TryGetDouble(learned, "templateX", out var x) ||
            !TryGetDouble(learned, "templateY", out var y) ||
            !TryGetDouble(learned, "templateWidth", out var width) ||
            !TryGetDouble(learned, "templateHeight", out var height))
        {
            return false;
        }

        parameters["standardX"] = (x + width / 2.0).ToString("0.###", CultureInfo.InvariantCulture);
        parameters["standardY"] = (y + height / 2.0).ToString("0.###", CultureInfo.InvariantCulture);
        parameters["standardAngle"] = "0";
        parameters.TryAdd("standardScale", "1");
        return true;
    }

    private bool TryGetTemplateRoiDefinition(out RoiDefinition templateRoi)
    {
        if (_templateRoiEditor is not null)
        {
            templateRoi = _templateRoiEditor.ToDefinition();
            return true;
        }

        if (TryReadTemplateRoiDefinition(out templateRoi))
        {
            return true;
        }

        if (!TryGetTemplateRoiGeometry(out var x, out var y, out var width, out var height))
        {
            templateRoi = null!;
            return false;
        }

        templateRoi = new RoiDefinition
        {
            Id = "template-roi",
            Name = "Template ROI",
            Shape = GetTemplateShape(),
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Angle = GetDouble(_parameters, "templateRoiAngle", 0),
            Radius = GetDouble(_parameters, "templateRoiRadius", Math.Min(width, height) / 2d)
        };
        return true;
    }

    private Dictionary<string, string> BuildActiveRequestParameters(
        IReadOnlyDictionary<string, string> persisted,
        out int expectedCount)
    {
        var request = new Dictionary<string, string>(persisted, StringComparer.OrdinalIgnoreCase);
        expectedCount = 1;
        if (SelectedEngine == TemplateMatchingEngine.Halcon)
        {
            request[TemplateMatchingParameterCatalog.Engine] = TemplateMatchingEngine.Halcon.ToString();
            request[TemplateMatchingParameterCatalog.MatchMode] = "Shape";
            if (_toolKind == VisionToolKind.MultiTargetMatch)
            {
                request["multiMatchMode"] = "Shape";
            }

            var parsed = TemplateMatchingParameterCatalog.ParseHalcon(request, GetCardinality());
            expectedCount = parsed.ExpectedCount;
            request.Remove(TemplateMatchingParameterCatalog.LegacyMatchCount);
            if (_toolKind == VisionToolKind.MultiTargetMatch)
            {
                request[TemplateMatchingParameterCatalog.ExpectedCount] =
                    expectedCount.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                request.Remove(TemplateMatchingParameterCatalog.ExpectedCount);
            }

            return request;
        }

        if (_toolKind == VisionToolKind.MultiTargetMatch &&
            request.TryGetValue("minCount", out var minimumRaw) &&
            int.TryParse(minimumRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimum) &&
            minimum is >= 1 and <= 100)
        {
            expectedCount = minimum;
        }

        return request;
    }

    private TemplateModelOwner CreateModelOwner()
    {
        return new TemplateModelOwner(
            _previewRecipe?.Id ?? string.Empty,
            _previewRecipe?.GetActiveFlow().Id ?? string.Empty,
            _toolId);
    }

    private void EnsureStableOwnerForHalcon()
    {
        if (SelectedEngine != TemplateMatchingEngine.Halcon)
        {
            return;
        }

        var owner = CreateModelOwner();
        if (string.IsNullOrWhiteSpace(owner.RecipeId) ||
            string.IsNullOrWhiteSpace(owner.FlowId) ||
            string.IsNullOrWhiteSpace(owner.ToolId))
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    "HALCON learning and trial run require stable RecipeId, FlowId, and ToolId values."));
        }
    }

    private bool HasActiveLearnedTemplateModel(IReadOnlyDictionary<string, string> parameters)
    {
        if (SelectedEngine != TemplateMatchingEngine.Halcon)
        {
            return HasLearnedTemplateModel(parameters);
        }

        if (_halconLearnedSuccessfullyInSession)
        {
            return true;
        }

        try
        {
            return TemplateModelParameterCodec.ReadHalcon(parameters) is not null;
        }
        catch (TemplateMatchingConfigurationException)
        {
            return false;
        }
    }

    private async Task<RoiReferenceCaptureResult> CaptureRoiReferencePoseIfNeededAsync()
    {
        if (!_roiReferenceDirty)
        {
            return RoiReferenceCaptureResult.Success();
        }

        if (_toolKind != VisionToolKind.MultiTargetMatch)
        {
            _roiReferenceDirty = false;
            return RoiReferenceCaptureResult.Success();
        }

        var sourceToolId = GetPositionInputSourceToolId();
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            RemoveRoiReferencePose();
            _roiReferenceDirty = false;
            return RoiReferenceCaptureResult.Success();
        }

        var currentPose = await ReadCurrentPositionInputPoseAsync();
        if (currentPose.Status == PoseReadStatus.Invalid)
        {
            StatusText = currentPose.ErrorMessage;
            return RoiReferenceCaptureResult.Failure(currentPose);
        }

        if (currentPose.Status == PoseReadStatus.Missing)
        {
            StatusText = "模板位置未就绪。请先运行模板定位，再保存或预览跟随 ROI。";
            return RoiReferenceCaptureResult.Failure(currentPose);
        }

        SetRoiReferencePose(currentPose.Pose!);
        _roiReferenceDirty = false;
        return RoiReferenceCaptureResult.Success(currentPose);
    }

    private async Task<RoiMappingResult> GetMappedSearchRoiAsync(
        RoiDefinition? roi,
        PoseReadResult referencePose,
        PoseReadResult? capturedCurrentPose)
    {
        if (_toolKind != VisionToolKind.MultiTargetMatch)
        {
            return RoiMappingResult.Success(roi);
        }

        var sourceToolId = GetPositionInputSourceToolId();
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return RoiMappingResult.Success(roi);
        }

        if (referencePose.Status == PoseReadStatus.Invalid)
        {
            return RoiMappingResult.Failure(referencePose.ErrorMessage);
        }

        var currentPose = capturedCurrentPose ?? await ReadCurrentPositionInputPoseAsync();
        if (currentPose.Status == PoseReadStatus.Invalid)
        {
            return RoiMappingResult.Failure(currentPose.ErrorMessage);
        }

        if (currentPose.Status == PoseReadStatus.Missing)
        {
            return RoiMappingResult.Success(roi);
        }

        if (referencePose.Status == PoseReadStatus.Missing)
        {
            return RoiMappingResult.Success(roi);
        }

        if (roi is null)
        {
            return RoiMappingResult.Success(null);
        }

        return RoiMappingResult.Success(PoseSimilarityTransform.MapRoi(roi, referencePose.Pose!, currentPose.Pose!));
    }

    private async Task<PoseReadResult> ReadCurrentPositionInputPoseAsync()
    {
        if (CurrentFrame is null || _pipeline is null)
        {
            return PoseReadResult.Missing();
        }

        var sourceToolId = GetPositionInputSourceToolId();
        var recipe = BuildPositionSourcePreviewRecipe(sourceToolId);
        if (recipe is null)
        {
            return PoseReadResult.Missing();
        }

        var pipelineResult = await _pipeline.ExecuteAsync(recipe, CurrentFrame);
        var sourceResult = pipelineResult.ToolResults.LastOrDefault(result =>
            string.Equals(result.ToolId, sourceToolId, StringComparison.OrdinalIgnoreCase));
        if (sourceResult?.Outcome != InspectionOutcome.Ok ||
            !TryGetDouble(sourceResult.Data, "x", out var x) ||
            !TryGetDouble(sourceResult.Data, "y", out var y))
        {
            return PoseReadResult.Missing();
        }

        TryGetDouble(sourceResult.Data, "angle", out var angle);
        var scale = 1d;
        if (sourceResult.Data.ContainsKey("scale") &&
            (!TryGetDouble(sourceResult.Data, "scale", out scale) ||
             !PoseSimilarityTransform.IsValidScale(scale)))
        {
            return PoseReadResult.Invalid(
                "Position input mapping failed: PositionInput.Scale must be finite and greater than zero.");
        }

        return PoseReadResult.Success(new Pose2D(x, y, angle) { Scale = scale });
    }

    private void ClearFailedRunResult(string errorMessage)
    {
        _lastTemplateMatch = null;
        ClearMultiTargetResults();
        RefreshPreviewOverlays();
        StatusText = errorMessage;
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
        _parameters["roiReferencePoseScale"] = pose.Scale.ToString("R", CultureInfo.InvariantCulture);
        _parameters["roiReferencePoseToolId"] = GetPositionInputSourceToolId();
    }

    private void RemoveRoiReferencePose()
    {
        _parameters.Remove("roiReferencePoseX");
        _parameters.Remove("roiReferencePoseY");
        _parameters.Remove("roiReferencePoseAngle");
        _parameters.Remove("roiReferencePoseScale");
        _parameters.Remove("roiReferencePoseToolId");
    }

    private PoseReadResult ReadConfiguredReferencePose()
    {
        if (_toolKind != VisionToolKind.MultiTargetMatch)
        {
            return PoseReadResult.Missing();
        }

        var sourceToolId = GetPositionInputSourceToolId();
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return PoseReadResult.Missing();
        }

        var taughtReference = ReadRoiReferencePose(_parameters, sourceToolId);
        return taughtReference.Status == PoseReadStatus.Missing
            ? ReadPositionSourceReferencePose(sourceToolId)
            : taughtReference;
    }

    private PoseReadResult ReadPositionSourceReferencePose(string sourceToolId)
    {
        var source = _previewRecipe?.GetActiveFlow().Tools.FirstOrDefault(tool =>
            string.Equals(tool.Id, sourceToolId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return PoseReadResult.Missing();
        }

        var scale = 1d;
        if (source.Parameters.ContainsKey("standardScale") &&
            (!TryGetDouble(source.Parameters, "standardScale", out scale) ||
             !PoseSimilarityTransform.IsValidScale(scale)))
        {
            return PoseReadResult.Invalid(CreateInvalidScaleMessage("standardScale"));
        }

        if (TryGetDouble(source.Parameters, "standardX", out var standardX) &&
            TryGetDouble(source.Parameters, "standardY", out var standardY))
        {
            TryGetDouble(source.Parameters, "standardAngle", out var standardAngle);
            return PoseReadResult.Success(
                new Pose2D(standardX, standardY, standardAngle) { Scale = scale });
        }

        if (!TryGetDouble(source.Parameters, "templateX", out var templateX) ||
            !TryGetDouble(source.Parameters, "templateY", out var templateY) ||
            !TryGetDouble(source.Parameters, "templateWidth", out var templateWidth) ||
            !TryGetDouble(source.Parameters, "templateHeight", out var templateHeight) ||
            templateWidth <= 0 ||
            templateHeight <= 0)
        {
            return PoseReadResult.Missing();
        }

        return PoseReadResult.Success(
            new Pose2D(
                templateX + templateWidth / 2.0,
                templateY + templateHeight / 2.0,
                0)
            {
                Scale = scale
            });
    }

    private static PoseReadResult ReadRoiReferencePose(
        IReadOnlyDictionary<string, string> parameters,
        string sourceToolId)
    {
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return PoseReadResult.Missing();
        }

        var referenceToolId = parameters.GetValueOrDefault("roiReferencePoseToolId") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(referenceToolId) &&
            !string.Equals(referenceToolId, sourceToolId, StringComparison.OrdinalIgnoreCase))
        {
            return PoseReadResult.Missing();
        }

        var scale = 1d;
        if (parameters.ContainsKey("roiReferencePoseScale") &&
            (!TryGetDouble(parameters, "roiReferencePoseScale", out scale) ||
             !PoseSimilarityTransform.IsValidScale(scale)))
        {
            return PoseReadResult.Invalid(CreateInvalidScaleMessage("roiReferencePoseScale"));
        }

        if (!TryGetDouble(parameters, "roiReferencePoseX", out var x) ||
            !TryGetDouble(parameters, "roiReferencePoseY", out var y))
        {
            return PoseReadResult.Missing();
        }

        TryGetDouble(parameters, "roiReferencePoseAngle", out var angle);
        return PoseReadResult.Success(new Pose2D(x, y, angle) { Scale = scale });
    }

    private static string CreateInvalidScaleMessage(string parameter)
    {
        return $"Position input mapping failed: {parameter} must be finite and greater than zero.";
    }

    private void SetMultiTargetResults(IReadOnlyList<MultiTargetMatchCandidate> matches)
    {
        ClearMultiTargetResults();
        _multiMatches = matches;
        for (var index = 0; index < matches.Count; index++)
        {
            var candidate = matches[index];
            var point = MultiTargetMatchPointItem.FromCandidate(index + 1, candidate);
            _multiTargetResultPoints.Add(point);
            _multiTargetCandidates.Add(point, candidate);
        }

        RaisePropertyChanged(nameof(HasMultiTargetResultPoints));
        RaisePropertyChanged(nameof(MultiTargetResultSummary));
    }

    private void ApplySelectedMultiTargetResultPoint()
    {
        if (_toolKind != VisionToolKind.MultiTargetMatch ||
            SelectedMultiTargetResultPoint is not { } selected ||
            !_multiTargetCandidates.TryGetValue(selected, out var candidate))
        {
            _hasMatchResult = false;
            _matchX = 0;
            _matchY = 0;
            _matchAngle = 0;
            _matchScale = 1;
            ScoreText = "-";
            PoseText = "-";
            return;
        }

        var pose = candidate.Pose;
        _matchX = pose.X;
        _matchY = pose.Y;
        _matchAngle = pose.Angle;
        _matchScale = pose.Scale;
        _hasMatchResult = true;
        ScoreText = candidate.Score.ToString("0.000", CultureInfo.InvariantCulture);
        PoseText = $"Count:{MultiTargetResultPoints.Count} Selected #{selected.Index} " +
                   $"X:{pose.X:0.0} Y:{pose.Y:0.0} A:{pose.Angle:0.00}";
    }

    private void ClearMultiTargetResults()
    {
        SelectedMultiTargetResultPoint = null;
        _multiMatches = Array.Empty<MultiTargetMatchCandidate>();
        _multiTargetCandidates.Clear();
        _multiTargetResultPoints.Clear();
        _hasMatchResult = false;
        _matchState = VisionOverlayState.Neutral;
        _matchX = 0;
        _matchY = 0;
        _matchAngle = 0;
        _matchScale = 1;
        ScoreText = "-";
        PoseText = "-";
        DurationText = "0ms";
        RaisePropertyChanged(nameof(HasMultiTargetResultPoints));
        RaisePropertyChanged(nameof(MultiTargetResultSummary));
    }

    private async Task RunFlowAsync(CancellationToken cancellationToken)
    {
        await RunToolAsync(cancellationToken);
        RunFlowRequested = true;
        CloseRequested?.Invoke(this, true);
    }

    private void RefreshPreviewOverlays()
    {
        PreviewOverlays.Clear();
        var frame = CurrentFrame;
        if (frame is null)
        {
            return;
        }

        if (ShowSearchRegion)
        {
            var searchRegion = TemplateMatcher.GetSearchRegion(frame, _mappedSearchRoi ?? FindSelectedRoi());
            PreviewOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Rectangle,
                State = VisionOverlayState.Warning,
                Label = string.IsNullOrWhiteSpace(RoiId) ? "搜索区域" : "绑定ROI搜索区",
                X = searchRegion.X,
                Y = searchRegion.Y,
                Width = searchRegion.Width,
                Height = searchRegion.Height
            });
        }

        if (ShowTemplateRegion)
        {
            var templateRegionOverlay = CreateTemplateRegionOverlay();
            if (templateRegionOverlay is not null)
            {
                PreviewOverlays.Add(templateRegionOverlay);
            }
        }

        if (!_hasMatchResult)
        {
            return;
        }

        if (_toolKind == VisionToolKind.MultiTargetMatch)
        {
            if (SelectedMultiTargetResultPoint is not { } selectedPoint ||
                !_multiTargetCandidates.TryGetValue(selectedPoint, out var match))
            {
                return;
            }

            if (match.Shape.Equals("Circle", StringComparison.OrdinalIgnoreCase))
            {
                PreviewOverlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Circle,
                    State = _matchState,
                    Label = $"#{selectedPoint.Index}",
                    X = match.X,
                    Y = match.Y,
                    Radius = match.Radius > 0 ? match.Radius : Math.Max(match.Width, match.Height) / 2.0
                });
                PreviewOverlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Cross,
                    State = _matchState,
                    Label = string.Empty,
                    X = match.X,
                    Y = match.Y,
                    Angle = match.Angle
                });
                return;
            }

            PreviewOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangle,
                State = _matchState,
                Label = $"#{selectedPoint.Index}",
                X = match.X,
                Y = match.Y,
                Width = Math.Max(12, match.Width),
                Height = Math.Max(12, match.Height),
                Angle = match.Angle
            });
            PreviewOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Cross,
                State = _matchState,
                Label = string.Empty,
                X = match.X,
                Y = match.Y,
                Angle = match.Angle
            });

            return;
        }

        if (_lastTemplateMatch is null)
        {
            return;
        }

        foreach (var overlay in TemplateLocateOverlayFactory.Create(_lastTemplateMatch))
        {
            PreviewOverlays.Add(overlay);
        }
    }

    private VisionOverlayItem? CreateTemplateRegionOverlay()
    {
        if (_templateRoiEditor is not null)
        {
            return VisionOverlayItem.FromRoi(_templateRoiEditor.ToDefinition(), VisionOverlayState.Neutral) with
            {
                Label = "模板区域"
            };
        }

        return null;
    }

    private void LoadSearchRoiEditor()
    {
        RemoveEditor(_searchRoiEditor);
        _searchRoiEditor = null;

        var definition = FindSelectedRoi();
        if (definition is null)
        {
            if (ReferenceEquals(SelectedEditableRoi, _searchRoiEditor))
            {
                SelectedEditableRoi = _templateRoiEditor;
            }

            return;
        }

        _searchRoiEditor = RoiEditorItem.FromDefinition(definition);
        AddEditor(_searchRoiEditor);
        SelectedEditableRoi = _searchRoiEditor;
    }

    private void LoadTemplateRoiEditor()
    {
        if (HasLearnedTemplateModel(_parameters))
        {
            return;
        }

        if (TryReadTemplateRoiDefinition(out var savedDefinition))
        {
            SetTemplateRoiEditor(RoiEditorItem.FromDefinition(savedDefinition));
            return;
        }

        if (!TryGetTemplateRoiGeometry(out var x, out var y, out var width, out var height))
        {
            return;
        }

        var shape = GetTemplateShape();
        var definition = new RoiDefinition
        {
            Id = "template-roi",
            Name = "Template ROI",
            Shape = shape,
            X = shape is RoiShapeKind.Circle or RoiShapeKind.RotatedRectangle ? x + width / 2.0 : x,
            Y = shape is RoiShapeKind.Circle or RoiShapeKind.RotatedRectangle ? y + height / 2.0 : y,
            Width = width,
            Height = height,
            Radius = Math.Min(width, height) / 2.0,
            Angle = GetDouble(_parameters, "templateRoiAngle", GetDouble(_parameters, "templateAngle", 0))
        };

        SetTemplateRoiEditor(RoiEditorItem.FromDefinition(definition));
    }

    private void LoadTemplateMaskEditors()
    {
        if (HasLearnedTemplateModel(_parameters))
        {
            return;
        }

        foreach (var maskRoi in ReadTemplateMaskRois())
        {
            AddTemplateMaskEditor(RoiEditorItem.FromDefinition(maskRoi));
        }
    }

    private void EnsureTemplateMaskEditorsLoaded()
    {
        if (_templateMaskEditors.Count > 0)
        {
            return;
        }

        foreach (var maskRoi in ReadTemplateMaskRois())
        {
            AddTemplateMaskEditor(RoiEditorItem.FromDefinition(maskRoi));
        }
    }

    private void SetTemplateRoiEditor(RoiEditorItem item)
    {
        RemoveEditor(_templateRoiEditor);
        _templateRoiEditor = item;
        AddEditor(item);
        SelectedEditableRoi = item;
        SyncTemplateRoiParameters();
        RefreshPreviewOverlays();
    }

    private void HideTemplateRoiEditor()
    {
        RemoveEditor(_templateRoiEditor);
        _templateRoiEditor = null;
        RemoveTemplateMaskEditors();
        SelectedEditableRoi = _searchRoiEditor;
        ShowTemplateRegion = false;
    }

    private void AddTemplateMaskEditor(RoiEditorItem item)
    {
        if (_templateMaskEditors.Any(editor => string.Equals(editor.Id, item.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _templateMaskEditors.Add(item);
        AddEditor(item);
        SelectedEditableRoi = item;
        SyncTemplateMaskParameters();
    }

    private void RemoveTemplateMaskEditors()
    {
        foreach (var editor in _templateMaskEditors.ToArray())
        {
            RemoveEditor(editor);
        }

        _templateMaskEditors.Clear();
    }

    private void AddEditor(RoiEditorItem item)
    {
        if (!EditableRois.Any(roi => string.Equals(roi.Id, item.Id, StringComparison.OrdinalIgnoreCase)))
        {
            item.PropertyChanged += OnEditableRoiChanged;
            if (ReferenceEquals(item, _searchRoiEditor))
            {
                EditableRois.Insert(0, item);
            }
            else
            {
                EditableRois.Add(item);
            }
        }
    }

    private void RemoveEditor(RoiEditorItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.PropertyChanged -= OnEditableRoiChanged;
        EditableRois.Remove(item);
    }

    private void OnEditableRoiChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RoiEditorItem item)
        {
            return;
        }

        if (ReferenceEquals(item, _templateRoiEditor))
        {
            SyncTemplateRoiParameters();
        }
        else if (_templateMaskEditors.Contains(item))
        {
            SyncTemplateMaskParameters();
        }
        else if (ReferenceEquals(item, _searchRoiEditor))
        {
            _mappedSearchRoi = null;
            _roiReferenceDirty = _toolKind == VisionToolKind.MultiTargetMatch;
            UpsertCreatedRoi(item.ToDefinition());
        }

        RefreshPreviewOverlays();
    }

    private void SyncEditorRois()
    {
        if (_searchRoiEditor is not null)
        {
            UpsertCreatedRoi(_searchRoiEditor.ToDefinition());
        }

        SyncTemplateRoiParameters();
        SyncTemplateMaskParameters();
    }

    private void SyncTemplateRoiParameters()
    {
        if (_templateRoiEditor is null)
        {
            return;
        }

        var bounds = GetRoiBounds(_templateRoiEditor);
        _parameters["templateRoiJson"] = JsonSerializer.Serialize(_templateRoiEditor.ToDefinition());
        _parameters["templateRoiShape"] = _templateRoiEditor.Shape.ToString();
        _parameters["templateRoiX"] = bounds.X.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["templateRoiY"] = bounds.Y.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["templateRoiWidth"] = bounds.Width.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["templateRoiHeight"] = bounds.Height.ToString("0.###", CultureInfo.InvariantCulture);
        _parameters["templateRoiAngle"] = _templateRoiEditor.Angle.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void SyncTemplateMaskParameters()
    {
        if (_templateMaskEditors.Count == 0)
        {
            return;
        }

        var definitions = _templateMaskEditors.Select(editor => editor.ToDefinition()).ToArray();
        _parameters["templateMaskRoisJson"] = JsonSerializer.Serialize(definitions);
    }

    private IReadOnlyList<RoiDefinition> ReadTemplateMaskRois()
    {
        if (!_parameters.TryGetValue("templateMaskRoisJson", out var json) || string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<RoiDefinition>();
        }

        try
        {
            return JsonSerializer.Deserialize<RoiDefinition[]>(json) ?? Array.Empty<RoiDefinition>();
        }
        catch (JsonException)
        {
            _log.Warning("VisionDebug", $"{Name} 模板掩膜 ROI 参数无法读取，已忽略。");
            return Array.Empty<RoiDefinition>();
        }
    }

    private bool TryReadTemplateRoiDefinition(out RoiDefinition definition)
    {
        definition = default!;
        if (!_parameters.TryGetValue("templateRoiJson", out var json) || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RoiDefinition>(json);
            if (parsed is null)
            {
                return false;
            }

            definition = parsed;
            return true;
        }
        catch (JsonException)
        {
            _log.Warning("VisionDebug", $"{Name} 模板 ROI 参数无法读取，已忽略。");
            return false;
        }
    }

    private void UpsertCreatedRoi(RoiDefinition definition)
    {
        var existing = CreatedRois.FirstOrDefault(roi => string.Equals(roi.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var index = CreatedRois.IndexOf(existing);
            CreatedRois[index] = definition;
            return;
        }

        CreatedRois.Add(definition);
    }

    private static RoiBounds GetRoiBounds(RoiEditorItem roi)
    {
        return roi.Shape switch
        {
            RoiShapeKind.Circle => new RoiBounds(roi.X - roi.Radius, roi.Y - roi.Radius, roi.Radius * 2, roi.Radius * 2),
            RoiShapeKind.Polygon when roi.Points.Count > 0 => new RoiBounds(
                roi.Points.Min(point => point.X),
                roi.Points.Min(point => point.Y),
                roi.Points.Max(point => point.X) - roi.Points.Min(point => point.X),
                roi.Points.Max(point => point.Y) - roi.Points.Min(point => point.Y)),
            RoiShapeKind.RotatedRectangle => new RoiBounds(roi.X - roi.Width / 2.0, roi.Y - roi.Height / 2.0, roi.Width, roi.Height),
            _ => new RoiBounds(roi.X, roi.Y, roi.Width, roi.Height)
        };
    }

    private RoiShapeKind GetTemplateShape()
    {
        return _parameters.TryGetValue("templateRoiShape", out var shape) &&
               Enum.TryParse<RoiShapeKind>(shape, true, out var parsed)
            ? parsed
            : RoiShapeKind.Rectangle;
    }

    private bool TryGetTemplateRoiGeometry(out double x, out double y, out double width, out double height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;

        if (TryGetDouble(_parameters, "templateRoiX", out x) &&
            TryGetDouble(_parameters, "templateRoiY", out y) &&
            TryGetDouble(_parameters, "templateRoiWidth", out width) &&
            TryGetDouble(_parameters, "templateRoiHeight", out height))
        {
            return true;
        }

        return TryGetDouble(_parameters, "templateX", out x) &&
               TryGetDouble(_parameters, "templateY", out y) &&
               TryGetDouble(_parameters, "templateWidth", out width) &&
               TryGetDouble(_parameters, "templateHeight", out height);
    }

    private static TemplateMatchingEngine ResolveSelectedEngine(
        IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue(TemplateMatchingParameterCatalog.Engine, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return TemplateMatchingEngine.OpenCv;
        }

        return Enum.TryParse<TemplateMatchingEngine>(raw.Trim(), true, out var engine) &&
               engine != TemplateMatchingEngine.Unknown
            ? engine
            : TemplateMatchingEngine.Unknown;
    }

    private void LoadHalconEditorValues(IReadOnlyDictionary<string, string> parameters)
    {
        var defaults = TemplateMatchingParameterCatalog.CreateStrictDefaults(GetCardinality());
        _halconAngleStartDeg = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.AngleStartDeg);
        _halconAngleExtentDeg = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.AngleExtentDeg);
        _halconScaleMin = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.ScaleMin);
        _halconScaleMax = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.ScaleMax);
        _halconCandidateMinScore = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.CandidateMinScore);
        _halconOuterCoverageMin = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.OuterCoverageMin);
        _halconInnerCoverageMin = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.InnerCoverageMin);
        _halconEdgeTolerancePx = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.EdgeTolerancePx);
        _halconPolarityAgreementMin = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.PolarityAgreementMin);
        _halconCandidateMaxOverlap = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.CandidateMaxOverlap);
        _halconMaxOverlap = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.MaxOverlap);
        _halconGreediness = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.Greediness);
        _halconSubPixel = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.SubPixel);
        _halconNumLevels = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.NumLevels);
        _halconOperatorTimeoutMs = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.OperatorTimeoutMs);
        _halconCandidateLimit = GetRaw(parameters, defaults, TemplateMatchingParameterCatalog.CandidateLimit);
        var defaultExpectedCount = _toolKind == VisionToolKind.MultiTargetMatch
            ? defaults[TemplateMatchingParameterCatalog.ExpectedCount]
            : "1";
        _halconExpectedCount = parameters.TryGetValue(TemplateMatchingParameterCatalog.ExpectedCount, out var expected)
            ? expected
            : _selectedEngine == TemplateMatchingEngine.Halcon &&
              parameters.TryGetValue(TemplateMatchingParameterCatalog.LegacyMatchCount, out var legacy)
                ? legacy
                : defaultExpectedCount;
    }

    private TemplateMatchingPreset? DetectPreset()
    {
        if (SelectedEngine != TemplateMatchingEngine.Halcon)
        {
            return null;
        }

        foreach (var preset in PresetOptions)
        {
            var values = TemplateMatchingParameterCatalog.CreateDefaults(preset, GetCardinality());
            if (HalconEditorValuesMatch(values))
            {
                return preset;
            }
        }

        return null;
    }

    private bool HalconEditorValuesMatch(IReadOnlyDictionary<string, string> values)
    {
        return string.Equals(HalconAngleStartDeg, values[TemplateMatchingParameterCatalog.AngleStartDeg], StringComparison.Ordinal) &&
               string.Equals(HalconAngleExtentDeg, values[TemplateMatchingParameterCatalog.AngleExtentDeg], StringComparison.Ordinal) &&
               string.Equals(HalconScaleMin, values[TemplateMatchingParameterCatalog.ScaleMin], StringComparison.Ordinal) &&
               string.Equals(HalconScaleMax, values[TemplateMatchingParameterCatalog.ScaleMax], StringComparison.Ordinal) &&
               string.Equals(HalconCandidateMinScore, values[TemplateMatchingParameterCatalog.CandidateMinScore], StringComparison.Ordinal) &&
               string.Equals(HalconOuterCoverageMin, values[TemplateMatchingParameterCatalog.OuterCoverageMin], StringComparison.Ordinal) &&
               string.Equals(HalconInnerCoverageMin, values[TemplateMatchingParameterCatalog.InnerCoverageMin], StringComparison.Ordinal) &&
               string.Equals(HalconEdgeTolerancePx, values[TemplateMatchingParameterCatalog.EdgeTolerancePx], StringComparison.Ordinal) &&
               string.Equals(HalconPolarityAgreementMin, values[TemplateMatchingParameterCatalog.PolarityAgreementMin], StringComparison.Ordinal) &&
               string.Equals(HalconCandidateMaxOverlap, values[TemplateMatchingParameterCatalog.CandidateMaxOverlap], StringComparison.Ordinal) &&
               string.Equals(HalconMaxOverlap, values[TemplateMatchingParameterCatalog.MaxOverlap], StringComparison.Ordinal) &&
               string.Equals(HalconGreediness, values[TemplateMatchingParameterCatalog.Greediness], StringComparison.Ordinal) &&
               string.Equals(HalconSubPixel, values[TemplateMatchingParameterCatalog.SubPixel], StringComparison.Ordinal) &&
               string.Equals(HalconNumLevels, values[TemplateMatchingParameterCatalog.NumLevels], StringComparison.Ordinal) &&
               string.Equals(HalconOperatorTimeoutMs, values[TemplateMatchingParameterCatalog.OperatorTimeoutMs], StringComparison.Ordinal) &&
               string.Equals(HalconCandidateLimit, values[TemplateMatchingParameterCatalog.CandidateLimit], StringComparison.Ordinal);
    }

    private void ApplyPreset(TemplateMatchingPreset preset)
    {
        var values = TemplateMatchingParameterCatalog.CreateDefaults(preset, GetCardinality());
        var generationChanged =
            !string.Equals(HalconAngleStartDeg, values[TemplateMatchingParameterCatalog.AngleStartDeg], StringComparison.Ordinal) ||
            !string.Equals(HalconAngleExtentDeg, values[TemplateMatchingParameterCatalog.AngleExtentDeg], StringComparison.Ordinal) ||
            !string.Equals(HalconScaleMin, values[TemplateMatchingParameterCatalog.ScaleMin], StringComparison.Ordinal) ||
            !string.Equals(HalconScaleMax, values[TemplateMatchingParameterCatalog.ScaleMax], StringComparison.Ordinal) ||
            !string.Equals(HalconNumLevels, values[TemplateMatchingParameterCatalog.NumLevels], StringComparison.Ordinal);
        _applyingPreset = true;
        try
        {
            HalconAngleStartDeg = values[TemplateMatchingParameterCatalog.AngleStartDeg];
            HalconAngleExtentDeg = values[TemplateMatchingParameterCatalog.AngleExtentDeg];
            HalconScaleMin = values[TemplateMatchingParameterCatalog.ScaleMin];
            HalconScaleMax = values[TemplateMatchingParameterCatalog.ScaleMax];
            HalconCandidateMinScore = values[TemplateMatchingParameterCatalog.CandidateMinScore];
            HalconOuterCoverageMin = values[TemplateMatchingParameterCatalog.OuterCoverageMin];
            HalconInnerCoverageMin = values[TemplateMatchingParameterCatalog.InnerCoverageMin];
            HalconEdgeTolerancePx = values[TemplateMatchingParameterCatalog.EdgeTolerancePx];
            HalconPolarityAgreementMin = values[TemplateMatchingParameterCatalog.PolarityAgreementMin];
            HalconCandidateMaxOverlap = values[TemplateMatchingParameterCatalog.CandidateMaxOverlap];
            HalconMaxOverlap = values[TemplateMatchingParameterCatalog.MaxOverlap];
            HalconGreediness = values[TemplateMatchingParameterCatalog.Greediness];
            HalconSubPixel = values[TemplateMatchingParameterCatalog.SubPixel];
            HalconNumLevels = values[TemplateMatchingParameterCatalog.NumLevels];
            HalconOperatorTimeoutMs = values[TemplateMatchingParameterCatalog.OperatorTimeoutMs];
            HalconCandidateLimit = values[TemplateMatchingParameterCatalog.CandidateLimit];
        }
        finally
        {
            _applyingPreset = false;
        }

        SetProperty(ref _selectedPreset, preset, nameof(SelectedPreset));
        if (generationChanged)
        {
            _halconLearnedSuccessfullyInSession = false;
            RequiresRelearn = true;
        }
    }

    private bool SetHalconEditorValue(
        ref string storage,
        string? value,
        bool requiresRelearn = false,
        [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref storage, value ?? string.Empty, propertyName))
        {
            return false;
        }

        if (!_applyingPreset)
        {
            if (_selectedPreset is not null)
            {
                _selectedPreset = null;
                RaisePropertyChanged(nameof(SelectedPreset));
            }

            if (requiresRelearn)
            {
                _halconLearnedSuccessfullyInSession = false;
                RequiresRelearn = true;
            }
        }

        return true;
    }

    private TemplateMatchCardinality GetCardinality()
    {
        return _toolKind == VisionToolKind.MultiTargetMatch
            ? TemplateMatchCardinality.ExactCount
            : TemplateMatchCardinality.Single;
    }

    private static string GetRaw(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> defaults,
        string key)
    {
        return parameters.TryGetValue(key, out var value) ? value : defaults[key];
    }

    private void OverlayHalconEditorValues(IDictionary<string, string> parameters)
    {
        parameters[TemplateMatchingParameterCatalog.AngleStartDeg] = HalconAngleStartDeg;
        parameters[TemplateMatchingParameterCatalog.AngleExtentDeg] = HalconAngleExtentDeg;
        parameters[TemplateMatchingParameterCatalog.ScaleMin] = HalconScaleMin;
        parameters[TemplateMatchingParameterCatalog.ScaleMax] = HalconScaleMax;
        parameters[TemplateMatchingParameterCatalog.CandidateMinScore] = HalconCandidateMinScore;
        parameters[TemplateMatchingParameterCatalog.OuterCoverageMin] = HalconOuterCoverageMin;
        parameters[TemplateMatchingParameterCatalog.InnerCoverageMin] = HalconInnerCoverageMin;
        parameters[TemplateMatchingParameterCatalog.EdgeTolerancePx] = HalconEdgeTolerancePx;
        parameters[TemplateMatchingParameterCatalog.PolarityAgreementMin] = HalconPolarityAgreementMin;
        parameters[TemplateMatchingParameterCatalog.CandidateMaxOverlap] = HalconCandidateMaxOverlap;
        parameters[TemplateMatchingParameterCatalog.MaxOverlap] = HalconMaxOverlap;
        parameters[TemplateMatchingParameterCatalog.Greediness] = HalconGreediness;
        parameters[TemplateMatchingParameterCatalog.SubPixel] = HalconSubPixel;
        parameters[TemplateMatchingParameterCatalog.NumLevels] = HalconNumLevels;
        parameters[TemplateMatchingParameterCatalog.OperatorTimeoutMs] = HalconOperatorTimeoutMs;
        parameters[TemplateMatchingParameterCatalog.CandidateLimit] = HalconCandidateLimit;
        if (_toolKind == VisionToolKind.MultiTargetMatch)
        {
            parameters[TemplateMatchingParameterCatalog.ExpectedCount] = HalconExpectedCount;
        }
    }

    private static string FormatDiagnostic(TemplateMatchingDiagnostic diagnostic)
    {
        return $"{diagnostic.Code}: {diagnostic.UserMessage}";
    }

    private static string FormatDiagnostic(TemplateMatchingConfigurationException exception)
    {
        return $"{exception.Code}: {exception.Message}";
    }

    private Dictionary<string, string> BuildCurrentParameters()
    {
        var parameters = new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase)
        {
            [TemplateMatchingParameterCatalog.Engine] = SelectedEngine.ToString(),
            ["templateShape"] = SelectedTemplateShape,
            ["showTemplateRegion"] = ShowTemplateRegion.ToString(),
            ["showSearchRegion"] = ShowSearchRegion.ToString(),
            ["showCrosshair"] = ShowCrosshair.ToString(),
            ["useSubPixel"] = UseSubPixel.ToString(),
            ["enabledOutputs"] = FormatEnabledOutputKeys()
        };

        if (SelectedEngine is TemplateMatchingEngine.OpenCv or TemplateMatchingEngine.ManagedNcc)
        {
            parameters["minScore"] = MinScore.ToString("0.###", CultureInfo.InvariantCulture);
            parameters["pyramidLevels"] = PyramidLevels.ToString(CultureInfo.InvariantCulture);
            parameters["angleStart"] = AngleStart.ToString("0.###", CultureInfo.InvariantCulture);
            parameters["angleExtent"] = AngleExtent.ToString("0.###", CultureInfo.InvariantCulture);
            parameters["angleStep"] = GetParameterValue("angleStep", "2");
            parameters[TemplateMatchingParameterCatalog.MatchMode] = _toolKind == VisionToolKind.MultiTargetMatch
                ? _openCvMatchModeState
                : SelectedMatchMode;
            if (_toolKind == VisionToolKind.MultiTargetMatch)
            {
                parameters["multiMatchMode"] = SelectedMatchMode;
            }

            parameters.Remove(OpenCvMatchModeStateKey);
            parameters.Remove(OpenCvMultiMatchModeStateKey);
            parameters["autoLearnTemplate"] = GetParameterValue("autoLearnTemplate", "False");
            parameters["cannyLow"] = GetEdgeCannyLow().ToString("0.###", CultureInfo.InvariantCulture);
            parameters["cannyHigh"] = GetEdgeCannyHigh().ToString("0.###", CultureInfo.InvariantCulture);
            parameters["polarity"] = Polarity;
            parameters["contrast"] = Contrast.ToString("0.###", CultureInfo.InvariantCulture);
            parameters["autoContrast"] = AutoContrast.ToString();
            parameters[TemplateMatchingParameterCatalog.LegacyMatchCount] =
                MatchCount.ToString(CultureInfo.InvariantCulture);

            if (_toolKind == VisionToolKind.MultiTargetMatch)
            {
                parameters.Remove("shapeScoreVersion");
                parameters.Remove("shapeCoverageDistance");
            }
            else if (SelectedMatchMode.Equals("Shape", StringComparison.OrdinalIgnoreCase))
            {
                parameters["shapeScoreVersion"] = "2";
                parameters["shapeCoverageDistance"] = GetParameterValue("shapeCoverageDistance", "3");
            }
        }
        else if (SelectedEngine == TemplateMatchingEngine.Halcon)
        {
            if (_rejectLoadedHalconMode)
            {
                throw new TemplateMatchingConfigurationException(
                    TemplateMatchingDiagnostics.Create(
                        TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode,
                        "HALCON template matching supports Shape mode only."));
            }

            parameters[OpenCvMatchModeStateKey] = _openCvMatchModeState;
            if (_toolKind == VisionToolKind.MultiTargetMatch)
            {
                parameters[OpenCvMultiMatchModeStateKey] = _openCvMultiMatchModeState;
                parameters["multiMatchMode"] = "Shape";
            }

            OverlayHalconEditorValues(parameters);
            parameters[TemplateMatchingParameterCatalog.MatchMode] = "Shape";
            _ = TemplateMatchingParameterCatalog.ParseHalcon(parameters, GetCardinality());
        }
        else
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigUnknownEngine,
                    $"Unknown template matching engine '{_parameters.GetValueOrDefault(TemplateMatchingParameterCatalog.Engine)}'."));
        }

        return parameters;
    }

    private double GetEdgeCannyLow()
    {
        if (AutoContrast)
        {
            return GetDouble(_parameters, "cannyLow", 60);
        }

        return Math.Clamp(Contrast, 1, 254);
    }

    private double GetEdgeCannyHigh()
    {
        if (AutoContrast)
        {
            return GetDouble(_parameters, "cannyHigh", 160);
        }

        return Math.Clamp(Math.Max(GetEdgeCannyLow() + 1, Contrast * 2.5), 2, 255);
    }

    private string FormatEnabledOutputKeys()
    {
        var keys = OutputOptions
            .Where(option => option.IsEnabled)
            .Select(option => option.Key)
            .ToArray();
        return keys.Length == 0
            ? "ResultOutput"
            : string.Join(",", keys);
    }

    private static IEnumerable<ToolOutputOptionItem> CreateOutputOptions(VisionToolKind kind, IReadOnlyDictionary<string, string> parameters)
    {
        var enabled = ParseEnabledOutputKeys(parameters.GetValueOrDefault("enabledOutputs"));
        if (kind == VisionToolKind.MultiTargetMatch)
        {
            if (enabled.Contains("BestPositionOutput"))
            {
                enabled.Add("PositionOutput");
                enabled.Add("OriginOutput");
            }

            yield return new ToolOutputOptionItem("CountOutput", "数量", "Number", enabled.Contains("CountOutput"));
            yield return new ToolOutputOptionItem("PositionOutput", "位置", "Pose", enabled.Contains("PositionOutput"));
            yield return new ToolOutputOptionItem("ScoreOutput", "分数", "Number", enabled.Contains("ScoreOutput"));
            yield return new ToolOutputOptionItem("XOutput", "X坐标", "Number", enabled.Contains("XOutput"));
            yield return new ToolOutputOptionItem("YOutput", "Y坐标", "Number", enabled.Contains("YOutput"));
            yield return new ToolOutputOptionItem("AngleOutput", "角度", "Number", enabled.Contains("AngleOutput"));
            yield return new ToolOutputOptionItem("OriginOutput", "训练原点", "Pose", enabled.Contains("OriginOutput"));
            yield return new ToolOutputOptionItem("BestPositionOutput", "最佳位置", "Pose", enabled.Contains("BestPositionOutput"));
            yield return new ToolOutputOptionItem("AllPositionsOutput", "全部位置", "Pose[]", enabled.Contains("AllPositionsOutput"));
            yield return new ToolOutputOptionItem("ScoresOutput", "全部分数", "Number[]", enabled.Contains("ScoresOutput"));
            yield return new ToolOutputOptionItem("ScalesOutput", "全部尺度", "Number[]", enabled.Contains("ScalesOutput"));
            yield return new ToolOutputOptionItem("ResultOutput", "OK/NG", "Result", enabled.Contains("ResultOutput"));
            yield break;
        }

        yield return new ToolOutputOptionItem("PositionOutput", "位置", "Pose", enabled.Contains("PositionOutput"));
        yield return new ToolOutputOptionItem("ScoreOutput", "分数", "Number", enabled.Contains("ScoreOutput"));
        yield return new ToolOutputOptionItem("XOutput", "X坐标", "Number", enabled.Contains("XOutput"));
        yield return new ToolOutputOptionItem("YOutput", "Y坐标", "Number", enabled.Contains("YOutput"));
        yield return new ToolOutputOptionItem("AngleOutput", "角度", "Number", enabled.Contains("AngleOutput"));
        yield return new ToolOutputOptionItem("ScaleOutput", "尺度", "Number", enabled.Contains("ScaleOutput"));
        yield return new ToolOutputOptionItem("OriginOutput", "训练原点", "Pose", enabled.Contains("OriginOutput"));
        yield return new ToolOutputOptionItem("ResultOutput", "OK/NG", "Result", enabled.Contains("ResultOutput"));
    }

    private static HashSet<string> ParseEnabledOutputKeys(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>(
                ["PositionOutput", "OriginOutput", "ScaleOutput", "ResultOutput", "CountOutput", "BestPositionOutput", "ScalesOutput"],
                StringComparer.OrdinalIgnoreCase);
        }

        var keys = text
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keys.Count == 0
            ? new HashSet<string>(
                ["PositionOutput", "OriginOutput", "ScaleOutput", "ResultOutput", "CountOutput", "BestPositionOutput", "ScalesOutput"],
                StringComparer.OrdinalIgnoreCase)
            : keys;
    }

    private static string FormatParameters(IReadOnlyDictionary<string, string> parameters)
    {
        return string.Join("; ", parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Key))
            .Select(parameter => $"{parameter.Key.Trim()}={parameter.Value}"));
    }

    private string GetParameterValue(string key, string fallback)
    {
        return _parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string NormalizeMatchMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Shape";
        }

        return value.Trim() switch
        {
            "Gray" => "GrayNcc",
            "Ncc" => "GrayNcc",
            "NCC" => "GrayNcc",
            "GrayNcc" => "GrayNcc",
            "GrayCcorr" => "GrayCcorr",
            "GraySqDiff" => "GraySqDiff",
            "FeatureOrb" => "FeatureOrb",
            "ORB" => "FeatureOrb",
            "Circle" or "Circular" or "CircularBlob" or "BlobCircle" => "CircularBlob",
            _ => "Shape"
        };
    }

    private static string GetMatchModeName(string value)
    {
        return NormalizeMatchMode(value) switch
        {
            "GrayNcc" => "灰度NCC",
            "GrayCcorr" => "灰度相关",
            "GraySqDiff" => "灰度差异",
            "FeatureOrb" => "特征点ORB",
            "CircularBlob" => "圆形目标",
            _ => "形状匹配"
        };
    }

    private static bool HasLearnedTemplateModel(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("templateImagePng", out var png) && !string.IsNullOrWhiteSpace(png))
        {
            return true;
        }

        // Check for file-based model
        if (parameters.TryGetValue("modelPath", out var modelPath) &&
            !string.IsNullOrWhiteSpace(modelPath) &&
            File.Exists(modelPath))
        {
            return true;
        }

        // Backward-compatible Base64 check
        return GetInt(parameters, "templateWidth", 0) >= 12 &&
               GetInt(parameters, "templateHeight", 0) >= 12 &&
               parameters.TryGetValue("templatePixels", out var pixels) &&
               !string.IsNullOrWhiteSpace(pixels);
    }

    private RoiDefinition? FindSelectedRoi()
    {
        if (string.IsNullOrWhiteSpace(RoiId))
        {
            return null;
        }

        return CreatedRois.FirstOrDefault(roi => string.Equals(roi.Id, RoiId, StringComparison.OrdinalIgnoreCase))
               ?? _availableRois.FirstOrDefault(roi => string.Equals(roi.Id, RoiId, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveTemplateModelParameters()
    {
        foreach (var key in new[]
                 {
                     "templateVersion",
                     "templateX",
                     "templateY",
                     "templateWidth",
                     "templateHeight",
                     "templateRoiJson",
                     "templateRoiShape",
                     "templateRoiX",
                     "templateRoiY",
                     "templateRoiWidth",
                     "templateRoiHeight",
                     "templateRoiAngle",
                     "templateAngle",
                     "templateFrameWidth",
                     "templateFrameHeight",
                     "templatePixels",
                     "templateImagePng",
                     "templatePreviewPng",
                     "templateEdgeOverlayPng",
                     "templateMaskRoisJson",
                     "templateMaskPng",
                     "templateSourceRoiId",
                     "standardX",
                     "standardY",
                     "standardAngle",
                     "standardScale"
                 })
        {
            _parameters.Remove(key);
        }
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

    private static string GetString(IReadOnlyDictionary<string, string> parameters, string key, string fallback)
    {
        return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> parameters, string key, int fallback)
    {
        return parameters.TryGetValue(key, out var value) && int.TryParse(value, out var result)
            ? result
            : fallback;
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

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> parameters, string key, out double result)
    {
        result = 0;
        return parameters.TryGetValue(key, out var value) &&
               double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private enum RoiPlacementTarget
    {
        None,
        Search,
        Template,
        TemplateMask
    }

    private enum PoseReadStatus
    {
        Missing,
        Success,
        Invalid
    }

    private sealed record PoseReadResult(PoseReadStatus Status, Pose2D? Pose, string ErrorMessage)
    {
        public static PoseReadResult Missing()
        {
            return new PoseReadResult(PoseReadStatus.Missing, null, string.Empty);
        }

        public static PoseReadResult Success(Pose2D pose)
        {
            return new PoseReadResult(PoseReadStatus.Success, pose, string.Empty);
        }

        public static PoseReadResult Invalid(string errorMessage)
        {
            return new PoseReadResult(PoseReadStatus.Invalid, null, errorMessage);
        }
    }

    private sealed record RoiMappingResult(bool IsSuccess, RoiDefinition? Roi, string ErrorMessage)
    {
        public static RoiMappingResult Success(RoiDefinition? roi)
        {
            return new RoiMappingResult(true, roi, string.Empty);
        }

        public static RoiMappingResult Failure(string errorMessage)
        {
            return new RoiMappingResult(false, null, errorMessage);
        }
    }

    private sealed record RoiReferenceCaptureResult(bool IsSuccess, PoseReadResult? CurrentPose)
    {
        public static RoiReferenceCaptureResult Success(PoseReadResult? currentPose = null)
        {
            return new RoiReferenceCaptureResult(true, currentPose);
        }

        public static RoiReferenceCaptureResult Failure(PoseReadResult currentPose)
        {
            return new RoiReferenceCaptureResult(false, currentPose);
        }
    }

    private sealed record RoiBounds(double X, double Y, double Width, double Height);
}

public sealed class TemplateLocateTabItem : BindableBase
{
    private bool _isSelected;

    public TemplateLocateTabItem(string key, string title, string icon)
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

public sealed class TemplateShapeItem : BindableBase
{
    private bool _isSelected;

    public TemplateShapeItem(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed record TemplateMatchModeItem(string Key, string Name, string Description)
{
    public override string ToString()
    {
        return Name;
    }
}
