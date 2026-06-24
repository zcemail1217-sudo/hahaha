using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Vision.UI.Services;
using VisionStation.Devices;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.ViewModels;

public sealed class AcquireImageToolDialogViewModel : BindableBase
{
    private static readonly string[] SupportedImageExtensions = [".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff"];

    private readonly ICameraDevice _camera;
    private readonly ICameraDeviceDiscovery _cameraDiscovery;
    private readonly IConfigurableCameraDevice _configurableCamera;
    private readonly ICameraDiagnosticsProvider _cameraDiagnostics;
    private readonly IImageFrameFileService _imageFiles;
    private readonly IAppLogService _log;
    private readonly bool _enabled;
    private CancellationTokenSource? _realtimeCts;
    private string _name;
    private string _selectedSourceKey;
    private string _selectedDevice;
    private CameraDeviceSelectionItem? _selectedCameraDevice;
    private string _cameraSerial;
    private string _selectedFilePath;
    private string _selectedDirectoryPath;
    private double _exposureTimeUs;
    private int _heartbeatTimeoutMs;
    private bool _clearBufferBeforeTrigger;
    private string _triggerSource;
    private bool _runFlowOnTrigger;
    private bool _convertColorToGray;
    private bool _showCrosshair;
    private bool _defineImageSize;
    private bool _enableVariableImageInput;
    private bool _isRealtimeDisplayEnabled;
    private bool _isBusy;
    private ImageFrame? _currentFrame;
    private string _durationText = "0ms";
    private string _statusText = "等待采图";
    private string _cameraDiagnosticsText = "相机诊断：未连接";

    public AcquireImageToolDialogViewModel(
        VisionToolItem tool,
        string flowName,
        ICameraDevice camera,
        ICameraDeviceDiscovery cameraDiscovery,
        IConfigurableCameraDevice configurableCamera,
        ICameraDiagnosticsProvider cameraDiagnostics,
        IImageFrameFileService imageFiles,
        IAppLogService log)
    {
        _camera = camera;
        _cameraDiscovery = cameraDiscovery;
        _configurableCamera = configurableCamera;
        _cameraDiagnostics = cameraDiagnostics;
        _imageFiles = imageFiles;
        _log = log;

        var parameters = ParseParameters(tool.ParametersText);

        _name = tool.Name;
        _enabled = tool.Enabled;
        _selectedSourceKey = GetString(parameters, "source", "Camera");
        _selectedDevice = GetString(parameters, "device", "SIM-CAM-01");
        _cameraSerial = GetString(parameters, "cameraSerial", string.Empty);
        _selectedFilePath = GetString(parameters, "filePath", string.Empty);
        _selectedDirectoryPath = GetString(parameters, "directoryPath", string.Empty);
        _exposureTimeUs = GetExposureTimeUs(parameters, 0);
        _heartbeatTimeoutMs = (int)Math.Clamp(GetDouble(parameters, "heartbeatTimeoutMs", 3000), 1000, 60000);
        _clearBufferBeforeTrigger = GetBool(parameters, "clearBufferBeforeTrigger", true);
        _triggerSource = GetString(parameters, "triggerSource", "软件触发");
        _runFlowOnTrigger = GetBool(parameters, "runFlowOnTrigger", false);
        _convertColorToGray = GetBool(parameters, "convertColorToGray", true);
        _showCrosshair = GetBool(parameters, "showCrosshair", true);
        _defineImageSize = GetBool(parameters, "defineImageSize", true);
        _enableVariableImageInput = GetBool(parameters, "enableVariableImageInput", false);

        WindowTitle = string.IsNullOrWhiteSpace(flowName)
            ? _name
            : $"{_name} [ {flowName}. {_name} ]";

        SourceTabs =
        [
            new AcquisitionSourceTabItem("Camera", "设备采集", "\uE722"),
            new AcquisitionSourceTabItem("File", "文件采集", "\uE8A5"),
            new AcquisitionSourceTabItem("Directory", "目录采集", "\uE8B7")
        ];

        DeviceList = new ObservableCollection<CameraDeviceSelectionItem>();

        TriggerSources = new ObservableCollection<string>
        {
            "软件触发",
            "Line0",
            "Line1",
            "连续采集"
        };

        SelectSource(SourceTabs.FirstOrDefault(tab => tab.Key == _selectedSourceKey) ?? SourceTabs[0]);

        SelectSourceCommand = new DelegateCommand<AcquisitionSourceTabItem>(SelectSource);
        RefreshDevicesCommand = new DelegateCommand(async () => await RefreshDevicesAsync(), () => !IsBusy)
            .ObservesProperty(() => IsBusy);
        SelectImageFileCommand = new DelegateCommand(async () => await SelectImageFileAsync(), () => !IsBusy)
            .ObservesProperty(() => IsBusy);
        BrowseDirectoryCommand = new DelegateCommand(async () => await BrowseDirectoryAsync(), () => !IsBusy)
            .ObservesProperty(() => IsBusy);
        ToggleRealtimeCommand = new DelegateCommand(async () => await ToggleRealtimeAsync(), () => !IsBusy && IsCameraSource)
            .ObservesProperty(() => IsBusy)
            .ObservesProperty(() => IsCameraSource);
        SaveImageCommand = new DelegateCommand(async () => await SaveImageAsync(), () => CurrentFrame is not null && !IsBusy)
            .ObservesProperty(() => CurrentFrame)
            .ObservesProperty(() => IsBusy);
        RunToolCommand = new DelegateCommand(async () => { await RunToolAsync(); }, () => !IsBusy)
            .ObservesProperty(() => IsBusy);
        RunFlowCommand = new DelegateCommand(async () => await RunFlowAsync(), () => !IsBusy)
            .ObservesProperty(() => IsBusy);
        CloseCommand = new DelegateCommand(Close);

        RefreshCameraDiagnostics();
        _ = RefreshDevicesAsync();
    }

