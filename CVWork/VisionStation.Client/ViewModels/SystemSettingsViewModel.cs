using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Client.Services;
using VisionStation.Communication;
using VisionStation.Communication.UI.ViewModels;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Client.ViewModels;

public sealed class SystemSettingsViewModel : BindableBase, IDisposable
{
    private const string UnsavedChangesKey = "system-settings";
    private static readonly HashSet<string> EditableSettingProperties = new(StringComparer.Ordinal)
    {
        nameof(MesUploadEnabled),
        nameof(MesEndpoint),
        nameof(MesLineCode),
        nameof(MesStationCode),
        nameof(MesEquipmentCode),
        nameof(MesProcessCode),
        nameof(MesProductCode),
        nameof(MesUploadMode),
        nameof(MesApiToken),
        nameof(PlcProtocol),
        nameof(PlcIpAddress),
        nameof(PlcPort),
        nameof(PlcStationNo),
        nameof(PlcConnectTimeoutMs),
        nameof(PlcHeartbeatIntervalMs),
        nameof(PlcHeartbeatAddress),
        nameof(PlcResultAddress),
        nameof(MachineName),
        nameof(InspectionTimeoutMs),
        nameof(ImageRetentionDays),
        nameof(SaveOriginalImage),
        nameof(SaveResultImage)
    };

    private readonly IDeviceConfigurationRepository _configurationRepository;
    private readonly IRecipeRepository _recipes;
    private readonly ProductionDashboardLayoutService _dashboardLayoutService;
    private readonly ICommunicationChannelRuntime _communicationChannels;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IUnsavedChangesService _unsavedChanges;
    private Recipe? _dashboardRecipe;
    private string _mesEndpoint = string.Empty;
    private string _mesLineCode = string.Empty;
    private string _mesStationCode = string.Empty;
    private string _mesEquipmentCode = string.Empty;
    private string _mesProcessCode = string.Empty;
    private string _mesProductCode = string.Empty;
    private string _mesUploadMode = string.Empty;
    private string _mesApiToken = string.Empty;
    private bool _mesUploadEnabled;
    private string _plcProtocol = string.Empty;
    private string _plcIpAddress = string.Empty;
    private string _plcPort = string.Empty;
    private string _plcStationNo = string.Empty;
    private string _plcConnectTimeoutMs = string.Empty;
    private string _plcHeartbeatIntervalMs = string.Empty;
    private string _plcHeartbeatAddress = string.Empty;
    private string _plcResultAddress = string.Empty;
    private string _machineName = string.Empty;
    private string _inspectionTimeoutMs = string.Empty;
    private string _imageRetentionDays = string.Empty;
    private bool _saveOriginalImage;
    private bool _saveResultImage;
    private string _statusText = "系统参数已载入";
    private string _dashboardLayoutStatusText = "正在加载检测窗口配置...";
    private string _tcpDebugRuntimeText = "请选择 TCP 调试通道。";
    private string _serialDebugRuntimeText = "请选择串口调试通道。";
    private SystemParameterItem? _selectedParameter;
    private DashboardDisplayPaneSettingsItem? _selectedDashboardPane;
    private TcpCommunicationChannelItem? _selectedTcpChannel;
    private SerialCommunicationChannelItem? _selectedSerialChannel;
    private TcpCommunicationChannelItem? _selectedTcpDebugChannel;
    private SerialCommunicationChannelItem? _selectedSerialDebugChannel;
    private bool _hasUnsavedChanges;
    private bool _isLoadingSettings;

    public SystemSettingsViewModel(
        DeviceConfiguration configuration,
        IDeviceConfigurationRepository configurationRepository,
        IRecipeRepository recipes,
        ProductionDashboardLayoutService dashboardLayoutService,
        IDeviceRuntime deviceRuntime,
        ICommunicationChannelRuntime communicationChannels,
        ITcpDebugSession tcpSession,
        ISerialDebugSession serialSession,
        IUiDispatcher uiDispatcher,
        IUnsavedChangesService unsavedChanges)
    {
        _configurationRepository = configurationRepository;
        _recipes = recipes;
        _dashboardLayoutService = dashboardLayoutService;
        _communicationChannels = communicationChannels;
        _uiDispatcher = uiDispatcher;
        _unsavedChanges = unsavedChanges;
        TcpDebug = new TcpDebugViewModel(tcpSession, uiDispatcher);
        FieldbusDebug = new FieldbusCommunicationDebugViewModel(
            deviceRuntime,
            serialSession,
            configuration,
            SaveCommunicationDebugConfigurationAsync,
            _ => { },
            uiDispatcher);
        FieldbusDebug.SetWorkbenchKind(DeviceDebugWorkbenchKind.Serial);
        Parameters.CollectionChanged += OnParametersCollectionChanged;
        PropertyChanged += OnSettingsPropertyChanged;

        SaveSettingsCommand = new DelegateCommand(async () => await SaveSettingsAsync());
        ReloadSettingsCommand = new DelegateCommand(async () => await ReloadSettingsAsync());
        AddParameterCommand = new DelegateCommand(AddParameter);
        RemoveParameterCommand = new DelegateCommand(RemoveSelectedParameter);
        AddTcpChannelCommand = new DelegateCommand(AddTcpChannel);
        RemoveTcpChannelCommand = new DelegateCommand(RemoveSelectedTcpChannel);
        AddSerialChannelCommand = new DelegateCommand(AddSerialChannel);
        RemoveSerialChannelCommand = new DelegateCommand(RemoveSelectedSerialChannel);
        TcpChannelDebugConnectCommand = new DelegateCommand(async () => await ConnectSelectedTcpDebugAsync());
        TcpChannelDebugDisconnectCommand = new DelegateCommand(async () => await DisconnectSelectedTcpDebugAsync());
        TcpChannelDebugSendCommand = new DelegateCommand(async () => await SendSelectedTcpDebugAsync());
        TcpChannelDebugClearCommand = new DelegateCommand(ClearSelectedTcpDebug);
        SerialChannelDebugConnectCommand = new DelegateCommand(async () => await ConnectSelectedSerialDebugAsync());
        SerialChannelDebugDisconnectCommand = new DelegateCommand(async () => await DisconnectSelectedSerialDebugAsync());
        SerialChannelDebugSendCommand = new DelegateCommand(async () => await SendSelectedSerialDebugAsync());
        SerialChannelDebugClearCommand = new DelegateCommand(ClearSelectedSerialDebug);
        AddDashboardPaneCommand = new DelegateCommand(AddDashboardPane);
        RemoveDashboardPaneCommand = new DelegateCommand(RemoveSelectedDashboardPane, CanRemoveSelectedDashboardPane);

        _communicationChannels.FrameReceived += OnCommunicationRuntimeFrameReceived;
        LoadSettings(configuration.SystemSettings);
        _ = LoadDashboardLayoutAsync();
    }

    public ObservableCollection<SystemParameterItem> Parameters { get; } = new();

    public ObservableCollection<TcpCommunicationChannelItem> TcpChannels { get; } = new();

    public ObservableCollection<SerialCommunicationChannelItem> SerialChannels { get; } = new();

    public ObservableCollection<InspectionFlowOptionItem> DashboardFlowOptions { get; } = new();

    public ObservableCollection<DashboardDisplayPaneSettingsItem> DashboardPanes { get; } = new();

    public TcpDebugViewModel TcpDebug { get; }

    public FieldbusCommunicationDebugViewModel FieldbusDebug { get; }

    public DelegateCommand SaveSettingsCommand { get; }

    public DelegateCommand ReloadSettingsCommand { get; }

    public DelegateCommand AddParameterCommand { get; }

    public DelegateCommand RemoveParameterCommand { get; }

    public DelegateCommand AddTcpChannelCommand { get; }

    public DelegateCommand RemoveTcpChannelCommand { get; }

    public DelegateCommand AddSerialChannelCommand { get; }

    public DelegateCommand RemoveSerialChannelCommand { get; }

    public DelegateCommand TcpChannelDebugConnectCommand { get; }

    public DelegateCommand TcpChannelDebugDisconnectCommand { get; }

    public DelegateCommand TcpChannelDebugSendCommand { get; }

    public DelegateCommand TcpChannelDebugClearCommand { get; }

    public DelegateCommand SerialChannelDebugConnectCommand { get; }

    public DelegateCommand SerialChannelDebugDisconnectCommand { get; }

    public DelegateCommand SerialChannelDebugSendCommand { get; }

    public DelegateCommand SerialChannelDebugClearCommand { get; }

    public DelegateCommand AddDashboardPaneCommand { get; }

