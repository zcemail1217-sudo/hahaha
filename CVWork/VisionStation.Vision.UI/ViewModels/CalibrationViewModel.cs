using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using VisionStation.Application;
using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.Services;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Vision.UI.ViewModels;

public sealed class CalibrationViewModel : BindableBase
{
    private static readonly string[] ImageExtensions = [".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff"];

    private readonly IRecipeRepository _recipes;
    private readonly OpenCvCalibrationService _calibration;
    private readonly IImageFrameFileService _imageFiles;
    private readonly ICameraDevice _camera;
    private readonly IAxisController _axis;
    private readonly IVisionPipeline _pipeline;
    private readonly IRegionManager _regionManager;
    private Recipe? _currentRecipe;
    private CameraCalibrationResult? _cameraResult;
    private PlaneCalibrationResult? _planeResult;
    private VisionToolOptionItem? _selectedPointTool;
    private AutoPlaneSampleItem? _selectedAutoPlaneSample;
    private ChessboardObservationItem? _selectedObservation;
    private PlaneCalibrationModel _selectedPlaneModel = PlaneCalibrationModel.Affine;
    private CalibrationQualitySummary _planeQualitySummary = CalibrationQualitySummary.Empty();
    private CalibrationQualitySummary _cameraQualitySummary = CalibrationQualitySummary.Empty();
    private int _selectedCalibrationTabIndex;
    private bool _isBusy;
    private bool _isAdvancedPlaneSettingsExpanded;
    private bool _useEncoderPosition = true;
    private bool _returnToStartAfterAuto = true;
    private string _currentRecipeText = "-";
    private string _axisXKeyText = "AxisX";
    private string _axisYKeyText = "AxisY";
    private string _gridStepXText = "10";
    private string _gridStepYText = "10";
    private string _motionSpeedText = "50";
    private string _motionAccelerationText = "100";
    private string _settleDelayMsText = "100";
    private string _planeUnitText = "mm";
    private string _ransacThresholdText = "1";
    private string _planeStatusText = "等待自动九点标定";
    private string _planeQualityText = "尚未计算";
    private string _planeResultText = "-";
    private string _planeMatrixText = string.Empty;
    private string _chessboardColumnsText = "9";
    private string _chessboardRowsText = "6";
    private string _squareSizeText = "1";
    private string _cameraUnitText = "mm";
    private string _cameraStatusText = "请选择棋盘格图片目录";
    private string _cameraQualityText = "尚未计算";
    private string _cameraResultText = "-";
    private string _cameraMatrixText = string.Empty;
    private string _distortionText = string.Empty;

    public CalibrationViewModel(
        IRecipeRepository recipes,
        OpenCvCalibrationService calibration,
        IImageFrameFileService imageFiles,
        ICameraDevice camera,
        IAxisController axis,
        IVisionPipeline pipeline,
        IRegionManager regionManager)
    {
        _recipes = recipes;
        _calibration = calibration;
        _imageFiles = imageFiles;
        _camera = camera;
        _axis = axis;
        _pipeline = pipeline;
        _regionManager = regionManager;

        BackToVisionCommand = new DelegateCommand(BackToVision);
        ReloadCommand = new DelegateCommand(async () => await ReloadAsync());
        RunCameraDirectoryCalibrationCommand = new DelegateCommand(async () => await RunCameraDirectoryCalibrationAsync());
        ClearCameraImagesCommand = new DelegateCommand(ClearCameraImages);
        SaveCameraCommand = new DelegateCommand(async () => await SaveCameraAsync());
        RunAutoPlaneCalibrationCommand = new DelegateCommand(async () => await RunAutoPlaneCalibrationAsync());
        ClearAutoPlaneSamplesCommand = new DelegateCommand(ClearAutoPlaneSamples);
        CalculatePlaneCommand = new DelegateCommand(CalculatePlaneFromSamples);
        SavePlaneCommand = new DelegateCommand(async () => await SavePlaneAsync());

        _ = ReloadAsync();
    }

    public ObservableCollection<VisionToolOptionItem> PointToolOptions { get; } = new();

    public ObservableCollection<AutoPlaneSampleItem> AutoPlaneSamples { get; } = new();

    public ObservableCollection<VisionOverlayItem> AutoPlaneOverlays { get; } = new();

    public ObservableCollection<ChessboardObservationItem> ChessboardObservations { get; } = new();

    public ObservableCollection<VisionOverlayItem> ChessboardOverlays { get; } = new();

    public IReadOnlyList<PlaneCalibrationModel> PlaneModels { get; } =
    [
        PlaneCalibrationModel.Affine,
        PlaneCalibrationModel.Homography,
        PlaneCalibrationModel.Auto
    ];

    public DelegateCommand BackToVisionCommand { get; }