    public event EventHandler<bool>? CloseRequested;

    public event EventHandler<ImageFrame>? FrameUpdated;

    public bool RunFlowRequested { get; private set; }

    public string WindowTitle { get; }

    public ObservableCollection<AcquisitionSourceTabItem> SourceTabs { get; }

    public ObservableCollection<CameraDeviceSelectionItem> DeviceList { get; }

    public ObservableCollection<string> TriggerSources { get; }

    public DelegateCommand<AcquisitionSourceTabItem> SelectSourceCommand { get; }

    public DelegateCommand RefreshDevicesCommand { get; }

    public DelegateCommand SelectImageFileCommand { get; }

    public DelegateCommand BrowseDirectoryCommand { get; }

    public DelegateCommand ToggleRealtimeCommand { get; }

    public DelegateCommand SaveImageCommand { get; }

    public DelegateCommand RunToolCommand { get; }

    public DelegateCommand RunFlowCommand { get; }

    public DelegateCommand CloseCommand { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string SelectedSourceKey
    {
        get => _selectedSourceKey;
        private set
        {
            if (SetProperty(ref _selectedSourceKey, value))
            {
                RaisePropertyChanged(nameof(IsCameraSource));
                RaisePropertyChanged(nameof(IsFileSource));
                RaisePropertyChanged(nameof(IsDirectorySource));
                RaisePropertyChanged(nameof(SourceDescription));
                RaisePropertyChanged(nameof(PrimaryActionText));
                ToggleRealtimeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCameraSource => SelectedSourceKey == "Camera";

    public bool IsFileSource => SelectedSourceKey == "File";

    public bool IsDirectorySource => SelectedSourceKey == "Directory";

    public string SourceDescription => SelectedSourceKey switch
    {
        "File" => "从本地图片文件取图，适合离线调试模板、ROI和流程。",
        "Directory" => "从图像目录取图，适合批量样图回放和稳定性测试。",
        _ => "从相机设备取图，支持曝光、触发源和实时显示。"
    };

    public string PrimaryActionText => SelectedSourceKey switch
    {
        "File" => string.IsNullOrWhiteSpace(SelectedFilePath) ? "选择图片" : "加载图片",
        "Directory" => string.IsNullOrWhiteSpace(SelectedDirectoryPath) ? "选择目录" : "目录取图",
        _ => "采集一张"
    };

    public string SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                SelectCurrentDeviceFromList();
            }
        }
    }

    public CameraDeviceSelectionItem? SelectedCameraDevice
    {
        get => _selectedCameraDevice;
        set
        {
            if (SetProperty(ref _selectedCameraDevice, value) && value is not null)
            {
                _selectedDevice = value.DeviceId;
                RaisePropertyChanged(nameof(SelectedDevice));
                CameraSerial = value.SerialNumber;
            }
        }
    }

    public string CameraSerial
    {
        get => _cameraSerial;
        set => SetProperty(ref _cameraSerial, value);
    }

    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value))
            {
                RaisePropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public string SelectedDirectoryPath
    {
        get => _selectedDirectoryPath;
        set
        {
            if (SetProperty(ref _selectedDirectoryPath, value))
            {
                RaisePropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public double ExposureTimeUs
    {
        get => _exposureTimeUs;
        set => SetProperty(ref _exposureTimeUs, Math.Round(Math.Max(0, value), 1));
    }

    public int HeartbeatTimeoutMs
    {
        get => _heartbeatTimeoutMs;
        set => SetProperty(ref _heartbeatTimeoutMs, (int)Math.Clamp(value, 1000, 60000));
    }

    public bool ClearBufferBeforeTrigger
    {
        get => _clearBufferBeforeTrigger;
        set => SetProperty(ref _clearBufferBeforeTrigger, value);
    }

    public string TriggerSource
    {
        get => _triggerSource;
        set => SetProperty(ref _triggerSource, value);
    }

    public bool RunFlowOnTrigger
    {
        get => _runFlowOnTrigger;
        set => SetProperty(ref _runFlowOnTrigger, value);
    }

    public bool ConvertColorToGray
    {
        get => _convertColorToGray;
        set => SetProperty(ref _convertColorToGray, value);
    }

    public bool ShowCrosshair
    {
        get => _showCrosshair;
        set => SetProperty(ref _showCrosshair, value);
    }

    public bool DefineImageSize
    {
        get => _defineImageSize;
        set => SetProperty(ref _defineImageSize, value);
    }

    public bool EnableVariableImageInput
    {
        get => _enableVariableImageInput;
        set => SetProperty(ref _enableVariableImageInput, value);
    }

    public bool IsRealtimeDisplayEnabled
    {
        get => _isRealtimeDisplayEnabled;
        set => SetProperty(ref _isRealtimeDisplayEnabled, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public ImageFrame? CurrentFrame
    {
        get => _currentFrame;
        private set => SetProperty(ref _currentFrame, value);
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

    public string CameraDiagnosticsText
    {
        get => _cameraDiagnosticsText;
        private set => SetProperty(ref _cameraDiagnosticsText, value);
    }

    public void ApplyTo(VisionToolItem tool)
    {
        tool.Name = string.IsNullOrWhiteSpace(Name) ? tool.Name : Name.Trim();
        tool.Kind = VisionToolKind.AcquireImage;
        tool.Enabled = _enabled;
        tool.RoiId = string.Empty;

        var parameters = ParseParameters(tool.ParametersText);
        parameters["source"] = SelectedSourceKey;
        parameters["device"] = SelectedDevice;
        parameters["cameraSerial"] = CameraSerial;
        parameters["filePath"] = SelectedFilePath;
        parameters["directoryPath"] = SelectedDirectoryPath;
        parameters.Remove("exposureMs");
        parameters["exposureUs"] = ExposureTimeUs.ToString("0.#");
        parameters["heartbeatTimeoutMs"] = HeartbeatTimeoutMs.ToString();
        parameters["clearBufferBeforeTrigger"] = ClearBufferBeforeTrigger.ToString();
        parameters["triggerSource"] = TriggerSource;
        parameters["runFlowOnTrigger"] = RunFlowOnTrigger.ToString();
        parameters["convertColorToGray"] = ConvertColorToGray.ToString();
        parameters["showCrosshair"] = ShowCrosshair.ToString();
        parameters["defineImageSize"] = DefineImageSize.ToString();
        parameters["enableVariableImageInput"] = EnableVariableImageInput.ToString();

        tool.ParametersText = string.Join("; ", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
    }

    private async Task RefreshDevicesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var devices = await _cameraDiscovery.DiscoverAsync();
            var selectedDeviceId = SelectedDevice;
            DeviceList.Clear();
            foreach (var device in devices)
            {
                DeviceList.Add(new CameraDeviceSelectionItem(device));
            }

            if (DeviceList.Count == 0)
            {
                SelectedCameraDevice = null;
                StatusText = "未发现相机，请确认 MVS 客户端可见相机后刷新";
                return;
            }

            SelectedCameraDevice =
                DeviceList.FirstOrDefault(device => device.Matches(selectedDeviceId)) ??
                DeviceList.FirstOrDefault(device => device.Matches(CameraSerial)) ??
                DeviceList[0];
            StatusText = $"发现 {DeviceList.Count} 台相机";
            RefreshCameraDiagnostics();
        }
        catch (Exception ex)
        {
            StatusText = $"刷新相机列表失败：{ex.Message}";
            _log.Error("VisionDebug", $"{Name} 刷新相机列表失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SelectCurrentDeviceFromList()
    {
        if (DeviceList.Count == 0 || string.IsNullOrWhiteSpace(_selectedDevice))
        {
            return;
        }

        var selected = DeviceList.FirstOrDefault(device => device.Matches(_selectedDevice));
        if (selected is not null && !ReferenceEquals(SelectedCameraDevice, selected))
        {
            _selectedCameraDevice = selected;
            RaisePropertyChanged(nameof(SelectedCameraDevice));
        }
    }

    private void SelectSource(AcquisitionSourceTabItem tab)
    {
        if (tab is null)
        {
            return;
        }

        foreach (var sourceTab in SourceTabs)
        {
            sourceTab.IsSelected = ReferenceEquals(sourceTab, tab);
        }

        if (tab.Key != "Camera")
        {
            StopRealtime();
        }

        SelectedSourceKey = tab.Key;
        StatusText = tab.Key switch
        {
            "File" => "文件采集：请选择或加载本地图像",
            "Directory" => "目录采集：请选择图像目录后取图",
            _ => "设备采集：等待触发"
        };
    }

    private async Task ToggleRealtimeAsync()
    {
        if (IsRealtimeDisplayEnabled)
        {
            StopRealtime();
            return;
        }

        if (!IsCameraSource)
        {
            StatusText = "实时显示只支持设备采集";
            return;
        }

        _realtimeCts = new CancellationTokenSource();
        IsRealtimeDisplayEnabled = true;
        StatusText = "实时显示已开启";

        try
        {
            while (!_realtimeCts.IsCancellationRequested)
            {
                await GrabCameraFrameAsync(_realtimeCts.Token);
                await Task.Delay(250, _realtimeCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop path.
        }
        finally
        {
            IsRealtimeDisplayEnabled = false;
            _realtimeCts?.Dispose();
            _realtimeCts = null;
            if (StatusText == "实时显示已开启")
            {
                StatusText = "实时显示已停止";
            }
        }
    }

    private async Task SelectImageFileAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var frame = await _imageFiles.PickImageAsync();
            if (frame is null)
            {
                StatusText = "已取消选择图片";
                return;
            }

            SelectedFilePath = frame.Source;
            ApplyFrame(frame);
            stopwatch.Stop();
            DurationText = $"{stopwatch.ElapsedMilliseconds}ms";
            StatusText = $"文件加载成功 {frame.Width}x{frame.Height}";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DurationText = $"{stopwatch.ElapsedMilliseconds}ms";
            StatusText = $"文件加载失败：{ex.Message}";
            _log.Error("VisionDebug", $"{Name} 文件加载失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BrowseDirectoryAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var directory = await _imageFiles.PickDirectoryAsync();
            if (string.IsNullOrWhiteSpace(directory))
            {
                StatusText = "已取消选择目录";
                return;
            }

            SelectedDirectoryPath = directory;
            StatusText = "目录已选择";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> RunToolAsync()
    {
        if (IsBusy)
        {
            return false;
        }

        StopRealtime();
        IsBusy = true;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ImageFrame? frame;
            if (IsCameraSource)
            {
                frame = await GrabCameraFrameAsync(CancellationToken.None);
            }
            else if (IsFileSource)
            {
                frame = await LoadFileSourceFrameAsync();
            }
            else
            {
                frame = await LoadDirectorySourceFrameAsync();
            }

            stopwatch.Stop();
            if (frame is null)
            {
                StatusText = IsFileSource ? "未选择图片" : "未选择图像目录";
                return false;
            }

            DurationText = $"{stopwatch.ElapsedMilliseconds}ms";
            StatusText = $"取图成功 {frame.Width}x{frame.Height}";
            _log.Info("VisionDebug", $"{Name} 取图成功：{frame.Width}x{frame.Height}");
            return true;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DurationText = $"{stopwatch.ElapsedMilliseconds}ms";
            StatusText = $"取图失败：{ex.Message}";
            _log.Error("VisionDebug", $"{Name} 取图失败：{ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunFlowAsync()
    {
        if (await RunToolAsync())
        {
            StatusText = "流程运行完成";
            _log.Info("VisionDebug", $"{Name} 触发流程运行完成");
            RunFlowRequested = true;
            CloseRequested?.Invoke(this, true);
        }
    }

    private async Task SaveImageAsync()
    {
        if (CurrentFrame is null)
        {
            StatusText = "没有可保存的图像";
            return;
        }

        try
        {
            var path = await _imageFiles.SaveImageAsync(CurrentFrame);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText = "已取消图像另存";
                return;
            }

            StatusText = "图像另存成功";
            _log.Info("VisionDebug", $"{Name} 图像另存：{path}");
        }
        catch (Exception ex)
        {
            StatusText = $"图像另存失败：{ex.Message}";
            _log.Error("VisionDebug", $"{Name} 图像另存失败：{ex.Message}");
        }
    }

    private async Task<ImageFrame?> LoadFileSourceFrameAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            var picked = await _imageFiles.PickImageAsync();
            if (picked is null)
            {
                return null;
            }

            SelectedFilePath = picked.Source;
            ApplyFrame(picked);
            return CurrentFrame ?? picked;
        }

        var frame = await _imageFiles.LoadImageAsync(SelectedFilePath);
        ApplyFrame(frame);
        return CurrentFrame ?? frame;
    }

    private async Task<ImageFrame?> LoadDirectorySourceFrameAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedDirectoryPath))
        {
            SelectedDirectoryPath = await _imageFiles.PickDirectoryAsync() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(SelectedDirectoryPath))
        {
            return null;
        }

        var file = Directory.EnumerateFiles(SelectedDirectoryPath)
            .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (file is null)
        {
            StatusText = "目录内没有支持的图像文件";
            return null;
        }

        SelectedFilePath = file;
        var frame = await _imageFiles.LoadImageAsync(file);
        ApplyFrame(frame);
        return CurrentFrame ?? frame;
    }

    private async Task<ImageFrame> GrabCameraFrameAsync(CancellationToken cancellationToken)
    {
        await ApplyCameraSettingsAsync(cancellationToken);

        if (_camera.Snapshot.State != DeviceConnectionState.Connected)
        {
            await _camera.ConnectAsync(cancellationToken);
        }

        var frame = await _camera.GrabAsync(cancellationToken);
        ApplyFrame(frame);
        RefreshCameraDiagnostics();
        return CurrentFrame ?? frame;
    }

    private Task ApplyCameraSettingsAsync(CancellationToken cancellationToken)
    {
        var deviceId = SelectedCameraDevice?.DeviceId ?? SelectedDevice;
        return _configurableCamera.ApplyAcquisitionSettingsAsync(
            new CameraAcquisitionSettings
            {
                DeviceId = deviceId,
                ExposureTimeMs = ExposureTimeUs / 1000.0,
                TriggerSource = TriggerSource,
                HeartbeatTimeoutMs = HeartbeatTimeoutMs,
                ClearBufferBeforeTrigger = ClearBufferBeforeTrigger
            },
            cancellationToken);
    }

    private void RefreshCameraDiagnostics()
    {
        var diagnostics = _cameraDiagnostics.GetDiagnostics();
        var size = diagnostics.Width > 0 && diagnostics.Height > 0
            ? $"{diagnostics.Width}x{diagnostics.Height}"
            : "-";
        var packet = diagnostics.PacketSize > 0 ? diagnostics.PacketSize.ToString() : "-";
        var payload = diagnostics.PayloadSize > 0 ? diagnostics.PayloadSize.ToString() : "-";
        var frame = diagnostics.FrameNumber > 0 ? diagnostics.FrameNumber.ToString() : "-";
        var lost = diagnostics.LostPacketCount > 0 ? diagnostics.LostPacketCount.ToString() : "0";
        var error = string.IsNullOrWhiteSpace(diagnostics.LastErrorCode)
            ? string.Empty
            : $"，最后错误 {diagnostics.LastErrorCode}";

        CameraDiagnosticsText =
            $"相机诊断：{diagnostics.ConnectionState}，帧 {frame}，尺寸 {size}，Payload {payload}，包 {packet}，心跳 {diagnostics.HeartbeatTimeoutMs}ms，丢包 {lost}{error}";
    }

    private void ApplyFrame(ImageFrame frame)
    {
        CurrentFrame = ConvertColorToGray ? ToGray8(frame) : frame;
        FrameUpdated?.Invoke(this, CurrentFrame);
    }

    private void Close()
    {
        StopRealtime();
        CloseRequested?.Invoke(this, true);
    }

    private void StopRealtime()
    {
        if (_realtimeCts is null)
        {
            return;
        }

        _realtimeCts.Cancel();
        StatusText = "实时显示已停止";
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

    private static double GetExposureTimeUs(IReadOnlyDictionary<string, string> parameters, double fallback)
    {
        if (parameters.TryGetValue("exposureUs", out var exposureUs) && double.TryParse(exposureUs, out var us))
        {
            return us;
        }

        return parameters.TryGetValue("exposureMs", out var exposureMs) && double.TryParse(exposureMs, out var ms)
            ? ms * 1000.0
            : fallback;
    }
}

public sealed class AcquisitionSourceTabItem : BindableBase
{
    private bool _isSelected;

    public AcquisitionSourceTabItem(string key, string title, string icon)
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

public sealed class CameraDeviceSelectionItem
{
    public CameraDeviceSelectionItem(CameraDeviceInfo device)
    {
        Device = device;
    }

    public CameraDeviceInfo Device { get; }

    public string DeviceId => Device.DeviceId;

    public string DisplayName => Device.DisplayName;

    public string SerialNumber => Device.SerialNumber;

    public string Details
    {
        get
        {
            var parts = new[]
            {
                Device.TransportLayer,
                Device.Model,
                string.IsNullOrWhiteSpace(Device.IpAddress) ? null : $"IP {Device.IpAddress}",
                string.IsNullOrWhiteSpace(Device.AccessStatus) ? null : Device.AccessStatus
            };
            return string.Join(" / ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public bool Matches(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(Device.DeviceId, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Device.SerialNumber, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Device.DisplayName, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Device.UserDefinedName, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Device.IpAddress, value, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Details) ? DisplayName : $"{DisplayName}    {Details}";
    }
}