    public DelegateCommand RemoveDashboardPaneCommand { get; }

    public bool MesUploadEnabled
    {
        get => _mesUploadEnabled;
        set => SetProperty(ref _mesUploadEnabled, value);
    }

    public string MesEndpoint
    {
        get => _mesEndpoint;
        set => SetProperty(ref _mesEndpoint, value);
    }

    public string MesLineCode
    {
        get => _mesLineCode;
        set => SetProperty(ref _mesLineCode, value);
    }

    public string MesStationCode
    {
        get => _mesStationCode;
        set => SetProperty(ref _mesStationCode, value);
    }

    public string MesEquipmentCode
    {
        get => _mesEquipmentCode;
        set => SetProperty(ref _mesEquipmentCode, value);
    }

    public string MesProcessCode
    {
        get => _mesProcessCode;
        set => SetProperty(ref _mesProcessCode, value);
    }

    public string MesProductCode
    {
        get => _mesProductCode;
        set => SetProperty(ref _mesProductCode, value);
    }

    public string MesUploadMode
    {
        get => _mesUploadMode;
        set => SetProperty(ref _mesUploadMode, value);
    }

    public string MesApiToken
    {
        get => _mesApiToken;
        set => SetProperty(ref _mesApiToken, value);
    }

    public string PlcProtocol
    {
        get => _plcProtocol;
        set => SetProperty(ref _plcProtocol, value);
    }

    public string PlcIpAddress
    {
        get => _plcIpAddress;
        set => SetProperty(ref _plcIpAddress, value);
    }

    public string PlcPort
    {
        get => _plcPort;
        set => SetProperty(ref _plcPort, value);
    }

    public string PlcStationNo
    {
        get => _plcStationNo;
        set => SetProperty(ref _plcStationNo, value);
    }

    public string PlcConnectTimeoutMs
    {
        get => _plcConnectTimeoutMs;
        set => SetProperty(ref _plcConnectTimeoutMs, value);
    }

    public string PlcHeartbeatIntervalMs
    {
        get => _plcHeartbeatIntervalMs;
        set => SetProperty(ref _plcHeartbeatIntervalMs, value);
    }

    public string PlcHeartbeatAddress
    {
        get => _plcHeartbeatAddress;
        set => SetProperty(ref _plcHeartbeatAddress, value);
    }

    public string PlcResultAddress
    {
        get => _plcResultAddress;
        set => SetProperty(ref _plcResultAddress, value);
    }

    public string MachineName
    {
        get => _machineName;
        set => SetProperty(ref _machineName, value);
    }

    public string InspectionTimeoutMs
    {
        get => _inspectionTimeoutMs;
        set => SetProperty(ref _inspectionTimeoutMs, value);
    }

    public string ImageRetentionDays
    {
        get => _imageRetentionDays;
        set => SetProperty(ref _imageRetentionDays, value);
    }

    public bool SaveOriginalImage
    {
        get => _saveOriginalImage;
        set => SetProperty(ref _saveOriginalImage, value);
    }

    public bool SaveResultImage
    {
        get => _saveResultImage;
        set => SetProperty(ref _saveResultImage, value);
    }

    public SystemParameterItem? SelectedParameter
    {
        get => _selectedParameter;
        set => SetProperty(ref _selectedParameter, value);
    }

    public TcpCommunicationChannelItem? SelectedTcpChannel
    {
        get => _selectedTcpChannel;
        set => SetProperty(ref _selectedTcpChannel, value);
    }

    public SerialCommunicationChannelItem? SelectedSerialChannel
    {
        get => _selectedSerialChannel;
        set => SetProperty(ref _selectedSerialChannel, value);
    }

    public TcpCommunicationChannelItem? SelectedTcpDebugChannel
    {
        get => _selectedTcpDebugChannel;
        set
        {
            if (SetProperty(ref _selectedTcpDebugChannel, value))
            {
                RaisePropertyChanged(nameof(TcpChannelDebugConnectButtonText));
                ApplyTcpDebugChannel(value);
            }
        }
    }

    public SerialCommunicationChannelItem? SelectedSerialDebugChannel
    {
        get => _selectedSerialDebugChannel;
        set
        {
            if (SetProperty(ref _selectedSerialDebugChannel, value))
            {
                RaisePropertyChanged(nameof(SerialChannelDebugConnectButtonText));
                ApplySerialDebugChannel(value);
            }
        }
    }

    public string TcpDebugRuntimeText
    {
        get => _tcpDebugRuntimeText;
        private set => SetProperty(ref _tcpDebugRuntimeText, value);
    }

