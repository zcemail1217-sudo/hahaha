using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Client.Events;
using VisionStation.Client.Services;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Client.ViewModels;

public sealed class VariableCenterViewModel : BindableBase, IDisposable
{
    private const string UnsavedChangesKey = "variable-center";

    private readonly IRecipeRepository _recipes;
    private readonly IDeviceConfigurationRepository _configurationRepository;
    private readonly IInspectionRunner _inspectionRunner;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IEventAggregator _events;
    private readonly ICommunicationChannelRuntime _communicationChannels;
    private readonly IDeviceRuntime _devices;
    private readonly IPlcClient _plc;
    private readonly IDigitalIoController _digitalIo;
    private readonly IUnsavedChangesService _unsavedChanges;
    private readonly CancellationTokenSource _liveValueCancellation = new();
    private DeviceConfiguration _configuration;
    private Recipe? _loadedRecipe;
    private RecipeListItem? _selectedRecipe;
    private RecipeVariableItem? _selectedVariable;
    private string _recipeName = string.Empty;
    private string _productCode = string.Empty;
    private string _statusText = "正在加载变量中心...";
    private bool _isBusy;
    private bool _hasUnsavedChanges;
    private bool _isCurrentRecipe;
    private bool _isLoadingEditor;

    public VariableCenterViewModel(
        IRecipeRepository recipes,
        IDeviceConfigurationRepository configurationRepository,
        IInspectionRunner inspectionRunner,
        IUiDispatcher uiDispatcher,
        DeviceConfiguration configuration,
        IEventAggregator events,
        ICommunicationChannelRuntime communicationChannels,
        IDeviceRuntime devices,
        IPlcClient plc,
        IDigitalIoController digitalIo,
        IUnsavedChangesService unsavedChanges)
    {
        _recipes = recipes;
        _configurationRepository = configurationRepository;
        _inspectionRunner = inspectionRunner;
        _uiDispatcher = uiDispatcher;
        _configuration = configuration;
        _events = events;
        _communicationChannels = communicationChannels;
        _devices = devices;
        _plc = plc;
        _digitalIo = digitalIo;
        _unsavedChanges = unsavedChanges;

        Variables.CollectionChanged += OnVariablesChanged;
        ReloadCommand = new DelegateCommand(async () => await LoadAsync(), () => !IsBusy);
        SaveCommand = new DelegateCommand(async () => await SaveAsync(), CanSave);
        AddVariableCommand = new DelegateCommand<string>(AddVariable, _ => CanEditRecipe());
        RemoveVariableCommand = new DelegateCommand(RemoveSelectedVariable, () => SelectedVariable is not null && CanEditRecipe());
        SyncFromRecipeCommand = new DelegateCommand(SyncFromRecipe, CanEditRecipe);
        _inspectionRunner.RunCompleted += OnInspectionCompleted;
        _communicationChannels.FrameReceived += OnCommunicationFrameReceived;
        _configurationRepository.ConfigurationSaved += OnConfigurationSaved;

        _ = LoadAsync();
        _ = RunLiveValuePollingAsync(_liveValueCancellation.Token);
    }

    public ObservableCollection<RecipeListItem> Recipes { get; } = new();

    public ObservableCollection<RecipeVariableItem> Variables { get; } = new();

    public ObservableCollection<VariableReferenceItem> References { get; } = new();

    public ObservableCollection<RuntimeVariableValueItem> RuntimeValues { get; } = new();

    public IReadOnlyList<string> VariableDirections { get; } = ["Input", "Internal", "Output", "InOut"];

    public IReadOnlyList<string> DataTypes { get; } =
        ["string", "double", "int", "bool", "enum", "visionResult", "image", "point", "pose", "line", "circle", "roi", "json"];

    public IReadOnlyList<string> VariableSourceTypes { get; } = ["手动", "TCP", "串口", "PLC", "轴卡 IO", "运行值", "表达式"];

    public DelegateCommand ReloadCommand { get; }

    public DelegateCommand SaveCommand { get; }

    public DelegateCommand<string> AddVariableCommand { get; }

    public DelegateCommand RemoveVariableCommand { get; }

    public DelegateCommand SyncFromRecipeCommand { get; }

    public RecipeListItem? SelectedRecipe
    {
        get => _selectedRecipe;
        set
        {
            if (!SetProperty(ref _selectedRecipe, value))
            {
                return;
            }

            RaiseCommandStates();
            _ = LoadRecipeAsync(value?.Id);
        }
    }

