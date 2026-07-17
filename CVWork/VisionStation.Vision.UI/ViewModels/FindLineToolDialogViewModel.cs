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

public sealed class FindLineToolDialogViewModel : BindableBase
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
    private RoiEditorItem? _lineRoiEditor;
    private RoiEditorItem? _selectedEditableRoi;
    private string _selectedTabKey = "Home";
    private bool _isRoiPlacementArmed;
    private string _roiPlacementHint = string.Empty;
    private double _minScore;
    private int _caliperCount;
    private double _edgeThreshold;
    private string _polarity;
    private string _resultSelection;
    private double _caliperWidth;
    private bool _extendLine;
    private bool _showCrosshair = true;
    private bool _isBusy;
    private string _scoreText = "-";
    private string _durationText = "0ms";
    private string _statusText = "等待运行";
    private string _startRowText = "-";
    private string _startColumnText = "-";
    private string _endRowText = "-";
    private string _endColumnText = "-";
    private ToolResult? _previewResult;
    private bool _roiReferenceDirty;

    public FindLineToolDialogViewModel(
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
        _caliperCount = Math.Clamp(GetInt(_parameters, "caliperCount", 20), 2, 300);
        _edgeThreshold = Math.Clamp(GetDouble(_parameters, "edgeThreshold", 30), 0, 255);
        _polarity = GetString(_parameters, "linePolarity", "从暗到明");
        _resultSelection = GetString(_parameters, "resultSelection", "最强");
        _caliperWidth = Math.Clamp(GetDouble(_parameters, "caliperWidth", 4), 1, 200);
        _extendLine = GetBool(_parameters, "extendLine", false);
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
        ResultSelections = new ObservableCollection<string> { "最强", "第一个", "最后一个" };
        OutputOptions = new ObservableCollection<ToolOutputOptionItem>(CreateOutputOptions(_parameters));
        Tabs =
        [
            new FindLineTabItem("Home", "主页"),
            new FindLineTabItem("Output", "输出项"),
            new FindLineTabItem("Display", "显示")
        ];

        SelectTab(Tabs[0]);
        SelectTabCommand = new DelegateCommand<FindLineTabItem>(SelectTab);
        EditCaliperCommand = new DelegateCommand(ArmRoiPlacement, () => !IsMissingInputImage);
        PlaceRoiCommand = new DelegateCommand<Point2D>(PlaceRectangle2Roi);
        RunToolCommand = new DelegateCommand(async () => await RunToolAsync(), () => !IsBusy && !IsMissingInputImage)
            .ObservesProperty(() => IsBusy);
        RunFlowCommand = new DelegateCommand(async () => await RunFlowAsync(), () => !IsBusy && !IsMissingInputImage)
            .ObservesProperty(() => IsBusy);
        CloseCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, true));
        CancelCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, false));

        LoadLineRoiEditor();
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

    public ObservableCollection<string> ResultSelections { get; }

    public ObservableCollection<ToolOutputOptionItem> OutputOptions { get; }

    public ObservableCollection<FindLineTabItem> Tabs { get; }

    public DelegateCommand<FindLineTabItem> SelectTabCommand { get; }

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
            if (SetProperty(ref _caliperCount, Math.Clamp(value, 2, 300)))
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

    public bool ExtendLine
    {
        get => _extendLine;
        set => SetProperty(ref _extendLine, value);
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

    public string StartRowText
    {
        get => _startRowText;
        private set => SetProperty(ref _startRowText, value);
    }

    public string StartColumnText
    {
        get => _startColumnText;
        private set => SetProperty(ref _startColumnText, value);
    }

    public string EndRowText
    {
        get => _endRowText;
        private set => SetProperty(ref _endRowText, value);
    }

    public string EndColumnText
    {
        get => _endColumnText;
        private set => SetProperty(ref _endColumnText, value);
    }

    public async Task<bool> ApplyToAsync(VisionToolItem tool)
    {
        SyncLineRoi();
        if (!await CaptureRoiReferencePoseIfNeededAsync())
        {
            return false;
        }

        tool.Name = string.IsNullOrWhiteSpace(Name) ? tool.Name : Name.Trim();
        tool.Kind = VisionToolKind.FindLine;
        tool.Enabled = Enabled;
        tool.RoiId = _roiId;
        tool.ParametersText = FormatParameters(BuildParameters(includeRoiId: false));
        return true;
    }

    private void SelectTab(FindLineTabItem tab)
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

    private void LoadLineRoiEditor()
    {
        var definition = FindLineRoi();
        if (definition is null)
        {
            return;
        }

        SetLineRoiEditor(RoiEditorItem.FromDefinition(ToRectangle2(definition)));
    }

    private RoiDefinition? FindLineRoi()
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

        ShowLineRoiEditor();
        IsRoiPlacementArmed = true;
        RoiPlacementHint = "在图像上单击放置矩形二";
        StatusText = RoiPlacementHint;
    }

    private void PlaceRectangle2Roi(Point2D point)
    {
        if (!IsRoiPlacementArmed || CurrentFrame is null)
        {
            return;
        }

        QueueRoiRemoval(_lineRoiEditor?.Id);
        var roi = ToolRoiFactory.CreateRoiAt(
            Name,
            VisionToolKind.FindLine,
            RoiShapeKind.RotatedRectangle,
            CurrentFrame,
            1,
            point) with
        {
            Name = $"{GetOwnedRoiName()} 矩形二"
        };
        _roiId = roi.Id;
        UpsertCreatedRoi(roi);
        SetLineRoiEditor(RoiEditorItem.FromDefinition(roi));
        _roiReferenceDirty = true;
        IsRoiPlacementArmed = false;
        RoiPlacementHint = string.Empty;
        StatusText = "矩形二已放置";
    }

    private void SetLineRoiEditor(RoiEditorItem editor)
    {
        if (_lineRoiEditor is not null)
        {
            _lineRoiEditor.PropertyChanged -= OnLineRoiPropertyChanged;
        }

        EditableRois.Clear();
        _lineRoiEditor = editor;
        _lineRoiEditor.PropertyChanged += OnLineRoiPropertyChanged;
        EditableRois.Add(editor);
        SelectedEditableRoi = editor;
        ClearResultPreview();
    }

    private void ShowLineRoiEditor()
    {
        if (_lineRoiEditor is null || EditableRois.Contains(_lineRoiEditor))
        {
            return;
        }

        EditableRois.Add(_lineRoiEditor);
        SelectedEditableRoi = _lineRoiEditor;
    }

    private void HideLineRoiEditor()
    {
        if (_lineRoiEditor is null)
        {
            return;
        }

        EditableRois.Remove(_lineRoiEditor);
        if (ReferenceEquals(SelectedEditableRoi, _lineRoiEditor))
        {
            SelectedEditableRoi = null;
        }
    }

    private void SyncLineRoi()
    {
        if (_lineRoiEditor is null)
        {
            return;
        }

        var definition = _lineRoiEditor.ToDefinition() with
        {
            Shape = RoiShapeKind.RotatedRectangle,
            Name = string.IsNullOrWhiteSpace(_lineRoiEditor.Name)
                ? $"{GetOwnedRoiName()} 矩形二"
                : _lineRoiEditor.Name
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

        if (_lineRoiEditor is null)
        {
            StatusText = "请先编辑卡尺";
            return;
        }

        IsBusy = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            SyncLineRoi();
            if (!await CaptureRoiReferencePoseIfNeededAsync())
            {
                return;
            }

            var roi = _lineRoiEditor.ToDefinition();
            var definition = new VisionToolDefinition
            {
                Id = _toolId,
                Name = Name,
                Kind = VisionToolKind.FindLine,
                Enabled = Enabled,
                Parameters = BuildParameters(includeRoiId: true)
            };
            var recipe = BuildPreviewRecipe(definition, roi);
            var pipelineResult = await _pipeline.ExecuteAsync(recipe, CurrentFrame);
            var result = pipelineResult.ToolResults.LastOrDefault(item => string.Equals(item.ToolId, _toolId, StringComparison.OrdinalIgnoreCase));
            if (result is null)
            {
                StatusText = "预览运行失败：流程没有返回找线结果";
                return;
            }

            stopwatch.Stop();
            ScoreText = result.Data.GetValueOrDefault("score", "-");
            DurationText = $"{stopwatch.ElapsedMilliseconds}ms";
            StatusText = result.Message;
            UpdateResultCoordinates(result.Data);
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
            StatusText = "Template pose is not ready. Run template locate before saving or previewing this taught ROI.";
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

    private void UpdateResultCoordinates(IReadOnlyDictionary<string, string> data)
    {
        StartColumnText = FormatCoordinate(data.GetValueOrDefault("x1"));
        StartRowText = FormatCoordinate(data.GetValueOrDefault("y1"));
        EndColumnText = FormatCoordinate(data.GetValueOrDefault("x2"));
        EndRowText = FormatCoordinate(data.GetValueOrDefault("y2"));
    }

    private void ClearResultPreview()
    {
        _previewResult = null;
        ShowLineRoiEditor();
        RefreshPreviewOverlays();
    }

    private void OnLineRoiPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RoiEditorItem.X)
            or nameof(RoiEditorItem.Y)
            or nameof(RoiEditorItem.Width)
            or nameof(RoiEditorItem.Height)
            or nameof(RoiEditorItem.Angle)
            or nameof(RoiEditorItem.Shape)
            or nameof(RoiEditorItem.Geometry))
        {
            _roiReferenceDirty = true;
            ClearResultPreview();
        }
    }

    private void RefreshPreviewOverlays()
    {
        PreviewOverlays.Clear();
        var shouldShowSearchRoi = _previewResult is null || _previewResult.Outcome != InspectionOutcome.Ok;
        if (shouldShowSearchRoi)
        {
            var previewRoi = GetPreviewCaliperRoi();
            AddCaliperPreviewOverlays(previewRoi);
        }
        else
        {
            HideLineRoiEditor();
        }

        if (_previewResult is null ||
            !TryGetDouble(_previewResult.Data, "x1", out var x1) ||
            !TryGetDouble(_previewResult.Data, "y1", out var y1) ||
            !TryGetDouble(_previewResult.Data, "x2", out var x2) ||
            !TryGetDouble(_previewResult.Data, "y2", out var y2))
        {
            AddFailureMessageOverlay();
            return;
        }

        if (TryParsePointList(_previewResult.Data.GetValueOrDefault("caliperPoints"), out var caliperPoints))
        {
            PreviewOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.XMarker,
                State = VisionOverlayState.Neutral,
                Label = string.Empty,
                Points = caliperPoints
            });
        }

        PreviewOverlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.LineSegment,
            State = _previewResult.Outcome == InspectionOutcome.Ok ? VisionOverlayState.Ok : VisionOverlayState.Ng,
            Label = _previewResult.Outcome == InspectionOutcome.Ok ? string.Empty : _previewResult.Message,
            X = x1,
            Y = y1,
            X2 = x2,
            Y2 = y2
        });
    }

    private static bool TryParsePointList(string? text, out IReadOnlyList<Point2D> points)
    {
        var parsed = new List<Point2D>();
        points = parsed;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                continue;
            }

            parsed.Add(new Point2D(x, y));
        }

        return parsed.Count > 0;
    }

    private void AddFailureMessageOverlay()
    {
        if (_previewResult is null || _previewResult.Outcome == InspectionOutcome.Ok)
        {
            return;
        }

        var roi = GetPreviewCaliperRoi() ?? _lineRoiEditor;
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

    private RoiEditorItem? GetPreviewCaliperRoi()
    {
        if (_previewResult is null ||
            !TryGetDouble(_previewResult.Data, "searchRoiX", out var x) ||
            !TryGetDouble(_previewResult.Data, "searchRoiY", out var y) ||
            !TryGetDouble(_previewResult.Data, "searchRoiWidth", out var width) ||
            !TryGetDouble(_previewResult.Data, "searchRoiHeight", out var height))
        {
            return _lineRoiEditor;
        }

        TryGetDouble(_previewResult.Data, "searchRoiAngle", out var angle);
        var runtimeRoi = new RoiEditorItem
        {
            Name = "运行卡尺",
            Shape = RoiShapeKind.RotatedRectangle,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Angle = angle
        };
        if (HasMovedFromEditor(runtimeRoi))
        {
            HideLineRoiEditor();
            PreviewOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangle,
                State = VisionOverlayState.Warning,
                Label = runtimeRoi.Name,
                X = runtimeRoi.X,
                Y = runtimeRoi.Y,
                Width = runtimeRoi.Width,
                Height = runtimeRoi.Height,
                Angle = runtimeRoi.Angle
            });
        }

        return runtimeRoi;
    }

    private bool HasMovedFromEditor(RoiEditorItem runtimeRoi)
    {
        return _lineRoiEditor is null ||
               Math.Abs(runtimeRoi.X - _lineRoiEditor.X) > 1 ||
               Math.Abs(runtimeRoi.Y - _lineRoiEditor.Y) > 1 ||
               Math.Abs(runtimeRoi.Width - _lineRoiEditor.Width) > 1 ||
               Math.Abs(runtimeRoi.Height - _lineRoiEditor.Height) > 1 ||
               Math.Abs(NormalizeAngle(runtimeRoi.Angle - _lineRoiEditor.Angle)) > 0.5;
    }

    private void AddCaliperPreviewOverlays(RoiEditorItem? roi)
    {
        if (roi is null || roi.Shape != RoiShapeKind.RotatedRectangle)
        {
            return;
        }

        var caliperCount = Math.Clamp(CaliperCount, 2, 300);
        var caliperHeight = Math.Min(Math.Max(1, CaliperWidth), roi.Height);
        for (var index = 0; index < caliperCount; index++)
        {
            var localY = roi.Height <= 2
                ? 0
                : -roi.Height / 2.0 + roi.Height * (index + 0.5) / caliperCount;
            var center = ToImagePoint(roi, 0, localY);
            PreviewOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangle,
                State = VisionOverlayState.Neutral,
                X = center.X,
                Y = center.Y,
                Width = roi.Width,
                Height = caliperHeight,
                Angle = roi.Angle
            });
        }

        var start = ToImagePoint(roi, 0, 0);
        var end = ToImagePoint(roi, roi.Width / 2.0, 0);
        PreviewOverlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.DirectionAxis,
            X = start.X,
            Y = start.Y,
            X2 = end.X,
            Y2 = end.Y
        });
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

    private static Point2D ToImagePoint(RoiEditorItem roi, double localX, double localY)
    {
        var radians = roi.Angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Point2D(
            roi.X + localX * cos - localY * sin,
            roi.Y + localX * sin + localY * cos);
    }

    private Dictionary<string, string> BuildParameters(bool includeRoiId)
    {
        var parameters = new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["minScore"] = MinScore.ToString("0.###", CultureInfo.InvariantCulture),
            ["caliperCount"] = CaliperCount.ToString(CultureInfo.InvariantCulture),
            ["edgeThreshold"] = EdgeThreshold.ToString("0.###", CultureInfo.InvariantCulture),
            ["linePolarity"] = Polarity,
            ["resultSelection"] = ResultSelection,
            ["caliperWidth"] = CaliperWidth.ToString("0.###", CultureInfo.InvariantCulture),
            ["extendLine"] = ExtendLine.ToString(),
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
        yield return new ToolOutputOptionItem("LineOutput", "直线", "Line", enabled.Contains("LineOutput"));
        yield return new ToolOutputOptionItem("MidPointOutput", "中点", "Point", enabled.Contains("MidPointOutput"));
        yield return new ToolOutputOptionItem("ResultOutput", "OK/NG", "Result", enabled.Contains("ResultOutput"));
    }

    private static HashSet<string> ParseEnabledOutputKeys(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>(["LineOutput", "MidPointOutput", "ResultOutput"], StringComparer.OrdinalIgnoreCase);
        }

        var keys = text
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (keys.Count == 0)
        {
            keys = new HashSet<string>(["LineOutput", "MidPointOutput", "ResultOutput"], StringComparer.OrdinalIgnoreCase);
        }

        keys.Add("MidPointOutput");
        return keys;
    }

    private string GetOwnedRoiName()
    {
        return string.IsNullOrWhiteSpace(Name) ? "找线" : Name.Trim();
    }

    private static RoiDefinition ToRectangle2(RoiDefinition roi)
    {
        if (roi.Shape == RoiShapeKind.RotatedRectangle)
        {
            return roi;
        }

        return roi.Shape switch
        {
            RoiShapeKind.Circle => roi with
            {
                Shape = RoiShapeKind.RotatedRectangle,
                Width = Math.Max(10, roi.Radius * 2),
                Height = Math.Max(10, roi.Radius * 2),
                Angle = roi.Angle,
                Points = Array.Empty<Point2D>()
            },
            RoiShapeKind.Polygon when roi.Points.Count > 0 => ToBoundsRectangle2(roi, roi.Points.Min(point => point.X), roi.Points.Min(point => point.Y), roi.Points.Max(point => point.X), roi.Points.Max(point => point.Y)),
            _ => roi with
            {
                Shape = RoiShapeKind.RotatedRectangle,
                X = roi.X + roi.Width / 2.0,
                Y = roi.Y + roi.Height / 2.0,
                Angle = roi.Angle,
                Points = Array.Empty<Point2D>()
            }
        };
    }

    private static RoiDefinition ToBoundsRectangle2(RoiDefinition roi, double left, double top, double right, double bottom)
    {
        return roi with
        {
            Shape = RoiShapeKind.RotatedRectangle,
            X = left + (right - left) / 2.0,
            Y = top + (bottom - top) / 2.0,
            Width = Math.Max(10, right - left),
            Height = Math.Max(10, bottom - top),
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

public sealed class FindLineTabItem : BindableBase
{
    private bool _isSelected;

    public FindLineTabItem(string key, string title)
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