    public string SerialDebugRuntimeText
    {
        get => _serialDebugRuntimeText;
        private set => SetProperty(ref _serialDebugRuntimeText, value);
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
                    "系统设置",
                    value,
                    _ => SaveSettingsAsync(),
                    "系统参数、通讯通道或检测窗口布局");
            }
        }
    }

    public string TcpChannelDebugConnectButtonText => IsRuntimeManagedPolicy(SelectedTcpDebugChannel?.ConnectionPolicy)
        ? "重连"
        : TcpDebug.TcpConnectButtonText;

    public string SerialChannelDebugConnectButtonText => IsRuntimeManagedPolicy(SelectedSerialDebugChannel?.ConnectionPolicy)
        ? "重连"
        : FieldbusDebug.SerialConnectButtonText;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DashboardLayoutStatusText
    {
        get => _dashboardLayoutStatusText;
        private set => SetProperty(ref _dashboardLayoutStatusText, value);
    }

    public DashboardDisplayPaneSettingsItem? SelectedDashboardPane
    {
        get => _selectedDashboardPane;
        set
        {
            if (SetProperty(ref _selectedDashboardPane, value))
            {
                RemoveDashboardPaneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void LoadSettings(SystemSettingsConfiguration settings)
    {
        var selectedTcpDebugKey = SelectedTcpDebugChannel?.Key;
        var selectedSerialDebugKey = SelectedSerialDebugChannel?.Key;

        _isLoadingSettings = true;
        try
        {
            MesUploadEnabled = settings.Mes.UploadEnabled;
            MesEndpoint = settings.Mes.Endpoint;
            MesLineCode = settings.Mes.LineCode;
            MesStationCode = settings.Mes.StationCode;
            MesEquipmentCode = settings.Mes.EquipmentCode;
            MesProcessCode = settings.Mes.ProcessCode;
            MesProductCode = settings.Mes.ProductCode;
            MesUploadMode = settings.Mes.UploadMode;
            MesApiToken = settings.Mes.ApiToken;

            PlcProtocol = settings.Plc.Protocol;
            PlcIpAddress = settings.Plc.IpAddress;
            PlcPort = settings.Plc.Port.ToString(CultureInfo.InvariantCulture);
            PlcStationNo = settings.Plc.StationNo.ToString(CultureInfo.InvariantCulture);
            PlcConnectTimeoutMs = settings.Plc.ConnectTimeoutMs.ToString(CultureInfo.InvariantCulture);
            PlcHeartbeatIntervalMs = settings.Plc.HeartbeatIntervalMs.ToString(CultureInfo.InvariantCulture);
            PlcHeartbeatAddress = settings.Plc.HeartbeatAddress;
            PlcResultAddress = settings.Plc.ResultAddress;

            MachineName = settings.Parameters.MachineName;
            InspectionTimeoutMs = settings.Parameters.InspectionTimeoutMs.ToString(CultureInfo.InvariantCulture);
            ImageRetentionDays = settings.Parameters.ImageRetentionDays.ToString(CultureInfo.InvariantCulture);
            SaveOriginalImage = settings.Parameters.SaveOriginalImage;
            SaveResultImage = settings.Parameters.SaveResultImage;

            foreach (var parameter in Parameters)
            {
                parameter.PropertyChanged -= OnParameterChanged;
            }

            Parameters.Clear();
            foreach (var parameter in settings.Parameters.Items)
            {
                var item = SystemParameterItem.FromDefinition(parameter);
                ConfigureParameterItem(item);
                Parameters.Add(item);
            }

            foreach (var channel in TcpChannels)
            {
                channel.PropertyChanged -= OnTcpChannelChanged;
            }

            TcpChannels.Clear();
            foreach (var channel in settings.Communication.TcpChannels)
            {
                var item = TcpCommunicationChannelItem.FromDefinition(channel);
                item.PropertyChanged += OnTcpChannelChanged;
                TcpChannels.Add(item);
            }

            foreach (var channel in SerialChannels)
            {
                channel.PropertyChanged -= OnSerialChannelChanged;
            }

            SerialChannels.Clear();
            foreach (var channel in settings.Communication.SerialChannels)
            {
                var item = SerialCommunicationChannelItem.FromDefinition(channel);
                item.PropertyChanged += OnSerialChannelChanged;
                SerialChannels.Add(item);
            }

            SelectedTcpDebugChannel = TcpChannels.FirstOrDefault(channel =>
                string.Equals(channel.Key, selectedTcpDebugKey, StringComparison.OrdinalIgnoreCase))
                ?? TcpChannels.FirstOrDefault();
            SelectedSerialDebugChannel = SerialChannels.FirstOrDefault(channel =>
                string.Equals(channel.Key, selectedSerialDebugKey, StringComparison.OrdinalIgnoreCase))
                ?? SerialChannels.FirstOrDefault();
        }
        finally
        {
            _isLoadingSettings = false;
            HasUnsavedChanges = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        var current = await _configurationRepository.GetAsync();
        var settings = current.SystemSettings with
        {
            Mes = new MesUploadSettings
            {
                UploadEnabled = MesUploadEnabled,
                Endpoint = MesEndpoint?.Trim() ?? string.Empty,
                LineCode = MesLineCode?.Trim() ?? string.Empty,
                StationCode = MesStationCode?.Trim() ?? string.Empty,
                EquipmentCode = MesEquipmentCode?.Trim() ?? string.Empty,
                ProcessCode = MesProcessCode?.Trim() ?? string.Empty,
                ProductCode = MesProductCode?.Trim() ?? string.Empty,
                UploadMode = MesUploadMode?.Trim() ?? string.Empty,
                ApiToken = MesApiToken?.Trim() ?? string.Empty
            },
            Plc = new PlcCommunicationSettings
            {
                Protocol = PlcProtocol?.Trim() ?? string.Empty,
                IpAddress = PlcIpAddress?.Trim() ?? string.Empty,
                Port = ParseInt(PlcPort, 502),
                StationNo = ParseInt(PlcStationNo, 1),
                ConnectTimeoutMs = ParseInt(PlcConnectTimeoutMs, 3000),
                HeartbeatIntervalMs = ParseInt(PlcHeartbeatIntervalMs, 1000),
                HeartbeatAddress = PlcHeartbeatAddress?.Trim() ?? string.Empty,
                ResultAddress = PlcResultAddress?.Trim() ?? string.Empty
            },
            Communication = new CommunicationChannelSettings
            {
                TcpChannels = TcpChannels.Select(channel => channel.ToDefinition()).ToArray(),
                SerialChannels = SerialChannels.Select(channel => channel.ToDefinition()).ToArray()
            },
            Parameters = new RuntimeParameterSettings
            {
                MachineName = MachineName?.Trim() ?? string.Empty,
                InspectionTimeoutMs = ParseInt(InspectionTimeoutMs, 5000),
                ImageRetentionDays = ParseInt(ImageRetentionDays, 30),
                SaveOriginalImage = SaveOriginalImage,
                SaveResultImage = SaveResultImage,
                Items = Parameters.Select(parameter => parameter.ToDefinition()).ToArray()
            }
        };

        await _configurationRepository.SaveAsync(current with { SystemSettings = settings });
        SaveDashboardLayout();
        var reloaded = await _configurationRepository.GetAsync();
        FieldbusDebug.LoadConfiguration(reloaded, DeviceDebugWorkbenchKind.Serial);
        LoadSettings(reloaded.SystemSettings);
        StatusText = "系统参数已保存。";
    }

    private async Task ReloadSettingsAsync()
    {
        var configuration = await _configurationRepository.GetAsync();
        FieldbusDebug.LoadConfiguration(configuration, DeviceDebugWorkbenchKind.Serial);
        LoadSettings(configuration.SystemSettings);
        await LoadDashboardLayoutAsync();
        StatusText = "系统参数已重新载入。";
    }

    private async Task<DeviceConfiguration> SaveCommunicationDebugConfigurationAsync(DeviceConfiguration configuration)
    {
        await _configurationRepository.SaveAsync(configuration);
        var reloaded = await _configurationRepository.GetAsync();
        LoadSettings(reloaded.SystemSettings);
        return reloaded;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && EditableSettingProperties.Contains(e.PropertyName))
        {
            MarkDirty("系统参数已修改，保存后生效。");
        }
    }

    private void ConfigureParameterItem(SystemParameterItem item)
    {
        item.PropertyChanged -= OnParameterChanged;
        item.PropertyChanged += OnParameterChanged;
    }

    private void OnParametersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<SystemParameterItem>())
            {
                item.PropertyChanged -= OnParameterChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<SystemParameterItem>())
            {
                ConfigureParameterItem(item);
            }
        }

        MarkDirty("系统参数已修改，保存后生效。");
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty("系统参数已修改，保存后生效。");
    }

    private void MarkDirty(string statusText)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        HasUnsavedChanges = true;
        StatusText = statusText;
    }

    private void AddParameter()
    {
        var nextIndex = Parameters.Count + 1;
        Parameters.Add(new SystemParameterItem
        {
            Key = CreateUniqueKey("Param", nextIndex),
            Name = $"预留参数{nextIndex}",
            Value = string.Empty,
            Unit = string.Empty,
            Description = "待接入业务参数",
            Enabled = true
        });
        StatusText = "已新增预留参数，保存后生效。";
    }

    private void RemoveSelectedParameter()
    {
        if (SelectedParameter is null)
        {
            return;
        }

        Parameters.Remove(SelectedParameter);
        SelectedParameter = null;
        StatusText = "已移除参数，保存后生效。";
    }

    private void AddTcpChannel()
    {
        var nextIndex = TcpChannels.Count + 1;
        var item = new TcpCommunicationChannelItem
        {
            Key = CreateUniqueChannelKey("tcp", nextIndex, TcpChannels.Select(channel => channel.Key)),
            Name = $"TCP通道{nextIndex}",
            Enabled = true,
            Mode = "Client",
            Host = "127.0.0.1",
            Port = "502",
            ConnectTimeoutMs = "3000",
            ReceiveTimeoutMs = "1000",
            FrameMode = "Raw",
            Delimiter = "\\r\\n",
            FixedFrameLength = "8",
            LengthPrefixBytes = "2",
            MaxFrameLength = "4096",
            AppendDelimiterOnSend = true
        };
        TcpChannels.Add(item);
        SelectedTcpChannel = item;
        item.PropertyChanged += OnTcpChannelChanged;
        SelectedTcpDebugChannel = item;
        MarkDirty("已新增 TCP 通道，保存后生效。");
        StatusText = "已新增 TCP 通道，保存后配方工具可使用。";
    }

    private void RemoveSelectedTcpChannel()
    {
        if (SelectedTcpChannel is null)
        {
            return;
        }

        SelectedTcpChannel.PropertyChanged -= OnTcpChannelChanged;
        TcpChannels.Remove(SelectedTcpChannel);
        SelectedTcpChannel = TcpChannels.FirstOrDefault();
        SelectedTcpDebugChannel = SelectedTcpDebugChannel is not null && TcpChannels.Contains(SelectedTcpDebugChannel)
            ? SelectedTcpDebugChannel
            : TcpChannels.FirstOrDefault();
        MarkDirty("已移除 TCP 通道，保存后生效。");
        StatusText = "已移除 TCP 通道，保存后生效。";
    }

    private void AddSerialChannel()
    {
        var nextIndex = SerialChannels.Count + 1;
        var item = new SerialCommunicationChannelItem
        {
            Key = CreateUniqueChannelKey("serial", nextIndex, SerialChannels.Select(channel => channel.Key)),
            Name = $"串口通道{nextIndex}",
            Enabled = true,
            PortName = "COM3",
            BaudRate = "9600",
            DataBits = "8",
            Parity = "None",
            StopBits = "One",
            ReceiveTimeoutMs = "1000",
            FrameMode = "Raw",
            Delimiter = "\\r\\n",
            FixedFrameLength = "8",
            LengthPrefixBytes = "2",
            MaxFrameLength = "4096",
            AppendDelimiterOnSend = true
        };
        SerialChannels.Add(item);
        SelectedSerialChannel = item;
        item.PropertyChanged += OnSerialChannelChanged;
        SelectedSerialDebugChannel = item;
        MarkDirty("已新增串口通道，保存后生效。");
        StatusText = "已新增串口通道，保存后配方工具可使用。";
    }

    private void RemoveSelectedSerialChannel()
    {
        if (SelectedSerialChannel is null)
        {
            return;
        }

        SelectedSerialChannel.PropertyChanged -= OnSerialChannelChanged;
        SerialChannels.Remove(SelectedSerialChannel);
        SelectedSerialChannel = SerialChannels.FirstOrDefault();
        SelectedSerialDebugChannel = SelectedSerialDebugChannel is not null && SerialChannels.Contains(SelectedSerialDebugChannel)
            ? SelectedSerialDebugChannel
            : SerialChannels.FirstOrDefault();
        MarkDirty("已移除串口通道，保存后生效。");
        StatusText = "已移除串口通道，保存后生效。";
    }

    private void OnTcpChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedTcpDebugChannel))
        {
            ApplyTcpDebugChannel(SelectedTcpDebugChannel);
        }

        MarkDirty("TCP 通道已修改，保存后生效。");
    }

    private void OnSerialChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedSerialDebugChannel))
        {
            ApplySerialDebugChannel(SelectedSerialDebugChannel);
        }

        MarkDirty("串口通道已修改，保存后生效。");
    }

    private void ApplyTcpDebugChannel(TcpCommunicationChannelItem? channel)
    {
        if (channel is null)
        {
            SetTcpDebugRuntimeStatus("未选择 TCP 调试通道。");
            return;
        }

        TcpDebug.TcpMode = string.Equals(channel.Mode, "Server", StringComparison.OrdinalIgnoreCase) ? "Server" : "Client";
        TcpDebug.TcpHost = string.IsNullOrWhiteSpace(channel.Host) ? "127.0.0.1" : channel.Host;
        TcpDebug.TcpPort = string.IsNullOrWhiteSpace(channel.Port) ? "502" : channel.Port;
        TcpDebug.TcpFrameMode = SettingsItemParse.FrameMode(channel.FrameMode);
        TcpDebug.TcpDelimiterText = string.IsNullOrWhiteSpace(channel.Delimiter) ? "\\r\\n" : channel.Delimiter;
        TcpDebug.TcpFixedFrameLength = string.IsNullOrWhiteSpace(channel.FixedFrameLength) ? "8" : channel.FixedFrameLength;
        TcpDebug.TcpLengthPrefixBytes = SettingsItemParse.LengthPrefixBytes(channel.LengthPrefixBytes).ToString(CultureInfo.InvariantCulture);
        TcpDebug.TcpLengthPrefixLittleEndian = channel.LengthPrefixLittleEndian;
        TcpDebug.TcpMaxFrameLength = string.IsNullOrWhiteSpace(channel.MaxFrameLength) ? "4096" : channel.MaxFrameLength;
        TcpDebug.TcpAppendDelimiterOnSend = channel.AppendDelimiterOnSend;
        TcpDebug.TcpPrefixPayloadOnSend = channel.PrefixPayloadOnSend;
        _ = RefreshTcpDebugRuntimeAsync(channel);
    }

    private void ApplySerialDebugChannel(SerialCommunicationChannelItem? channel)
    {
        if (channel is null)
        {
            SetSerialDebugRuntimeStatus("未选择串口调试通道。");
            return;
        }

        FieldbusDebug.CommunicationSerialPort = string.IsNullOrWhiteSpace(channel.PortName) ? "COM3" : channel.PortName;
        FieldbusDebug.CommunicationBaudRate = string.IsNullOrWhiteSpace(channel.BaudRate) ? "9600" : channel.BaudRate;
        FieldbusDebug.SerialDataBits = string.IsNullOrWhiteSpace(channel.DataBits) ? "8" : channel.DataBits;
        FieldbusDebug.SerialParity = string.IsNullOrWhiteSpace(channel.Parity) ? Parity.None.ToString() : channel.Parity;
        FieldbusDebug.SerialStopBits = string.IsNullOrWhiteSpace(channel.StopBits) ? StopBits.One.ToString() : channel.StopBits;
        FieldbusDebug.SerialFrameMode = SettingsItemParse.FrameMode(channel.FrameMode);
        FieldbusDebug.SerialDelimiterText = string.IsNullOrWhiteSpace(channel.Delimiter) ? "\\r\\n" : channel.Delimiter;
        FieldbusDebug.SerialFixedFrameLength = string.IsNullOrWhiteSpace(channel.FixedFrameLength) ? "8" : channel.FixedFrameLength;
        FieldbusDebug.SerialLengthPrefixBytes = SettingsItemParse.LengthPrefixBytes(channel.LengthPrefixBytes).ToString(CultureInfo.InvariantCulture);
        FieldbusDebug.SerialLengthPrefixLittleEndian = channel.LengthPrefixLittleEndian;
        FieldbusDebug.SerialMaxFrameLength = string.IsNullOrWhiteSpace(channel.MaxFrameLength) ? "4096" : channel.MaxFrameLength;
        FieldbusDebug.SerialAppendDelimiterOnSend = channel.AppendDelimiterOnSend;
        FieldbusDebug.SerialPrefixPayloadOnSend = channel.PrefixPayloadOnSend;
        _ = RefreshSerialDebugRuntimeAsync(channel);
    }

    private async Task ConnectSelectedTcpDebugAsync()
    {
        var channel = SelectedTcpDebugChannel;
        if (channel is null)
        {
            SetTcpDebugRuntimeStatus("未选择 TCP 调试通道。");
            return;
        }

        var definition = channel.ToDefinition();
        var policy = CommunicationChannelConnectionPolicies.Normalize(definition.ConnectionPolicy);
        if (!IsRuntimeManagedPolicy(policy))
        {
            await TcpDebug.ConnectAsync();
            return;
        }

        try
        {
            var snapshot = await _communicationChannels.ReconnectTcpAsync(definition);
            TcpDebugRuntimeText = snapshot.StatusText;
            TcpDebug.ApplyRuntimeSnapshot(snapshot);
            RaisePropertyChanged(nameof(TcpChannelDebugConnectButtonText));
        }
        catch (Exception ex)
        {
            SetTcpDebugRuntimeStatus(ex.Message);
        }
    }

    private async Task DisconnectSelectedTcpDebugAsync()
    {
        var channel = SelectedTcpDebugChannel;
        if (channel is null)
        {
            SetTcpDebugRuntimeStatus("未选择 TCP 调试通道。");
            return;
        }

        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (!IsRuntimeManagedPolicy(policy))
        {
            await TcpDebug.DisconnectAsync();
            return;
        }

        SetTcpDebugRuntimeStatus(
            $"TCP 通道 '{channel.Key}' 由运行时策略 '{policy}' 持有，调试页不会单独断开它。需要断开时请修改连接策略，或停止对应运行阶段。");
    }

    private async Task SendSelectedTcpDebugAsync()
    {
        var channel = SelectedTcpDebugChannel;
        if (channel is null)
        {
            SetTcpDebugRuntimeStatus("未选择 TCP 调试通道。");
            return;
        }

        var definition = channel.ToDefinition();
        var policy = CommunicationChannelConnectionPolicies.Normalize(definition.ConnectionPolicy);
        if (!IsRuntimeManagedPolicy(policy))
        {
            await TcpDebug.SendAsync();
            return;
        }

        try
        {
            var payload = CommunicationFrameCodec.CreatePayload(
                TcpDebug.TcpPayload,
                TcpDebug.TcpSendAsHex,
                CreateFrameOptions(definition));
            if (payload.Length == 0)
            {
                throw new InvalidOperationException("发送内容不能为空。");
            }

            await _communicationChannels.ConnectAsync(policy);
            var sent = await _communicationChannels.TrySendTcpAsync(
                definition,
                payload);
            if (!sent)
            {
                SetTcpDebugRuntimeStatus(
                    $"TCP 通道 '{definition.Key}' 当前没有被运行时策略 '{policy}' 连接。请先保存设置，并确认对应策略已经启动。");
                return;
            }

            TcpDebug.AppendExternalLog("TX(运行时)", payload);
            TcpDebug.SetExternalStatus($"运行时 TCP 已发送：TX {payload.Length} bytes。");
            await RefreshTcpDebugRuntimeAsync(channel);
        }
        catch (Exception ex)
        {
            SetTcpDebugRuntimeStatus(ex.Message);
        }
    }

    private void ClearSelectedTcpDebug()
    {
        TcpDebug.ClearExternalLog();
    }

    private async Task ConnectSelectedSerialDebugAsync()
    {
        var channel = SelectedSerialDebugChannel;
        if (channel is null)
        {
            SetSerialDebugRuntimeStatus("未选择串口调试通道。");
            return;
        }

        var definition = channel.ToDefinition();
        var policy = CommunicationChannelConnectionPolicies.Normalize(definition.ConnectionPolicy);
        if (!IsRuntimeManagedPolicy(policy))
        {
            await FieldbusDebug.ConnectSerialAsync();
            return;
        }

        try
        {
            var snapshot = await _communicationChannels.ReconnectSerialAsync(definition);
            SerialDebugRuntimeText = snapshot.StatusText;
            FieldbusDebug.ApplySerialRuntimeSnapshot(snapshot);
            RaisePropertyChanged(nameof(SerialChannelDebugConnectButtonText));
        }
        catch (Exception ex)
        {
            SetSerialDebugRuntimeStatus(ex.Message);
        }
    }

    private async Task DisconnectSelectedSerialDebugAsync()
    {
        var channel = SelectedSerialDebugChannel;
        if (channel is null)
        {
            SetSerialDebugRuntimeStatus("未选择串口调试通道。");
            return;
        }

        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (!IsRuntimeManagedPolicy(policy))
        {
            await FieldbusDebug.DisconnectSerialAsync();
            return;
        }

        SetSerialDebugRuntimeStatus(
            $"串口通道 '{channel.Key}' 由运行时策略 '{policy}' 持有，调试页不会单独关闭它。需要断开时请修改连接策略，或停止对应运行阶段。");
    }

    private async Task SendSelectedSerialDebugAsync()
    {
        var channel = SelectedSerialDebugChannel;
        if (channel is null)
        {
            SetSerialDebugRuntimeStatus("未选择串口调试通道。");
            return;
        }

        var definition = channel.ToDefinition();
        var policy = CommunicationChannelConnectionPolicies.Normalize(definition.ConnectionPolicy);
        if (!IsRuntimeManagedPolicy(policy))
        {
            await FieldbusDebug.SendSerialAsync();
            return;
        }

        try
        {
            var payload = CommunicationFrameCodec.CreatePayload(
                FieldbusDebug.SerialPayload,
                FieldbusDebug.SerialSendAsHex,
                CreateFrameOptions(definition));
            if (payload.Length == 0)
            {
                throw new InvalidOperationException("发送内容不能为空。");
            }

            await _communicationChannels.ConnectAsync(policy);
            var sent = await _communicationChannels.TrySendSerialAsync(
                definition,
                payload);
            if (!sent)
            {
                SetSerialDebugRuntimeStatus(
                    $"串口通道 '{definition.Key}' 当前没有被运行时策略 '{policy}' 连接。请先保存设置，并确认对应策略已经启动。");
                return;
            }

            FieldbusDebug.AppendExternalSerialLog("TX(运行时)", payload);
            FieldbusDebug.SetExternalSerialStatus($"运行时串口已发送：TX {payload.Length} bytes。");
            await RefreshSerialDebugRuntimeAsync(channel);
        }
        catch (Exception ex)
        {
            SetSerialDebugRuntimeStatus(ex.Message);
        }
    }

    private void ClearSelectedSerialDebug()
    {
        FieldbusDebug.ClearExternalSerialLog();
    }

    private void OnCommunicationRuntimeFrameReceived(object? sender, CommunicationChannelRuntimeFrame frame)
    {
        _uiDispatcher.Invoke(() =>
        {
            if (string.Equals(frame.Kind, "TCP", StringComparison.OrdinalIgnoreCase) &&
                SelectedTcpDebugChannel is not null &&
                string.Equals(SelectedTcpDebugChannel.Key, frame.Key, StringComparison.OrdinalIgnoreCase))
            {
                TcpDebug.AppendExternalLog($"RX(运行时/{frame.Label})", frame.Payload);
                TcpDebug.SetExternalStatus($"运行时 TCP 已接收：{frame.Payload.Length} bytes / 累计 {frame.TotalFrames} 帧。");
                return;
            }

            if (string.Equals(frame.Kind, "Serial", StringComparison.OrdinalIgnoreCase) &&
                SelectedSerialDebugChannel is not null &&
                string.Equals(SelectedSerialDebugChannel.Key, frame.Key, StringComparison.OrdinalIgnoreCase))
            {
                FieldbusDebug.AppendExternalSerialLog($"RX(运行时/{frame.Label})", frame.Payload);
                FieldbusDebug.SetExternalSerialStatus($"运行时串口已接收：{frame.Payload.Length} bytes / 累计 {frame.TotalFrames} 帧。");
            }
        });
    }

    private async Task RefreshTcpDebugRuntimeAsync(TcpCommunicationChannelItem? expectedChannel = null)
    {
        var channel = expectedChannel ?? SelectedTcpDebugChannel;
        if (channel is null)
        {
            SetTcpDebugRuntimeStatus("未选择 TCP 调试通道。");
            return;
        }

        try
        {
            var snapshot = await _communicationChannels.GetTcpSnapshotAsync(channel.ToDefinition());
            if (expectedChannel is not null && !ReferenceEquals(expectedChannel, SelectedTcpDebugChannel))
            {
                return;
            }

            TcpDebugRuntimeText = snapshot.StatusText;
            TcpDebug.ApplyRuntimeSnapshot(snapshot);
            RaisePropertyChanged(nameof(TcpChannelDebugConnectButtonText));
        }
        catch (Exception ex)
        {
            SetTcpDebugRuntimeStatus(ex.Message);
        }
    }

    private async Task RefreshSerialDebugRuntimeAsync(SerialCommunicationChannelItem? expectedChannel = null)
    {
        var channel = expectedChannel ?? SelectedSerialDebugChannel;
        if (channel is null)
        {
            SetSerialDebugRuntimeStatus("未选择串口调试通道。");
            return;
        }

        try
        {
            var snapshot = await _communicationChannels.GetSerialSnapshotAsync(channel.ToDefinition());
            if (expectedChannel is not null && !ReferenceEquals(expectedChannel, SelectedSerialDebugChannel))
            {
                return;
            }

            SerialDebugRuntimeText = snapshot.StatusText;
            FieldbusDebug.ApplySerialRuntimeSnapshot(snapshot);
            RaisePropertyChanged(nameof(SerialChannelDebugConnectButtonText));
        }
        catch (Exception ex)
        {
            SetSerialDebugRuntimeStatus(ex.Message);
        }
    }

    private void SetTcpDebugRuntimeStatus(string statusText)
    {
        TcpDebugRuntimeText = statusText;
        TcpDebug.SetExternalStatus(statusText);
    }

    private void SetSerialDebugRuntimeStatus(string statusText)
    {
        SerialDebugRuntimeText = statusText;
        FieldbusDebug.SetExternalSerialStatus(statusText);
    }

    private static bool IsRuntimeManagedPolicy(string? connectionPolicy)
    {
        return CommunicationChannelConnectionPolicies.Normalize(connectionPolicy) != CommunicationChannelConnectionPolicies.OnDemand;
    }

    private static CommunicationFrameOptions CreateFrameOptions(TcpCommunicationChannelSettings channel)
    {
        return new CommunicationFrameOptions
        {
            FrameMode = channel.FrameMode,
            Delimiter = channel.Delimiter,
            FixedFrameLength = channel.FixedFrameLength,
            LengthPrefixBytes = channel.LengthPrefixBytes,
            LengthPrefixLittleEndian = channel.LengthPrefixLittleEndian,
            MaxFrameLength = channel.MaxFrameLength,
            AppendDelimiterOnSend = channel.AppendDelimiterOnSend,
            PrefixPayloadOnSend = channel.PrefixPayloadOnSend
        };
    }

    private static CommunicationFrameOptions CreateFrameOptions(SerialCommunicationChannelSettings channel)
    {
        return new CommunicationFrameOptions
        {
            FrameMode = channel.FrameMode,
            Delimiter = channel.Delimiter,
            FixedFrameLength = channel.FixedFrameLength,
            LengthPrefixBytes = channel.LengthPrefixBytes,
            LengthPrefixLittleEndian = channel.LengthPrefixLittleEndian,
            MaxFrameLength = channel.MaxFrameLength,
            AppendDelimiterOnSend = channel.AppendDelimiterOnSend,
            PrefixPayloadOnSend = channel.PrefixPayloadOnSend
        };
    }

    private string CreateUniqueKey(string prefix, int start)
    {
        var existing = Parameters.Select(parameter => parameter.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    private static string CreateUniqueChannelKey(string prefix, int start, IEnumerable<string> existingKeys)
    {
        var existing = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = Math.Max(start, 1);
        string key;
        do
        {
            key = $"{prefix}-{index}";
            index++;
        }
        while (existing.Contains(key));

        return key;
    }

    private async Task LoadDashboardLayoutAsync()
    {
        var recipe = (await _recipes.GetCurrentAsync()).WithNormalizedFlows();
        _dashboardRecipe = recipe;

        DashboardFlowOptions.Clear();
        foreach (var flow in recipe.EffectiveFlows)
        {
            DashboardFlowOptions.Add(new InspectionFlowOptionItem(flow.Id, flow.Name));
        }

        foreach (var pane in DashboardPanes)
        {
            pane.PropertyChanged -= OnDashboardPaneChanged;
        }

        DashboardPanes.Clear();
        var layout = _dashboardLayoutService.LoadRecipeLayout(recipe.Id);
        var panes = layout.Panes.Count > 0
            ? layout.Panes
            : [new ProductionDashboardPaneLayout { FlowId = recipe.CurrentFlowId }];

        foreach (var pane in panes)
        {
            AddDashboardPaneItem(ResolveDashboardFlowOption(pane.FlowId) ?? ResolveDefaultDashboardFlowOption());
        }

        if (DashboardPanes.Count == 0)
        {
            AddDashboardPaneItem(ResolveDefaultDashboardFlowOption());
        }

        RefreshDashboardPaneTitles();
        DashboardLayoutStatusText = $"{recipe.Name} / {DashboardPanes.Count} 个窗口";
        RemoveDashboardPaneCommand.RaiseCanExecuteChanged();
    }

    private void AddDashboardPane()
    {
        AddDashboardPaneItem(ResolveDefaultDashboardFlowOption());
        RefreshDashboardPaneTitles();
        SelectedDashboardPane = DashboardPanes.LastOrDefault();
        MarkDirty("检测窗口配置已修改，保存设置后生效。");
        DashboardLayoutStatusText = "已新增检测窗口，保存设置后生效。";
        RemoveDashboardPaneCommand.RaiseCanExecuteChanged();
    }

    private void AddDashboardPaneItem(InspectionFlowOptionItem? selectedFlow)
    {
        var pane = new DashboardDisplayPaneSettingsItem($"窗口 {DashboardPanes.Count + 1}", selectedFlow);
        pane.PropertyChanged += OnDashboardPaneChanged;
        DashboardPanes.Add(pane);
    }

    private bool CanRemoveSelectedDashboardPane()
    {
        return SelectedDashboardPane is not null && DashboardPanes.Count > 1;
    }

    private void RemoveSelectedDashboardPane()
    {
        if (SelectedDashboardPane is null || DashboardPanes.Count <= 1)
        {
            return;
        }

        SelectedDashboardPane.PropertyChanged -= OnDashboardPaneChanged;
        DashboardPanes.Remove(SelectedDashboardPane);
        SelectedDashboardPane = DashboardPanes.FirstOrDefault();
        RefreshDashboardPaneTitles();
        MarkDirty("检测窗口配置已修改，保存设置后生效。");
        DashboardLayoutStatusText = "已移除检测窗口，保存设置后生效。";
        RemoveDashboardPaneCommand.RaiseCanExecuteChanged();
    }

    private void OnDashboardPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardDisplayPaneSettingsItem.SelectedFlow))
        {
            MarkDirty("检测窗口配置已修改，保存设置后生效。");
            DashboardLayoutStatusText = "检测窗口配置已修改，保存设置后生效。";
        }
    }

    private void SaveDashboardLayout()
    {
        if (_dashboardRecipe is null)
        {
            return;
        }

        var layout = new ProductionDashboardRecipeLayout
        {
            Panes = DashboardPanes
                .Select(pane => new ProductionDashboardPaneLayout { FlowId = pane.BoundFlowId })
                .ToList()
        };

        _dashboardLayoutService.SaveRecipeLayout(_dashboardRecipe.Id, layout);
        DashboardLayoutStatusText = $"{_dashboardRecipe.Name} / {DashboardPanes.Count} 个窗口，已保存";
    }

    private InspectionFlowOptionItem? ResolveDefaultDashboardFlowOption()
    {
        return DashboardFlowOptions.FirstOrDefault(option => DashboardPanes.All(pane =>
                   !string.Equals(pane.BoundFlowId, option.FlowId, StringComparison.OrdinalIgnoreCase)))
               ?? DashboardFlowOptions.FirstOrDefault();
    }

    private InspectionFlowOptionItem? ResolveDashboardFlowOption(string? flowId)
    {
        return DashboardFlowOptions.FirstOrDefault(option =>
            string.Equals(option.FlowId, flowId, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshDashboardPaneTitles()
    {
        for (var index = 0; index < DashboardPanes.Count; index++)
        {
            DashboardPanes[index].Title = $"窗口 {index + 1}";
        }
    }

    private static int ParseInt(string? text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               int.TryParse(text, out value)
            ? value
            : fallback;
    }

    public void Dispose()
    {
        _communicationChannels.FrameReceived -= OnCommunicationRuntimeFrameReceived;
        TcpDebug.Dispose();
        FieldbusDebug.Dispose();
    }
}

public sealed class DashboardDisplayPaneSettingsItem : BindableBase
{
    private string _title;
    private InspectionFlowOptionItem? _selectedFlow;

    public DashboardDisplayPaneSettingsItem(string title, InspectionFlowOptionItem? selectedFlow)
    {
        _title = title;
        _selectedFlow = selectedFlow;
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public InspectionFlowOptionItem? SelectedFlow
    {
        get => _selectedFlow;
        set
        {
            if (SetProperty(ref _selectedFlow, value))
            {
                RaisePropertyChanged(nameof(BoundFlowId));
                RaisePropertyChanged(nameof(FlowName));
            }
        }
    }

    public string BoundFlowId => SelectedFlow?.FlowId ?? string.Empty;

    public string FlowName => SelectedFlow?.FlowName ?? "未绑定流程";
}

public sealed class TcpCommunicationChannelItem : BindableBase
{
    private static readonly IReadOnlyList<string> ModeOptionValues = ["Client", "Server"];
    private static readonly IReadOnlyList<string> FrameModeOptionValues = ["Raw", "Delimiter", "FixedLength", "LengthPrefix"];
    private static readonly IReadOnlyList<string> DelimiterOptionValues = [@"\r\n", @"\n", @"\r", ";", ","];
    private static readonly IReadOnlyList<string> LengthPrefixByteOptionValues = ["1", "2", "4"];
    private static readonly Lazy<IReadOnlyList<string>> HostOptionValues = new(BuildHostOptions);
    private static readonly IReadOnlyList<string> ConnectionPolicyOptionValues =
    [
        ConnectionPolicyDisplay.OnDemandText,
        ConnectionPolicyDisplay.ProductionText,
        ConnectionPolicyDisplay.StartupText
    ];
    private string _key = "tcp-main";
    private string _name = "TCP 通道";
    private bool _enabled = true;
    private string _connectionPolicy = CommunicationChannelConnectionPolicies.OnDemand;
    private string _mode = "Client";
    private string _host = "127.0.0.1";
    private string _port = "502";
    private string _connectTimeoutMs = "3000";
    private string _receiveTimeoutMs = "1000";
    private string _frameMode = "Raw";
    private string _delimiter = "\\r\\n";
    private string _fixedFrameLength = "8";
    private string _lengthPrefixBytes = "2";
    private bool _lengthPrefixLittleEndian;
    private string _maxFrameLength = "4096";
    private bool _appendDelimiterOnSend = true;
    private bool _prefixPayloadOnSend;
    private string _description = string.Empty;

    public string Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value ?? string.Empty))
            {
                RaisePropertyChanged(nameof(DebugDisplayText));
            }
        }
    }
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                RaisePropertyChanged(nameof(DebugDisplayText));
            }
        }
    }
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }
    public string ConnectionPolicy
    {
        get => _connectionPolicy;
        set
        {
            if (SetProperty(ref _connectionPolicy, CommunicationChannelConnectionPolicies.Normalize(value)))
            {
                RaisePropertyChanged(nameof(ConnectionPolicyText));
            }
        }
    }
    public string ConnectionPolicyText
    {
        get => ConnectionPolicyDisplay.ToText(ConnectionPolicy);
        set => ConnectionPolicy = ConnectionPolicyDisplay.ToValue(value);
    }
    public string Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, string.IsNullOrWhiteSpace(value) ? "Client" : value.Trim()))
            {
                RaisePropertyChanged(nameof(HostUsageText));
            }
        }
    }
    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value ?? string.Empty);
    }
    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value ?? string.Empty);
    }
    public string ConnectTimeoutMs
    {
        get => _connectTimeoutMs;
        set => SetProperty(ref _connectTimeoutMs, value ?? string.Empty);
    }
    public string ReceiveTimeoutMs
    {
        get => _receiveTimeoutMs;
        set => SetProperty(ref _receiveTimeoutMs, value ?? string.Empty);
    }
    public string FrameMode
    {
        get => _frameMode;
        set => SetProperty(ref _frameMode, value ?? string.Empty);
    }
    public string Delimiter
    {
        get => _delimiter;
        set => SetProperty(ref _delimiter, value ?? string.Empty);
    }
    public string FixedFrameLength
    {
        get => _fixedFrameLength;
        set => SetProperty(ref _fixedFrameLength, value ?? string.Empty);
    }
    public string LengthPrefixBytes
    {
        get => _lengthPrefixBytes;
        set => SetProperty(ref _lengthPrefixBytes, value ?? string.Empty);
    }
    public bool LengthPrefixLittleEndian
    {
        get => _lengthPrefixLittleEndian;
        set => SetProperty(ref _lengthPrefixLittleEndian, value);
    }
    public string MaxFrameLength
    {
        get => _maxFrameLength;
        set => SetProperty(ref _maxFrameLength, value ?? string.Empty);
    }
    public bool AppendDelimiterOnSend
    {
        get => _appendDelimiterOnSend;
        set => SetProperty(ref _appendDelimiterOnSend, value);
    }
    public bool PrefixPayloadOnSend
    {
        get => _prefixPayloadOnSend;
        set => SetProperty(ref _prefixPayloadOnSend, value);
    }
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value ?? string.Empty);
    }

    public IReadOnlyList<string> ModeOptions => ModeOptionValues;

    public string DebugDisplayText => string.IsNullOrWhiteSpace(Name) ? Key : $"{Key} / {Name}";

    public override string ToString()
    {
        return DebugDisplayText;
    }

    public IReadOnlyList<string> ConnectionPolicyOptions => ConnectionPolicyOptionValues;

    public IReadOnlyList<string> HostOptions => HostOptionValues.Value;

    public IReadOnlyList<string> FrameModeOptions => FrameModeOptionValues;

    public IReadOnlyList<string> DelimiterOptions => DelimiterOptionValues;

    public IReadOnlyList<string> LengthPrefixByteOptions => LengthPrefixByteOptionValues;

    public string HostUsageText => string.Equals(Mode, "Server", StringComparison.OrdinalIgnoreCase)
        ? "服务端监听本机地址，常用 0.0.0.0 表示监听所有网卡。"
        : "客户端填写目标服务端地址。";

    public static TcpCommunicationChannelItem FromDefinition(TcpCommunicationChannelSettings definition)
    {
        return new TcpCommunicationChannelItem
        {
            Key = definition.Key,
            Name = definition.Name,
            Enabled = definition.Enabled,
            ConnectionPolicy = CommunicationChannelConnectionPolicies.Normalize(definition.ConnectionPolicy),
            Mode = definition.Mode,
            Host = definition.Host,
            Port = definition.Port.ToString(CultureInfo.InvariantCulture),
            ConnectTimeoutMs = definition.ConnectTimeoutMs.ToString(CultureInfo.InvariantCulture),
            ReceiveTimeoutMs = definition.ReceiveTimeoutMs.ToString(CultureInfo.InvariantCulture),
            FrameMode = definition.FrameMode,
            Delimiter = definition.Delimiter,
            FixedFrameLength = definition.FixedFrameLength.ToString(CultureInfo.InvariantCulture),
            LengthPrefixBytes = definition.LengthPrefixBytes.ToString(CultureInfo.InvariantCulture),
            LengthPrefixLittleEndian = definition.LengthPrefixLittleEndian,
            MaxFrameLength = definition.MaxFrameLength.ToString(CultureInfo.InvariantCulture),
            AppendDelimiterOnSend = definition.AppendDelimiterOnSend,
            PrefixPayloadOnSend = definition.PrefixPayloadOnSend,
            Description = definition.Description
        };
    }

    public TcpCommunicationChannelSettings ToDefinition()
    {
        var mode = string.Equals(Mode, "Server", StringComparison.OrdinalIgnoreCase) ? "Server" : "Client";
        var host = Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            host = string.Equals(mode, "Server", StringComparison.OrdinalIgnoreCase) ? "0.0.0.0" : "127.0.0.1";
        }

        return new TcpCommunicationChannelSettings
        {
            Key = string.IsNullOrWhiteSpace(Key) ? Guid.NewGuid().ToString("N") : Key.Trim(),
            Name = Name?.Trim() ?? string.Empty,
            Enabled = Enabled,
            ConnectionPolicy = CommunicationChannelConnectionPolicies.Normalize(ConnectionPolicy),
            Mode = mode,
            Host = host,
            Port = SettingsItemParse.Int(Port, 502),
            ConnectTimeoutMs = SettingsItemParse.Int(ConnectTimeoutMs, 3000),
            ReceiveTimeoutMs = SettingsItemParse.Int(ReceiveTimeoutMs, 1000),
            FrameMode = SettingsItemParse.FrameMode(FrameMode),
            Delimiter = Delimiter?.Trim() ?? "\\r\\n",
            FixedFrameLength = SettingsItemParse.Int(FixedFrameLength, 8),
            LengthPrefixBytes = SettingsItemParse.LengthPrefixBytes(LengthPrefixBytes),
            LengthPrefixLittleEndian = LengthPrefixLittleEndian,
            MaxFrameLength = SettingsItemParse.Int(MaxFrameLength, 4096),
            AppendDelimiterOnSend = AppendDelimiterOnSend,
            PrefixPayloadOnSend = PrefixPayloadOnSend,
            Description = Description?.Trim() ?? string.Empty
        };
    }

    private static IReadOnlyList<string> BuildHostOptions()
    {
        var options = new List<string>
        {
            "127.0.0.1",
            "0.0.0.0",
            "localhost"
        };

        try
        {
            options.AddRange(Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString()));
        }
        catch
        {
            // Local address enumeration is best-effort; manual input remains available.
        }

        return options
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class SerialCommunicationChannelItem : BindableBase
{
    private static readonly IReadOnlyList<string> BaudRateOptionValues = ["9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600"];
    private static readonly IReadOnlyList<string> DataBitOptionValues = ["7", "8"];
    private static readonly IReadOnlyList<string> ParityOptionValues = ["None", "Odd", "Even", "Mark", "Space"];
    private static readonly IReadOnlyList<string> StopBitOptionValues = ["One", "OnePointFive", "Two"];
    private static readonly IReadOnlyList<string> FrameModeOptionValues = ["Raw", "Delimiter", "FixedLength", "LengthPrefix"];
    private static readonly IReadOnlyList<string> DelimiterOptionValues = [@"\r\n", @"\n", @"\r", ";", ","];
    private static readonly IReadOnlyList<string> LengthPrefixByteOptionValues = ["1", "2", "4"];
    private static readonly Lazy<IReadOnlyList<string>> PortNameOptionValues = new(BuildPortNameOptions);
    private static readonly IReadOnlyList<string> ConnectionPolicyOptionValues =
    [
        ConnectionPolicyDisplay.OnDemandText,
        ConnectionPolicyDisplay.ProductionText,
        ConnectionPolicyDisplay.StartupText
    ];
    private string _key = "serial-main";
    private string _name = "串口通道";
    private bool _enabled = true;
    private string _connectionPolicy = CommunicationChannelConnectionPolicies.OnDemand;
    private string _portName = "COM3";
    private string _baudRate = "9600";
    private string _dataBits = "8";
    private string _parity = "None";
    private string _stopBits = "One";
    private string _receiveTimeoutMs = "1000";
    private string _frameMode = "Raw";
    private string _delimiter = "\\r\\n";
    private string _fixedFrameLength = "8";
    private string _lengthPrefixBytes = "2";
    private bool _lengthPrefixLittleEndian;
    private string _maxFrameLength = "4096";
    private bool _appendDelimiterOnSend = true;
    private bool _prefixPayloadOnSend;
    private string _description = string.Empty;

    public string Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value ?? string.Empty))
            {
                RaisePropertyChanged(nameof(DebugDisplayText));
            }
        }
    }
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                RaisePropertyChanged(nameof(DebugDisplayText));
            }
        }
    }
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }
    public string ConnectionPolicy
    {
        get => _connectionPolicy;
        set
        {
            if (SetProperty(ref _connectionPolicy, CommunicationChannelConnectionPolicies.Normalize(value)))
            {
                RaisePropertyChanged(nameof(ConnectionPolicyText));
            }
        }
    }
    public string ConnectionPolicyText
    {
        get => ConnectionPolicyDisplay.ToText(ConnectionPolicy);
        set => ConnectionPolicy = ConnectionPolicyDisplay.ToValue(value);
    }
    public string PortName
    {
        get => _portName;
        set => SetProperty(ref _portName, value ?? string.Empty);
    }
    public string BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value ?? string.Empty);
    }
    public string DataBits
    {
        get => _dataBits;
        set => SetProperty(ref _dataBits, value ?? string.Empty);
    }
    public string Parity
    {
        get => _parity;
        set => SetProperty(ref _parity, value ?? string.Empty);
    }
    public string StopBits
    {
        get => _stopBits;
        set => SetProperty(ref _stopBits, value ?? string.Empty);
    }
    public string ReceiveTimeoutMs
    {
        get => _receiveTimeoutMs;
        set => SetProperty(ref _receiveTimeoutMs, value ?? string.Empty);
    }
    public string FrameMode
    {
        get => _frameMode;
        set => SetProperty(ref _frameMode, value ?? string.Empty);
    }
    public string Delimiter
    {
        get => _delimiter;
        set => SetProperty(ref _delimiter, value ?? string.Empty);
    }
    public string FixedFrameLength
    {
        get => _fixedFrameLength;
        set => SetProperty(ref _fixedFrameLength, value ?? string.Empty);
    }
    public string LengthPrefixBytes
    {
        get => _lengthPrefixBytes;
        set => SetProperty(ref _lengthPrefixBytes, value ?? string.Empty);
    }
    public bool LengthPrefixLittleEndian
    {
        get => _lengthPrefixLittleEndian;
        set => SetProperty(ref _lengthPrefixLittleEndian, value);
    }
    public string MaxFrameLength
    {
        get => _maxFrameLength;
        set => SetProperty(ref _maxFrameLength, value ?? string.Empty);
    }
    public bool AppendDelimiterOnSend
    {
        get => _appendDelimiterOnSend;
        set => SetProperty(ref _appendDelimiterOnSend, value);
    }
    public bool PrefixPayloadOnSend
    {
        get => _prefixPayloadOnSend;
        set => SetProperty(ref _prefixPayloadOnSend, value);
    }
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value ?? string.Empty);
    }

    public IReadOnlyList<string> PortNameOptions => PortNameOptionValues.Value;

    public string DebugDisplayText => string.IsNullOrWhiteSpace(Name) ? Key : $"{Key} / {Name}";

    public override string ToString()
    {
        return DebugDisplayText;
    }

    public IReadOnlyList<string> ConnectionPolicyOptions => ConnectionPolicyOptionValues;

    public IReadOnlyList<string> BaudRateOptions => BaudRateOptionValues;

    public IReadOnlyList<string> DataBitOptions => DataBitOptionValues;

    public IReadOnlyList<string> ParityOptions => ParityOptionValues;

    public IReadOnlyList<string> StopBitOptions => StopBitOptionValues;

    public IReadOnlyList<string> FrameModeOptions => FrameModeOptionValues;

    public IReadOnlyList<string> DelimiterOptions => DelimiterOptionValues;

    public IReadOnlyList<string> LengthPrefixByteOptions => LengthPrefixByteOptionValues;

    public static SerialCommunicationChannelItem FromDefinition(SerialCommunicationChannelSettings definition)
    {
        return new SerialCommunicationChannelItem
        {
            Key = definition.Key,
            Name = definition.Name,
            Enabled = definition.Enabled,
            ConnectionPolicy = CommunicationChannelConnectionPolicies.Normalize(definition.ConnectionPolicy),
            PortName = definition.PortName,
            BaudRate = definition.BaudRate.ToString(CultureInfo.InvariantCulture),
            DataBits = definition.DataBits.ToString(CultureInfo.InvariantCulture),
            Parity = definition.Parity,
            StopBits = definition.StopBits,
            ReceiveTimeoutMs = definition.ReceiveTimeoutMs.ToString(CultureInfo.InvariantCulture),
            FrameMode = definition.FrameMode,
            Delimiter = definition.Delimiter,
            FixedFrameLength = definition.FixedFrameLength.ToString(CultureInfo.InvariantCulture),
            LengthPrefixBytes = definition.LengthPrefixBytes.ToString(CultureInfo.InvariantCulture),
            LengthPrefixLittleEndian = definition.LengthPrefixLittleEndian,
            MaxFrameLength = definition.MaxFrameLength.ToString(CultureInfo.InvariantCulture),
            AppendDelimiterOnSend = definition.AppendDelimiterOnSend,
            PrefixPayloadOnSend = definition.PrefixPayloadOnSend,
            Description = definition.Description
        };
    }

    public SerialCommunicationChannelSettings ToDefinition()
    {
        return new SerialCommunicationChannelSettings
        {
            Key = string.IsNullOrWhiteSpace(Key) ? Guid.NewGuid().ToString("N") : Key.Trim(),
            Name = Name?.Trim() ?? string.Empty,
            Enabled = Enabled,
            ConnectionPolicy = CommunicationChannelConnectionPolicies.Normalize(ConnectionPolicy),
            PortName = PortName?.Trim() ?? string.Empty,
            BaudRate = SettingsItemParse.Int(BaudRate, 9600),
            DataBits = SettingsItemParse.Int(DataBits, 8),
            Parity = Parity?.Trim() ?? "None",
            StopBits = StopBits?.Trim() ?? "One",
            ReceiveTimeoutMs = SettingsItemParse.Int(ReceiveTimeoutMs, 1000),
            FrameMode = SettingsItemParse.FrameMode(FrameMode),
            Delimiter = Delimiter?.Trim() ?? "\\r\\n",
            FixedFrameLength = SettingsItemParse.Int(FixedFrameLength, 8),
            LengthPrefixBytes = SettingsItemParse.LengthPrefixBytes(LengthPrefixBytes),
            LengthPrefixLittleEndian = LengthPrefixLittleEndian,
            MaxFrameLength = SettingsItemParse.Int(MaxFrameLength, 4096),
            AppendDelimiterOnSend = AppendDelimiterOnSend,
            PrefixPayloadOnSend = PrefixPayloadOnSend,
            Description = Description?.Trim() ?? string.Empty
        };
    }

    private static IReadOnlyList<string> BuildPortNameOptions()
    {
        try
        {
            var ports = SerialPort.GetPortNames()
                .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ports.Length > 0)
            {
                return ports;
            }
        }
        catch
        {
            // Serial port enumeration can fail on machines without serial support.
        }

        return ["COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8"];
    }
}

