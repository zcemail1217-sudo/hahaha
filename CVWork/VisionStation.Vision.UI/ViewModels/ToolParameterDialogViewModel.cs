using System.Collections.ObjectModel;
using System.ComponentModel;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.ViewModels;

public sealed class ToolParameterDialogViewModel : BindableBase
{
    private static readonly string[] CommunicationParameterKeys =
    [
        "channelKey",
        "payload",
        "payloadMode",
        "waitResponse",
        "timeoutMs",
        "expectedContains"
    ];

    private string _name;
    private VisionToolKind _kind;
    private bool _enabled;
    private string _roiId;
    private RoiShapeOptionItem _selectedRoiShape;
    private ToolParameterItem? _selectedParameter;
    private readonly ImageFrame? _currentFrame;
    private readonly string _toolId;
    private readonly Recipe? _previewRecipe;
    private readonly CommunicationChannelSettings? _communicationSettings;
    private string _communicationChannelKey = string.Empty;
    private string _communicationPayload = string.Empty;
    private string _communicationPayloadMode = "Text";
    private string _communicationTimeoutMs = "1000";
    private string _communicationExpectedContains = string.Empty;
    private bool _communicationWaitResponse = true;

    public ToolParameterDialogViewModel(
        VisionToolItem tool,
        IReadOnlyList<RoiChoiceItem> roiChoices,
        IReadOnlyList<VisionToolKind> toolKinds,
        ImageFrame? currentFrame = null,
        Recipe? previewRecipe = null,
        CommunicationChannelSettings? communicationSettings = null)
    {
        _toolId = tool.Id;
        _previewRecipe = previewRecipe;
        _communicationSettings = communicationSettings;
        _name = tool.Name;
        _kind = tool.Kind;
        _enabled = tool.Enabled;
        _roiId = tool.RoiId;
        _currentFrame = currentFrame;
        _selectedRoiShape = ToolRoiFactory.ShapeOptions[0];

        RoiChoices = new ObservableCollection<RoiChoiceItem>(roiChoices);
        RoiShapeOptions = new ObservableCollection<RoiShapeOptionItem>(ToolRoiFactory.ShapeOptions);
        CreatedRois = new ObservableCollection<RoiDefinition>();
        ToolKinds = toolKinds;
        Parameters = new ObservableCollection<ToolParameterItem>(ParseParameters(tool.ParametersText));
        ResultInputDataTypeOptions = new ObservableCollection<ToolParameterChoiceItem>(CreateResultInputDataTypeOptions());
        CommunicationChannelOptions = new ObservableCollection<CommunicationChannelChoiceItem>();
        CommunicationPayloadModeOptions = new ObservableCollection<ToolParameterChoiceItem>(
        [
            new("Text", "文本"),
            new("Hex", "HEX")
        ]);
        RemoveMeasureDistanceLegacyParameters();
        AddMissingTemplateParameters(_kind);
        RefreshCommunicationParameters();
        InputBindings = new ObservableCollection<ToolInputBindingItem>();
        RefreshInputBindings();
        OutputOptions = new ObservableCollection<ToolOutputOptionItem>();
        RefreshOutputOptions();

        AddParameterCommand = new DelegateCommand(AddParameter);
        AddResultInputCommand = new DelegateCommand(AddResultInput, () => ShowResultInputEditor);
        CreateRoiCommand = new DelegateCommand(CreateRoi, () => RequiresRoi);
        DeleteResultInputCommand = new DelegateCommand<ToolInputBindingItem>(DeleteResultInput);
        DeleteParameterCommand = new DelegateCommand(DeleteSelectedParameter, () => SelectedParameter is not null)
            .ObservesProperty(() => SelectedParameter);
        ConfirmCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, true));
        CancelCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<RoiChoiceItem> RoiChoices { get; }

    public ObservableCollection<RoiShapeOptionItem> RoiShapeOptions { get; }

    public ObservableCollection<RoiDefinition> CreatedRois { get; }

    public IReadOnlyList<VisionToolKind> ToolKinds { get; }

    public ObservableCollection<ToolParameterItem> Parameters { get; }

    public ObservableCollection<ToolInputBindingItem> InputBindings { get; }

    public bool HasInputBindings => InputBindings.Count > 0;

    public bool ShowInputBindingsPanel => HasInputBindings || ShowResultInputEditor;

    public bool ShowResultInputEditor => Kind == VisionToolKind.Result;

    public ObservableCollection<ToolOutputOptionItem> OutputOptions { get; }

    public bool HasOutputOptions => OutputOptions.Count > 0;

    public ObservableCollection<ToolParameterChoiceItem> ResultInputDataTypeOptions { get; }

    public ObservableCollection<CommunicationChannelChoiceItem> CommunicationChannelOptions { get; }

    public ObservableCollection<ToolParameterChoiceItem> CommunicationPayloadModeOptions { get; }

    public bool ShowCommunicationParameters => IsCommunicationTool(Kind);

    public string CommunicationPanelTitle => Kind == VisionToolKind.SerialCommunication ? "串口通讯" : "TCP通讯";

    public string CommunicationChannelLabel => Kind == VisionToolKind.SerialCommunication ? "串口通道" : "TCP通道";

    public string CommunicationChannelKey
    {
        get => _communicationChannelKey;
        set
        {
            if (SetProperty(ref _communicationChannelKey, value?.Trim() ?? string.Empty))
            {
                RaisePropertyChanged(nameof(SelectedCommunicationChannelDescription));
            }
        }
    }

    public string SelectedCommunicationChannelDescription
    {
        get
        {
            var selected = CommunicationChannelOptions.FirstOrDefault(option =>
                string.Equals(option.Key, CommunicationChannelKey, StringComparison.OrdinalIgnoreCase));
            return selected?.Description ?? "先在系统设置的通讯通道中维护通道，再回到配方工具选择。";
        }
    }

    public string CommunicationPayload
    {
        get => _communicationPayload;
        set => SetProperty(ref _communicationPayload, value ?? string.Empty);
    }

    public string CommunicationPayloadMode
    {
        get => _communicationPayloadMode;
        set => SetProperty(ref _communicationPayloadMode, NormalizePayloadMode(value));
    }

    public bool CommunicationWaitResponse
    {
        get => _communicationWaitResponse;
        set => SetProperty(ref _communicationWaitResponse, value);
    }

    public string CommunicationTimeoutMs
    {
        get => _communicationTimeoutMs;
        set => SetProperty(ref _communicationTimeoutMs, string.IsNullOrWhiteSpace(value) ? "1000" : value.Trim());
    }

    public string CommunicationExpectedContains
    {
        get => _communicationExpectedContains;
        set => SetProperty(ref _communicationExpectedContains, value ?? string.Empty);
    }

    public DelegateCommand AddParameterCommand { get; }

    public DelegateCommand AddResultInputCommand { get; }

    public DelegateCommand CreateRoiCommand { get; }

    public DelegateCommand<ToolInputBindingItem> DeleteResultInputCommand { get; }

    public DelegateCommand DeleteParameterCommand { get; }

    public DelegateCommand ConfirmCommand { get; }

    public DelegateCommand CancelCommand { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public VisionToolKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                if (!RequiresRoi)
                {
                    RoiId = string.Empty;
                }

                RemoveMeasureDistanceLegacyParameters();
                AddMissingTemplateParameters(value);
                RefreshCommunicationParameters();
                RefreshInputBindings();
                RefreshOutputOptions();
                RaisePropertyChanged(nameof(RequiresRoi));
                RaisePropertyChanged(nameof(ShowToolKindSelector));
                RaisePropertyChanged(nameof(HideToolKindSelector));
                RaisePropertyChanged(nameof(HideRoiSelector));
                RaisePropertyChanged(nameof(ShowGenericToolHeader));
                RaisePropertyChanged(nameof(ShowResultInputEditor));
                RaisePropertyChanged(nameof(ShowInputBindingsPanel));
                RaisePropertyChanged(nameof(ShowCommunicationParameters));
                RaisePropertyChanged(nameof(CommunicationPanelTitle));
                RaisePropertyChanged(nameof(CommunicationChannelLabel));
                RaisePropertyChanged(nameof(ShowParameterTable));
                CreateRoiCommand.RaiseCanExecuteChanged();
                AddResultInputCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool RequiresRoi => ToolRoiFactory.RequiresRoi(Kind);

    public bool ShowToolKindSelector => Kind is not (VisionToolKind.MeasureDistance or VisionToolKind.Result);

    public bool HideToolKindSelector => !ShowToolKindSelector;

    public bool HideRoiSelector => !RequiresRoi;

    public bool ShowGenericToolHeader => Kind != VisionToolKind.Result;

    public bool ShowParameterTable => Kind is not (
        VisionToolKind.MeasureDistance or
        VisionToolKind.LineAngle or
        VisionToolKind.LineIntersection or
        VisionToolKind.FitLineFromPoints or
        VisionToolKind.Result);

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

    public RoiShapeOptionItem SelectedRoiShape
    {
        get => _selectedRoiShape;
        set => SetProperty(ref _selectedRoiShape, value);
    }

    public ToolParameterItem? SelectedParameter
    {
        get => _selectedParameter;
        set => SetProperty(ref _selectedParameter, value);
    }

    public void ApplyTo(VisionToolItem tool)
    {
        var originalParameters = ParseParameterDictionary(tool.ParametersText);
        tool.Name = string.IsNullOrWhiteSpace(Name) ? tool.Name : Name.Trim();
        tool.Kind = Kind;
        tool.Enabled = Enabled;
        tool.RoiId = RequiresRoi ? RoiId ?? string.Empty : string.Empty;
        var parameterRows = Parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Key));
        if (IsCommunicationTool(Kind))
        {
            parameterRows = parameterRows.Where(parameter => !IsCommunicationParameterKey(parameter.Key));
        }

        var parameters = Kind switch
        {
            VisionToolKind.MeasureDistance => new List<ToolParameterItem> { new("measurementMode", GetMeasurementMode()) },
            VisionToolKind.Result => parameterRows
                .Where(parameter => !IsInputConfigurationParameter(parameter))
                .Select(parameter => new ToolParameterItem(parameter.Key.Trim(), parameter.Value.Trim()))
                .Concat(CreateResultInputParameterItems())
                .ToList(),
            _ => parameterRows
                .Select(parameter => new ToolParameterItem(parameter.Key.Trim(), parameter.Value.Trim()))
                .ToList()
        };
        if (IsCommunicationTool(Kind))
        {
            parameters.InsertRange(0, CreateCommunicationParameterItems());
        }

        if (Kind == VisionToolKind.Result)
        {
            RestoreResultToolLayoutParameters(parameters, originalParameters);
        }

        var enabledOutputs = FormatEnabledOutputKeys();
        if (!string.IsNullOrWhiteSpace(enabledOutputs))
        {
            parameters.Add(new ToolParameterItem("enabledOutputs", enabledOutputs));
        }
        foreach (var input in InputBindings.Where(_ => Kind != VisionToolKind.Result))
        {
            var selected = input.SelectedOption;
            if (selected is null || selected.IsEmpty)
            {
                continue;
            }

            parameters.Add(new ToolParameterItem(GetConnectionToolParameterKey(input.TargetPortKey), selected.ToolId));
            parameters.Add(new ToolParameterItem(GetConnectionPortParameterKey(input.TargetPortKey), selected.PortKey));
        }

        tool.ParametersText = string.Join("; ", parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
    }

    private static void RestoreResultToolLayoutParameters(
        ICollection<ToolParameterItem> parameters,
        IReadOnlyDictionary<string, string> originalParameters)
    {
        RestoreParameter(parameters, originalParameters, "canvasX");
        RestoreParameter(parameters, originalParameters, "canvasY");
    }

    private static void RestoreParameter(
        ICollection<ToolParameterItem> parameters,
        IReadOnlyDictionary<string, string> originalParameters,
        string key)
    {
        if (parameters.Any(parameter => string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase)) ||
            !originalParameters.TryGetValue(key, out var value))
        {
            return;
        }

        parameters.Add(new ToolParameterItem(key, value));
    }

    private void RefreshCommunicationParameters()
    {
        if (!IsCommunicationTool(Kind))
        {
            CommunicationChannelOptions.Clear();
            return;
        }

        var defaults = VisionToolCatalog.GetDefaultParameters(Kind);
        var defaultChannelKey = defaults.GetValueOrDefault("channelKey") ??
                                (Kind == VisionToolKind.SerialCommunication ? "serial-main" : "tcp-main");
        var currentChannelKey = GetParameterValue("channelKey", defaultChannelKey);
        var payload = GetParameterValue("payload", defaults.GetValueOrDefault("payload") ?? string.Empty);
        var payloadMode = GetParameterValue("payloadMode", defaults.GetValueOrDefault("payloadMode") ?? "Text");
        var waitResponse = GetBoolParameter("waitResponse", true);
        var timeoutMs = GetParameterValue("timeoutMs", defaults.GetValueOrDefault("timeoutMs") ?? "1000");
        var expectedContains = GetParameterValue("expectedContains", defaults.GetValueOrDefault("expectedContains") ?? string.Empty);

        RemoveCommunicationParameterRows();
        CommunicationChannelOptions.Clear();
        foreach (var option in CreateCommunicationChannelOptions(Kind))
        {
            CommunicationChannelOptions.Add(option);
        }

        if (!CommunicationChannelOptions.Any(option => string.Equals(option.Key, currentChannelKey, StringComparison.OrdinalIgnoreCase)))
        {
            CommunicationChannelOptions.Insert(
                0,
                new CommunicationChannelChoiceItem(
                    currentChannelKey,
                    "当前参数",
                    false,
                    "当前工具保存的通道 Key 在系统设置中未找到，保存前建议切换到已配置通道。"));
        }

        if (CommunicationChannelOptions.Count == 0)
        {
            CommunicationChannelOptions.Add(new CommunicationChannelChoiceItem(
                defaultChannelKey,
                "默认通道",
                false,
                "系统设置里还没有可用通道，保存后请先到系统设置维护通讯通道。"));
        }

        CommunicationChannelKey = string.IsNullOrWhiteSpace(currentChannelKey)
            ? CommunicationChannelOptions.First().Key
            : currentChannelKey;
        CommunicationPayload = payload;
        CommunicationPayloadMode = payloadMode;
        CommunicationWaitResponse = waitResponse;
        CommunicationTimeoutMs = timeoutMs;
        CommunicationExpectedContains = expectedContains;
        RaisePropertyChanged(nameof(SelectedCommunicationChannelDescription));
    }

    private IEnumerable<CommunicationChannelChoiceItem> CreateCommunicationChannelOptions(VisionToolKind kind)
    {
        if (kind == VisionToolKind.TcpCommunication)
        {
            return (_communicationSettings?.TcpChannels ?? Array.Empty<TcpCommunicationChannelSettings>())
                .Select(channel => new CommunicationChannelChoiceItem(
                    channel.Key,
                    channel.Name,
                    channel.Enabled,
                    $"{channel.Mode}  {channel.Host}:{channel.Port}  拆帧:{channel.FrameMode}  超时:{channel.ReceiveTimeoutMs}ms"));
        }

        if (kind == VisionToolKind.SerialCommunication)
        {
            return (_communicationSettings?.SerialChannels ?? Array.Empty<SerialCommunicationChannelSettings>())
                .Select(channel => new CommunicationChannelChoiceItem(
                    channel.Key,
                    channel.Name,
                    channel.Enabled,
                    $"{channel.PortName}  {channel.BaudRate},{channel.DataBits},{channel.Parity},{channel.StopBits}  拆帧:{channel.FrameMode}  超时:{channel.ReceiveTimeoutMs}ms"));
        }

        return [];
    }

    private List<ToolParameterItem> CreateCommunicationParameterItems()
    {
        var defaultChannelKey = Kind == VisionToolKind.SerialCommunication ? "serial-main" : "tcp-main";
        var channelKey = string.IsNullOrWhiteSpace(CommunicationChannelKey)
            ? defaultChannelKey
            : CommunicationChannelKey.Trim();

        return
        [
            new("channelKey", channelKey),
            new("payload", CommunicationPayload ?? string.Empty),
            new("payloadMode", NormalizePayloadMode(CommunicationPayloadMode)),
            new("waitResponse", CommunicationWaitResponse.ToString()),
            new("timeoutMs", string.IsNullOrWhiteSpace(CommunicationTimeoutMs) ? "1000" : CommunicationTimeoutMs.Trim()),
            new("expectedContains", CommunicationExpectedContains ?? string.Empty)
        ];
    }

    private List<ToolParameterItem> CreateResultInputParameterItems()
    {
        var parameters = new List<ToolParameterItem>
        {
            new("resultInputCount", InputBindings.Count.ToString())
        };

        for (var index = 0; index < InputBindings.Count; index++)
        {
            var input = InputBindings[index];
            var targetPortKey = $"ResultInput{index + 1}";
            parameters.Add(new ToolParameterItem(GetResultInputDataTypeParameterKey(targetPortKey), NormalizeResultInputDataType(input.DataType)));

            var selected = input.SelectedOption;
            if (selected is null || selected.IsEmpty)
            {
                continue;
            }

            parameters.Add(new ToolParameterItem(GetConnectionToolParameterKey(targetPortKey), selected.ToolId));
            parameters.Add(new ToolParameterItem(GetConnectionPortParameterKey(targetPortKey), selected.PortKey));
        }

        return parameters;
    }

    private static IReadOnlyList<ToolParameterChoiceItem> CreateResultInputDataTypeOptions()
    {
        var knownTypes = Enum.GetValues<VisionToolKind>()
            .SelectMany(kind => VisionToolCatalog.GetOutputPorts(kind))
            .Select(port => VisionResultDataTypeMapper.ToVariableDataType(port.DataType))
            .Concat(VisionResultDataTypeMapper.VariableDataTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return VisionResultDataTypeMapper.VariableDataTypes
            .Where(knownTypes.Contains)
            .Concat(knownTypes.OrderBy(type => type, StringComparer.OrdinalIgnoreCase).Except(VisionResultDataTypeMapper.VariableDataTypes, StringComparer.OrdinalIgnoreCase))
            .Select(type => new ToolParameterChoiceItem(type, type))
            .ToArray();
    }

    private string NormalizeResultInputDataType(string? dataType)
    {
        var text = VisionResultDataTypeMapper.ToVariableDataType(dataType);
        return ResultInputDataTypeOptions.Any(option => string.Equals(option.Value, text, StringComparison.OrdinalIgnoreCase))
            ? ResultInputDataTypeOptions.First(option => string.Equals(option.Value, text, StringComparison.OrdinalIgnoreCase)).Value
            : "string";
    }

    private string GetParameterValue(string key, string defaultValue)
    {
        return Parameters.FirstOrDefault(parameter => string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? defaultValue;
    }

    private bool GetBoolParameter(string key, bool defaultValue)
    {
        var text = GetParameterValue(key, defaultValue.ToString());
        return bool.TryParse(text, out var value) ? value : defaultValue;
    }

    private void RemoveCommunicationParameterRows()
    {
        foreach (var parameter in Parameters.Where(parameter => IsCommunicationParameterKey(parameter.Key)).ToArray())
        {
            Parameters.Remove(parameter);
        }
    }

    private static bool IsCommunicationTool(VisionToolKind kind)
    {
        return kind is VisionToolKind.TcpCommunication or VisionToolKind.SerialCommunication;
    }

    private static bool IsCommunicationParameterKey(string key)
    {
        return CommunicationParameterKeys.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePayloadMode(string? value)
    {
        return string.Equals(value?.Trim(), "Hex", StringComparison.OrdinalIgnoreCase) ? "Hex" : "Text";
    }

    private void AddParameter()
    {
        var item = new ToolParameterItem("param", string.Empty);
        Parameters.Add(item);
        SelectedParameter = item;
    }

    private void CreateRoi()
    {
        if (!RequiresRoi)
        {
            return;
        }

        var roi = ToolRoiFactory.CreateDefaultRoi(Name, Kind, SelectedRoiShape.Kind, _currentFrame, RoiChoices.Count);
        CreatedRois.Add(roi);
        RoiChoices.Add(new RoiChoiceItem(roi.Id, roi.Name));
        RoiId = roi.Id;
    }

    private void DeleteSelectedParameter()
    {
        if (SelectedParameter is null)
        {
            return;
        }

        var item = SelectedParameter;
        Parameters.Remove(item);
        SelectedParameter = Parameters.FirstOrDefault();
    }

    private void AddResultInput()
    {
        if (!ShowResultInputEditor)
        {
            return;
        }

        var index = InputBindings.Count + 1;
        var input = CreateResultInputBinding(index, "Result", selectedOption: null);
        input.PropertyChanged += OnInputBindingPropertyChanged;
        InputBindings.Add(input);
        RaisePropertyChanged(nameof(HasInputBindings));
        RaisePropertyChanged(nameof(ShowInputBindingsPanel));
    }

    private void DeleteResultInput(ToolInputBindingItem? input)
    {
        if (input is null || !ShowResultInputEditor)
        {
            return;
        }

        input.PropertyChanged -= OnInputBindingPropertyChanged;
        InputBindings.Remove(input);
        RenumberResultInputBindings();
        RaisePropertyChanged(nameof(HasInputBindings));
        RaisePropertyChanged(nameof(ShowInputBindingsPanel));
    }

    private ToolInputBindingItem CreateResultInputBinding(int index, string dataType, ToolInputSourceOptionItem? selectedOption)
    {
        var normalizedType = NormalizeResultInputDataType(dataType);
        var options = CreateSourceOptions(normalizedType).ToArray();
        var selected = selectedOption is not null
            ? options.FirstOrDefault(option =>
                string.Equals(option.ToolId, selectedOption.ToolId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(option.PortKey, selectedOption.PortKey, StringComparison.OrdinalIgnoreCase))
            : null;

        return new ToolInputBindingItem(
            $"ResultInput{index}",
            $"结果{index}",
            normalizedType,
            options,
            selected,
            isTypeEditable: true,
            canDelete: true,
            dataTypeOptions: ResultInputDataTypeOptions);
    }

    private void RenumberResultInputBindings()
    {
        for (var index = 0; index < InputBindings.Count; index++)
        {
            InputBindings[index].RenameTarget($"ResultInput{index + 1}", $"结果{index + 1}");
        }
    }

    private void OnInputBindingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ShowResultInputEditor ||
            sender is not ToolInputBindingItem input ||
            !string.Equals(e.PropertyName, nameof(ToolInputBindingItem.DataType), StringComparison.Ordinal))
        {
            return;
        }

        RefreshResultInputSourceOptions(input);
    }

    private void RefreshResultInputSourceOptions(ToolInputBindingItem input)
    {
        var normalizedType = NormalizeResultInputDataType(input.DataType);
        if (!string.Equals(input.DataType, normalizedType, StringComparison.OrdinalIgnoreCase))
        {
            input.DataType = normalizedType;
            return;
        }

        var current = input.SelectedOption;
        var options = CreateSourceOptions(normalizedType).ToArray();
        var selected = current is not null
            ? options.FirstOrDefault(option =>
                string.Equals(option.ToolId, current.ToolId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(option.PortKey, current.PortKey, StringComparison.OrdinalIgnoreCase))
            : null;
        input.ReplaceOptions(options, selected);
    }

    private void AddMissingTemplateParameters(VisionToolKind kind)
    {
        foreach (var parameter in GetTemplateParameters(kind))
        {
            if (Parameters.Any(item => string.Equals(item.Key, parameter.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Parameters.Add(new ToolParameterItem(parameter.Key, parameter.Value));
        }
    }

    private void RemoveMeasureDistanceLegacyParameters()
    {
        if (Kind != VisionToolKind.MeasureDistance)
        {
            return;
        }

        foreach (var parameter in Parameters.Where(IsMeasureDistanceLegacyParameter).ToArray())
        {
            Parameters.Remove(parameter);
        }
    }

    private void RefreshInputBindings()
    {
        var existingConnections = GetExistingInputConnections();
        var parameterMap = CreateParameterMap(existingConnections);
        foreach (var parameter in Parameters.Where(IsInputConfigurationParameter).ToArray())
        {
            Parameters.Remove(parameter);
        }

        foreach (var binding in InputBindings)
        {
            binding.PropertyChanged -= OnInputBindingPropertyChanged;
        }

        InputBindings.Clear();
        foreach (var binding in CreateInputBindings(Kind, GetMeasurementMode(), existingConnections, parameterMap))
        {
            binding.PropertyChanged += OnInputBindingPropertyChanged;
            InputBindings.Add(binding);
        }

        RaisePropertyChanged(nameof(HasInputBindings));
        RaisePropertyChanged(nameof(ShowInputBindingsPanel));
    }

    private void RefreshOutputOptions()
    {
        var enabledOutputText = Parameters
            .FirstOrDefault(parameter => string.Equals(parameter.Key, "enabledOutputs", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var outputParameterRows = Parameters
            .Where(parameter => string.Equals(parameter.Key, "enabledOutputs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var parameter in outputParameterRows)
        {
            Parameters.Remove(parameter);
        }

        OutputOptions.Clear();
        foreach (var option in CreateOutputOptions(Kind, enabledOutputText, GetMeasurementMode()))
        {
            OutputOptions.Add(option);
        }

        RaisePropertyChanged(nameof(HasOutputOptions));
    }

    private Dictionary<string, (string ToolId, string PortKey)> GetExistingInputConnections()
    {
        var connections = new Dictionary<string, (string ToolId, string PortKey)>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in Parameters)
        {
            if (!parameter.Key.StartsWith("input:", StringComparison.OrdinalIgnoreCase) ||
                !parameter.Key.EndsWith(":toolId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetPortKey = parameter.Key["input:".Length..^":toolId".Length];
            var sourcePortKey = Parameters
                .FirstOrDefault(candidate => string.Equals(candidate.Key, GetConnectionPortParameterKey(targetPortKey), StringComparison.OrdinalIgnoreCase))
                ?.Value ?? string.Empty;
            connections[targetPortKey] = (parameter.Value, sourcePortKey);
        }

        return connections;
    }

    private IReadOnlyList<ToolInputBindingItem> CreateInputBindings(
        VisionToolKind kind,
        string measurementMode,
        IReadOnlyDictionary<string, (string ToolId, string PortKey)> existingConnections,
        IReadOnlyDictionary<string, string> parameterMap)
    {
        var ports = kind == VisionToolKind.Result
            ? VisionToolCatalog.GetResultInputPorts(parameterMap)
            : VisionToolCatalog.GetInputPorts(kind, measurementMode);
        var definitions = ports
            .Select(port => new ToolInputDefinition(port.Key, port.Name, port.DataType))
            .ToArray();

        return definitions
            .Select(definition =>
            {
                var options = CreateSourceOptions(definition.DataType).ToArray();
                existingConnections.TryGetValue(definition.Key, out var existing);
                var selected = options.FirstOrDefault(option =>
                    string.Equals(option.ToolId, existing.ToolId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(option.PortKey, existing.PortKey, StringComparison.OrdinalIgnoreCase));
                return new ToolInputBindingItem(
                    definition.Key,
                    definition.Name,
                    definition.DataType,
                    options,
                    selected,
                    isTypeEditable: kind == VisionToolKind.Result,
                    canDelete: kind == VisionToolKind.Result,
                    dataTypeOptions: kind == VisionToolKind.Result ? ResultInputDataTypeOptions : null);
            })
            .ToArray();
    }

    private Dictionary<string, string> CreateParameterMap(IReadOnlyDictionary<string, (string ToolId, string PortKey)> existingConnections)
    {
        var parameters = Parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Key))
            .GroupBy(parameter => parameter.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var connection in existingConnections)
        {
            parameters[GetConnectionToolParameterKey(connection.Key)] = connection.Value.ToolId;
            parameters[GetConnectionPortParameterKey(connection.Key)] = connection.Value.PortKey;
        }

        return parameters;
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

            foreach (var output in GetAllOutputDefinitions(tool.Kind))
            {
                var isCompatible = Kind == VisionToolKind.Result
                    ? VisionResultDataTypeMapper.AreCompatible(output.DataType, dataType)
                    : string.Equals(output.DataType, dataType, StringComparison.OrdinalIgnoreCase);
                if (!isCompatible)
                {
                    continue;
                }

                yield return new ToolInputSourceOptionItem(
                    tool.Id,
                    output.Key,
                    $"{tool.Name}.{output.Name}",
                    Kind == VisionToolKind.Result
                        ? VisionResultDataTypeMapper.ToVariableDataType(output.DataType)
                        : output.DataType);
            }
        }
    }

    private static IReadOnlyList<ToolOutputDefinition> GetAvailableOutputDefinitions(VisionToolDefinition tool)
    {
        var definitions = GetAllOutputDefinitions(tool.Kind);
        var enabled = GetEnabledOutputKeys(tool, definitions);
        return definitions.Where(definition => enabled.Contains(definition.Key)).ToArray();
    }

    private static IReadOnlyList<ToolOutputDefinition> GetAllOutputDefinitions(VisionToolKind kind)
    {
        return VisionToolCatalog.GetOutputPorts(kind)
            .Select(port => new ToolOutputDefinition(port.Key, port.Name, port.DataType))
            .ToArray();
    }

    private static HashSet<string> GetEnabledOutputKeys(VisionToolDefinition tool, IReadOnlyList<ToolOutputDefinition> definitions)
    {
        var validKeys = definitions.Select(definition => definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        tool.Parameters.TryGetValue("measurementMode", out var measurementMode);
        var defaults = VisionToolCatalog.GetDefaultOutputKeys(tool.Kind, measurementMode);
        AddRequiredGeometryOutputKeys(tool.Kind, defaults);

        if (!tool.Parameters.TryGetValue("enabledOutputs", out var text) || string.IsNullOrWhiteSpace(text))
        {
            return defaults;
        }

        var keys = text
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(validKeys.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddRequiredGeometryOutputKeys(tool.Kind, keys);

        return keys.Count == 0 ? defaults : keys;
    }

    private static void AddRequiredGeometryOutputKeys(VisionToolKind kind, ISet<string> keys)
    {
        switch (kind)
        {
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

    private static bool IsInputConnectionParameter(ToolParameterItem parameter)
    {
        return parameter.Key.StartsWith("input:", StringComparison.OrdinalIgnoreCase) &&
               (parameter.Key.EndsWith(":toolId", StringComparison.OrdinalIgnoreCase) ||
                parameter.Key.EndsWith(":portKey", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInputConfigurationParameter(ToolParameterItem parameter)
    {
        return IsInputConnectionParameter(parameter) ||
               parameter.Key.Equals("resultInputCount", StringComparison.OrdinalIgnoreCase) ||
               (parameter.Key.StartsWith("input:", StringComparison.OrdinalIgnoreCase) &&
                (parameter.Key.EndsWith(":dataType", StringComparison.OrdinalIgnoreCase) ||
                 parameter.Key.EndsWith(":name", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsMeasureDistanceLegacyParameter(ToolParameterItem parameter)
    {
        return parameter.Key.Equals("nominal", StringComparison.OrdinalIgnoreCase) ||
               parameter.Key.Equals("lower", StringComparison.OrdinalIgnoreCase) ||
               parameter.Key.Equals("upper", StringComparison.OrdinalIgnoreCase) ||
               parameter.Key.Equals("unit", StringComparison.OrdinalIgnoreCase);
    }

    private string GetMeasurementMode()
    {
        var parameter = Parameters.FirstOrDefault(parameter => string.Equals(parameter.Key, "measurementMode", StringComparison.OrdinalIgnoreCase));
        if (parameter is null)
        {
            return GuessMeasurementMode(Name);
        }

        var mode = NormalizeMeasurementMode(parameter.Value);
        parameter.Value = mode;
        return mode;
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

    private static string GetConnectionToolParameterKey(string targetPortKey)
    {
        return $"input:{targetPortKey}:toolId";
    }

    private static string GetConnectionPortParameterKey(string targetPortKey)
    {
        return $"input:{targetPortKey}:portKey";
    }

    private static string GetResultInputDataTypeParameterKey(string targetPortKey)
    {
        return $"input:{targetPortKey}:dataType";
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
        return keys.Length == 0
            ? "ResultOutput"
            : string.Join(",", keys);
    }

    private static IEnumerable<ToolOutputOptionItem> CreateOutputOptions(VisionToolKind kind, string? enabledOutputText, string? measurementMode)
    {
        var definitions = VisionToolCatalog.GetOutputPorts(kind);
        if (definitions.Count == 0)
        {
            return [];
        }

        var defaults = VisionToolCatalog.GetDefaultOutputKeys(kind, measurementMode).ToArray();
        var enabled = ParseEnabledOutputKeys(enabledOutputText, defaults);
        return definitions
            .Select(definition => new ToolOutputOptionItem(
                definition.Key,
                definition.Name,
                definition.DataType,
                enabled.Contains(definition.Key)))
            .ToArray();
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

    private static IReadOnlyList<ToolParameterItem> ParseParameters(string text)
    {
        var items = new List<ToolParameterItem>();
        foreach (var segment in text.Split(["\r\n", "\n", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = segment[..index].Trim();
            var value = index == segment.Length - 1 ? string.Empty : segment[(index + 1)..].Trim();
            items.Add(new ToolParameterItem(key, value));
        }

        return items;
    }

    private static IReadOnlyDictionary<string, string> ParseParameterDictionary(string text)
    {
        return ParseParameters(text)
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Key))
            .GroupBy(parameter => parameter.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> GetTemplateParameters(VisionToolKind kind)
    {
        return VisionToolCatalog.GetDefaultParameters(kind);
    }
}

internal sealed record ToolInputDefinition(string Key, string Name, string DataType);

internal sealed record ToolOutputDefinition(string Key, string Name, string DataType);

public sealed record ToolParameterChoiceItem(string Value, string Text)
{
    public override string ToString()
    {
        return Text;
    }
}

public sealed record CommunicationChannelChoiceItem(string Key, string Name, bool Enabled, string Description)
{
    public string DisplayName => Enabled
        ? $"{Key} / {Name}"
        : $"{Key} / {Name}（停用）";
}

public sealed class ToolParameterItem : BindableBase
{
    private string _key;
    private string _value;

    public ToolParameterItem(string key, string value)
    {
        _key = key;
        _value = value;
    }

    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}
