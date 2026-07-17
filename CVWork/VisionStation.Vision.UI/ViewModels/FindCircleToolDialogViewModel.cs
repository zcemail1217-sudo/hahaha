using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.Services;
using VisionStation.Domain;
using VisionStation.Vision;
using VisionStation.Vision.Tools;

namespace VisionStation.Vision.UI.ViewModels;

public sealed class FindCircleToolDialogViewModel : BindableBase
{
    private readonly IAppLogService _log;
    private readonly IVisionPipeline _pipeline;
    private readonly Recipe? _previewRecipe;
    private readonly string _toolId;
    private readonly IReadOnlyList<RoiDefinition> _availableRois;
    private readonly Dictionary<string, string> _parameters;
    private string _name;
    private bool _enabled;
    private string _roiId;
    private RoiEditorItem? _circleRoiEditor;
    private RoiEditorItem? _selectedEditableRoi;
    private string _selectedTabKey = "Home";
    private bool _isRoiPlacementArmed;
    private string _roiPlacementHint = string.Empty;
    private double _minScore;
    private int _caliperCount;
    private double _edgeThreshold;
    private string _polarity;
    private string _searchDirection;
    private string _resultSelection;
    private double _caliperWidth;
    private double _searchWidth;
    private bool _showCrosshair = true;
    private bool _isBusy;
    private string _scoreText = "-";
    private string _durationText = "0ms";
    private string _statusText = "等待运行";
    private string _centerRowText = "-";
    private string _centerColumnText = "-";
    private string _radiusText = "-";
    private ToolResult? _previewResult;
    private bool _roiReferenceDirty;
    private bool _syncingCircleEditor;