    public RecipeVariableItem? SelectedVariable
    {
        get => _selectedVariable;
        set
        {
            if (SetProperty(ref _selectedVariable, value))
            {
                RemoveVariableCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RecipeName
    {
        get => _recipeName;
        private set => SetProperty(ref _recipeName, value);
    }

    public string ProductCode
    {
        get => _productCode;
        private set => SetProperty(ref _productCode, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
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
                    "变量中心",
                    value,
                    _ => SaveAsync(),
                    RecipeName);
                RaisePropertyChanged(nameof(ChangeStateText));
            }
        }
    }

    public bool IsCurrentRecipe
    {
        get => _isCurrentRecipe;
        private set
        {
            if (SetProperty(ref _isCurrentRecipe, value))
            {
                RaisePropertyChanged(nameof(CurrentRecipeBadge));
            }
        }
    }

    public int InputCount => Variables.Count(variable => IsDirection(variable.Direction, "Input") || IsDirection(variable.Direction, "InOut"));

    public int InternalCount => Variables.Count(variable => IsDirection(variable.Direction, "Internal"));

    public int OutputCount => Variables.Count(variable => IsDirection(variable.Direction, "Output") || IsDirection(variable.Direction, "InOut"));

    public int RequiredCount => Variables.Count(variable => variable.Required);

    public int RuntimeValueCount => RuntimeValues.Count;

    public int VariableCount => Variables.Count;

    public string CurrentRecipeBadge => IsCurrentRecipe ? "当前生产配方" : "候选配方";

    public string ChangeStateText => HasUnsavedChanges ? "有未保存修改" : "已保存";

    private async Task LoadAsync(string? preferredRecipeId = null)
    {
        IsBusy = true;
        try
        {
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
                ? "未找到可用配方。"
                : $"已加载 {Recipes.Count} 个配方，当前变量表来自 {SelectedRecipe.Name}。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadRecipeAsync(string? recipeId)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            ClearEditor();
            return;
        }

        IsBusy = true;
        try
        {
            var recipe = await _recipes.GetAsync(recipeId) ?? await _recipes.GetCurrentAsync();
            var currentRecipeId = await _recipes.GetCurrentRecipeIdAsync();
            ApplyRecipe(recipe.WithNormalizedFlows(), currentRecipeId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyRecipe(Recipe recipe, string currentRecipeId)
    {
        _isLoadingEditor = true;
        try
        {
            _loadedRecipe = recipe;
            RecipeName = recipe.Name;
            ProductCode = recipe.ProductCode;
            IsCurrentRecipe = string.Equals(recipe.Id, currentRecipeId, StringComparison.OrdinalIgnoreCase);

            ReplaceVariables(recipe.Variables.Select(RecipeVariableItem.FromDefinition));
            RebuildReferences(recipe);
            SelectedVariable = Variables.FirstOrDefault();
            HasUnsavedChanges = false;
            StatusText = $"{recipe.Name} 变量中心已加载。";
        }
        finally
        {
            _isLoadingEditor = false;
            RefreshSummary();
            RaiseCommandStates();
        }
    }

    private async Task SaveAsync()
    {
        if (_loadedRecipe is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var recipe = _loadedRecipe with
            {
                Variables = Variables.Select(variable => variable.ToDefinition()).ToArray(),
                UpdatedAt = DateTimeOffset.Now
            };
            await _recipes.SaveAsync(recipe);
            _events.GetEvent<RecipeChangedEvent>().Publish(recipe.Id);
            await LoadAsync(recipe.Id);
            StatusText = $"{recipe.Name} 的变量配置已保存。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddVariable(string? direction)
    {
        var normalizedDirection = string.IsNullOrWhiteSpace(direction) ? "Input" : direction.Trim();
        if (string.Equals(normalizedDirection, "Parameter", StringComparison.OrdinalIgnoreCase))
        {
            var parameterIndex = Variables.Count + 1;
            var parameter = new RecipeVariableItem
            {
                Key = CreateUniqueKey("Param", parameterIndex),
                Name = $"参数-{parameterIndex}",
                Direction = "Input",
                DataType = "double",
                DefaultValue = "0",
                CurrentValue = "0",
                Source = "手动输入",
            Target = "配方/运行流程",
            Editable = true,
            Enabled = true,
            Description = "新建参数，可在配方中使用 ${Key} 调用。"
        };

            ConfigureVariableSourceOptions(parameter);
            Variables.Add(parameter);
            SelectedVariable = parameter;
            return;
        }

        var isExpressionVariable = string.Equals(normalizedDirection, "Expression", StringComparison.OrdinalIgnoreCase);
        if (isExpressionVariable)
        {
            normalizedDirection = "Internal";
        }
        var index = Variables.Count + 1;
        var item = new RecipeVariableItem
        {
            Key = CreateUniqueKey(isExpressionVariable ? "CalcVar" : normalizedDirection == "Output" ? "OutputVar" : "InputVar", index),
            Name = isExpressionVariable ? $"计算变量-{index}" : normalizedDirection switch
            {
                "Output" => $"输出变量-{index}",
                "Internal" => $"运行变量-{index}",
                "InOut" => $"双向变量-{index}",
                _ => $"输入变量-{index}"
            },
            Direction = normalizedDirection,
            DataType = isExpressionVariable ? "double" : "string",
            Source = isExpressionVariable ? "Expression:${MeasuredWidth}-${ProductWidth}" : normalizedDirection == "Output" ? "VisionResult" : "External",
            Target = normalizedDirection == "Output" ? "PLC/MES/ResultTable" : "RuntimeValues",
            Editable = true,
            Enabled = true,
            Expression = string.Empty,
            Description = "新增变量"
        };

        ConfigureVariableSourceOptions(item);
        Variables.Add(item);
        SelectedVariable = item;
    }

    private void RemoveSelectedVariable()
    {
        if (SelectedVariable is null)
        {
            return;
        }

        var removeIndex = Variables.IndexOf(SelectedVariable);
        Variables.Remove(SelectedVariable);
        SelectedVariable = removeIndex >= 0 && removeIndex < Variables.Count
            ? Variables[removeIndex]
            : Variables.LastOrDefault();
    }

    private void SyncFromRecipe()
    {
        if (_loadedRecipe is null)
        {
            return;
        }

        foreach (var parameter in _loadedRecipe.ProductParameters)
        {
            UpsertVariable(new RecipeVariableDefinition
            {
                Id = $"var-param-{parameter.Id}",
                Key = CreateVariableKey(parameter.Name, parameter.Id),
                Name = parameter.Name,
                Direction = "Input",
                DataType = "string",
                DefaultValue = parameter.Value,
                CurrentValue = parameter.Value,
                Unit = parameter.Unit,
                Source = $"配方参数:{parameter.Id}",
                Target = "RuntimeValues",
                Editable = parameter.Editable,
                Enabled = true,
                Description = parameter.Description
            });
        }

        foreach (var result in _loadedRecipe.VisionResults)
        {
            UpsertVariable(new RecipeVariableDefinition
            {
                Id = $"var-result-{result.Id}",
                Key = FirstNonEmpty(result.ExternalAlias, result.Name, result.SourceKey, result.Id),
                Name = result.Name,
                Direction = "Output",
                DataType = string.IsNullOrWhiteSpace(result.DataType) ? "string" : result.DataType,
                Unit = result.Unit,
                Source = FirstNonEmpty($"{result.SourceToolId}:{result.SourceKey}", result.SourceKey),
                Target = string.IsNullOrWhiteSpace(result.PlcAddress) ? result.ExternalAlias : $"PLC:{result.PlcAddress}",
                Required = result.ParticipateInJudge,
                Enabled = true,
                Description = result.Description
            });
        }

        foreach (var signal in _loadedRecipe.PlcSignals)
        {
            var isRead = string.Equals(signal.Direction, "Read", StringComparison.OrdinalIgnoreCase);
            UpsertVariable(new RecipeVariableDefinition
            {
                Id = $"var-signal-{signal.Id}",
                Key = CreateVariableKey(signal.Name, signal.Id),
                Name = signal.Name,
                Direction = isRead ? "Input" : "Output",
                DataType = "bool",
                DefaultValue = signal.TriggerValue,
                CurrentValue = signal.TriggerValue,
                Source = isRead ? $"PLC:{signal.Address}" : "RuntimeValues",
                Target = isRead ? "RuntimeValues" : $"PLC:{signal.Address}",
                Required = signal.Blocking,
                Enabled = true,
                Description = signal.Description
            });
        }

        foreach (var axis in ResolveAxisDefinitions())
        {
            UpsertVariable(new RecipeVariableDefinition
            {
                Id = $"var-axis-{axis.Key}-encoder",
                Key = $"Axis.{axis.Key}.EncoderPosition",
                Name = $"{axis.Name}反馈位置",
                Direction = "Internal",
                DataType = "double",
                Unit = "mm",
                Source = $"AxisStatus:{axis.Key}.EncoderPosition",
                Target = "RuntimeValues",
                Enabled = true,
                Description = axis.Description
            });
            UpsertVariable(new RecipeVariableDefinition
            {
                Id = $"var-axis-{axis.Key}-command",
                Key = $"Axis.{axis.Key}.CommandPosition",
                Name = $"{axis.Name}指令位置",
                Direction = "Internal",
                DataType = "double",
                Unit = "mm",
                Source = $"AxisStatus:{axis.Key}.CommandPosition",
                Target = "RuntimeValues",
                Enabled = true,
                Description = axis.Description
            });
            UpsertVariable(new RecipeVariableDefinition
            {
                Id = $"var-axis-{axis.Key}-inpos",
                Key = $"Axis.{axis.Key}.InPosition",
                Name = $"{axis.Name}到位状态",
                Direction = "Internal",
                DataType = "bool",
                Source = $"AxisStatus:{axis.Key}.InPosition",
                Target = "RuntimeValues",
                Enabled = true,
                Description = axis.Description
            });
        }

        foreach (var step in _loadedRecipe.ProcessSteps.Where(step => !string.IsNullOrWhiteSpace(step.ResultKey)))
        {
            UpsertVariable(new RecipeVariableDefinition
            {
                Id = $"var-step-{step.Id}",
                Key = step.ResultKey,
                Name = step.ResultKey,
                Direction = "Internal",
                DataType = "string",
                Source = $"运行步骤:{step.StepNo}-{step.Name}",
                Target = step.OutputTarget,
                Enabled = true,
                Description = step.Description
            });
        }

        StatusText = "已从产品参数、视觉结果、PLC 信号和运行步骤同步变量，保存后生效。";
        MarkDirty();
    }

    private void UpsertVariable(RecipeVariableDefinition definition)
    {
        var existing = Variables.FirstOrDefault(variable =>
            string.Equals(variable.Source, definition.Source, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(variable.Key, definition.Key, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var item = RecipeVariableItem.FromDefinition(definition);
            ConfigureVariableSourceOptions(item);
            Variables.Add(item);
            return;
        }

        existing.Apply(definition);
        ConfigureVariableSourceOptions(existing);
    }

    private void ReplaceVariables(IEnumerable<RecipeVariableItem> items)
    {
        foreach (var variable in Variables)
        {
            variable.PropertyChanged -= OnVariableChanged;
        }

        Variables.Clear();
        foreach (var item in items)
        {
            ConfigureVariableSourceOptions(item);
            item.PropertyChanged += OnVariableChanged;
            Variables.Add(item);
        }
    }

    private void ConfigureVariableSourceOptions(RecipeVariableItem item)
    {
        var recipe = _loadedRecipe;
        item.ConfigureSourceBindingOptions(
            _configuration.SystemSettings.Communication.TcpChannels.Where(channel => channel.Enabled).Select(channel => channel.Key),
            _configuration.SystemSettings.Communication.SerialChannels.Where(channel => channel.Enabled).Select(channel => channel.Key),
            ResolvePlcSourceOptions(recipe),
            ResolveIoSourceOptions(),
            ResolveRuntimeSourceOptions(recipe));
    }

    private IReadOnlyList<string> ResolvePlcSourceOptions(Recipe? recipe)
    {
        var values = new List<string>
        {
            _configuration.SystemSettings.Plc.HeartbeatAddress,
            _configuration.SystemSettings.Plc.ResultAddress
        };

        if (recipe is not null)
        {
            values.AddRange(recipe.PlcSignals.Select(signal => signal.Address));
            values.AddRange(recipe.VisionResults.Select(result => result.PlcAddress));
            values.AddRange(recipe.ProcessSteps.Select(step => step.SignalId));
            values.AddRange(recipe.ProcessSteps.Select(step => step.OutputTarget));
        }

        return NormalizeOptionValues(values);
    }

    private IReadOnlyList<string> ResolveIoSourceOptions()
    {
        return NormalizeOptionValues(_configuration.IoPoints
            .Where(point => point.Enabled)
            .SelectMany(point => new[] { point.Key, point.Address }));
    }

    private IReadOnlyList<string> ResolveRuntimeSourceOptions(Recipe? recipe)
    {
        var values = new List<string>
        {
            "RecipeId",
            "RecipeName",
            "ProductCode",
            "BatchId",
            "OperatorName",
            "TriggeredByPlc",
            "MachineName",
            "InspectionTimeoutMs",
            "ImageRetentionDays",
            "OverallResult",
            "LastSignalValue"
        };

        values.AddRange(_configuration.SystemSettings.Parameters.Items
            .Where(parameter => parameter.Enabled)
            .SelectMany(parameter => new[] { parameter.Key, parameter.Name }));

        if (recipe is not null)
        {
            values.AddRange(recipe.ProductParameters.SelectMany(parameter => new[] { parameter.Id, parameter.Name }));
            values.AddRange(recipe.Variables.Where(variable => variable.Enabled).SelectMany(variable => new[] { variable.Key, variable.Name }));
            values.AddRange(recipe.ProcessSteps.Select(step => step.ResultKey));
            values.AddRange(recipe.ProcessSteps.Select(step => step.OutputTarget));
            values.AddRange(recipe.VisionResults.Select(result => FirstNonEmpty(result.ExternalAlias, result.Name, result.SourceKey)));
        }

        foreach (var axis in ResolveAxisDefinitions())
        {
            values.Add($"Axis.{axis.Key}.EncoderPosition");
            values.Add($"Axis.{axis.Key}.CommandPosition");
            values.Add($"Axis.{axis.Key}.Position");
            values.Add($"Axis.{axis.Key}.InPosition");
            values.Add($"{axis.Key}.Position");
            values.Add($"{axis.Key}.InPosition");
        }

        return NormalizeOptionValues(values);
    }

    private void ApplyDeviceConfiguration(DeviceConfiguration configuration)
    {
        _configuration = configuration;
        var wasLoading = _isLoadingEditor;
        _isLoadingEditor = true;
        try
        {
            foreach (var item in Variables)
            {
                ConfigureVariableSourceOptions(item);
            }

            if (_loadedRecipe is not null)
            {
                RebuildReferences(_loadedRecipe);
            }
        }
        finally
        {
            _isLoadingEditor = wasLoading;
        }

        StatusText = "系统参数已更新，参数来源下拉列表已刷新。";
    }

    private void RebuildReferences(Recipe recipe)
    {
        References.Clear();
        var runtimeParameters = _configuration.SystemSettings.Parameters;
        References.Add(new VariableReferenceItem("系统参数", "设备名称", runtimeParameters.MachineName, "RuntimeValues", "#FF7AD7FF"));
        References.Add(new VariableReferenceItem("系统参数", "检测超时", runtimeParameters.InspectionTimeoutMs.ToString(), "RuntimeValues", "#FF7AD7FF"));
        References.Add(new VariableReferenceItem("系统参数", "图像保留天数", runtimeParameters.ImageRetentionDays.ToString(), "RuntimeValues", "#FF7AD7FF"));
        foreach (var parameter in runtimeParameters.Items.Where(parameter => parameter.Enabled))
        {
            References.Add(new VariableReferenceItem("系统参数", parameter.Name, parameter.Value, parameter.Key, "#FF7AD7FF"));
        }

        foreach (var axis in ResolveAxisDefinitions())
        {
            References.Add(new VariableReferenceItem("轴状态", axis.Name, $"Axis.{axis.Key}.Position", "RuntimeValues", "#FFFFC95A"));
            References.Add(new VariableReferenceItem("轴状态", axis.Name, $"Axis.{axis.Key}.InPosition", "RuntimeValues", "#FFFFC95A"));
        }

        foreach (var point in _configuration.IoPoints.Where(point => point.Enabled))
        {
            References.Add(new VariableReferenceItem("轴卡 IO", point.Name, FirstNonEmpty(point.Address, point.Key), point.Direction.ToString(), "#FFFFC95A"));
        }
        foreach (var parameter in recipe.ProductParameters)
        {
            References.Add(new VariableReferenceItem("产品参数", parameter.Name, parameter.Value, "RuntimeValues", "#FF7AD7FF"));
        }

        foreach (var result in recipe.VisionResults)
        {
            References.Add(new VariableReferenceItem("视觉结果", result.Name, $"{result.SourceToolId}:{result.SourceKey}", FirstNonEmpty(result.ExternalAlias, result.PlcAddress), "#FF5CE08A"));
        }

        foreach (var signal in recipe.PlcSignals)
        {
            References.Add(new VariableReferenceItem("PLC信号", signal.Name, signal.Address, signal.Direction, "#FFFFC95A"));
        }

        foreach (var step in recipe.ProcessSteps.Where(step => !string.IsNullOrWhiteSpace(step.ResultKey)))
        {
            References.Add(new VariableReferenceItem("运行步骤", step.Name, step.ResultKey, step.OutputTarget, "#FFBFA2FF"));
        }
    }

    private void ApplyRuntimeSnapshot(InspectionRunResult run)
    {
        RuntimeValues.Clear();
        var values = new Dictionary<string, string>(run.RuntimeValues, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in run.Result.ResultData)
        {
            values[pair.Key] = pair.Value;
        }

        foreach (var pair in values.OrderBy(pair => ResolveRuntimeKind(pair.Key)).ThenBy(pair => pair.Key))
        {
            var kind = ResolveRuntimeKind(pair.Key);
            RuntimeValues.Add(new RuntimeVariableValueItem(
                kind,
                pair.Key,
                pair.Value,
                run.Result.Timestamp.ToString("HH:mm:ss"),
                ResolveRuntimeKindBrush(kind)));
        }

        foreach (var variable in Variables.Where(variable => variable.Enabled))
        {
            variable.TryApplyLiveValue(values, run.Result.Timestamp);
        }

        RaisePropertyChanged(nameof(RuntimeValueCount));
        StatusText = $"最新检测运行变量已刷新：{RuntimeValues.Count} 项。";
    }

    private void OnInspectionCompleted(object? sender, InspectionRunResult run)
    {
        _uiDispatcher.Invoke(() => ApplyRuntimeSnapshot(run));
    }

    private void OnConfigurationSaved(object? sender, DeviceConfiguration configuration)
    {
        _uiDispatcher.Invoke(() => ApplyDeviceConfiguration(configuration));
    }

    private void OnCommunicationFrameReceived(object? sender, CommunicationChannelRuntimeFrame frame)
    {
        var text = DecodeFramePayload(frame.Payload);
        _uiDispatcher.Invoke(() => ApplyCommunicationFrame(frame.Kind, frame.Key, text, DateTimeOffset.Now));
    }

    private void ApplyCommunicationFrame(string kind, string channelKey, string value, DateTimeOffset updatedAt)
    {
        var updated = false;
        foreach (var variable in Variables.Where(variable => variable.Enabled))
        {
            updated |= variable.TryApplyCommunicationFrameValue(kind, channelKey, value, updatedAt);
        }

        if (updated)
        {
            StatusText = $"{kind} {channelKey} live value updated.";
        }
    }

    private async Task RunLiveValuePollingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _communicationChannels.ConnectAsync(CommunicationChannelConnectionPolicies.Production, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshPolledLiveValuesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _uiDispatcher.Invoke(() => StatusText = $"Live value refresh failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task RefreshPolledLiveValuesAsync(CancellationToken cancellationToken)
    {
        LivePollRequest[] requests = [];
        _uiDispatcher.Invoke(() =>
        {
            requests = Variables
                .Where(variable => variable.Enabled)
                .Select(variable => new LivePollRequest(
                    variable,
                    variable.SourceType,
                    variable.SourceBindingKey,
                    variable.Source,
                    variable.Expression))
                .Where(IsPollableLiveSource)
                .ToArray();
        });

        if (requests.Length == 0)
        {
            return;
        }

        var updates = new List<(RecipeVariableItem Variable, string Value, DateTimeOffset UpdatedAt)>();
        foreach (var request in requests)
        {
            var value = await TryReadLiveValueAsync(request, cancellationToken);
            if (value is null)
            {
                continue;
            }

            updates.Add((request.Variable, value, DateTimeOffset.Now));
        }

        if (updates.Count == 0)
        {
            return;
        }

        _uiDispatcher.Invoke(() =>
        {
            foreach (var (variable, value, updatedAt) in updates)
            {
                variable.SetLiveValue(value, updatedAt);
            }
        });
    }

    private async Task<string?> TryReadLiveValueAsync(LivePollRequest request, CancellationToken cancellationToken)
    {
        if (IsPlcSourceType(request.SourceType))
        {
            return await ReadPlcLiveValueAsync(request, cancellationToken);
        }

        if (IsIoSourceType(request.SourceType))
        {
            return await ReadIoLiveValueAsync(request, cancellationToken);
        }

        if (IsTcpSourceType(request.SourceType))
        {
            return await ExchangeTcpLiveValueAsync(request, cancellationToken);
        }

        if (IsSerialSourceType(request.SourceType))
        {
            return await ExchangeSerialLiveValueAsync(request, cancellationToken);
        }

        return null;
    }

    private async Task<string?> ReadPlcLiveValueAsync(LivePollRequest request, CancellationToken cancellationToken)
    {
        var parts = SplitSource(request.Source);
        var deviceKey = parts.Length > 2 ? parts[1] : string.Empty;
        var address = parts.Length > 2 ? parts[2] : request.BindingKey;
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(deviceKey) &&
            _devices.TryGet<IAddressableDeviceClient>(deviceKey, out var device))
        {
            await EnsureConnectedAsync(device, cancellationToken);
            return await device.ReadAsync(address, cancellationToken);
        }

        if (_plc.Snapshot.State != DeviceConnectionState.Connected)
        {
            await _plc.ConnectAsync(cancellationToken);
        }

        return await _plc.ReadAddressAsync(address, cancellationToken);
    }

    private async Task<string?> ReadIoLiveValueAsync(LivePollRequest request, CancellationToken cancellationToken)
    {
        var parts = SplitSource(request.Source);
        var deviceKey = parts.Length > 2 ? parts[1] : string.Empty;
        var pointKey = parts.Length > 2 ? parts[2] : request.BindingKey;
        if (string.IsNullOrWhiteSpace(pointKey))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(deviceKey) &&
            _devices.TryGet<IDigitalIoDeviceClient>(deviceKey, out var device))
        {
            await EnsureConnectedAsync(device, cancellationToken);
            var deviceStatus = await device.Controller.GetPointStatusAsync(pointKey, cancellationToken);
            return deviceStatus.Value ? "1" : "0";
        }

        if (_digitalIo.Snapshot.State != DeviceConnectionState.Connected)
        {
            await _digitalIo.ConnectAsync(cancellationToken);
        }

        var status = await _digitalIo.GetPointStatusAsync(pointKey, cancellationToken);
        return status.Value ? "1" : "0";
    }

    private async Task<string?> ExchangeTcpLiveValueAsync(LivePollRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Expression))
        {
            return null;
        }

        var channel = _configuration.SystemSettings.Communication.TcpChannels.FirstOrDefault(item =>
            item.Enabled && string.Equals(item.Key, request.BindingKey, StringComparison.OrdinalIgnoreCase));
        if (channel is null)
        {
            return null;
        }

        var response = await _communicationChannels.TryExchangeTcpAsync(
            channel,
            Encoding.UTF8.GetBytes(request.Expression),
            Math.Clamp(channel.ReceiveTimeoutMs, 100, 1000),
            true,
            cancellationToken);
        return response is null ? null : DecodeFramePayload(response);
    }

    private async Task<string?> ExchangeSerialLiveValueAsync(LivePollRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Expression))
        {
            return null;
        }

        var channel = _configuration.SystemSettings.Communication.SerialChannels.FirstOrDefault(item =>
            item.Enabled && string.Equals(item.Key, request.BindingKey, StringComparison.OrdinalIgnoreCase));
        if (channel is null)
        {
            return null;
        }

        var response = await _communicationChannels.TryExchangeSerialAsync(
            channel,
            Encoding.UTF8.GetBytes(request.Expression),
            Math.Clamp(channel.ReceiveTimeoutMs, 100, 1000),
            true,
            cancellationToken);
        return response is null ? null : DecodeFramePayload(response);
    }

    private static string ResolveRuntimeKind(string key)
    {
        if (key.StartsWith("Axis.", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(".EncoderPosition", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(".CommandPosition", StringComparison.OrdinalIgnoreCase))
        {
            return "轴状态";
        }

        if (key.StartsWith("Vision.", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("tool-", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Output", StringComparison.OrdinalIgnoreCase))
        {
            return "视觉结果";
        }

        if (key.StartsWith("PLC", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("plc", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Device", StringComparison.OrdinalIgnoreCase))
        {
            return "设备数据";
        }

        if (key.Contains("ResultTable", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("OverallResult", StringComparison.OrdinalIgnoreCase))
        {
            return "结果表";
        }

        return "运行变量";
    }

    private static string ResolveRuntimeKindBrush(string kind)
    {
        return kind switch
        {
            "轴状态" => "#FFFFC95A",
            "视觉结果" => "#FF5CE08A",
            "设备数据" => "#FF7AD7FF",
            "结果表" => "#FFFF8A65",
            _ => "#FFBFA2FF"
        };
    }

    private void ClearEditor()
    {
        _loadedRecipe = null;
        RecipeName = string.Empty;
        ProductCode = string.Empty;
        ReplaceVariables([]);
        References.Clear();
        RuntimeValues.Clear();
        RaisePropertyChanged(nameof(RuntimeValueCount));
        SelectedVariable = null;
        HasUnsavedChanges = false;
        StatusText = "未选择配方。";
        RefreshSummary();
        RaiseCommandStates();
    }

    private void OnVariablesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<RecipeVariableItem>())
            {
                item.PropertyChanged -= OnVariableChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<RecipeVariableItem>())
            {
                item.PropertyChanged += OnVariableChanged;
            }
        }

        MarkDirty();
        RefreshSummary();
        RaiseCommandStates();
    }

    private void OnVariableChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsLiveValueProperty(e.PropertyName))
        {
            return;
        }

        MarkDirty();
        RefreshSummary();
    }

    private static bool IsLiveValueProperty(string? propertyName)
    {
        return propertyName is nameof(RecipeVariableItem.LiveValue)
            or nameof(RecipeVariableItem.LiveValueUpdatedAt)
            or nameof(RecipeVariableItem.DisplayedCurrentValue);
    }

    private bool CanEditRecipe()
    {
        return !IsBusy && _loadedRecipe is not null;
    }

    private bool CanSave()
    {
        return CanEditRecipe() && HasUnsavedChanges;
    }

    private void MarkDirty()
    {
        if (_isLoadingEditor)
        {
            return;
        }

        HasUnsavedChanges = true;
        if (!string.IsNullOrWhiteSpace(RecipeName))
        {
            StatusText = $"{RecipeName} 的变量配置有未保存修改。";
        }

        RaiseCommandStates();
    }

    private void RefreshSummary()
    {
        RaisePropertyChanged(nameof(InputCount));
        RaisePropertyChanged(nameof(InternalCount));
        RaisePropertyChanged(nameof(OutputCount));
        RaisePropertyChanged(nameof(RequiredCount));
        RaisePropertyChanged(nameof(VariableCount));
    }

    private void RaiseCommandStates()
    {
        ReloadCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        AddVariableCommand.RaiseCanExecuteChanged();
        RemoveVariableCommand.RaiseCanExecuteChanged();
        SyncFromRecipeCommand.RaiseCanExecuteChanged();
    }

    private string CreateUniqueKey(string prefix, int start)
    {
        var existing = Variables.Select(variable => variable.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = Math.Max(start, 1);
        string key;
        do
        {
            key = $"{prefix}{index}";
            index++;
        }
        while (existing.Contains(key));

        return key;
    }

    private IReadOnlyList<AxisPointDefinition> ResolveAxisDefinitions()
    {
        var axes = _configuration.Axes.Where(axis => axis.Enabled).ToArray();
        return axes.Length == 0
            ?
            [
                new AxisPointDefinition
                {
                    Key = AxisDefaults.PrimaryAxisKey,
                    Name = AxisDefaults.PrimaryAxisKey,
                    Description = "Default axis runtime status"
                }
            ]
            : axes;
    }

    private static string CreateVariableKey(string name, string fallback)
    {
        var seed = string.IsNullOrWhiteSpace(name) ? fallback : name;
        var characters = seed
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_')
            .ToArray();
        var key = new string(characters).Trim('_');
        return string.IsNullOrWhiteSpace(key) ? fallback : key;
    }

    private static bool IsDirection(string direction, string expected)
    {
        return string.Equals(direction, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static IReadOnlyList<string> NormalizeOptionValues(IEnumerable<string?> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPollableLiveSource(LivePollRequest request)
    {
        if (IsPlcSourceType(request.SourceType) || IsIoSourceType(request.SourceType))
        {
            return !string.IsNullOrWhiteSpace(request.BindingKey);
        }

        if (IsTcpSourceType(request.SourceType) || IsSerialSourceType(request.SourceType))
        {
            return !string.IsNullOrWhiteSpace(request.BindingKey) &&
                   !string.IsNullOrWhiteSpace(request.Expression);
        }

        return false;
    }

    private static bool IsTcpSourceType(string sourceType)
    {
        return string.Equals(sourceType, "TCP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSerialSourceType(string sourceType)
    {
        return sourceType.Contains("Serial", StringComparison.OrdinalIgnoreCase) ||
               sourceType.Contains('\u4e32');
    }

    private static bool IsPlcSourceType(string sourceType)
    {
        return string.Equals(sourceType, "PLC", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIoSourceType(string sourceType)
    {
        return sourceType.Contains("IO", StringComparison.OrdinalIgnoreCase) ||
               sourceType.Contains('\u8f74');
    }

    private static string[] SplitSource(string source)
    {
        return (source ?? string.Empty).Split(
            ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task EnsureConnectedAsync(IDeviceClient device, CancellationToken cancellationToken)
    {
        if (device.Snapshot.State != DeviceConnectionState.Connected)
        {
            await device.ConnectAsync(cancellationToken);
        }
    }

    private static string DecodeFramePayload(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(payload).TrimEnd('\r', '\n', '\0');
    }

    public void Dispose()
    {
        _liveValueCancellation.Cancel();
        _liveValueCancellation.Dispose();
        _inspectionRunner.RunCompleted -= OnInspectionCompleted;
        _communicationChannels.FrameReceived -= OnCommunicationFrameReceived;
        _configurationRepository.ConfigurationSaved -= OnConfigurationSaved;
    }

    private sealed record LivePollRequest(
        RecipeVariableItem Variable,
        string SourceType,
        string BindingKey,
        string Source,
        string Expression);
}

public sealed class RecipeVariableItem : BindableBase, IVisionFlowResultPreviewVariable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _key = "Variable";
    private string _name = "变量";
    private string _direction = "Input";
    private string _dataType = "string";
    private string _defaultValue = string.Empty;
    private string _currentValue = string.Empty;
    private bool _hasLiveValue;
    private string _liveValue = string.Empty;
    private DateTimeOffset? _liveValueUpdatedAt;
    private string _unit = string.Empty;
    private string _source = string.Empty;
    private string _target = string.Empty;
    private string _expression = string.Empty;
    private bool _required;
    private bool _editable = true;
    private bool _enabled = true;
    private string _description = string.Empty;
    private IReadOnlyList<string> _tcpSourceBindingOptions = Array.Empty<string>();
    private IReadOnlyList<string> _serialSourceBindingOptions = Array.Empty<string>();
    private IReadOnlyList<string> _plcSourceBindingOptions = Array.Empty<string>();
    private IReadOnlyList<string> _ioSourceBindingOptions = Array.Empty<string>();
    private IReadOnlyList<string> _runtimeSourceBindingOptions = Array.Empty<string>();
    private IReadOnlyList<string> _sourceBindingOptions = Array.Empty<string>();

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
                RaisePropertyChanged(nameof(ReferenceText));
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

    public string Direction
    {
        get => _direction;
        set
        {
            if (SetProperty(ref _direction, value))
            {
                RaisePropertyChanged(nameof(DirectionText));
                RaisePropertyChanged(nameof(DirectionBrush));
            }
        }
    }

    public string DataType
    {
        get => _dataType;
        set
        {
            var previous = NormalizeDataType(_dataType);
            var normalized = NormalizeDataType(value);
            if (SetProperty(ref _dataType, normalized))
            {
                ApplyDataTypeDefaults(previous);
                RaisePropertyChanged(nameof(DataTypeHint));
                RaisePropertyChanged(nameof(SourceTypeOptions));
                RefreshSourceBindingOptions();
            }
        }
    }

    public string DefaultValue
    {
        get => _defaultValue;
        set
        {
            if (SetProperty(ref _defaultValue, value))
            {
                RaisePropertyChanged(nameof(DisplayedCurrentValue));
            }
        }
    }

    public string CurrentValue
    {
        get => _currentValue;
        set
        {
            if (SetProperty(ref _currentValue, value))
            {
                RaisePropertyChanged(nameof(DisplayedCurrentValue));
            }
        }
    }

    public string LiveValue
    {
        get => _liveValue;
        private set
        {
            if (SetProperty(ref _liveValue, value))
            {
                RaisePropertyChanged(nameof(DisplayedCurrentValue));
            }
        }
    }

    public DateTimeOffset? LiveValueUpdatedAt
    {
        get => _liveValueUpdatedAt;
        private set
        {
            if (SetProperty(ref _liveValueUpdatedAt, value))
            {
                RaisePropertyChanged(nameof(LiveValueUpdatedAtText));
            }
        }
    }

    public string DisplayedCurrentValue => _hasLiveValue ? LiveValue : FirstNonEmpty(CurrentValue, DefaultValue);

    public string ValueStateText => !Enabled ? "停用" : _hasLiveValue ? "实时" : "配置值";

    public string ValueStateBrush => !Enabled ? "#FF7A8798" : _hasLiveValue ? "#FF5CE08A" : "#FFFFC95A";

    public string LiveValueUpdatedAtText => LiveValueUpdatedAt?.ToString("HH:mm:ss") ?? "-";

    public string Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, value);
    }

    public string Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value))
            {
                RaisePropertyChanged(nameof(SourceType));
                RaisePropertyChanged(nameof(SourceBindingKey));
                RaisePropertyChanged(nameof(UsesSourceBinding));
                RefreshSourceBindingOptions();
            }
        }
    }

    public string Target
    {
        get => _target;
        set => SetProperty(ref _target, value);
    }

    public string Expression
    {
        get => _expression;
        set => SetProperty(ref _expression, value);
    }

    public bool Required
    {
        get => _required;
        set => SetProperty(ref _required, value);
    }

    public bool Editable
    {
        get => _editable;
        set => SetProperty(ref _editable, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                RaisePropertyChanged(nameof(ValueStateText));
                RaisePropertyChanged(nameof(ValueStateBrush));
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string ReferenceText => string.IsNullOrWhiteSpace(Key) ? string.Empty : $"${{{Key.Trim()}}}";

    public string DisplayText => string.IsNullOrWhiteSpace(Name)
        ? ReferenceText
        : $"{Name} / {ReferenceText}";

    public string DataTypeHint => NormalizeDataType(DataType) switch
    {
        "double" => "小数数值",
        "int" => "整数数值",
        "bool" => "布尔开关",
        "enum" => "枚举文本",
        "point" => "二维点",
        "pose" => "位置姿态",
        "json" => "JSON 对象",
        _ => "文本"
    };

    public IReadOnlyList<string> SourceTypeOptions => NormalizeDataType(DataType) switch
    {
        "bool" => ["手动", "PLC", "轴卡 IO", "TCP", "运行值", "表达式"],
        "double" or "int" => ["手动", "TCP", "串口", "PLC", "运行值", "表达式"],
        "point" or "pose" => ["手动", "TCP", "串口", "运行值", "表达式"],
        "json" => ["手动", "TCP", "串口", "运行值", "表达式"],
        _ => ["手动", "TCP", "串口", "PLC", "轴卡 IO", "运行值", "表达式"]
    };

    public string SourceType
    {
        get => ParseSourceType(Source);
        set => Source = BuildSource(value, ResolveSourceBindingDefault(value, SourceBindingKey));
    }

    public string SourceBindingKey
    {
        get => ParseSourceBindingKey(Source);
        set => Source = BuildSource(SourceType, value);
    }

    public bool UsesSourceBinding => RequiresSourceBinding(SourceType);

    public IReadOnlyList<string> SourceBindingOptions
    {
        get => _sourceBindingOptions;
        private set => SetProperty(ref _sourceBindingOptions, value);
    }

    public override string ToString()
    {
        return DisplayText;
    }

    public bool TryApplyLiveValue(
        IReadOnlyDictionary<string, string> values,
        DateTimeOffset updatedAt)
    {
        if (!UsesLiveValueSource())
        {
            return false;
        }

        foreach (var key in ResolveLiveValueKeys())
        {
            if (!values.TryGetValue(key, out var value))
            {
                continue;
            }

            SetLiveValue(value, updatedAt);
            return true;
        }

        return false;
    }

    public bool TryApplyCommunicationFrameValue(string kind, string channelKey, string value, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(channelKey) || string.IsNullOrWhiteSpace(kind))
        {
            return false;
        }

        var sourceType = SourceType;
        var matchesKind =
            string.Equals(kind, "TCP", StringComparison.OrdinalIgnoreCase) && IsTcpSourceType(sourceType) ||
            string.Equals(kind, "Serial", StringComparison.OrdinalIgnoreCase) && IsSerialSourceType(sourceType);
        if (!matchesKind)
        {
            return false;
        }

        if (!string.Equals(SourceBindingKey, channelKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        SetLiveValue(value, updatedAt);
        return true;
    }

    public void ClearLiveValue()
    {
        _hasLiveValue = false;
        LiveValue = string.Empty;
        LiveValueUpdatedAt = null;
        RaisePropertyChanged(nameof(DisplayedCurrentValue));
        RaisePropertyChanged(nameof(ValueStateText));
        RaisePropertyChanged(nameof(ValueStateBrush));
    }

    public void SetLiveValue(string? value, DateTimeOffset updatedAt)
    {
        var hadLiveValue = _hasLiveValue;
        _hasLiveValue = true;
        LiveValue = value ?? string.Empty;
        LiveValueUpdatedAt = updatedAt;
        if (!hadLiveValue)
        {
            RaisePropertyChanged(nameof(DisplayedCurrentValue));
            RaisePropertyChanged(nameof(ValueStateText));
            RaisePropertyChanged(nameof(ValueStateBrush));
        }
    }

    public string DirectionText => Direction switch
    {
        "Output" => "输出",
        "Internal" => "运行中",
        "InOut" => "双向",
        _ => "输入"
    };

    public string DirectionBrush => Direction switch
    {
        "Output" => "#FF5CE08A",
        "Internal" => "#FFBFA2FF",
        "InOut" => "#FFFFC95A",
        _ => "#FF7AD7FF"
    };

    public static RecipeVariableItem FromDefinition(RecipeVariableDefinition definition)
    {
        var item = new RecipeVariableItem();
        item.Apply(definition);
        return item;
    }

    public void Apply(RecipeVariableDefinition definition)
    {
        Id = string.IsNullOrWhiteSpace(definition.Id) ? Id : definition.Id;
        Key = definition.Key;
        Name = definition.Name;
        Direction = definition.Direction;
        DataType = definition.DataType;
        DefaultValue = definition.DefaultValue;
        CurrentValue = definition.CurrentValue;
        Unit = definition.Unit;
        Source = definition.Source;
        Target = definition.Target;
        Expression = definition.Expression;
        Required = definition.Required;
        Editable = definition.Editable;
        Enabled = definition.Enabled;
        Description = definition.Description;
    }

    public void ConfigureSourceBindingOptions(
        IEnumerable<string> tcpChannelKeys,
        IEnumerable<string> serialChannelKeys,
        IEnumerable<string>? plcBindingKeys = null,
        IEnumerable<string>? ioBindingKeys = null,
        IEnumerable<string>? runtimeBindingKeys = null)
    {
        _tcpSourceBindingOptions = NormalizeSourceOptions(tcpChannelKeys, "tcp-main");
        _serialSourceBindingOptions = NormalizeSourceOptions(serialChannelKeys, "serial-main");
        _plcSourceBindingOptions = NormalizeSourceOptions(plcBindingKeys ?? Array.Empty<string>(), "D100");
        _ioSourceBindingOptions = NormalizeSourceOptions(ioBindingKeys ?? Array.Empty<string>(), "X0");
        _runtimeSourceBindingOptions = NormalizeSourceOptions(runtimeBindingKeys ?? Array.Empty<string>(), "OverallResult");
        RefreshSourceBindingOptions();
    }

    public RecipeVariableDefinition ToDefinition()
    {
        return new RecipeVariableDefinition
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Key = string.IsNullOrWhiteSpace(Key) ? Name : Key.Trim(),
            Name = Name?.Trim() ?? string.Empty,
            Direction = string.IsNullOrWhiteSpace(Direction) ? "Input" : Direction.Trim(),
            DataType = string.IsNullOrWhiteSpace(DataType) ? "string" : DataType.Trim(),
            DefaultValue = DefaultValue?.Trim() ?? string.Empty,
            CurrentValue = CurrentValue?.Trim() ?? string.Empty,
            Unit = Unit?.Trim() ?? string.Empty,
            Source = Source?.Trim() ?? string.Empty,
            Target = Target?.Trim() ?? string.Empty,
            Expression = Expression?.Trim() ?? string.Empty,
            Required = Required,
            Editable = Editable,
            Enabled = Enabled,
            Description = Description?.Trim() ?? string.Empty
        };
    }

    private static string NormalizeDataType(string? dataType)
    {
        var text = dataType?.Trim().ToLowerInvariant() ?? string.Empty;
        return text switch
        {
            "double" or "float" or "decimal" or "number" => "double",
            "int" or "integer" or "long" => "int",
            "bool" or "boolean" => "bool",
            "enum" => "enum",
            "visionresult" or "vision-result" or "vision_result" => "visionResult",
            "image" or "picture" => "image",
            "point" => "point",
            "pose" => "pose",
            "line" => "line",
            "circle" => "circle",
            "roi" or "region" => "roi",
            "json" => "json",
            _ => "string"
        };
    }

    private IEnumerable<string> ResolveLiveValueKeys()
    {
        return new[]
            {
                SourceBindingKey,
                Key,
                Name,
                Target
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private void ApplyDataTypeDefaults(string previousDataType)
    {
        DefaultValue = CoerceValueForDataType(DefaultValue, DataType, previousDataType);
        CurrentValue = CoerceValueForDataType(CurrentValue, DataType, previousDataType);
    }

    private static string CoerceValueForDataType(string? value, string dataType, string previousDataType)
    {
        var text = value?.Trim() ?? string.Empty;
        return NormalizeDataType(dataType) switch
        {
            "double" => double.TryParse(text, out _) ? text : "0",
            "int" => int.TryParse(text, out _) ? text : "0",
            "bool" => TryNormalizeBool(text, out var boolText) ? boolText : "False",
            "point" => string.IsNullOrWhiteSpace(text) ? "0,0" : text,
            "pose" => string.IsNullOrWhiteSpace(text) ? "0,0,0" : text,
            "json" => string.IsNullOrWhiteSpace(text) ? "{}" : text,
            "enum" => ShouldClearTextDefault(previousDataType, text) ? string.Empty : text,
            "string" => ShouldClearTextDefault(previousDataType, text) ? string.Empty : text,
            _ => text
        };
    }

    private static bool ShouldClearTextDefault(string previousDataType, string text)
    {
        return previousDataType is "double" or "int" or "bool" &&
               (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "False", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryNormalizeBool(string text, out string value)
    {
        if (bool.TryParse(text, out var boolValue))
        {
            value = boolValue ? "True" : "False";
            return true;
        }

        if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "on", StringComparison.OrdinalIgnoreCase))
        {
            value = "True";
            return true;
        }

        if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
        {
            value = "False";
            return true;
        }

        value = "False";
        return false;
    }

    private static string ParseSourceType(string? source)
    {
        var text = source?.Trim() ?? string.Empty;
        if (text.StartsWith("TCP:", StringComparison.OrdinalIgnoreCase))
        {
            return "TCP";
        }

        if (text.StartsWith("Serial:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("串口:", StringComparison.OrdinalIgnoreCase))
        {
            return "串口";
        }

        if (text.StartsWith("PLC:", StringComparison.OrdinalIgnoreCase))
        {
            return "PLC";
        }

        if (text.StartsWith("IO:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("轴卡IO:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("轴卡 IO:", StringComparison.OrdinalIgnoreCase))
        {
            return "轴卡 IO";
        }

        if (text.StartsWith("Expression:", StringComparison.OrdinalIgnoreCase))
        {
            return "表达式";
        }

        if (text.StartsWith("RuntimeValues", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("运行值:", StringComparison.OrdinalIgnoreCase))
        {
            return "运行值";
        }

        return "手动";
    }

    private static string ParseSourceBindingKey(string? source)
    {
        var text = source?.Trim() ?? string.Empty;
        var separatorIndex = text.IndexOf(':');
        if (separatorIndex < 0 || separatorIndex >= text.Length - 1)
        {
            return IsPlainSourceBinding(text) ? text : string.Empty;
        }

        return text[(separatorIndex + 1)..].Trim();
    }

    private static bool IsPlainSourceBinding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains('\u624b') ||
            text.Contains("Manual", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("External", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("RuntimeValues", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string BuildSource(string? sourceType, string? bindingKey)
    {
        var source = string.IsNullOrWhiteSpace(sourceType) ? "手动" : sourceType.Trim();
        var key = bindingKey?.Trim() ?? string.Empty;
        if (IsManualSourceType(source))
        {
            return "手动输入";
        }

        if (IsTcpSourceType(source))
        {
            return $"TCP:{(string.IsNullOrWhiteSpace(key) ? "tcp-main" : key)}";
        }

        if (IsSerialSourceType(source))
        {
            return $"Serial:{(string.IsNullOrWhiteSpace(key) ? "serial-main" : key)}";
        }

        if (IsPlcSourceType(source))
        {
            return $"PLC:{key}";
        }

        if (IsIoSourceType(source))
        {
            return $"IO:{key}";
        }

        if (IsRuntimeSourceType(source))
        {
            return string.IsNullOrWhiteSpace(key) ? "RuntimeValues" : $"RuntimeValues:{key}";
        }

        if (IsExpressionSourceType(source))
        {
            return $"Expression:{key}";
        }

        return source switch
        {
            "TCP" => $"TCP:{(string.IsNullOrWhiteSpace(key) ? "tcp-main" : key)}",
            "串口" => $"串口:{(string.IsNullOrWhiteSpace(key) ? "serial-main" : key)}",
            "PLC" => $"PLC:{key}",
            "轴卡 IO" => $"IO:{key}",
            "运行值" => string.IsNullOrWhiteSpace(key) ? "RuntimeValues" : $"运行值:{key}",
            "表达式" => $"Expression:{key}",
            _ => string.IsNullOrWhiteSpace(key) ? "手动输入" : key
        };
    }

    private void RefreshSourceBindingOptions()
    {
        var current = SourceBindingKey;
        var sourceType = SourceType;
        if (!RequiresSourceBinding(sourceType))
        {
            SourceBindingOptions = Array.Empty<string>();
            return;
        }

        var dynamicOptions = ResolveSourceBindingOptions(sourceType);
        SourceBindingOptions = NormalizeSourceOptions(dynamicOptions.Prepend(current), string.Empty);
        if (string.IsNullOrWhiteSpace(current) &&
            SourceBindingOptions.Count > 0 &&
            RequiresSourceBinding(sourceType))
        {
            SourceBindingKey = SourceBindingOptions[0];
        }

        if (RequiresSourceBinding(sourceType))
        {
            return;
        }

        var options = SourceType switch
        {
            "TCP" => _tcpSourceBindingOptions,
            "串口" => _serialSourceBindingOptions,
            "PLC" => ["D100", "M100", "DB1.DBW0"],
            "轴卡 IO" => ["X0", "DI0", "Input0"],
            "运行值" => ["LastSignalValue", "OverallResult"],
            _ => Array.Empty<string>()
        };

        SourceBindingOptions = NormalizeSourceOptions(options.Prepend(current), string.Empty);
        if (string.IsNullOrWhiteSpace(current) &&
            SourceBindingOptions.Count > 0 &&
            SourceType is "TCP" or "涓插彛" or "PLC" or "杞村崱 IO" or "杩愯鍊?")
        {
            SourceBindingKey = SourceBindingOptions[0];
        }
    }

    private string ResolveSourceBindingDefault(string? sourceType, string current)
    {
        if (!string.IsNullOrWhiteSpace(current))
        {
            return current;
        }

        var type = sourceType?.Trim() ?? string.Empty;
        if (IsTcpSourceType(type))
        {
            return _tcpSourceBindingOptions.FirstOrDefault() ?? "tcp-main";
        }

        if (IsSerialSourceType(type))
        {
            return _serialSourceBindingOptions.FirstOrDefault() ?? "serial-main";
        }

        if (IsPlcSourceType(type))
        {
            return _plcSourceBindingOptions.FirstOrDefault() ?? "D100";
        }

        if (IsIoSourceType(type))
        {
            return _ioSourceBindingOptions.FirstOrDefault() ?? "X0";
        }

        if (IsRuntimeSourceType(type))
        {
            return _runtimeSourceBindingOptions.FirstOrDefault() ?? "OverallResult";
        }

        if (IsExpressionSourceType(type))
        {
            return Expression;
        }

        if (RequiresSourceBinding(type) || IsExpressionSourceType(type))
        {
            return string.Empty;
        }

        if (string.Equals(type, "TCP", StringComparison.OrdinalIgnoreCase))
        {
            return _tcpSourceBindingOptions.FirstOrDefault() ?? "tcp-main";
        }

        if (type.Contains('\u4e32') || type.Contains("Serial", StringComparison.OrdinalIgnoreCase))
        {
            return _serialSourceBindingOptions.FirstOrDefault() ?? "serial-main";
        }

        if (string.Equals(type, "PLC", StringComparison.OrdinalIgnoreCase))
        {
            return "D100";
        }

        if (type.Contains("IO", StringComparison.OrdinalIgnoreCase) || type.Contains('\u8f74'))
        {
            return "X0";
        }

        if (type.Contains("Runtime", StringComparison.OrdinalIgnoreCase) || type.Contains('\u8fd0'))
        {
            return "LastSignalValue";
        }

        return string.Empty;
        /*
        return type switch
        {
            "TCP" => _tcpSourceBindingOptions.FirstOrDefault() ?? "tcp-main",
            "涓插彛" => _serialSourceBindingOptions.FirstOrDefault() ?? "serial-main",
            "PLC" => "D100",
            "杞村崱 IO" => "X0",
            "杩愯鍊? => "LastSignalValue",
            _ => string.Empty
        };*/
    }

    private IReadOnlyList<string> ResolveSourceBindingOptions(string sourceType)
    {
        if (IsTcpSourceType(sourceType))
        {
            return _tcpSourceBindingOptions;
        }

        if (IsSerialSourceType(sourceType))
        {
            return _serialSourceBindingOptions;
        }

        if (IsPlcSourceType(sourceType))
        {
            return _plcSourceBindingOptions;
        }

        if (IsIoSourceType(sourceType))
        {
            return _ioSourceBindingOptions;
        }

        if (IsRuntimeSourceType(sourceType))
        {
            return _runtimeSourceBindingOptions;
        }

        return Array.Empty<string>();
    }

    private static bool RequiresSourceBinding(string sourceType)
    {
        return IsTcpSourceType(sourceType) ||
               IsSerialSourceType(sourceType) ||
               IsPlcSourceType(sourceType) ||
               IsIoSourceType(sourceType) ||
               IsRuntimeSourceType(sourceType);
    }

    private bool UsesLiveValueSource()
    {
        var sourceType = SourceType;
        return RequiresSourceBinding(sourceType) || IsExpressionSourceType(sourceType);
    }

    private static bool IsManualSourceType(string sourceType)
    {
        return string.IsNullOrWhiteSpace(sourceType) ||
               sourceType.Contains("Manual", StringComparison.OrdinalIgnoreCase) ||
               sourceType.Contains('\u624b');
    }

    private static bool IsTcpSourceType(string sourceType)
    {
        return string.Equals(sourceType, "TCP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSerialSourceType(string sourceType)
    {
        return sourceType.Contains("Serial", StringComparison.OrdinalIgnoreCase) ||
               sourceType.Contains('\u4e32') ||
               sourceType.Contains("涓插彛", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlcSourceType(string sourceType)
    {
        return string.Equals(sourceType, "PLC", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIoSourceType(string sourceType)
    {
        return sourceType.Contains("IO", StringComparison.OrdinalIgnoreCase) ||
               sourceType.Contains('\u8f74') ||
               sourceType.Contains("杞村崱", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntimeSourceType(string sourceType)
    {
        return sourceType.Contains("Runtime", StringComparison.OrdinalIgnoreCase) ||
               sourceType.Contains('\u8fd0') ||
               sourceType.Contains("杩愯", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpressionSourceType(string sourceType)
    {
        return sourceType.Contains("Expression", StringComparison.OrdinalIgnoreCase) ||
               sourceType.Contains('\u8868') ||
               sourceType.Contains("琛ㄨ", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> NormalizeSourceOptions(IEnumerable<string> values, string fallback)
    {
        var options = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (options.Length > 0)
        {
            return options;
        }

        return string.IsNullOrWhiteSpace(fallback) ? Array.Empty<string>() : [fallback];
    }
}

public sealed record VariableReferenceItem(string Kind, string Name, string Source, string Target, string Brush);

public sealed record RuntimeVariableValueItem(string Kind, string Key, string Value, string UpdatedAt, string Brush);