    public DelegateCommand ReloadCommand { get; }

    public DelegateCommand RunCameraDirectoryCalibrationCommand { get; }

    public DelegateCommand ClearCameraImagesCommand { get; }

    public DelegateCommand SaveCameraCommand { get; }

    public DelegateCommand RunAutoPlaneCalibrationCommand { get; }

    public DelegateCommand ClearAutoPlaneSamplesCommand { get; }

    public DelegateCommand CalculatePlaneCommand { get; }

    public DelegateCommand SavePlaneCommand { get; }

    public string CurrentRecipeText
    {
        get => _currentRecipeText;
        private set => SetProperty(ref _currentRecipeText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(CanRunAutomation));
            }
        }
    }

    public bool CanRunAutomation => !IsBusy;

    public int SelectedCalibrationTabIndex
    {
        get => _selectedCalibrationTabIndex;
        set => SetProperty(ref _selectedCalibrationTabIndex, value);
    }

    public bool IsAdvancedPlaneSettingsExpanded
    {
        get => _isAdvancedPlaneSettingsExpanded;
        set => SetProperty(ref _isAdvancedPlaneSettingsExpanded, value);
    }

    public VisionToolOptionItem? SelectedPointTool
    {
        get => _selectedPointTool;
        set => SetProperty(ref _selectedPointTool, value);
    }

    public AutoPlaneSampleItem? SelectedAutoPlaneSample
    {
        get => _selectedAutoPlaneSample;
        set
        {
            if (SetProperty(ref _selectedAutoPlaneSample, value))
            {
                RaisePropertyChanged(nameof(SelectedAutoPlaneFrame));
                RaisePropertyChanged(nameof(IsAutoPlanePreviewMissing));
                UpdateAutoPlaneOverlays();
            }
        }
    }

    public ImageFrame? SelectedAutoPlaneFrame =>
        SelectedAutoPlaneSample?.Frame is { Width: > 0, Height: > 0 } frame ? frame : null;

    public bool IsAutoPlanePreviewMissing => SelectedAutoPlaneFrame is null;

    public ChessboardObservationItem? SelectedObservation
    {
        get => _selectedObservation;
        set
        {
            if (SetProperty(ref _selectedObservation, value))
            {
                RaisePropertyChanged(nameof(SelectedChessboardFrame));
                RaisePropertyChanged(nameof(IsChessboardPreviewMissing));
                UpdateChessboardOverlays();
            }
        }
    }

    public ImageFrame? SelectedChessboardFrame =>
        SelectedObservation?.Frame is { Width: > 0, Height: > 0 } frame ? frame : null;

    public bool IsChessboardPreviewMissing => SelectedChessboardFrame is null;

    public string AxisXKeyText
    {
        get => _axisXKeyText;
        set => SetProperty(ref _axisXKeyText, value);
    }

    public string AxisYKeyText
    {
        get => _axisYKeyText;
        set => SetProperty(ref _axisYKeyText, value);
    }

    public string GridStepXText
    {
        get => _gridStepXText;
        set => SetProperty(ref _gridStepXText, value);
    }

    public string GridStepYText
    {
        get => _gridStepYText;
        set => SetProperty(ref _gridStepYText, value);
    }

    public string MotionSpeedText
    {
        get => _motionSpeedText;
        set => SetProperty(ref _motionSpeedText, value);
    }

    public string MotionAccelerationText
    {
        get => _motionAccelerationText;
        set => SetProperty(ref _motionAccelerationText, value);
    }

    public string SettleDelayMsText
    {
        get => _settleDelayMsText;
        set => SetProperty(ref _settleDelayMsText, value);
    }

    public string PlaneUnitText
    {
        get => _planeUnitText;
        set => SetProperty(ref _planeUnitText, value);
    }

    public string RansacThresholdText
    {
        get => _ransacThresholdText;
        set => SetProperty(ref _ransacThresholdText, value);
    }

    public PlaneCalibrationModel SelectedPlaneModel
    {
        get => _selectedPlaneModel;
        set => SetProperty(ref _selectedPlaneModel, value);
    }

    public bool UseEncoderPosition
    {
        get => _useEncoderPosition;
        set => SetProperty(ref _useEncoderPosition, value);
    }

    public bool ReturnToStartAfterAuto
    {
        get => _returnToStartAfterAuto;
        set => SetProperty(ref _returnToStartAfterAuto, value);
    }

    public string PlaneStatusText
    {
        get => _planeStatusText;
        private set => SetProperty(ref _planeStatusText, value);
    }

    public string PlaneProgressText
    {
        get
        {
            var ok = AutoPlaneSamples.Count(item => item.Success);
            return $"采集点 {ok}/{AutoPlaneSamples.Count} OK，自动九点至少需要 3 个有效点";
        }
    }

    public string PlaneQualityText
    {
        get => _planeQualityText;
        private set => SetProperty(ref _planeQualityText, value);
    }

    public CalibrationQualitySummary PlaneQualitySummary
    {
        get => _planeQualitySummary;
        private set => SetProperty(ref _planeQualitySummary, value);
    }

    public string PlaneResultText
    {
        get => _planeResultText;
        private set => SetProperty(ref _planeResultText, value);
    }

    public string PlaneMatrixText
    {
        get => _planeMatrixText;
        private set => SetProperty(ref _planeMatrixText, value);
    }

    public string ChessboardColumnsText
    {
        get => _chessboardColumnsText;
        set => SetProperty(ref _chessboardColumnsText, value);
    }

    public string ChessboardRowsText
    {
        get => _chessboardRowsText;
        set => SetProperty(ref _chessboardRowsText, value);
    }

    public string SquareSizeText
    {
        get => _squareSizeText;
        set => SetProperty(ref _squareSizeText, value);
    }

    public string CameraUnitText
    {
        get => _cameraUnitText;
        set => SetProperty(ref _cameraUnitText, value);
    }

    public string CameraStatusText
    {
        get => _cameraStatusText;
        private set => SetProperty(ref _cameraStatusText, value);
    }

    public string CameraProgressText
    {
        get
        {
            var found = ChessboardObservations.Count(item => item.Found);
            var failed = ChessboardObservations.Count - found;
            return $"有效棋盘图 {found}/{ChessboardObservations.Count}，失败 {failed}，内参计算至少需要 3 张有效图";
        }
    }

    public string CameraQualityText
    {
        get => _cameraQualityText;
        private set => SetProperty(ref _cameraQualityText, value);
    }

    public CalibrationQualitySummary CameraQualitySummary
    {
        get => _cameraQualitySummary;
        private set => SetProperty(ref _cameraQualitySummary, value);
    }

    public string CameraResultText
    {
        get => _cameraResultText;
        private set => SetProperty(ref _cameraResultText, value);
    }

    public string CameraMatrixText
    {
        get => _cameraMatrixText;
        private set => SetProperty(ref _cameraMatrixText, value);
    }

    public string DistortionText
    {
        get => _distortionText;
        private set => SetProperty(ref _distortionText, value);
    }

    private async Task ReloadAsync()
    {
        _currentRecipe = await _recipes.GetCurrentAsync();
        CurrentRecipeText = $"{_currentRecipe.Name} / {_currentRecipe.ProductCode}";
        ReloadPointToolOptions(_currentRecipe);

        if (_currentRecipe.Camera.PlaneCalibration is { } plane)
        {
            _planeResult = plane;
            SelectedPlaneModel = plane.Model;
            PlaneUnitText = plane.Unit;
            ApplyPlaneResult(plane, "已载入当前配方平面标定");
        }

        if (_currentRecipe.Camera.CameraCalibration is { } camera)
        {
            _cameraResult = camera;
            ChessboardColumnsText = camera.Pattern.Columns.ToString(CultureInfo.InvariantCulture);
            ChessboardRowsText = camera.Pattern.Rows.ToString(CultureInfo.InvariantCulture);
            SquareSizeText = camera.Pattern.SquareSize.ToString("0.###", CultureInfo.InvariantCulture);
            CameraUnitText = camera.Pattern.Unit;
            ApplyCameraResult(camera, "已载入当前配方相机内参");
        }
    }

    private void BackToVision()
    {
        _regionManager.RequestNavigate(RegionNames.MainRegion, NavigationKeys.VisionDebug);
    }

    private async Task RunCameraDirectoryCalibrationAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var directory = await _imageFiles.PickDirectoryAsync();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            IsBusy = true;
            ClearCameraImages();
            CameraStatusText = $"正在检测目录：{directory}";
            var pattern = CreatePattern();
            var paths = Directory.EnumerateFiles(directory)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var path in paths)
            {
                try
                {
                    var frame = await _imageFiles.LoadImageAsync(path);
                    AddChessboardObservation(frame, pattern);
                }
                catch (Exception ex)
                {
                    ChessboardObservations.Add(ChessboardObservationItem.Failed(path, ex.Message));
                }

                RaisePropertyChanged(nameof(CameraProgressText));
            }

            var valid = ChessboardObservations.Where(item => item.Found).Select(item => item.Result).ToArray();
            if (valid.Length < 3)
            {
                CameraStatusText = $"目录检测完成，但有效图只有 {valid.Length} 张，无法计算内参";
                CameraQualityText = "有效图不足，请补充更多角点检测成功的棋盘格图像";
                CameraQualitySummary = CalibrationQualitySummary.Empty(CameraQualityText);
                return;
            }

            _cameraResult = _calibration.CalibrateCamera(valid, pattern, minimumViews: 3);
            ApplyCameraResult(_cameraResult, $"自动相机标定完成：有效 {valid.Length} 张，失败 {ChessboardObservations.Count - valid.Length} 张");
        }
        catch (Exception ex)
        {
            CameraStatusText = $"自动相机标定失败：{ex.Message}";
            CameraQualityText = "计算失败，请检查棋盘格参数、图片目录和图像质量";
            CameraQualitySummary = CalibrationQualitySummary.Empty(CameraQualityText);
        }
        finally
        {
            IsBusy = false;
            RaisePropertyChanged(nameof(CameraProgressText));
        }
    }

    private async Task RunAutoPlaneCalibrationAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (SelectedPointTool is null)
        {
            PlaneStatusText = "请先选择一个视觉找点工具";
            return;
        }

        var axisX = string.IsNullOrWhiteSpace(AxisXKeyText) ? "AxisX" : AxisXKeyText.Trim();
        var axisY = string.IsNullOrWhiteSpace(AxisYKeyText) ? "AxisY" : AxisYKeyText.Trim();
        var stepX = ParseDouble(GridStepXText, 10);
        var stepY = ParseDouble(GridStepYText, 10);
        var speed = Math.Max(0.001, ParseDouble(MotionSpeedText, 50));
        var acceleration = Math.Max(0.001, ParseDouble(MotionAccelerationText, 100));
        var settleDelay = Math.Max(0, ParseInt(SettleDelayMsText, 100));

        try
        {
            IsBusy = true;
            ClearAutoPlaneSamples();
            var recipe = (_currentRecipe ?? await _recipes.GetCurrentAsync()).WithNormalizedFlows();
            var samplingRecipe = BuildSamplingRecipe(recipe, SelectedPointTool.ToolId);
            var startXStatus = await _axis.GetAxisStatusAsync(axisX);
            var startYStatus = await _axis.GetAxisStatusAsync(axisY);
            var startX = ReadAxisPosition(startXStatus);
            var startY = ReadAxisPosition(startYStatus);
            var sequence = CreateNinePointSequence(startX, startY, stepX, stepY).ToArray();

            for (var index = 0; index < sequence.Length; index++)
            {
                var point = sequence[index];
                PlaneStatusText = $"自动九点采集中：{index + 1}/{sequence.Length} 移动到 X={Format(point.X)} Y={Format(point.Y)}";
                var sample = new AutoPlaneSampleItem
                {
                    Index = index + 1,
                    TargetX = Format(point.X),
                    TargetY = Format(point.Y),
                    Status = "运动中"
                };
                AutoPlaneSamples.Add(sample);
                SelectedAutoPlaneSample = sample;
                RaisePropertyChanged(nameof(PlaneProgressText));

                try
                {
                    await MoveAxisAsync(axisX, point.X, speed, acceleration);
                    await MoveAxisAsync(axisY, point.Y, speed, acceleration);
                    if (settleDelay > 0)
                    {
                        await Task.Delay(settleDelay);
                    }

                    var frame = await _camera.GrabAsync();
                    sample.Frame = frame;
                    RaisePropertyChanged(nameof(SelectedAutoPlaneFrame));
                    var result = await _pipeline.ExecuteAsync(samplingRecipe, frame);
                    var toolResult = result.ToolResults.FirstOrDefault(item => string.Equals(item.ToolId, SelectedPointTool.ToolId, StringComparison.OrdinalIgnoreCase));
                    var actualXStatus = await _axis.GetAxisStatusAsync(axisX);
                    var actualYStatus = await _axis.GetAxisStatusAsync(axisY);
                    var actualX = ReadAxisPosition(actualXStatus);
                    var actualY = ReadAxisPosition(actualYStatus);
                    sample.ActualX = Format(actualX);
                    sample.ActualY = Format(actualY);

                    if (toolResult is null)
                    {
                        sample.MarkFailure("未找到指定视觉工具结果");
                    }
                    else if (toolResult.Outcome != InspectionOutcome.Ok)
                    {
                        sample.MarkFailure(toolResult.Message);
                    }
                    else if (!TryExtractImagePoint(toolResult, out var imagePoint, out var pointError))
                    {
                        sample.MarkFailure(pointError);
                    }
                    else
                    {
                        sample.ImageX = Format(imagePoint.X);
                        sample.ImageY = Format(imagePoint.Y);
                        sample.WorldX = Format(actualX);
                        sample.WorldY = Format(actualY);
                        sample.Status = "OK";
                        sample.Message = toolResult.Message;
                    }
                }
                catch (Exception ex)
                {
                    sample.MarkFailure(ex.Message);
                }

                UpdateAutoPlaneOverlays();
                RaisePropertyChanged(nameof(PlaneProgressText));
            }

            CalculatePlaneFromSamples();
            var planeReady = _planeResult is not null;
            if (planeReady)
            {
                PlaneStatusText = "自动九点采集并计算完成，正在保存到当前配方";
                await PersistPlaneAsync();
            }
            else
            {
                PlaneStatusText = "自动九点采集完成，但有效点不足或计算失败";
            }

            if (ReturnToStartAfterAuto)
            {
                PlaneStatusText += "，正在回起点";
                await MoveAxisAsync(axisX, startX, speed, acceleration);
                await MoveAxisAsync(axisY, startY, speed, acceleration);
            }

            PlaneStatusText = planeReady
                ? ReturnToStartAfterAuto
                    ? "自动九点标定完成，平面标定已保存到当前配方，已回起点"
                    : "自动九点标定完成，平面标定已保存到当前配方"
                : ReturnToStartAfterAuto
                    ? "自动九点采集完成，已回起点，但未生成可保存的平面标定"
                    : "自动九点采集完成，但未生成可保存的平面标定";
        }
        catch (Exception ex)
        {
            PlaneStatusText = $"自动九点标定失败：{ex.Message}";
            PlaneQualityText = "请检查轴连接、相机连接和所选视觉找点工具";
            PlaneQualitySummary = CalibrationQualitySummary.Empty(PlaneQualityText);
        }
        finally
        {
            IsBusy = false;
            RaisePropertyChanged(nameof(PlaneProgressText));
        }
    }

    private void ClearAutoPlaneSamples()
    {
        AutoPlaneSamples.Clear();
        AutoPlaneOverlays.Clear();
        SelectedAutoPlaneSample = null;
        _planeResult = null;
        PlaneQualityText = "尚未计算";
        PlaneQualitySummary = CalibrationQualitySummary.Empty();
        PlaneResultText = "-";
        PlaneMatrixText = string.Empty;
        PlaneStatusText = "自动九点采集已清空";
        RaisePropertyChanged(nameof(PlaneProgressText));
    }

    private void CalculatePlaneFromSamples()
    {
        try
        {
            _planeResult = null;
            PlaneResultText = "-";
            PlaneMatrixText = string.Empty;

            var pairs = AutoPlaneSamples
                .Where(item => item.Success)
                .Select(item => item.TryToPair(out var pair) ? pair : null)
                .Where(pair => pair is not null)
                .Cast<CalibrationPointPair>()
                .ToArray();

            if (!CalibrationPlaneValidation.CanCalculate(SelectedPlaneModel, pairs.Length, out var validationMessage))
            {
                PlaneStatusText = "有效点不足，无法计算平面标定";
                PlaneQualityText = validationMessage;
                PlaneQualitySummary = CalibrationQualitySummary.Empty(validationMessage);
                return;
            }

            _planeResult = _calibration.CalibratePlane(
                pairs,
                SelectedPlaneModel,
                string.IsNullOrWhiteSpace(PlaneUnitText) ? "mm" : PlaneUnitText.Trim(),
                ParseDouble(RansacThresholdText, 1));
            ApplyPlaneResult(_planeResult, "平面标定完成");
        }
        catch (Exception ex)
        {
            PlaneStatusText = $"平面标定失败：{ex.Message}";
            PlaneQualityText = "计算失败，请检查视觉点和轴坐标对应关系";
            PlaneQualitySummary = CalibrationQualitySummary.Empty(PlaneQualityText);
        }
    }

    private async Task SavePlaneAsync()
    {
        if (_planeResult is null)
        {
            PlaneStatusText = "请先完成自动九点并计算平面标定";
            return;
        }

        await PersistPlaneAsync();
        PlaneStatusText = "平面标定已保存到当前配方，可由坐标转换工具调用";
    }

    private async Task PersistPlaneAsync()
    {
        var result = _planeResult ?? throw new InvalidOperationException("Plane calibration result is not available.");
        var recipe = _currentRecipe ?? await _recipes.GetCurrentAsync();
        var updated = recipe with
        {
            Camera = recipe.Camera with { PlaneCalibration = result }
        };
        var saved = await _recipes.SaveAsync(updated);
        _currentRecipe = saved;
    }

    private void ClearCameraImages()
    {
        ChessboardObservations.Clear();
        ChessboardOverlays.Clear();
        SelectedObservation = null;
        _cameraResult = null;
        CameraQualityText = "尚未计算";
        CameraQualitySummary = CalibrationQualitySummary.Empty();
        CameraResultText = "-";
        CameraMatrixText = string.Empty;
        DistortionText = string.Empty;
        CameraStatusText = "棋盘格图像已清空";
        RaisePropertyChanged(nameof(CameraProgressText));
    }

    private async Task SaveCameraAsync()
    {
        if (_cameraResult is null)
        {
            CameraStatusText = "请先完成相机内参标定";
            return;
        }

        var recipe = _currentRecipe ?? await _recipes.GetCurrentAsync();
        var updated = recipe with
        {
            Camera = recipe.Camera with { CameraCalibration = _cameraResult }
        };
        var saved = await _recipes.SaveAsync(updated);
        _currentRecipe = saved;
        CameraStatusText = "相机内参已保存到当前配方";
    }

    private void AddChessboardObservation(ImageFrame frame, ChessboardCalibrationPattern pattern)
    {
        var result = _calibration.DetectChessboard(frame, pattern);
        var item = new ChessboardObservationItem(result, frame);
        ChessboardObservations.Add(item);
        SelectedObservation = item;
        CameraStatusText = result.Found
            ? $"检测成功：{Path.GetFileName(frame.Source)}"
            : $"检测失败：{Path.GetFileName(frame.Source)}";
    }

    private void ApplyPlaneResult(PlaneCalibrationResult result, string status)
    {
        PlaneStatusText = status;
        PlaneResultText =
            $"模型={result.Model}  RMS={result.RmsError:0.###} {result.Unit}  Max={result.MaxError:0.###} {result.Unit}  点数={result.PointCount}  内点={result.InlierCount}";
        PlaneMatrixText = CalibrationProfileText.FormatMatrix(result.ImageToWorldMatrix);
        PlaneQualityText = result.RmsError <= 0.05
            ? "误差很好，可直接用于坐标转换"
            : result.RmsError <= 0.2
                ? "误差可用，建议确认最大误差点"
                : "误差偏大，建议检查视觉找点稳定性、轴反馈和点位顺序";
        PlaneQualitySummary = CalibrationQualitySummary.FromPlane(result);
    }

    private void ApplyCameraResult(CameraCalibrationResult result, string status)
    {
        CameraStatusText = status;
        CameraResultText =
            $"RMS={result.RmsReprojectionError:0.###} px  图像={result.ImageWidth}x{result.ImageHeight}  视图={result.Views.Count}";
        CameraMatrixText = CalibrationProfileText.FormatMatrix(result.CameraMatrix);
        DistortionText = CalibrationProfileText.FormatMatrix(result.DistortionCoefficients);
        CameraQualityText = result.RmsReprojectionError <= 0.5
            ? "重投影误差较好"
            : result.RmsReprojectionError <= 1.0
                ? "重投影误差可用，建议增加不同角度图像"
                : "重投影误差偏大，请剔除失败图或重拍棋盘格图像";
        CameraQualitySummary = CalibrationQualitySummary.FromCamera(result);
    }

    private void ReloadPointToolOptions(Recipe recipe)
    {
        PointToolOptions.Clear();
        foreach (var tool in recipe.GetActiveFlow().Tools.Where(SupportsPointSampling))
        {
            PointToolOptions.Add(new VisionToolOptionItem(tool.Id, tool.Name, tool.Kind));
        }

        SelectedPointTool = PointToolOptions.FirstOrDefault();
    }

    private void UpdateAutoPlaneOverlays()
    {
        AutoPlaneOverlays.Clear();
        foreach (var sample in AutoPlaneSamples.Where(item => item.TryGetImagePoint(out _)))
        {
            if (!sample.TryGetImagePoint(out var point))
            {
                continue;
            }

            AutoPlaneOverlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Cross,
                State = sample.Success ? VisionOverlayState.Ok : VisionOverlayState.Warning,
                Label = sample.Index.ToString(CultureInfo.InvariantCulture),
                X = point.X,
                Y = point.Y
            });
        }
    }

    private void UpdateChessboardOverlays()
    {
        ChessboardOverlays.Clear();
        if (SelectedObservation is not { Found: true } observation)
        {
            return;
        }

        ChessboardOverlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.XMarker,
            State = VisionOverlayState.Ok,
            Label = "角点",
            Points = observation.Result.ImagePoints
        });
    }

    private async Task MoveAxisAsync(string axisKey, double position, double speed, double acceleration)
    {
        await _axis.MoveAbsoluteAsync(new AxisMoveCommand
        {
            AxisKey = axisKey,
            Position = position,
            Speed = speed,
            Acceleration = acceleration,
            Deceleration = acceleration,
            Timeout = TimeSpan.FromSeconds(10),
            WaitForCompletion = true
        });
    }

    private double ReadAxisPosition(AxisStatus status)
    {
        return UseEncoderPosition ? status.EncoderPosition : status.CommandPosition;
    }

    private ChessboardCalibrationPattern CreatePattern()
    {
        return new ChessboardCalibrationPattern
        {
            Columns = ParseInt(ChessboardColumnsText, 9),
            Rows = ParseInt(ChessboardRowsText, 6),
            SquareSize = ParseDouble(SquareSizeText, 1),
            Unit = string.IsNullOrWhiteSpace(CameraUnitText) ? "mm" : CameraUnitText.Trim()
        };
    }

    private static Recipe BuildSamplingRecipe(Recipe recipe, string selectedToolId)
    {
        var flow = recipe.GetActiveFlow();
        var tools = flow.Tools.TakeWhileIncluding(tool => !string.Equals(tool.Id, selectedToolId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return recipe.WithActiveFlow(flow with { Tools = tools });
    }

    private static IEnumerable<Point2D> CreateNinePointSequence(double startX, double startY, double stepX, double stepY)
    {
        for (var row = -1; row <= 1; row++)
        {
            for (var column = -1; column <= 1; column++)
            {
                yield return new Point2D(startX + column * stepX, startY + row * stepY);
            }
        }
    }

    private static bool SupportsPointSampling(VisionToolDefinition tool)
    {
        return tool.Kind is VisionToolKind.TemplateLocate
            or VisionToolKind.MultiTargetMatch
            or VisionToolKind.FindCircle
            or VisionToolKind.DefectDetect
            or VisionToolKind.TemplatePoint
            or VisionToolKind.LineIntersection;
    }

    private static bool TryExtractImagePoint(ToolResult result, out Point2D point, out string error)
    {
        point = new Point2D(0, 0);
        error = string.Empty;
        var aliases = new (string X, string Y)[]
        {
            ("x", "y"),
            ("X", "Y"),
            ("centerX", "centerY"),
            ("midX", "midY"),
            ("circleX", "circleY"),
            ("imageX", "imageY")
        };

        foreach (var (xKey, yKey) in aliases)
        {
            if (TryGetDouble(result.Data, xKey, out var x) && TryGetDouble(result.Data, yKey, out var y))
            {
                point = new Point2D(x, y);
                return true;
            }
        }

        error = "视觉工具结果中没有可用的 X/Y 点位字段";
        return false;
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> data, string key, out double value)
    {
        if (data.TryGetValue(key, out var text))
        {
            return TryParseDouble(text, out value);
        }

        foreach (var item in data)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return TryParseDouble(item.Value, out value);
            }
        }

        value = 0;
        return false;
    }

    private static int ParseInt(string? text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               int.TryParse(text, out value)
            ? value
            : fallback;
    }

    private static double ParseDouble(string? text, double fallback)
    {
        return TryParseDouble(text, out var value) ? value : fallback;
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
               double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

public enum CalibrationQualityLevel
{
    None,
    Good,
    Warning,
    Bad
}

public static class CalibrationPlaneValidation
{
    public static bool CanCalculate(PlaneCalibrationModel model, int validPointCount, out string message)
    {
        if (validPointCount < 3)
        {
            message = "至少需要 3 个有效点";
            return false;
        }

        if (model == PlaneCalibrationModel.Homography && validPointCount < 4)
        {
            message = "Homography 至少需要 4 个有效点";
            return false;
        }

        message = string.Empty;
        return true;
    }
}

public sealed record CalibrationQualitySummary(
    CalibrationQualityLevel Level,
    string Label,
    string Details,
    string MaxErrorPointText)
{
    public static CalibrationQualitySummary Empty(string details = "尚未计算") =>
        new(CalibrationQualityLevel.None, "-", details, "-");

    public static CalibrationQualitySummary FromPlane(PlaneCalibrationResult? result)
    {
        if (result is null)
        {
            return Empty();
        }

        var level = result.RmsError <= 0.05
            ? CalibrationQualityLevel.Good
            : result.RmsError <= 0.2
                ? CalibrationQualityLevel.Warning
                : CalibrationQualityLevel.Bad;

        var details =
            $"RMS={Format(result.RmsError)} {result.Unit}  Max={Format(result.MaxError)} {result.Unit}  点数={result.PointCount}  内点={result.InlierCount}";

        return new CalibrationQualitySummary(level, GetLabel(level), details, GetMaxErrorPointText(result));
    }

    public static CalibrationQualitySummary FromCamera(CameraCalibrationResult? result)
    {
        if (result is null)
        {
            return Empty();
        }

        var level = result.RmsReprojectionError <= 0.5
            ? CalibrationQualityLevel.Good
            : result.RmsReprojectionError <= 1.0
                ? CalibrationQualityLevel.Warning
                : CalibrationQualityLevel.Bad;

        var details =
            $"RMS={Format(result.RmsReprojectionError)} px  图像={result.ImageWidth}x{result.ImageHeight}  视图={result.Views.Count}";

        return new CalibrationQualitySummary(level, GetLabel(level), details, "-");
    }

    private static string GetLabel(CalibrationQualityLevel level)
    {
        return level switch
        {
            CalibrationQualityLevel.Good => "可用",
            CalibrationQualityLevel.Warning => "警告",
            CalibrationQualityLevel.Bad => "不建议保存",
            _ => "-"
        };
    }

    private static string GetMaxErrorPointText(PlaneCalibrationResult result)
    {
        if (result.PointErrors.Count == 0)
        {
            return "-";
        }

        var maxIndex = 0;
        var maxError = result.PointErrors[0].Error;
        for (var index = 1; index < result.PointErrors.Count; index++)
        {
            var error = result.PointErrors[index].Error;
            if (error > maxError)
            {
                maxError = error;
                maxIndex = index;
            }
        }

        return $"最大误差点：#{maxIndex + 1} ({Format(maxError)} {result.Unit})";
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

public sealed record VisionToolOptionItem(string ToolId, string Name, VisionToolKind Kind)
{
    public override string ToString()
    {
        return $"{Name} ({Kind})";
    }
}

public sealed class AutoPlaneSampleItem : BindableBase
{
    private ImageFrame? _frame;
    private string _actualX = string.Empty;
    private string _actualY = string.Empty;
    private string _imageX = string.Empty;
    private string _imageY = string.Empty;
    private string _worldX = string.Empty;
    private string _worldY = string.Empty;
    private string _status = "-";
    private string _message = string.Empty;

    public int Index { get; init; }

    public string TargetX { get; init; } = string.Empty;

    public string TargetY { get; init; } = string.Empty;

    public string ActualX
    {
        get => _actualX;
        set => SetProperty(ref _actualX, value);
    }

    public string ActualY
    {
        get => _actualY;
        set => SetProperty(ref _actualY, value);
    }

    public string ImageX
    {
        get => _imageX;
        set => SetProperty(ref _imageX, value);
    }

    public string ImageY
    {
        get => _imageY;
        set => SetProperty(ref _imageY, value);
    }

    public string WorldX
    {
        get => _worldX;
        set => SetProperty(ref _worldX, value);
    }

    public string WorldY
    {
        get => _worldY;
        set => SetProperty(ref _worldY, value);
    }

    public ImageFrame? Frame
    {
        get => _frame;
        set => SetProperty(ref _frame, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public bool Success => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);

    public void MarkFailure(string message)
    {
        Status = "NG";
        Message = message;
    }

    public bool TryGetImagePoint(out Point2D point)
    {
        point = new Point2D(0, 0);
        if (!TryParse(ImageX, out var imageX) || !TryParse(ImageY, out var imageY))
        {
            return false;
        }

        point = new Point2D(imageX, imageY);
        return true;
    }

    public bool TryToPair(out CalibrationPointPair pair)
    {
        pair = default!;
        if (!TryParse(ImageX, out var imageX) ||
            !TryParse(ImageY, out var imageY) ||
            !TryParse(WorldX, out var worldX) ||
            !TryParse(WorldY, out var worldY))
        {
            return false;
        }

        pair = new CalibrationPointPair(new Point2D(imageX, imageY), new Point2D(worldX, worldY));
        return true;
    }

    private static bool TryParse(string? text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
               double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }
}

public sealed record ChessboardObservationItem(ChessboardDetectionResult Result, ImageFrame Frame, string? FailureMessage = null)
{
    public string Source => Frame.Source;

    public string FileName => string.IsNullOrWhiteSpace(Source) ? Result.FrameId : Path.GetFileName(Source);

    public bool Found => Result.Found;

    public string Status => Found ? "OK" : "NG";

    public string ImageSize => Result.ImageWidth <= 0 || Result.ImageHeight <= 0 ? "-" : $"{Result.ImageWidth}x{Result.ImageHeight}";

    public int PointCount => Result.ImagePoints.Count;

    public string Message => FailureMessage ?? Result.Message;

    public static ChessboardObservationItem Failed(string path, string message)
    {
        return new ChessboardObservationItem(
            new ChessboardDetectionResult
            {
                FrameId = Path.GetFileName(path),
                Message = message
            },
            new ImageFrame(
                Guid.NewGuid().ToString("N"),
                0,
                0,
                0,
                PixelFormatKind.Gray8,
                [],
                DateTimeOffset.Now,
                path),
            message);
    }
}

internal static class EnumerableExtensions
{
    public static IEnumerable<T> TakeWhileIncluding<T>(this IEnumerable<T> source, Func<T, bool> predicate)
    {
        foreach (var item in source)
        {
            yield return item;
            if (!predicate(item))
            {
                yield break;
            }
        }
    }
}