internal static class ConnectionPolicyDisplay
{
    public const string OnDemandText = "按需连接";

    public const string ProductionText = "生产开始连接";

    public const string StartupText = "软件启动自动连接";

    public static string ToText(string? value)
    {
        return CommunicationChannelConnectionPolicies.Normalize(value) switch
        {
            CommunicationChannelConnectionPolicies.Production => ProductionText,
            CommunicationChannelConnectionPolicies.Startup => StartupText,
            _ => OnDemandText
        };
    }

    public static string ToValue(string? text)
    {
        return text?.Trim() switch
        {
            ProductionText => CommunicationChannelConnectionPolicies.Production,
            StartupText => CommunicationChannelConnectionPolicies.Startup,
            _ => CommunicationChannelConnectionPolicies.OnDemand
        };
    }
}

internal static class SettingsItemParse
{
    public static int Int(string? text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               int.TryParse(text, out value)
            ? value
            : fallback;
    }

    public static int LengthPrefixBytes(string? text)
    {
        return Int(text, 2) switch
        {
            1 => 1,
            4 => 4,
            _ => 2
        };
    }

    public static string FrameMode(string? text)
    {
        return text?.Trim() switch
        {
            "Delimiter" => "Delimiter",
            "FixedLength" => "FixedLength",
            "LengthPrefix" => "LengthPrefix",
            _ => "Raw"
        };
    }
}

public sealed class SystemParameterItem : BindableBase
{
    private string _key = "Param";
    private string _name = "预留参数";
    private string _value = string.Empty;
    private string _unit = string.Empty;
    private string _description = string.Empty;
    private bool _enabled = true;

    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
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

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public static SystemParameterItem FromDefinition(SystemParameterDefinition definition)
    {
        return new SystemParameterItem
        {
            Key = definition.Key,
            Name = definition.Name,
            Value = definition.Value,
            Unit = definition.Unit,
            Description = definition.Description,
            Enabled = definition.Enabled
        };
    }

    public SystemParameterDefinition ToDefinition()
    {
        return new SystemParameterDefinition
        {
            Key = string.IsNullOrWhiteSpace(Key) ? Guid.NewGuid().ToString("N") : Key.Trim(),
            Name = Name?.Trim() ?? string.Empty,
            Value = Value?.Trim() ?? string.Empty,
            Unit = Unit?.Trim() ?? string.Empty,
            Description = Description?.Trim() ?? string.Empty,
            Enabled = Enabled
        };
    }
}
