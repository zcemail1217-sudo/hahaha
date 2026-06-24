using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Domain;
using VisionStation.Vision;
using VisionStation.Vision.Tools;

namespace VisionStation.Vision.UI.ViewModels;

public sealed class ImageProcessToolDialogViewModel : BindableBase
{
    private readonly Recipe? _previewRecipe;
    private readonly string _toolId;
    private readonly Dictionary<string, string> _parameters;
    private readonly string _operation;
    private ImageFrame? _previewDisplayFrame;
    private ImageFrame? _processedFrame;
    private bool _isBusy;
    private string _displayModeText;
    private string _statusText;
    private string _durationText = "0ms";
    private string _name;
    private bool _enabled;
    private string _thresholdMode;
    private string _polarity;
    private double _threshold;
    private double _grayMin;
    private double _grayMax;
    private int _adaptiveBlockSize;
    private double _adaptiveC;
    private string _filterType;
    private int _kernelSize;
    private double _sigmaColor;
    private double _sigmaSpace;
    private string _morphType;
    private string _kernelShape;
    private int _iterations;

    public ImageProcessToolDialogViewModel(
        VisionToolItem tool,
        string flowName,
        ImageFrame? currentFrame,
        Recipe? previewRecipe,
        IVisionPipeline pipeline)
    {
        _previewRecipe = previewRecipe;
        _toolId = tool.Id;
        _parameters = ParseParameters(tool.ParametersText);
        AddMissingDefaults(_parameters);

        _operation = NormalizeOperation(GetString(_parameters, "operation", "Threshold"));
        _name = tool.Name;
        _enabled = tool.Enabled;
        _thresholdMode = NormalizeThresholdMode(GetString(_parameters, "thresholdMode", "Otsu"));
        _polarity = NormalizePolarity(GetString(_parameters, "polarity", "Dark"));
        _threshold = ClampByte(GetDouble(_parameters, "threshold", 128));
        _grayMin = ClampByte(GetDouble(_parameters, "grayMin", GetDouble(_parameters, "grayLower", 0)));
        _grayMax = ClampByte(GetDouble(_parameters, "grayMax", GetDouble(_parameters, "grayUpper", 255)));
        _adaptiveBlockSize = NormalizeOdd(GetInt(_parameters, "adaptiveBlockSize", 41), 3, 501);
        _adaptiveC = GetDouble(_parameters, "adaptiveC", 5);
        _filterType = NormalizeFilterType(GetString(_parameters, "filterType", "Gaussian"));
        _kernelSize = NormalizeOdd(GetInt(_parameters, "kernelSize", GetInt(_parameters, "morphSize", 3)), 1, 99);
        _sigmaColor = Math.Clamp(GetDouble(_parameters, "sigmaColor", 45), 1, 255);
        _sigmaSpace = Math.Clamp(GetDouble(_parameters, "sigmaSpace", 45), 1, 255);
        _morphType = NormalizeMorphType(GetString(_parameters, "morphType", "Open"));
        _kernelShape = NormalizeKernelShape(GetString(_parameters, "kernelShape", "Rect"));
        _iterations = Math.Clamp(GetInt(_parameters, "iterations", 1), 1, 32);

        WindowTitle = string.IsNullOrWhiteSpace(flowName)
            ? $"{ToolDisplayName}参数配置"
            : $"{ToolDisplayName}参数配置 [ {flowName}. {tool.Name} ]";
        CurrentFrame = currentFrame;
        _previewDisplayFrame = currentFrame;
        _displayModeText = currentFrame is null ? "无输入图像" : "输入图像";
        _statusText = currentFrame is null ? "未连接输入图像" : "等待运行预览";
        InputFrameInfo = currentFrame is null
            ? "当前无调试图像"
            : $"{currentFrame.Width} x {currentFrame.Height} / {currentFrame.Format}";

        ThresholdModes = new ObservableCollection<ImageProcessOptionItem>
        {
            new("Fixed", "固定阈值"),
            new("Range", "灰度范围"),
            new("Otsu", "大津法 Otsu"),
            new("Triangle", "三角法"),
            new("Adaptive", "自适应")
        };
        PolarityOptions = new ObservableCollection<ImageProcessOptionItem>
        {
            new("Dark", "暗目标"),
            new("Bright", "亮目标")
        };
        FilterTypes = new ObservableCollection<ImageProcessOptionItem>
        {
            new("Gaussian", "高斯滤波"),
            new("Mean", "均值滤波"),
            new("Median", "中值滤波"),
            new("Bilateral", "双边滤波"),
            new("Sharpen", "锐化")
        };
        MorphTypes = new ObservableCollection<ImageProcessOptionItem>
        {
            new("Open", "开运算"),
            new("Close", "闭运算"),
            new("Erode", "腐蚀"),
            new("Dilate", "膨胀"),
            new("Gradient", "形态梯度"),
            new("TopHat", "顶帽"),
            new("BlackHat", "黑帽")
        };
        KernelShapes = new ObservableCollection<ImageProcessOptionItem>
        {
            new("Rect", "矩形"),
            new("Ellipse", "椭圆"),
            new("Cross", "十字")
        };

        InputBindings = new ObservableCollection<ToolInputBindingItem>(CreateInputBindings(GetExistingInputConnections()));
        OutputOptions = new ObservableCollection<ToolOutputOptionItem>(
            CreateOutputOptions(_parameters.GetValueOrDefault("enabledOutputs")));

        ConfirmCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, true));
        CancelCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, false));
        RunPreviewCommand = new DelegateCommand(async () => await RunPreviewAsync(), () => !IsBusy)
            .ObservesProperty(() => IsBusy);
        ShowInputImageCommand = new DelegateCommand(ShowInputImage, () => CurrentFrame is not null);
        ShowProcessedImageCommand = new DelegateCommand(ShowProcessedImage, () => ProcessedFrame is not null)
            .ObservesProperty(() => ProcessedFrame);
    }

    public event EventHandler<bool>? CloseRequested;

    public string WindowTitle { get; }

    public string HeaderIcon => _operation switch
    {
        "Filter" => "\uE71A",
        "Morphology" => "\uE8EE",
        _ => "\uE9D9"
    };

    public string ToolDisplayName => _operation switch
    {
        "Filter" => "滤波降噪",
        "Morphology" => "形态学",
        _ => "二值化"
    };

    public string ToolTypeText => $"ImageProcess / {ToolDisplayName}";

    public ImageFrame? CurrentFrame { get; }

    public bool IsMissingInputImage => PreviewInputFrame is null;

    public string MissingInputImageText => "请先采集或运行到有输入图像，再打开本工具调参。";

    public ImageFrame? PreviewDisplayFrame
    {
        get => _previewDisplayFrame;
        private set
        {
            if (SetProperty(ref _previewDisplayFrame, value))
            {
                RaisePropertyChanged(nameof(IsMissingInputImage));
            }
        }
    }

    public ImageFrame? ProcessedFrame
    {
        get => _processedFrame;
        private set
        {
            if (SetProperty(ref _processedFrame, value))
            {
                RaisePropertyChanged(nameof(HasProcessedFrame));
            }
        }
    }

    public bool HasProcessedFrame => ProcessedFrame is not null;

    public string DisplayModeText
    {
        get => _displayModeText;
        private set => SetProperty(ref _displayModeText, value);
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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RunPreviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private ImageFrame? PreviewInputFrame => CurrentFrame ?? _previewDisplayFrame ?? _processedFrame;

    public string InputFrameInfo { get; }

    public ObservableCollection<ImageProcessOptionItem> ThresholdModes { get; }

    public ObservableCollection<ImageProcessOptionItem> PolarityOptions { get; }

    public ObservableCollection<ImageProcessOptionItem> FilterTypes { get; }

    public ObservableCollection<ImageProcessOptionItem> MorphTypes { get; }

    public ObservableCollection<ImageProcessOptionItem> KernelShapes { get; }

    public ObservableCollection<ToolInputBindingItem> InputBindings { get; }

    public bool HasInputBindings => InputBindings.Count > 0;

    public ObservableCollection<ToolOutputOptionItem> OutputOptions { get; }

    public bool HasOutputOptions => OutputOptions.Count > 0;

    public DelegateCommand ConfirmCommand { get; }

    public DelegateCommand CancelCommand { get; }

    public DelegateCommand RunPreviewCommand { get; }

    public DelegateCommand ShowInputImageCommand { get; }

    public DelegateCommand ShowProcessedImageCommand { get; }

    public bool IsThresholdOperation => _operation == "Threshold";

    public bool IsFilterOperation => _operation == "Filter";

    public bool IsMorphologyOperation => _operation == "Morphology";

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

    public string ThresholdMode
    {
        get => _thresholdMode;
        set
        {
            var normalized = NormalizeThresholdMode(value);
            if (SetProperty(ref _thresholdMode, normalized))
            {
                RaisePropertyChanged(nameof(IsFixedMode));
                RaisePropertyChanged(nameof(IsRangeMode));
                RaisePropertyChanged(nameof(IsAdaptiveMode));
                RaisePropertyChanged(nameof(IsAutomaticMode));
                RaisePropertyChanged(nameof(ThresholdModeHint));
            }
        }
    }

    public string Polarity
    {
        get => _polarity;
        set => SetProperty(ref _polarity, NormalizePolarity(value));
    }

    public double Threshold
    {
        get => _threshold;
        set => SetProperty(ref _threshold, ClampByte(value));
    }

    public double GrayMin
    {
        get => _grayMin;
        set => SetProperty(ref _grayMin, ClampByte(value));
    }

    public double GrayMax
    {
        get => _grayMax;
        set => SetProperty(ref _grayMax, ClampByte(value));
    }

    public int AdaptiveBlockSize
    {
        get => _adaptiveBlockSize;
        set => SetProperty(ref _adaptiveBlockSize, NormalizeOdd(value, 3, 501));
    }

    public double AdaptiveC
    {
        get => _adaptiveC;
        set => SetProperty(ref _adaptiveC, Math.Clamp(value, -255, 255));
    }

    public string FilterType
    {
        get => _filterType;
        set
        {
            var normalized = NormalizeFilterType(value);
            if (SetProperty(ref _filterType, normalized))
            {
                RaisePropertyChanged(nameof(FilterModeHint));
                RaisePropertyChanged(nameof(IsBilateralFilter));
                RaisePropertyChanged(nameof(UsesKernelSize));
            }
        }
    }

    public int KernelSize
    {
        get => _kernelSize;
        set => SetProperty(ref _kernelSize, NormalizeOdd(value, 1, 99));
    }

    public double SigmaColor
    {
        get => _sigmaColor;
        set => SetProperty(ref _sigmaColor, Math.Clamp(value, 1, 255));
    }

    public double SigmaSpace
    {
        get => _sigmaSpace;
        set => SetProperty(ref _sigmaSpace, Math.Clamp(value, 1, 255));
    }

    public string MorphType
    {
        get => _morphType;
        set
        {
            var normalized = NormalizeMorphType(value);
            if (SetProperty(ref _morphType, normalized))
            {
                RaisePropertyChanged(nameof(MorphologyModeHint));
            }
        }
    }

    public string KernelShape
    {
        get => _kernelShape;
        set => SetProperty(ref _kernelShape, NormalizeKernelShape(value));
    }

    public int Iterations
    {
        get => _iterations;
        set => SetProperty(ref _iterations, Math.Clamp(value, 1, 32));
    }

    public bool IsFixedMode => ThresholdMode == "Fixed";

    public bool IsRangeMode => ThresholdMode == "Range";

    public bool IsAdaptiveMode => ThresholdMode == "Adaptive";

    public bool IsAutomaticMode => ThresholdMode is "Otsu" or "Triangle";

    public string ThresholdModeHint => ThresholdMode switch
    {
        "Fixed" => "单阈值分割",
        "Range" => "保留灰度区间",
        "Adaptive" => "局部阈值分割",
        "Triangle" => "自动三角阈值",
        _ => "自动大津阈值"
    };

    public bool IsBilateralFilter => FilterType == "Bilateral";

    public bool UsesKernelSize => FilterType != "Sharpen";

    public string FilterModeHint => FilterType switch
    {
        "Mean" => "平滑均值滤波",
        "Median" => "椒盐噪声抑制",
        "Bilateral" => "保边降噪",
        "Sharpen" => "边缘锐化增强",
        _ => "通用高斯降噪"
    };

    public string MorphologyModeHint => MorphType switch
    {
        "Erode" => "缩小亮区域",
        "Dilate" => "扩张亮区域",
        "Close" => "填补暗孔洞",
        "Gradient" => "提取边缘轮廓",
        "TopHat" => "突出小亮特征",
        "BlackHat" => "突出小暗特征",
        _ => "去除小亮噪声"
    };

    public void ApplyTo(VisionToolItem tool)
    {
        tool.Name = string.IsNullOrWhiteSpace(Name) ? tool.Name : Name.Trim();
        tool.Kind = VisionToolKind.ImageProcess;
        tool.Enabled = Enabled;
        tool.RoiId = string.Empty;
        tool.ParametersText = FormatParameters(BuildCurrentParameters(includeInputBindings: true));
    }

    private async Task RunPreviewAsync()
    {
        var inputFrame = PreviewInputFrame;
        if (inputFrame is null)
        {
            StatusText = "没有可用输入图像";
            return;
        }

        IsBusy = true;
        StatusText = $"正在运行预览... {DateTimeOffset.Now:HH:mm:ss.fff}";
        DisplayModeText = "正在处理...";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var previewSourceId = $"{_toolId}-preview-source";
            var parameters = BuildCurrentParameters(includeInputBindings: false);
            parameters["inputImageToolId"] = previewSourceId;
            parameters["inputImageSourceToolId"] = previewSourceId;
            parameters[GetConnectionToolParameterKey("ImageInput")] = previewSourceId;
            parameters[GetConnectionPortParameterKey("ImageInput")] = "ImageOutput";

            var sourceTool = new VisionToolDefinition
            {
                Id = previewSourceId,
                Name = "预览输入",
                Kind = VisionToolKind.AcquireImage,
                Enabled = true
            };
            var processTool = new VisionToolDefinition
            {
                Id = _toolId,
                Name = string.IsNullOrWhiteSpace(Name) ? ToolDisplayName : Name.Trim(),
                Kind = VisionToolKind.ImageProcess,
                Enabled = true,
                Parameters = parameters
            };

            using var context = new VisionToolContext(_previewRecipe ?? new Recipe(), inputFrame);
            var acquireResult = await new AcquireImageTool().ExecuteAsync(sourceTool, context);
            context.SetPortOutput(sourceTool, "ResultOutput", acquireResult);
            context.ToolResults.Add(acquireResult);
            context.CaptureToolFrame(sourceTool);

            var toolResult = await new ImageProcessTool().ExecuteAsync(processTool, context);
            context.SetPortOutput(processTool, "ResultOutput", toolResult);
            context.ToolResults.Add(toolResult);
            context.CaptureToolFrame(processTool);

            var outputFrame = context.CapturedToolFrames.TryGetValue(_toolId, out var capturedFrame)
                ? capturedFrame
                : context.ResultFrame;

            ProcessedFrame = outputFrame;
            ShowProcessedImage();
            DurationText = toolResult?.Duration.TotalMilliseconds.ToString("0.0ms", CultureInfo.InvariantCulture)
                           ?? stopwatch.Elapsed.TotalMilliseconds.ToString("0.0ms", CultureInfo.InvariantCulture);
            StatusText = $"{(toolResult?.Message ?? "预览完成")} / {outputFrame.Format} {outputFrame.Width}x{outputFrame.Height}";
        }
        catch (Exception ex)
        {
            StatusText = $"预览失败：{ex.Message}";
            ShowInputImage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowInputImage()
    {
        PreviewDisplayFrame = CurrentFrame;
        DisplayModeText = CurrentFrame is null ? "无输入图像" : "输入图像";
    }

    private void ShowProcessedImage()
    {
        if (ProcessedFrame is null)
        {
            return;
        }

        PreviewDisplayFrame = ProcessedFrame;
        DisplayModeText = "处理结果";
    }

    private Dictionary<string, string> BuildCurrentParameters(bool includeInputBindings)
    {
        var parameters = new Dictionary<string, string>(_parameters, StringComparer.OrdinalIgnoreCase);
        RemoveInputConnections(parameters);

        parameters["operation"] = _operation;
        if (IsFilterOperation)
        {
            ApplyFilterParameters(parameters);
        }
        else if (IsMorphologyOperation)
        {
            ApplyMorphologyParameters(parameters);
        }
        else
        {
            ApplyThresholdParameters(parameters);
        }

        parameters["enabledOutputs"] = FormatEnabledOutputKeys();

        if (!includeInputBindings)
        {
            return parameters;
        }

        foreach (var input in InputBindings)
        {
            var selected = input.SelectedOption;
            if (selected is null || selected.IsEmpty)
            {
                continue;
            }

            parameters[GetConnectionToolParameterKey(input.TargetPortKey)] = selected.ToolId;
            parameters[GetConnectionPortParameterKey(input.TargetPortKey)] = selected.PortKey;
        }

        return parameters;
    }

    private void ApplyThresholdParameters(IDictionary<string, string> parameters)
    {
        var min = ClampByte(GrayMin);
        var max = ClampByte(GrayMax);
        if (max < min)
        {
            (min, max) = (max, min);
        }

        parameters["thresholdMode"] = ThresholdMode;
        parameters["polarity"] = Polarity;
        parameters["threshold"] = ToInvariant(ClampByte(Threshold));
        parameters["grayMin"] = ToInvariant(min);
        parameters["grayMax"] = ToInvariant(max);
        parameters["adaptiveBlockSize"] = NormalizeOdd(AdaptiveBlockSize, 3, 501).ToString(CultureInfo.InvariantCulture);
        parameters["adaptiveC"] = ToInvariant(AdaptiveC);
    }

    private void ApplyFilterParameters(IDictionary<string, string> parameters)
    {
        parameters["filterType"] = FilterType;
        parameters["kernelSize"] = NormalizeOdd(KernelSize, 1, 99).ToString(CultureInfo.InvariantCulture);
        parameters["sigmaColor"] = ToInvariant(SigmaColor);
        parameters["sigmaSpace"] = ToInvariant(SigmaSpace);
    }

    private void ApplyMorphologyParameters(IDictionary<string, string> parameters)
    {
        parameters["morphType"] = MorphType;
        parameters["kernelShape"] = KernelShape;
        parameters["kernelSize"] = NormalizeOdd(KernelSize, 1, 99).ToString(CultureInfo.InvariantCulture);
        parameters["iterations"] = Math.Clamp(Iterations, 1, 32).ToString(CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<ToolInputBindingItem> CreateInputBindings(
        IReadOnlyDictionary<string, (string ToolId, string PortKey)> existingConnections)
    {
        return VisionToolCatalog.GetInputPorts(VisionToolKind.ImageProcess)
            .Select(port =>
            {
                var options = CreateSourceOptions(port.DataType).ToArray();
                existingConnections.TryGetValue(port.Key, out var existing);
                var selected = options.FirstOrDefault(option =>
                    string.Equals(option.ToolId, existing.ToolId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(option.PortKey, existing.PortKey, StringComparison.OrdinalIgnoreCase));
                return new ToolInputBindingItem(port.Key, port.Name, port.DataType, options, selected);
            })
            .ToArray();
    }

    private IEnumerable<ToolInputSourceOptionItem> CreateSourceOptions(string dataType)
    {
        yield return new ToolInputSourceOptionItem(string.Empty, string.Empty, "不绑定", dataType);

        var activeFlow = _previewRecipe?.GetActiveFlow();
        if (activeFlow is null)
        {
            yield break;
        }

        foreach (var tool in activeFlow.Tools)
        {
            if (string.Equals(tool.Id, _toolId, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            foreach (var output in VisionToolCatalog.GetOutputPorts(tool.Kind))
            {
                if (!string.Equals(output.DataType, dataType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new ToolInputSourceOptionItem(
                    tool.Id,
                    output.Key,
                    $"{tool.Name}.{output.Name}",
                    output.DataType);
            }
        }
    }

    private Dictionary<string, (string ToolId, string PortKey)> GetExistingInputConnections()
    {
        var connections = new Dictionary<string, (string ToolId, string PortKey)>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in _parameters)
        {
            if (!parameter.Key.StartsWith("input:", StringComparison.OrdinalIgnoreCase) ||
                !parameter.Key.EndsWith(":toolId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetPortKey = parameter.Key["input:".Length..^":toolId".Length];
            connections[targetPortKey] = (
                parameter.Value,
                _parameters.GetValueOrDefault(GetConnectionPortParameterKey(targetPortKey), string.Empty));
        }

        return connections;
    }

    private static IEnumerable<ToolOutputOptionItem> CreateOutputOptions(string? enabledOutputText)
    {
        var definitions = VisionToolCatalog.GetOutputPorts(VisionToolKind.ImageProcess);
        var defaults = VisionToolCatalog.GetDefaultOutputKeys(VisionToolKind.ImageProcess).ToArray();
        var enabled = ParseEnabledOutputKeys(enabledOutputText, defaults);
        return definitions.Select(definition => new ToolOutputOptionItem(
            definition.Key,
            definition.Name,
            definition.DataType,
            enabled.Contains(definition.Key)));
    }

    private string FormatEnabledOutputKeys()
    {
        if (!HasOutputOptions)
        {
            return string.Empty;
        }

        var keys = OutputOptions
            .Where(option => option.IsEnabled)
            .Select(option => option.Key)
            .ToArray();
        return keys.Length == 0 ? "ResultOutput" : string.Join(",", keys);
    }

    private static HashSet<string> ParseEnabledOutputKeys(string? text, IReadOnlyList<string> defaults)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaults.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var keys = text
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keys.Count == 0 ? defaults.ToHashSet(StringComparer.OrdinalIgnoreCase) : keys;
    }

    private static void AddMissingDefaults(IDictionary<string, string> parameters)
    {
        foreach (var pair in VisionToolDefaults.CreateImageProcessParameters())
        {
            if (!parameters.ContainsKey(pair.Key))
            {
                parameters[pair.Key] = pair.Value;
            }
        }
    }

    private static void RemoveInputConnections(IDictionary<string, string> parameters)
    {
        foreach (var key in parameters.Keys.Where(IsInputConnectionParameter).ToArray())
        {
            parameters.Remove(key);
        }
    }

    private static bool IsInputConnectionParameter(string key)
    {
        return key.Equals("inputImageToolId", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("inputImageSourceToolId", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("resultInputToolId", StringComparison.OrdinalIgnoreCase) ||
               (key.StartsWith("input:", StringComparison.OrdinalIgnoreCase) &&
                (key.EndsWith(":toolId", StringComparison.OrdinalIgnoreCase) ||
                 key.EndsWith(":portKey", StringComparison.OrdinalIgnoreCase)));
    }

    private static string GetConnectionToolParameterKey(string targetPortKey)
    {
        return $"input:{targetPortKey}:toolId";
    }

    private static string GetConnectionPortParameterKey(string targetPortKey)
    {
        return $"input:{targetPortKey}:portKey";
    }

    private static Dictionary<string, string> ParseParameters(string text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in text.Split(["\r\n", "\n", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            parameters[segment[..index].Trim()] = index == segment.Length - 1
                ? string.Empty
                : segment[(index + 1)..].Trim();
        }

        return parameters;
    }

    private static string FormatParameters(IReadOnlyDictionary<string, string> parameters)
    {
        return string.Join("; ", parameters.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string GetString(IReadOnlyDictionary<string, string> parameters, string key, string fallback)
    {
        return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> parameters, string key, double fallback)
    {
        if (!parameters.TryGetValue(key, out var text))
        {
            return fallback;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariantValue)
            ? invariantValue
            : double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var currentValue)
                ? currentValue
                : fallback;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> parameters, string key, int fallback)
    {
        return (int)Math.Round(GetDouble(parameters, key, fallback));
    }

    private static string NormalizeThresholdMode(string? value)
    {
        return value?.Trim() switch
        {
            "Range" or "GrayRange" => "Range",
            "Adaptive" => "Adaptive",
            "Triangle" => "Triangle",
            "Otsu" => "Otsu",
            _ => "Fixed"
        };
    }

    private static string NormalizeOperation(string? value)
    {
        return value?.Trim() switch
        {
            "Filter" or "Blur" or "Denoise" => "Filter",
            "Morph" or "Morphology" => "Morphology",
            _ => "Threshold"
        };
    }

    private static string NormalizeFilterType(string? value)
    {
        return value?.Trim() switch
        {
            "Mean" or "Average" or "Box" => "Mean",
            "Median" => "Median",
            "Bilateral" => "Bilateral",
            "Sharpen" or "HighPass" => "Sharpen",
            _ => "Gaussian"
        };
    }

    private static string NormalizeMorphType(string? value)
    {
        return value?.Trim() switch
        {
            "Erode" => "Erode",
            "Dilate" => "Dilate",
            "Close" => "Close",
            "Gradient" => "Gradient",
            "TopHat" or "WhiteTopHat" => "TopHat",
            "BlackHat" => "BlackHat",
            _ => "Open"
        };
    }

    private static string NormalizeKernelShape(string? value)
    {
        return value?.Trim() switch
        {
            "Ellipse" or "Circle" => "Ellipse",
            "Cross" => "Cross",
            _ => "Rect"
        };
    }

    private static string NormalizePolarity(string? value)
    {
        return value?.Trim() switch
        {
            "Bright" or "Light" => "Bright",
            _ => "Dark"
        };
    }

    private static double ClampByte(double value)
    {
        return Math.Clamp(double.IsNaN(value) || double.IsInfinity(value) ? 0 : value, 0, 255);
    }

    private static int NormalizeOdd(int value, int min, int max)
    {
        var kernel = Math.Clamp(value, min, max);
        return kernel % 2 == 0 ? Math.Min(max, kernel + 1) : kernel;
    }

    private static string ToInvariant(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

public sealed record ImageProcessOptionItem(string Key, string Name)
{
    public override string ToString()
    {
        return Name;
    }
}