    public FindCircleToolDialogViewModel(
        VisionToolItem tool,
        IReadOnlyList<RoiDefinition> rois,
        string flowName,
        ImageFrame? currentFrame,
        Recipe? previewRecipe,
        IVisionPipeline pipeline,
        IAppLogService log)
    {
        _log = log;
        _pipeline = pipeline;
        _previewRecipe = previewRecipe;
        _toolId = tool.Id;
        _availableRois = rois;
        _parameters = ParseParameters(tool.ParametersText);
        _roiReferenceDirty = !HasRoiReferencePose(_parameters);
        _name = tool.Name;
        _enabled = tool.Enabled;
        _roiId = tool.RoiId;
        _minScore = Math.Clamp(GetDouble(_parameters, "minScore", 0.5), 0, 1);
        _caliperCount = Math.Clamp(GetInt(_parameters, "caliperCount", 24), 3, 720);
        _edgeThreshold = Math.Clamp(GetDouble(_parameters, "edgeThreshold", 30), 0, 255);
        _polarity = GetString(_parameters, "circlePolarity", "从暗到明");
        _searchDirection = GetString(_parameters, "searchDirection", "从内到外");
        _resultSelection = GetString(_parameters, "resultSelection", "最强");
        _caliperWidth = Math.Clamp(GetDouble(_parameters, "caliperWidth", 4), 1, 200);
        _searchWidth = Math.Clamp(GetDouble(_parameters, "searchWidth", 24), 2, 1000);
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
        Polarities = new ObservableCollection<string> { "从暗到明", "从明到暗", "全部" };
        SearchDirections = new ObservableCollection<string> { "从内到外", "从外到内" };
        ResultSelections = new ObservableCollection<string> { "最强", "第一个", "最后一个" };
        OutputOptions = new ObservableCollection<ToolOutputOptionItem>(CreateOutputOptions(_parameters));
        Tabs =
        [
            new FindCircleTabItem("Home", "主页"),
            new FindCircleTabItem("Output", "输出项"),
            new FindCircleTabItem("Display", "显示")
        ];

        SelectTab(Tabs[0]);
        SelectTabCommand = new DelegateCommand<FindCircleTabItem>(SelectTab);
        EditCaliperCommand = new DelegateCommand(ArmRoiPlacement, () => !IsMissingInputImage);
        PlaceRoiCommand = new DelegateCommand<Point2D>(PlaceCircleRoi);
        RunToolCommand = new DelegateCommand(async () => await RunToolAsync(), () => !IsBusy && !IsMissingInputImage)
            .ObservesProperty(() => IsBusy);
        RunFlowCommand = new DelegateCommand(async () => await RunFlowAsync(), () => !IsBusy && !IsMissingInputImage)
            .ObservesProperty(() => IsBusy);
        CloseCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, true));
        CancelCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, false));

        LoadCircleRoiEditor();
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

    public ObservableCollection<string> Polarities { get; }

    public ObservableCollection<string> SearchDirections { get; }

    public ObservableCollection<string> ResultSelections { get; }

    public ObservableCollection<ToolOutputOptionItem> OutputOptions { get; }

    public ObservableCollection<FindCircleTabItem> Tabs { get; }

    public DelegateCommand<FindCircleTabItem> SelectTabCommand { get; }

    public DelegateCommand EditCaliperCommand { get; }

    public DelegateCommand<Point2D> PlaceRoiCommand { get; }

    public DelegateCommand RunToolCommand { get; }

    public DelegateCommand RunFlowCommand { get; }

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
                RaisePropertyChanged(nameof(IsOutputTab));
                RaisePropertyChanged(nameof(IsDisplayTab));
            }
        }
    }

    public bool IsHomeTab => SelectedTabKey == "Home";

    public bool IsOutputTab => SelectedTabKey == "Output";

    public bool IsDisplayTab => SelectedTabKey == "Display";

    public double MinScore
    {
        get => _minScore;
        set => SetProperty(ref _minScore, Math.Clamp(value, 0, 1));
    }

    public int CaliperCount
    {
        get => _caliperCount;
        set
        {
            if (SetProperty(ref _caliperCount, Math.Clamp(value, 3, 720)))
            {
                ClearResultPreview();
            }
        }
    }

    public double EdgeThreshold
    {
        get => _edgeThreshold;
        set => SetProperty(ref _edgeThreshold, Math.Clamp(value, 0, 255));
    }

    public string Polarity
    {
        get => _polarity;
        set => SetProperty(ref _polarity, value);
    }

    public string SearchDirection
    {
        get => _searchDirection;
        set => SetProperty(ref _searchDirection, value);
    }

    public string ResultSelection
    {
        get => _resultSelection;
        set => SetProperty(ref _resultSelection, value);
    }

    public double CaliperWidth
    {
        get => _caliperWidth;
        set
        {
            if (SetProperty(ref _caliperWidth, Math.Clamp(value, 1, 200)))
            {
                ClearResultPreview();
            }
        }
    }

    public double SearchWidth
    {
        get => _searchWidth;
        set
        {
            var width = Math.Clamp(value, 2, 1000);
            if (SetProperty(ref _searchWidth, width))
            {
                UpdateCircleEditorSearchWidth(width);
                ClearResultPreview();
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

    public string ScoreText
    {
        get => _scoreText;
        private set => SetProperty(ref _scoreText, value);
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

    public string CenterRowText
    {
        get => _centerRowText;
        private set => SetProperty(ref _centerRowText, value);
    }

    public string CenterColumnText
    {
        get => _centerColumnText;
        private set => SetProperty(ref _centerColumnText, value);
    }

    public string RadiusText
    {
        get => _radiusText;
        private set => SetProperty(ref _radiusText, value);
    }

    public async Task<bool> ApplyToAsync(VisionToolItem tool)
    {
        SyncCircleRoi();
        if (!await CaptureRoiReferencePoseIfNeededAsync())
        {
            return false;
        }

        tool.Name = string.IsNullOrWhiteSpace(Name) ? tool.Name : Name.Trim();
        tool.Kind = VisionToolKind.FindCircle;
        tool.Enabled = Enabled;
        tool.RoiId = _roiId;
        tool.ParametersText = FormatParameters(BuildParameters(includeRoiId: false));
        return true;
    }

    private void SelectTab(FindCircleTabItem tab)
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

    private void LoadCircleRoiEditor()
    {
        var definition = FindCircleRoi();
        if (definition is null)
        {
            return;
        }

        SetCircleRoiEditor(RoiEditorItem.FromDefinition(ToCircle(definition)));
    }

    private RoiDefinition? FindCircleRoi()
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

        IsRoiPlacementArmed = true;
        ShowCircleRoiEditor();
        RoiPlacementHint = "在图像上单击放置圆卡尺";
        StatusText = RoiPlacementHint;
    }

    private void PlaceCircleRoi(Point2D point)
    {
        if (!IsRoiPlacementArmed || CurrentFrame is null)
        {
            return;
        }

        QueueRoiRemoval(_circleRoiEditor?.Id);
        var roi = ToolRoiFactory.CreateRoiAt(
            Name,
            VisionToolKind.FindCircle,
            RoiShapeKind.Circle,
            CurrentFrame,
            1,
            point) with
        {
            Name = $"{GetOwnedRoiName()} 圆卡尺"
        };
        roi = roi with
        {
            Name = $"{GetOwnedRoiName()} 圆卡尺",
            Radius = GetDefaultCircleRadius(CurrentFrame)
        };
        _roiId = roi.Id;
        UpsertCreatedRoi(roi);
        SetCircleRoiEditor(RoiEditorItem.FromDefinition(roi));
        _roiReferenceDirty = true;
        IsRoiPlacementArmed = false;
        RoiPlacementHint = string.Empty;
        StatusText = "圆卡尺已放置";
    }

    private void SetCircleRoiEditor(RoiEditorItem editor)
    {
        if (_circleRoiEditor is not null)
        {
            _circleRoiEditor.PropertyChanged -= OnCircleRoiPropertyChanged;
        }

        EditableRois.Clear();
        editor.CaliperSearchWidth = SearchWidth;
        _circleRoiEditor = editor;
        _circleRoiEditor.PropertyChanged += OnCircleRoiPropertyChanged;
        EditableRois.Add(editor);
        SelectedEditableRoi = editor;
        ClearResultPreview();
    }

    private void ShowCircleRoiEditor()
    {
        if (_circleRoiEditor is null || EditableRois.Contains(_circleRoiEditor))
        {
            return;
        }

        EditableRois.Add(_circleRoiEditor);
        SelectedEditableRoi = _circleRoiEditor;
    }

    private void HideCircleRoiEditor()
    {
        if (_circleRoiEditor is null)
        {
            return;
        }

        EditableRois.Remove(_circleRoiEditor);
        if (ReferenceEquals(SelectedEditableRoi, _circleRoiEditor))
        {
            SelectedEditableRoi = null;
        }
    }

    private void SyncCircleRoi()
    {
        if (_circleRoiEditor is null)
        {
            return;
        }

        var definition = _circleRoiEditor.ToDefinition() with
        {
            Shape = RoiShapeKind.Circle,
            Name = string.IsNullOrWhiteSpace(_circleRoiEditor.Name)
                ? $"{GetOwnedRoiName()} 圆卡尺"
                : _circleRoiEditor.Name,
            Width = 0,
            Height = 0,
            Angle = 0,
            Points = Array.Empty<Point2D>()
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

        if (_circleRoiEditor is null)
        {
            StatusText = "请先编辑圆卡尺";
            return;
        }

        IsBusy = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            SyncCircleRoi();
            if (!await CaptureRoiReferencePoseIfNeededAsync())
            {
                return;
            }

            var roi = _circleRoiEditor.ToDefinition();
            var definition = new VisionToolDefinition
            {
                Id = _toolId,
                Name = Name,
                Kind = VisionToolKind.FindCircle,
                Enabled = Enabled,
                Parameters = BuildParameters(includeRoiId: true)
            };
            var recipe = BuildPreviewRecipe(definition, roi);
            var pipelineResult = await _pipeline.ExecuteAsync(recipe, CurrentFrame);
            var result = pipelineResult.ToolResults.LastOrDefault(item => string.Equals(item.ToolId, _toolId, StringComparison.OrdinalIgnoreCase));
            if (result is null)
            {
                StatusText = "预览运行失败：流程没有返回找圆结果";
                return;
            }

            stopwatch.Stop();
            ScoreText = result.Data.GetValueOrDefault("score", "-");
            DurationText = $"{stopwatch.ElapsedMilliseconds}ms";
            StatusText = result.Message;
            UpdateResultValues(result.Data);
            _previewResult = result;
            RefreshPreviewOverlays();
            _log.Info("VisionDebug", $"{Name} {result.Message} score={ScoreText}");
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

    private Recipe BuildPreviewRecipe(VisionToolDefinition definition, RoiDefinition roi)
    {
        var previewRecipe = _previewRecipe ?? new Recipe
        {
            Tools = [definition],
            Rois = [roi]
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
            .Where(item => !string.Equals(item.Id, roi.Id, StringComparison.OrdinalIgnoreCase))
            .Append(roi)
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
            StatusText = "模板位置未就绪。请先运行模板定位，再保存或预览圆卡尺。";
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
        var scale = 1d;
        if (sourceResult.Data.ContainsKey("scale") &&
            (!TryGetDouble(sourceResult.Data, "scale", out scale) ||
             !PoseSimilarityTransform.IsValidScale(scale)))
        {
            return null;
        }

        return new Pose2D(x, y, angle) { Scale = scale };
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
        _parameters["roiReferencePoseScale"] = pose.Scale.ToString("0.###", CultureInfo.InvariantCulture);
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

    private static bool HasRoiReferencePose(IReadOnlyDictionary<string, string> parameters)
    {
        var sourceToolId = parameters.GetValueOrDefault("input:PositionInput:toolId") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return false;
        }

        var referenceToolId = parameters.GetValueOrDefault("roiReferencePoseToolId") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(referenceToolId) &&
            !string.Equals(referenceToolId, sourceToolId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryGetRoiReferencePose(parameters, out _);
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
        var scale = 1d;
        if (parameters.ContainsKey("roiReferencePoseScale") &&
            (!TryGetDouble(parameters, "roiReferencePoseScale", out scale) ||
             !PoseSimilarityTransform.IsValidScale(scale)))
        {
            return false;
        }

        pose = new Pose2D(x, y, angle) { Scale = scale };
        return true;
    }

    private void UpdateResultValues(IReadOnlyDictionary<string, string> data)
    {
        CenterColumnText = FormatCoordinate(data.GetValueOrDefault("x"));
        CenterRowText = FormatCoordinate(data.GetValueOrDefault("y"));
        RadiusText = FormatCoordinate(data.GetValueOrDefault("radius"));
    }

    private void RefreshPreviewOverlays()
    {
        PreviewOverlays.Clear();
        var shouldShowSearchRoi = _previewResult is null || _previewResult.Outcome != InspectionOutcome.Ok;
        if (shouldShowSearchRoi)
        {
            var previewRoi = GetPreviewCircleRoi();
            AddCircleCaliperPreviewOverlays(previewRoi);
        }
        else
        {
            HideCircleRoiEditor();
        }

        if (_previewResult is null ||
            !TryGetDouble(_previewResult.Data, "x", out var x) ||
            !TryGetDouble(_previewResult.Data, "y", out var y) ||
            !TryGetDouble(_previewResult.Data, "radius", out var radius))
        {
            AddFailureMessageOverlay();
            return;
        }

        PreviewOverlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Circle,
            State = _previewResult.Outcome == InspectionOutcome.Ok ? VisionOverlayState.Ok : VisionOverlayState.Ng,
            Label = _previewResult.Outcome == InspectionOutcome.Ok ? string.Empty : _previewResult.Message,
            X = x,
            Y = y,
            Radius = radius
        });
    }

    private void AddFailureMessageOverlay()
    {
        if (_previewResult is null || _previewResult.Outcome == InspectionOutcome.Ok)
        {
            return;
        }

        var roi = GetPreviewCircleRoi() ?? _circleRoiEditor;
        if (roi is null)
        {
            return;
        }

        PreviewOverlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Cross,
            State = VisionOverlayState.Ng,
            Label = _previewResult.Message,
            X = roi.X,
            Y = roi.Y
        });
    }

    private RoiEditorItem? GetPreviewCircleRoi()
    {
        if (_previewResult is null ||
            !TryGetDouble(_previewResult.Data, "searchRoiX", out var x) ||
            !TryGetDouble(_previewResult.Data, "searchRoiY", out var y) ||
            !TryGetDouble(_previewResult.Data, "searchRoiRadius", out var radius))
        {
            return _circleRoiEditor;
        }

        var runtimeRoi = new RoiEditorItem
        {
            Name = "运行圆卡尺",
            Shape = RoiShapeKind.Circle,
            X = x,
            Y = y,
            Radius = radius,
            CaliperSearchWidth = SearchWidth
        };
        if (HasMovedFromEditor(runtimeRoi))
        {
            HideCircleRoiEditor();
        }

        return runtimeRoi;
    }

    private bool HasMovedFromEditor(RoiEditorItem runtimeRoi)
    {
        return _circleRoiEditor is null ||
               Math.Abs(runtimeRoi.X - _circleRoiEditor.X) > 1 ||
               Math.Abs(runtimeRoi.Y - _circleRoiEditor.Y) > 1 ||
               Math.Abs(runtimeRoi.Radius - _circleRoiEditor.Radius) > 1;
    }

    private void AddCircleCaliperPreviewOverlays(RoiEditorItem? roi)
    {
        if (roi is null || roi.Shape != RoiShapeKind.Circle)
        {
            return;
        }

        var radius = Math.Max(1, roi.Radius);
        var searchWidth = Math.Max(2, SearchWidth);
        var caliperWidth = Math.Max(1, CaliperWidth);
        var visualCaliperLength = Math.Clamp(searchWidth * 0.35, 3, Math.Min(searchWidth, 10));
        var visualCaliperWidth = Math.Clamp(caliperWidth, 1, 3);
        var innerRadius = Math.Max(1, radius - searchWidth / 2.0);
        var outerRadius = Math.Max(innerRadius + 1, radius + searchWidth / 2.0);
        PreviewOverlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.CircleAnnulus,
            State = VisionOverlayState.Warning,
            X = roi.X,
            Y = roi.Y,
            Width = innerRadius,
            Radius = outerRadius
        });

        var visibleCalipers = Math.Min(Math.Clamp(CaliperCount, 3, 720), 96);
        for (var index = 0; index < visibleCalipers; index++)
        {
            var angle = index * Math.PI * 2.0 / visibleCalipers;
            PreviewOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangleOutline,
                State = VisionOverlayState.Ng,
                X = roi.X + Math.Cos(angle) * radius,
                Y = roi.Y + Math.Sin(angle) * radius,
                Width = visualCaliperLength,
                Height = visualCaliperWidth,
                Angle = angle * 180.0 / Math.PI
            });
        }
    }

    private void ClearResultPreview()
    {
        _previewResult = null;
        ShowCircleRoiEditor();
        RefreshPreviewOverlays();
    }

    private void OnCircleRoiPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncingCircleEditor)
        {
            return;
        }

        if (sender is RoiEditorItem editor &&
            e.PropertyName is nameof(RoiEditorItem.CaliperSearchWidth) or nameof(RoiEditorItem.Radius))
        {
            SyncSearchWidthFromCircleEditor(editor);
        }

        if (e.PropertyName is nameof(RoiEditorItem.X)
            or nameof(RoiEditorItem.Y)
            or nameof(RoiEditorItem.Radius)
            or nameof(RoiEditorItem.CaliperSearchWidth)
            or nameof(RoiEditorItem.Shape)
            or nameof(RoiEditorItem.Geometry))
        {
            _roiReferenceDirty = true;
            ClearResultPreview();
        }
    }

    private void UpdateCircleEditorSearchWidth(double width)
    {
        if (_circleRoiEditor is null || Math.Abs(_circleRoiEditor.CaliperSearchWidth - width) <= 0.01)
        {
            return;
        }

        _syncingCircleEditor = true;
        try
        {
            _circleRoiEditor.CaliperSearchWidth = width;
        }
        finally
        {
            _syncingCircleEditor = false;
        }
    }

    private void SyncSearchWidthFromCircleEditor(RoiEditorItem editor)
    {
        var width = Math.Clamp(editor.CaliperSearchWidth, 2, 1000);
        if (Math.Abs(_searchWidth - width) <= 0.01)
        {
            return;
        }

        _searchWidth = width;
        RaisePropertyChanged(nameof(SearchWidth));
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180)
        {
            angle -= 360;
        }

        while (angle <= -180)
        {
            angle += 360;
        }

        return angle;
    }

    private static double GetDefaultCircleRadius(ImageFrame frame)
    {
        return Math.Clamp(Math.Min(frame.Width, frame.Height) * 0.035, 12, 80);
    }

    private Dictionary<string, string> BuildParameters(bool includeRoiId)
    {
        var parameters = new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["minScore"] = MinScore.ToString("0.###", CultureInfo.InvariantCulture),
            ["caliperCount"] = CaliperCount.ToString(CultureInfo.InvariantCulture),
            ["edgeThreshold"] = EdgeThreshold.ToString("0.###", CultureInfo.InvariantCulture),
            ["circlePolarity"] = Polarity,
            ["searchDirection"] = SearchDirection,
            ["resultSelection"] = ResultSelection,
            ["caliperWidth"] = CaliperWidth.ToString("0.###", CultureInfo.InvariantCulture),
            ["searchWidth"] = SearchWidth.ToString("0.###", CultureInfo.InvariantCulture),
            ["showCrosshair"] = ShowCrosshair.ToString(),
            ["enabledOutputs"] = FormatEnabledOutputKeys()
        };
        if (includeRoiId && !string.IsNullOrWhiteSpace(_roiId))
        {
            parameters["roiId"] = _roiId;
        }

        return parameters;
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

    private static IEnumerable<ToolOutputOptionItem> CreateOutputOptions(IReadOnlyDictionary<string, string> parameters)
    {
        var enabled = ParseEnabledOutputKeys(parameters.GetValueOrDefault("enabledOutputs"));
        yield return new ToolOutputOptionItem("CircleOutput", "圆", "Circle", enabled.Contains("CircleOutput"));
        yield return new ToolOutputOptionItem("CenterOutput", "圆心", "Point", enabled.Contains("CenterOutput"));
        yield return new ToolOutputOptionItem("RadiusOutput", "半径", "Number", enabled.Contains("RadiusOutput"));
        yield return new ToolOutputOptionItem("ResultOutput", "OK/NG", "Result", enabled.Contains("ResultOutput"));
    }

    private static HashSet<string> ParseEnabledOutputKeys(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>(["CircleOutput", "CenterOutput", "ResultOutput"], StringComparer.OrdinalIgnoreCase);
        }

        var keys = text
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (keys.Count == 0)
        {
            keys = new HashSet<string>(["CircleOutput", "CenterOutput", "ResultOutput"], StringComparer.OrdinalIgnoreCase);
        }

        keys.Add("CenterOutput");
        return keys;
    }

    private string GetOwnedRoiName()
    {
        return string.IsNullOrWhiteSpace(Name) ? "找圆" : Name.Trim();
    }

    private static RoiDefinition ToCircle(RoiDefinition roi)
    {
        if (roi.Shape == RoiShapeKind.Circle)
        {
            return roi;
        }

        return roi.Shape switch
        {
            RoiShapeKind.RotatedRectangle => roi with
            {
                Shape = RoiShapeKind.Circle,
                Radius = Math.Max(5, Math.Min(roi.Width, roi.Height) / 2.0),
                Width = 0,
                Height = 0,
                Angle = 0,
                Points = Array.Empty<Point2D>()
            },
            RoiShapeKind.Polygon when roi.Points.Count > 0 => ToBoundsCircle(
                roi,
                roi.Points.Min(point => point.X),
                roi.Points.Min(point => point.Y),
                roi.Points.Max(point => point.X),
                roi.Points.Max(point => point.Y)),
            _ => roi with
            {
                Shape = RoiShapeKind.Circle,
                X = roi.X + roi.Width / 2.0,
                Y = roi.Y + roi.Height / 2.0,
                Radius = Math.Max(5, Math.Min(roi.Width, roi.Height) / 2.0),
                Width = 0,
                Height = 0,
                Angle = 0,
                Points = Array.Empty<Point2D>()
            }
        };
    }

    private static RoiDefinition ToBoundsCircle(RoiDefinition roi, double left, double top, double right, double bottom)
    {
        return roi with
        {
            Shape = RoiShapeKind.Circle,
            X = left + (right - left) / 2.0,
            Y = top + (bottom - top) / 2.0,
            Radius = Math.Max(5, Math.Min(right - left, bottom - top) / 2.0),
            Width = 0,
            Height = 0,
            Angle = 0,
            Points = Array.Empty<Point2D>()
        };
    }

    private static string FormatParameters(IReadOnlyDictionary<string, string> parameters)
    {
        return string.Join("; ", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
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
        return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        return parameters.TryGetValue(key, out var value) && bool.TryParse(value, out var result) ? result : fallback;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> parameters, string key, int fallback)
    {
        return parameters.TryGetValue(key, out var value) &&
               int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> parameters, string key, double fallback)
    {
        return parameters.TryGetValue(key, out var value) &&
               double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> parameters, string key, out double value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatCoordinate(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.###", CultureInfo.InvariantCulture)
            : "-";
    }
}

public sealed class FindCircleTabItem : BindableBase
{
    private bool _isSelected;

    public FindCircleTabItem(string key, string title)
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
