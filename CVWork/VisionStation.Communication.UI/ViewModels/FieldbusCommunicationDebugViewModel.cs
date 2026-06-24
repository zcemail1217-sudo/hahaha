using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Windows;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Application;
using VisionStation.Communication;
using VisionStation.Devices;
using VisionStation.Domain;

namespace VisionStation.Communication.UI.ViewModels;

public sealed class FieldbusCommunicationDebugViewModel : BindableBase, IDisposable
{
    private const string FrameRaw = "Raw";
    private const string FrameDelimiter = "Delimiter";
    private const string FrameFixedLength = "FixedLength";
    private const string FrameLengthPrefix = "LengthPrefix";
    private const int MaxLogCharacters = 60000;
    private const int MaxQueuedLogCharacters = 24000;

    private static readonly TextSelectionOption[] TargetCommunicationBrands =
    [
        new("Generic", "仿真"),
        new("Modbus", "Modbus"),
        new("Mitsubishi", "三菱"),
        new("Siemens", "西门子"),
        new("Omron", "欧姆龙"),
        new("Inovance", "汇川")
    ];

    private static readonly IReadOnlyDictionary<string, TextSelectionOption[]> TargetProtocolsByBrand =
        new Dictionary<string, TextSelectionOption[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Generic"] =
            [
                new("Simulated", "仿真")
            ],
            ["Modbus"] =
            [
                new("ModbusTcp", "Modbus TCP"),
                new("ModbusUdp", "Modbus UDP"),
                new("ModbusRtu", "Modbus RTU"),
                new("ModbusAscii", "Modbus ASCII"),
                new("ModbusRtuOverTcp", "Modbus RTU over TCP"),
                new("ModbusAsciiOverTcp", "Modbus ASCII over TCP")
            ],
            ["Mitsubishi"] =
            [
                new("MelsecCip", "EtherNet/IP(CIP)"),
                new("MelsecA1E", "A-1E (Binary)"),
                new("MelsecA1EAscii", "A-1E (ASCII)"),
                new("MelsecMc", "MC (Binary)"),
                new("MelsecMcUdp", "MC Udp(Binary)"),
                new("MelsecMcAscii", "MC (ASCII)"),
                new("MelsecMcAsciiUdp", "MC Udp(ASCII)"),
                new("MelsecMcR", "MC-R (Binary)"),
                new("MelsecFxSerial", "Fx Serial [编程口]"),
                new("MelsecFxSerialOverTcp", "Fx Serial OverTcp"),
                new("MelsecFxLinks", "Fx Links [485]"),
                new("MelsecFxLinksOverTcp", "Fx Links OverTcp"),
                new("MelsecA3C", "A-3C (串口)"),
                new("MelsecA3COverTcp", "A-3C OverTcp")
            ],
            ["Siemens"] =
            [
                new("SiemensS7S1200", "S7-S1200"),
                new("SiemensS7S1500", "S7-S1500"),
                new("SiemensS7S300", "S7-S300"),
                new("SiemensS7S400", "S7-S400"),
                new("SiemensS7200", "S7-S200"),
                new("SiemensS7200Smart", "S7-S200 smart"),
                new("SiemensFetchWrite", "Fetch/Write"),
                new("SiemensWebApi", "WebApi"),
                new("SiemensPPI", "PPI"),
                new("SiemensPPIOverTcp", "PPI OverTcp"),
                new("SiemensMPI", "MPI")
            ],
            ["Omron"] =
            [
                new("OmronFins", "Fins Tcp"),
                new("OmronFinsUdp", "Fins Udp"),
                new("OmronCip", "EtherNet/IP(CIP)"),
                new("OmronConnectedCip", "Connected CIP"),
                new("OmronHostLink", "HostLink [串口]"),
                new("OmronHostLinkOverTcp", "HostLink OverTcp"),
                new("OmronHostLinkCMode", "HostLink C-Mode"),
                new("OmronHostLinkCModeOverTcp", "C-Mode OverTcp")
            ],
            ["Inovance"] =
            [
                new("InovanceSerial", "InovanceSerial"),
                new("InovanceSerialOverTcp", "InovanceSerialOverTcp"),
                new("InovanceTcpNet", "InovanceTcpNet")
            ]
        };

    private readonly IDeviceRuntime _deviceRuntime;
    private readonly ISerialDebugSession _serialSession;
    private readonly Func<DeviceConfiguration, Task<DeviceConfiguration>> _saveConfigurationAsync;
    private readonly Action<DeviceSnapshot> _upsertSnapshot;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly object _serialLogGate = new();
    private readonly StringBuilder _serialPendingLog = new();
    private DeviceConfiguration _configuration;
    private DeviceDebugWorkbenchKind _workbenchKind = DeviceDebugWorkbenchKind.Plc;
    private string _selectedCommunicationDeviceKey = "plc-main";
    private string _communicationProtocol = "ModbusTcp";
    private string _communicationBrand = "Modbus";
    private string _communicationIpAddress = "192.168.1.10";
    private string _communicationPort = "502";
    private string _communicationSerialPort = "COM3";
    private string _communicationBaudRate = "9600";
    private string _communicationStationNo = "1";
    private string _communicationModel = string.Empty;
    private string _communicationAddress = "D100";
    private string _communicationWriteValue = "1";
    private string _communicationReadValue = string.Empty;
    private string _communicationStatusText = "选择通讯类型和设备后开始调试";
    private string _serialDataBits = "8";
    private string _serialParity = Parity.None.ToString();
    private string _serialStopBits = StopBits.One.ToString();
    private string _serialPayload = string.Empty;
    private string _serialResponse = string.Empty;
    private string _serialStatusText = "串口调试台待命";
    private string _serialPeerText = "未打开";
    private string _serialFrameMode = FrameRaw;
    private string _serialDelimiterText = @"\r\n";
    private string _serialFixedFrameLength = "8";
    private string _serialLengthPrefixBytes = "2";
    private string _serialMaxFrameLength = "4096";
    private bool _serialSendAsHex;
    private bool _serialLengthPrefixLittleEndian;
    private bool _serialAppendDelimiterOnSend = true;
    private bool _serialPrefixPayloadOnSend;
    private bool _serialIsConnected;
    private bool _disposed;
    private bool _loadingConfiguration;
    private bool _updatingCommunicationCatalog;
    private int _serialLogFlushScheduled;
    private int _serialDroppedLogLines;
    private long _serialReceivedBytes;
    private long _serialReceivedFrames;

    public FieldbusCommunicationDebugViewModel(
        IDeviceRuntime deviceRuntime,
        ISerialDebugSession serialSession,
        DeviceConfiguration configuration,
        Func<DeviceConfiguration, Task<DeviceConfiguration>> saveConfigurationAsync,
        Action<DeviceSnapshot> upsertSnapshot,
        IUiDispatcher uiDispatcher)
    {
        _deviceRuntime = deviceRuntime;
        _serialSession = serialSession;
        _configuration = configuration;
        _saveConfigurationAsync = saveConfigurationAsync;
        _upsertSnapshot = upsertSnapshot;
        _uiDispatcher = uiDispatcher;

        ConnectCommunicationDeviceCommand = new DelegateCommand(async () => await ConnectCommunicationDeviceAsync());
        ReadCommunicationAddressCommand = new DelegateCommand(async () => await ReadCommunicationAddressAsync());
        WriteCommunicationAddressCommand = new DelegateCommand(async () => await WriteCommunicationAddressAsync());
        SaveCommunicationDeviceCommand = new DelegateCommand(async () => await SaveCommunicationDeviceAsync());
        SerialConnectCommand = new DelegateCommand(async () => await ConnectSerialAsync());
        SerialDisconnectCommand = new DelegateCommand(async () => await DisconnectSerialAsync());
        SerialSendCommand = new DelegateCommand(async () => await SendSerialAsync());
        SerialClearCommand = new DelegateCommand(ClearSerialLog);

        _serialSession.StateChanged += OnSerialSessionStateChanged;
        _serialSession.FrameReceived += OnSerialSessionFrameReceived;

        InitializeCommunicationCatalog();
        LoadConfiguration(configuration, configuration.Debug.WorkbenchKind);
    }

    public event EventHandler? SelectionChanged;

    public ObservableCollection<TextSelectionOption> CommunicationDeviceOptions { get; } = new();

    public ObservableCollection<TextSelectionOption> CommunicationProtocolOptions { get; } = new(
    [
        new TextSelectionOption("Simulated", "仿真"),
        new TextSelectionOption("ModbusTcp", "Modbus TCP")
    ]);

    public ObservableCollection<TextSelectionOption> PlcBrandOptions { get; } = new(TargetCommunicationBrands);

    public ObservableCollection<TextSelectionOption> SerialFrameModeOptions { get; } = new(
    [
        new TextSelectionOption(FrameRaw, "原始流"),
        new TextSelectionOption(FrameDelimiter, "分隔符"),
        new TextSelectionOption(FrameFixedLength, "固定长度"),
        new TextSelectionOption(FrameLengthPrefix, "长度前缀")
    ]);

    public ObservableCollection<TextSelectionOption> TcpLengthPrefixByteOptions { get; } = new(
    [
        new TextSelectionOption("1", "1 byte"),
        new TextSelectionOption("2", "2 bytes"),
        new TextSelectionOption("4", "4 bytes")
    ]);

    public ObservableCollection<TextSelectionOption> SerialParityOptions { get; } = new(
    [
        new TextSelectionOption(Parity.None.ToString(), "None"),
        new TextSelectionOption(Parity.Odd.ToString(), "Odd"),
        new TextSelectionOption(Parity.Even.ToString(), "Even"),
        new TextSelectionOption(Parity.Mark.ToString(), "Mark"),
        new TextSelectionOption(Parity.Space.ToString(), "Space")
    ]);

    public ObservableCollection<TextSelectionOption> SerialStopBitsOptions { get; } = new(
    [
        new TextSelectionOption(StopBits.One.ToString(), "1"),
        new TextSelectionOption(StopBits.Two.ToString(), "2"),
        new TextSelectionOption(StopBits.OnePointFive.ToString(), "1.5")
    ]);

    public DelegateCommand ConnectCommunicationDeviceCommand { get; }

    public DelegateCommand ReadCommunicationAddressCommand { get; }

    public DelegateCommand WriteCommunicationAddressCommand { get; }

    public DelegateCommand SaveCommunicationDeviceCommand { get; }

    public DelegateCommand SerialConnectCommand { get; }

    public DelegateCommand SerialDisconnectCommand { get; }

    public DelegateCommand SerialSendCommand { get; }

    public DelegateCommand SerialClearCommand { get; }

    public string SelectedCommunicationDeviceKey
    {
        get => _selectedCommunicationDeviceKey;
        set
        {
            if (SetProperty(ref _selectedCommunicationDeviceKey, value?.Trim() ?? string.Empty))
            {
                ApplySelectedCommunicationDevice();
                if (!_loadingConfiguration)
                {
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    public string CommunicationProtocol
    {
        get => _communicationProtocol;
        set
        {
            if (SetProperty(ref _communicationProtocol, string.IsNullOrWhiteSpace(value) ? "ModbusTcp" : value.Trim()))
            {
                CommunicationBrand = ResolveSupportedCommunicationBrand(ResolveBrandFromProtocol(_communicationProtocol));
                RaiseConnectionVisibilityProperties();
            }
        }
    }

    public string CommunicationBrand
    {
        get => _communicationBrand;
        set
        {
            var brand = ResolveSupportedCommunicationBrand(value);
            if (SetProperty(ref _communicationBrand, brand))
            {
                RebuildCommunicationProtocolOptions(brand);
            }
        }
    }

    public string CommunicationIpAddress
    {
        get => _communicationIpAddress;
        set => SetProperty(ref _communicationIpAddress, value?.Trim() ?? string.Empty);
    }

    public string CommunicationPort
    {
        get => _communicationPort;
        set => SetProperty(ref _communicationPort, value?.Trim() ?? string.Empty);
    }

    public string CommunicationSerialPort
    {
        get => _communicationSerialPort;
        set => SetProperty(ref _communicationSerialPort, value?.Trim() ?? string.Empty);
    }

    public string CommunicationBaudRate
    {
        get => _communicationBaudRate;
        set => SetProperty(ref _communicationBaudRate, value?.Trim() ?? string.Empty);
    }

    public string CommunicationStationNo
    {
        get => _communicationStationNo;
        set => SetProperty(ref _communicationStationNo, value?.Trim() ?? string.Empty);
    }

    public string CommunicationModel
    {
        get => _communicationModel;
        set => SetProperty(ref _communicationModel, value?.Trim() ?? string.Empty);
    }

    public string CommunicationAddress
    {
        get => _communicationAddress;
        set => SetProperty(ref _communicationAddress, value?.Trim() ?? string.Empty);
    }

    public string CommunicationWriteValue
    {
        get => _communicationWriteValue;
        set => SetProperty(ref _communicationWriteValue, value ?? string.Empty);
    }

    public string CommunicationReadValue
    {
        get => _communicationReadValue;
        private set => SetProperty(ref _communicationReadValue, value ?? string.Empty);
    }

    public string CommunicationStatusText
    {
        get => _communicationStatusText;
        private set => SetProperty(ref _communicationStatusText, value ?? string.Empty);
    }

    public string SerialDataBits
    {
        get => _serialDataBits;
        set => SetProperty(ref _serialDataBits, string.IsNullOrWhiteSpace(value) ? "8" : value.Trim());
    }

    public string SerialParity
    {
        get => _serialParity;
        set => SetProperty(ref _serialParity, string.IsNullOrWhiteSpace(value) ? Parity.None.ToString() : value.Trim());
    }

    public string SerialStopBits
    {
        get => _serialStopBits;
        set => SetProperty(ref _serialStopBits, string.IsNullOrWhiteSpace(value) ? StopBits.One.ToString() : value.Trim());
    }

    public string SerialPayload
    {
        get => _serialPayload;
        set => SetProperty(ref _serialPayload, value ?? string.Empty);
    }

    public string SerialResponse
    {
        get => _serialResponse;
        private set => SetProperty(ref _serialResponse, value ?? string.Empty);
    }

    public string SerialStatusText
    {
        get => _serialStatusText;
        private set => SetProperty(ref _serialStatusText, value ?? string.Empty);
    }

    public string SerialConnectButtonText => _serialIsConnected ? "已打开" : "打开串口";

    public string SerialDisconnectButtonText => "关闭";

    public string SerialSendButtonText => _serialIsConnected ? "发送" : "先打开串口";

    public string SerialSessionText => _serialIsConnected
        ? $"串口已打开：{SerialPeerText}"
        : "串口未打开";

    public string SerialPeerText
    {
        get => _serialPeerText;
        private set
        {
            if (SetProperty(ref _serialPeerText, value ?? string.Empty))
            {
                RaiseSerialSessionProperties();
            }
        }
    }

    public bool SerialIsConnected
    {
        get => _serialIsConnected;
        private set
        {
            if (SetProperty(ref _serialIsConnected, value))
            {
                RaiseSerialSessionProperties();
            }
        }
    }

    public string SerialFrameMode
    {
        get => _serialFrameMode;
        set
        {
            var resolved = value?.Trim() switch
            {
                FrameDelimiter => FrameDelimiter,
                FrameFixedLength => FrameFixedLength,
                FrameLengthPrefix => FrameLengthPrefix,
                _ => FrameRaw
            };

            if (SetProperty(ref _serialFrameMode, resolved))
            {
                RaiseSerialFrameProperties();
            }
        }
    }

    public string SerialDelimiterText
    {
        get => _serialDelimiterText;
        set => SetProperty(ref _serialDelimiterText, string.IsNullOrWhiteSpace(value) ? @"\r\n" : value.Trim());
    }

    public string SerialFixedFrameLength
    {
        get => _serialFixedFrameLength;
        set => SetProperty(ref _serialFixedFrameLength, string.IsNullOrWhiteSpace(value) ? "1" : value.Trim());
    }

    public string SerialLengthPrefixBytes
    {
        get => _serialLengthPrefixBytes;
        set => SetProperty(ref _serialLengthPrefixBytes, value?.Trim() is "1" or "2" or "4" ? value.Trim() : "2");
    }

    public bool SerialLengthPrefixLittleEndian
    {
        get => _serialLengthPrefixLittleEndian;
        set => SetProperty(ref _serialLengthPrefixLittleEndian, value);
    }

    public string SerialMaxFrameLength
    {
        get => _serialMaxFrameLength;
        set => SetProperty(ref _serialMaxFrameLength, string.IsNullOrWhiteSpace(value) ? "4096" : value.Trim());
    }

    public bool SerialAppendDelimiterOnSend
    {
        get => _serialAppendDelimiterOnSend;
        set => SetProperty(ref _serialAppendDelimiterOnSend, value);
    }

    public bool SerialPrefixPayloadOnSend
    {
        get => _serialPrefixPayloadOnSend;
        set => SetProperty(ref _serialPrefixPayloadOnSend, value);
    }

    public Visibility TcpConnectionFieldsVisibility => _workbenchKind == DeviceDebugWorkbenchKind.Plc &&
                                                       !IsSimulatedProtocol(CommunicationProtocol) &&
                                                       !IsSerialProtocol(CommunicationProtocol)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SerialConnectionFieldsVisibility => _workbenchKind == DeviceDebugWorkbenchKind.Plc &&
                                                          IsSerialProtocol(CommunicationProtocol)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ModbusTcpConnectionFieldsVisibility => _workbenchKind == DeviceDebugWorkbenchKind.Modbus &&
                                                             !IsSerialProtocol(CommunicationProtocol)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ModbusSerialConnectionFieldsVisibility => _workbenchKind == DeviceDebugWorkbenchKind.Modbus &&
                                                                IsSerialProtocol(CommunicationProtocol)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SerialDelimiterOptionsVisibility => SerialFrameMode == FrameDelimiter
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SerialFixedLengthOptionsVisibility => SerialFrameMode == FrameFixedLength
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SerialLengthPrefixOptionsVisibility => SerialFrameMode == FrameLengthPrefix
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool SerialSendAsHex
    {
        get => _serialSendAsHex;
        set => SetProperty(ref _serialSendAsHex, value);
    }

    public string ResolveSelectedDebugDeviceKey(string fallback)
    {
        return FirstNonEmpty(SelectedCommunicationDeviceKey, CommunicationDeviceOptions.FirstOrDefault()?.Value, fallback);
    }

    public void LoadConfiguration(DeviceConfiguration configuration, DeviceDebugWorkbenchKind workbenchKind)
    {
        _loadingConfiguration = true;
        try
        {
            _configuration = configuration;
            _workbenchKind = workbenchKind;
            InitializeCommunicationCatalog();
            RebuildCommunicationDeviceOptions();

            SelectedCommunicationDeviceKey = string.IsNullOrWhiteSpace(configuration.Debug.SelectedDeviceKey)
                ? CommunicationDeviceOptions.FirstOrDefault()?.Value ?? "plc-main"
                : configuration.Debug.SelectedDeviceKey;

            if (CommunicationDeviceOptions.Count > 0 &&
                CommunicationDeviceOptions.All(option => !string.Equals(option.Value, SelectedCommunicationDeviceKey, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedCommunicationDeviceKey = CommunicationDeviceOptions[0].Value;
            }

            ApplySelectedCommunicationDevice();
            ApplyDebugModeCommunicationCatalog();
            RaiseConnectionVisibilityProperties();
        }
        finally
        {
            _loadingConfiguration = false;
        }
    }

    public void SetWorkbenchKind(DeviceDebugWorkbenchKind workbenchKind)
    {
        if (_workbenchKind == workbenchKind)
        {
            RaiseConnectionVisibilityProperties();
            return;
        }

        _workbenchKind = workbenchKind;
        RebuildCommunicationDeviceOptions();
        if (_workbenchKind is DeviceDebugWorkbenchKind.Plc or DeviceDebugWorkbenchKind.Modbus or DeviceDebugWorkbenchKind.Serial)
        {
            if (CommunicationDeviceOptions.All(option => !string.Equals(option.Value, SelectedCommunicationDeviceKey, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedCommunicationDeviceKey = CommunicationDeviceOptions.FirstOrDefault()?.Value ?? string.Empty;
            }

            ApplySelectedCommunicationDevice();
        }

        ApplyDebugModeCommunicationCatalog();
        RaiseConnectionVisibilityProperties();
    }

    public async Task ConnectCommunicationDeviceAsync()
    {
        try
        {
            var device = ResolveSelectedAddressableDevice();
            await EnsureDeviceConnectedAsync(device);
            _upsertSnapshot(device.Snapshot);
            CommunicationStatusText = $"{device.Key} 已连接：{device.Snapshot.Message}";
        }
        catch (Exception ex)
        {
            CommunicationStatusText = ex.Message;
        }
    }

    public async Task ReadCommunicationAddressAsync()
    {
        try
        {
            var device = ResolveSelectedAddressableDevice();
            await EnsureDeviceConnectedAsync(device);
            var value = await device.ReadAsync(CommunicationAddress);
            CommunicationReadValue = value;
            CommunicationStatusText = $"{device.Key} 读取 {CommunicationAddress} = {value}";
            _upsertSnapshot(device.Snapshot);
        }
        catch (Exception ex)
        {
            CommunicationStatusText = ex.Message;
        }
    }

    public async Task WriteCommunicationAddressAsync()
    {
        try
        {
            var device = ResolveSelectedAddressableDevice();
            await EnsureDeviceConnectedAsync(device);
            await device.WriteAsync(CommunicationAddress, CommunicationWriteValue);
            CommunicationStatusText = $"{device.Key} 写入 {CommunicationAddress} <= {CommunicationWriteValue}";
            _upsertSnapshot(device.Snapshot);
        }
        catch (Exception ex)
        {
            CommunicationStatusText = ex.Message;
        }
    }

    public async Task SaveCommunicationDeviceAsync()
    {
        try
        {
            var selectedKey = SelectedCommunicationDeviceKey?.Trim() ?? string.Empty;
            var updatedDevices = _configuration.Devices
                .Select(device => string.Equals(device.Key, selectedKey, StringComparison.OrdinalIgnoreCase)
                    ? UpdateCommunicationDeviceDefinition(device)
                    : device)
                .ToArray();

            var debug = new DeviceDebugConfiguration
            {
                WorkbenchKind = _workbenchKind,
                SelectedDeviceKey = FirstNonEmpty(selectedKey, CommunicationDeviceOptions.FirstOrDefault()?.Value)
            };

            var updated = _configuration with
            {
                Devices = updatedDevices,
                Debug = debug,
                SystemSettings = string.Equals(selectedKey, "plc-main", StringComparison.OrdinalIgnoreCase)
                    ? _configuration.SystemSettings with { Plc = CreatePlcSettingsFromCommunicationFields() }
                    : _configuration.SystemSettings
            };

            _configuration = await _saveConfigurationAsync(updated);
            LoadConfiguration(_configuration, _workbenchKind);
            CommunicationStatusText = "通讯调试参数已保存；已注册设备的连接参数需要重启软件后完全生效。";
        }
        catch (Exception ex)
        {
            CommunicationStatusText = ex.Message;
        }
    }

    public async Task ConnectSerialAsync()
    {
        try
        {
            if (SerialIsConnected)
            {
                SerialStatusText = $"串口已打开：{SerialPeerText}";
                return;
            }

            await _serialSession.OpenAsync(CreateSerialSessionSettings());
        }
        catch (Exception ex)
        {
            SerialStatusText = ex.Message;
        }
    }

    public Task DisconnectSerialAsync()
    {
        _serialSession.Close("串口已关闭。");
        return Task.CompletedTask;
    }

    public async Task SendSerialAsync()
    {
        try
        {
            if (!SerialIsConnected)
            {
                throw new InvalidOperationException("请先打开串口。");
            }

            var payload = CreateSerialSendPayload();
            if (payload.Length == 0)
            {
                throw new InvalidOperationException("发送内容不能为空。");
            }

            await _serialSession.SendAsync(payload);
            AppendSerialLog("TX", payload);
            SerialStatusText = $"串口已发送：{payload.Length} bytes";
        }
        catch (Exception ex)
        {
            SerialStatusText = ex.Message;
        }
    }

    public void ApplySerialRuntimeSnapshot(CommunicationChannelRuntimeSnapshot snapshot)
    {
        SerialIsConnected = snapshot.IsConnected;
        SerialPeerText = snapshot.PeerText;
        SerialStatusText = snapshot.StatusText;
    }

    public void SetExternalSerialStatus(string statusText)
    {
        SerialStatusText = statusText;
    }

    public void AppendExternalSerialLog(string direction, byte[] payload)
    {
        AppendSerialLog(direction, payload);
    }

    public void ClearExternalSerialLog()
    {
        ClearSerialLog();
    }

    private void RebuildCommunicationDeviceOptions()
    {
        CommunicationDeviceOptions.Clear();
        foreach (var device in _configuration.Devices.Where(MatchesCurrentDebugMode).OrderBy(device => device.Key, StringComparer.OrdinalIgnoreCase))
        {
            CommunicationDeviceOptions.Add(new TextSelectionOption(device.Key, $"{device.Key} / {device.Name} / {device.Driver}"));
        }
    }

    private bool MatchesCurrentDebugMode(DeviceDefinition device)
    {
        return _workbenchKind switch
        {
            DeviceDebugWorkbenchKind.Plc => device.Kind == DeviceKind.Plc,
            DeviceDebugWorkbenchKind.Modbus => device.Kind is DeviceKind.Plc or DeviceKind.Instrument &&
                                               IsModbusProtocol(FirstNonEmpty(GetOption(device.Options, "protocol"), device.Driver)),
            DeviceDebugWorkbenchKind.Serial => device.Kind is DeviceKind.Plc or DeviceKind.Instrument &&
                                               (IsSerialProtocol(FirstNonEmpty(GetOption(device.Options, "protocol"), device.Driver)) ||
                                                !string.IsNullOrWhiteSpace(device.Connection.SerialPort)),
            _ => false
        };
    }

    private void ApplySelectedCommunicationDevice()
    {
        var device = ResolveSelectedDeviceDefinition();
        if (device is null)
        {
            return;
        }

        var options = device.Options;
        CommunicationProtocol = FirstNonEmpty(GetOption(options, "protocol"), device.Driver, _workbenchKind == DeviceDebugWorkbenchKind.Serial ? "ModbusRtu" : "ModbusTcp");
        CommunicationBrand = ResolveBrandFromProtocol(CommunicationProtocol);
        CommunicationIpAddress = FirstNonEmpty(device.Connection.IpAddress, GetOption(options, "ipAddress"), GetOption(options, "host"), "192.168.1.10");
        CommunicationPort = (device.Connection.Port > 0 ? device.Connection.Port : ResolveDefaultPort(CommunicationProtocol))
            .ToString(CultureInfo.InvariantCulture);
        CommunicationSerialPort = FirstNonEmpty(device.Connection.SerialPort, GetOption(options, "serialPort"), "COM3");
        CommunicationBaudRate = (device.Connection.BaudRate > 0 ? device.Connection.BaudRate : ParseInt(GetOption(options, "baudRate"), 9600))
            .ToString(CultureInfo.InvariantCulture);
        CommunicationStationNo = FirstNonEmpty(device.Connection.StationNo, GetOption(options, "stationNo"), "1");
        CommunicationModel = FirstNonEmpty(GetOption(options, "model"), GetOption(options, "plcType"), GetOption(options, "series"));
        SerialDataBits = FirstNonEmpty(GetOption(options, "dataBits"), "8");
        SerialParity = FirstNonEmpty(GetOption(options, "parity"), Parity.None.ToString());
        SerialStopBits = FirstNonEmpty(GetOption(options, "stopBits"), StopBits.One.ToString());
        SerialFrameMode = FirstNonEmpty(GetOption(options, "frameMode"), FrameRaw);
        SerialDelimiterText = FirstNonEmpty(GetOption(options, "delimiter"), @"\r\n");
        SerialFixedFrameLength = FirstNonEmpty(GetOption(options, "fixedFrameLength"), "8");
        SerialLengthPrefixBytes = FirstNonEmpty(GetOption(options, "lengthPrefixBytes"), "2");
        SerialLengthPrefixLittleEndian = bool.TryParse(GetOption(options, "lengthPrefixLittleEndian"), out var prefixLittleEndian) && prefixLittleEndian;
        SerialMaxFrameLength = FirstNonEmpty(GetOption(options, "maxFrameLength"), "4096");
        SerialAppendDelimiterOnSend = !bool.TryParse(GetOption(options, "appendDelimiterOnSend"), out var appendDelimiter) || appendDelimiter;
        SerialPrefixPayloadOnSend = bool.TryParse(GetOption(options, "prefixPayloadOnSend"), out var prefixPayload) && prefixPayload;
        CommunicationStatusText = $"已加载 {device.Key} / {device.Name}。";
    }

    private DeviceDefinition? ResolveSelectedDeviceDefinition()
    {
        if (string.IsNullOrWhiteSpace(SelectedCommunicationDeviceKey))
        {
            return null;
        }

        return _configuration.Devices.FirstOrDefault(device =>
            string.Equals(device.Key, SelectedCommunicationDeviceKey, StringComparison.OrdinalIgnoreCase));
    }

    private IAddressableDeviceClient ResolveSelectedAddressableDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedCommunicationDeviceKey))
        {
            throw new InvalidOperationException("请先选择通讯设备。");
        }

        if (_deviceRuntime.TryGet<IAddressableDeviceClient>(SelectedCommunicationDeviceKey, out var device))
        {
            return device;
        }

        throw new InvalidOperationException($"设备 {SelectedCommunicationDeviceKey} 未注册或未启用，请保存配置并重启软件后再调试。");
    }

    private DeviceDefinition UpdateCommunicationDeviceDefinition(DeviceDefinition device)
    {
        var options = new Dictionary<string, string>(device.Options, StringComparer.OrdinalIgnoreCase);
        var driver = device.Driver;

        if (_workbenchKind == DeviceDebugWorkbenchKind.Plc)
        {
            options["protocol"] = CommunicationProtocol;
            options["brand"] = CommunicationBrand;
            driver = string.IsNullOrWhiteSpace(CommunicationProtocol) ? device.Driver : CommunicationProtocol.Trim();

            if (!string.IsNullOrWhiteSpace(CommunicationModel))
            {
                options["model"] = CommunicationModel;
            }
        }
        else if (_workbenchKind == DeviceDebugWorkbenchKind.Modbus)
        {
            options["protocol"] = CommunicationProtocol;
            options["brand"] = "Modbus";
            driver = string.IsNullOrWhiteSpace(CommunicationProtocol) ? device.Driver : CommunicationProtocol.Trim();
        }
        else if (_workbenchKind == DeviceDebugWorkbenchKind.Serial)
        {
            options["serialPort"] = CommunicationSerialPort;
            options["baudRate"] = CommunicationBaudRate;
            options["dataBits"] = SerialDataBits;
            options["parity"] = SerialParity;
            options["stopBits"] = SerialStopBits;
            options["frameMode"] = SerialFrameMode;
            options["delimiter"] = SerialDelimiterText;
            options["fixedFrameLength"] = SerialFixedFrameLength;
            options["lengthPrefixBytes"] = SerialLengthPrefixBytes;
            options["lengthPrefixLittleEndian"] = SerialLengthPrefixLittleEndian.ToString();
            options["maxFrameLength"] = SerialMaxFrameLength;
            options["appendDelimiterOnSend"] = SerialAppendDelimiterOnSend.ToString();
            options["prefixPayloadOnSend"] = SerialPrefixPayloadOnSend.ToString();
        }

        return device with
        {
            Driver = driver,
            Connection = device.Connection with
            {
                IpAddress = CommunicationIpAddress,
                Port = ParseInt(CommunicationPort, ResolveDefaultPort(CommunicationProtocol)),
                SerialPort = CommunicationSerialPort,
                BaudRate = ParseInt(CommunicationBaudRate, 9600),
                StationNo = CommunicationStationNo
            },
            Options = options
        };
    }

    private PlcCommunicationSettings CreatePlcSettingsFromCommunicationFields()
    {
        return _configuration.SystemSettings.Plc with
        {
            Protocol = CommunicationProtocol,
            IpAddress = CommunicationIpAddress,
            Port = ParseInt(CommunicationPort, ResolveDefaultPort(CommunicationProtocol)),
            StationNo = ParseInt(CommunicationStationNo, 1),
            Model = CommunicationModel,
            Options = new Dictionary<string, string>(_configuration.SystemSettings.Plc.Options, StringComparer.OrdinalIgnoreCase)
            {
                ["brand"] = CommunicationBrand,
                ["serialPort"] = CommunicationSerialPort,
                ["baudRate"] = CommunicationBaudRate,
                ["dataBits"] = SerialDataBits,
                ["parity"] = SerialParity,
                ["stopBits"] = SerialStopBits
            }
        };
    }

    private SerialDebugSessionSettings CreateSerialSessionSettings()
    {
        if (string.IsNullOrWhiteSpace(CommunicationSerialPort))
        {
            throw new InvalidOperationException("串口号不能为空。");
        }

        var baudRate = ParseInt(CommunicationBaudRate, 0);
        if (baudRate <= 0)
        {
            throw new InvalidOperationException("波特率必须是大于 0 的数字。");
        }

        var dataBits = ParseInt(SerialDataBits, 8);
        if (dataBits is < 5 or > 8)
        {
            throw new InvalidOperationException("数据位必须在 5 到 8 之间。");
        }

        var parity = ParseEnumValue(SerialParity, Parity.None);
        var stopBits = ParseEnumValue(SerialStopBits, StopBits.One);
        if (stopBits == StopBits.None)
        {
            throw new InvalidOperationException("停止位不能为 None。");
        }

        return new SerialDebugSessionSettings
        {
            PortName = CommunicationSerialPort.Trim(),
            BaudRate = baudRate,
            Parity = parity,
            DataBits = dataBits,
            StopBits = stopBits,
            FrameOptions = CreateSerialFrameOptions()
        };
    }

    private byte[] CreateSerialSendPayload()
    {
        return CommunicationFrameCodec.CreatePayload(SerialPayload, SerialSendAsHex, CreateSerialFrameOptions());
    }

    private CommunicationFrameOptions CreateSerialFrameOptions()
    {
        return new CommunicationFrameOptions
        {
            FrameMode = SerialFrameMode,
            Delimiter = SerialDelimiterText,
            FixedFrameLength = ParseInt(SerialFixedFrameLength, 1),
            LengthPrefixBytes = ParseInt(SerialLengthPrefixBytes, 2),
            LengthPrefixLittleEndian = SerialLengthPrefixLittleEndian,
            MaxFrameLength = ParseInt(SerialMaxFrameLength, 4096),
            AppendDelimiterOnSend = SerialAppendDelimiterOnSend,
            PrefixPayloadOnSend = SerialPrefixPayloadOnSend
        };
    }

    private void ClearSerialLog()
    {
        SerialResponse = string.Empty;
        SerialStatusText = "串口日志已清空。";
    }

    private void AppendSerialLog(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        AppendSerialLogBlock(line);
    }

    private void AppendSerialLogBlock(string block)
    {
        SerialResponse = string.IsNullOrWhiteSpace(SerialResponse)
            ? block
            : $"{SerialResponse}{Environment.NewLine}{block}";

        if (SerialResponse.Length > MaxLogCharacters)
        {
            SerialResponse = SerialResponse[^MaxLogCharacters..];
        }
    }

    private void AppendSerialLog(string direction, byte[] payload)
    {
        AppendSerialLog($"{direction} {payload.Length} bytes  {FormatSerialPayload(payload)}");
    }

    private void QueueSerialLog(string direction, byte[] payload)
    {
        QueueSerialLog($"{direction} {payload.Length} bytes  {FormatSerialPayload(payload)}");
    }

    private void QueueSerialLog(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        lock (_serialLogGate)
        {
            if (_serialPendingLog.Length > MaxQueuedLogCharacters)
            {
                _serialDroppedLogLines++;
                return;
            }

            if (_serialPendingLog.Length > 0)
            {
                _serialPendingLog.AppendLine();
            }

            _serialPendingLog.Append(line);
        }

        ScheduleSerialLogFlush();
    }

    private void ScheduleSerialLogFlush()
    {
        if (Interlocked.Exchange(ref _serialLogFlushScheduled, 1) == 0)
        {
            _ = FlushSerialLogSoonAsync();
        }
    }

    private async Task FlushSerialLogSoonAsync()
    {
        try
        {
            await Task.Delay(80);
            if (_disposed)
            {
                return;
            }

            string block;
            int dropped;
            lock (_serialLogGate)
            {
                block = _serialPendingLog.ToString();
                _serialPendingLog.Clear();
                dropped = _serialDroppedLogLines;
                _serialDroppedLogLines = 0;
            }

            if (string.IsNullOrWhiteSpace(block) && dropped == 0)
            {
                return;
            }

            _uiDispatcher.Invoke(() =>
            {
                if (dropped > 0)
                {
                    AppendSerialLogBlock($"{DateTime.Now:HH:mm:ss.fff}  LOG DROP {dropped} lines，接收过快，已保护界面刷新");
                }

                if (!string.IsNullOrWhiteSpace(block))
                {
                    AppendSerialLogBlock(block);
                }

                var bytes = Interlocked.Read(ref _serialReceivedBytes);
                var frames = Interlocked.Read(ref _serialReceivedFrames);
                SerialStatusText = $"串口已接收：{bytes} bytes / {frames} 帧";
            });
        }
        finally
        {
            Interlocked.Exchange(ref _serialLogFlushScheduled, 0);
            lock (_serialLogGate)
            {
                if (_serialPendingLog.Length > 0 && !_disposed)
                {
                    ScheduleSerialLogFlush();
                }
            }
        }
    }

    private void OnSerialSessionStateChanged(object? sender, CommunicationSessionState state)
    {
        _uiDispatcher.Invoke(() =>
        {
            SerialIsConnected = state.IsConnected;
            SerialPeerText = state.PeerText;
            SerialStatusText = state.StatusText;
            if (!string.IsNullOrWhiteSpace(state.LogText))
            {
                AppendSerialLog(state.LogText);
            }
        });
    }

    private void OnSerialSessionFrameReceived(object? sender, CommunicationSessionFrame frame)
    {
        Interlocked.Exchange(ref _serialReceivedBytes, frame.TotalBytes);
        Interlocked.Exchange(ref _serialReceivedFrames, frame.TotalFrames);
        QueueSerialLog(frame.Label, frame.Payload);
    }

    private void ApplyDebugModeCommunicationCatalog()
    {
        if (_workbenchKind == DeviceDebugWorkbenchKind.Modbus)
        {
            CommunicationBrand = "Modbus";
        }
        else if (_workbenchKind == DeviceDebugWorkbenchKind.Plc &&
                 string.Equals(CommunicationBrand, "Modbus", StringComparison.OrdinalIgnoreCase))
        {
            CommunicationBrand = "Mitsubishi";
        }
    }

    private void InitializeCommunicationCatalog()
    {
        ReplaceTextOptions(PlcBrandOptions, TargetCommunicationBrands);
        RebuildCommunicationProtocolOptions(ResolveSupportedCommunicationBrand(_communicationBrand));
    }

    private void RebuildCommunicationProtocolOptions(string brand)
    {
        if (_updatingCommunicationCatalog)
        {
            return;
        }

        _updatingCommunicationCatalog = true;
        try
        {
            var supportedBrand = ResolveSupportedCommunicationBrand(brand);
            var options = GetProtocolOptionsForBrand(supportedBrand);
            var currentProtocol = CommunicationProtocol;

            ReplaceTextOptions(CommunicationProtocolOptions, options);

            if (CommunicationProtocolOptions.Count > 0 &&
                CommunicationProtocolOptions.All(option => !string.Equals(option.Value, currentProtocol, StringComparison.OrdinalIgnoreCase)))
            {
                CommunicationProtocol = CommunicationProtocolOptions[0].Value;
            }
        }
        finally
        {
            _updatingCommunicationCatalog = false;
        }
    }

    private void RaiseConnectionVisibilityProperties()
    {
        RaisePropertyChanged(nameof(TcpConnectionFieldsVisibility));
        RaisePropertyChanged(nameof(SerialConnectionFieldsVisibility));
        RaisePropertyChanged(nameof(ModbusTcpConnectionFieldsVisibility));
        RaisePropertyChanged(nameof(ModbusSerialConnectionFieldsVisibility));
    }

    private void RaiseSerialSessionProperties()
    {
        RaisePropertyChanged(nameof(SerialConnectButtonText));
        RaisePropertyChanged(nameof(SerialDisconnectButtonText));
        RaisePropertyChanged(nameof(SerialSendButtonText));
        RaisePropertyChanged(nameof(SerialSessionText));
    }

    private void RaiseSerialFrameProperties()
    {
        RaisePropertyChanged(nameof(SerialDelimiterOptionsVisibility));
        RaisePropertyChanged(nameof(SerialFixedLengthOptionsVisibility));
        RaisePropertyChanged(nameof(SerialLengthPrefixOptionsVisibility));
    }

    private string ResolveSupportedCommunicationBrand(string? brand)
    {
        var candidate = string.IsNullOrWhiteSpace(brand)
            ? (_workbenchKind == DeviceDebugWorkbenchKind.Modbus ? "Modbus" : "Mitsubishi")
            : brand.Trim();

        if (TargetCommunicationBrands.Any(option => string.Equals(option.Value, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return TargetCommunicationBrands.First(option => string.Equals(option.Value, candidate, StringComparison.OrdinalIgnoreCase)).Value;
        }

        return _workbenchKind == DeviceDebugWorkbenchKind.Modbus ? "Modbus" : "Mitsubishi";
    }

    private string FormatSerialPayload(byte[] payload)
    {
        return CommunicationFrameCodec.FormatPayload(payload, SerialSendAsHex);
    }

    private static async Task EnsureDeviceConnectedAsync(IDeviceClient device)
    {
        if (device.Snapshot.State != DeviceConnectionState.Connected)
        {
            await device.ConnectAsync();
        }
    }

    private static void ReplaceTextOptions(ObservableCollection<TextSelectionOption> target, IEnumerable<TextSelectionOption> source)
    {
        target.Clear();
        foreach (var option in source)
        {
            target.Add(option);
        }
    }

    private static TextSelectionOption[] GetProtocolOptionsForBrand(string brand)
    {
        return TargetProtocolsByBrand.TryGetValue(brand, out var options)
            ? options
            : TargetProtocolsByBrand["Mitsubishi"];
    }

    private static int ParseInt(string? text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               int.TryParse(text, out value)
            ? value
            : fallback;
    }

    private static TEnum ParseEnumValue<TEnum>(string? text, TEnum fallback)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(text, true, out var value)
            ? value
            : fallback;
    }

    private static string GetOption(IReadOnlyDictionary<string, string> options, string key)
    {
        return options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static bool IsModbusProtocol(string? value)
    {
        return NormalizeToken(value).Contains("modbus", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSerialProtocol(string? value)
    {
        var normalized = NormalizeProtocolKey(value);
        if (normalized.Contains("overtcp", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("udp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalized is "modbusrtu" or "modbusascii"
            or "inovanceserial"
            or "melsecfxserial" or "melsecfxlinks" or "melseca3c"
            or "siemensppi" or "siemensmpi"
            or "omronhostlink" or "omronhostlinkcmode"
            or "deltaserial" or "deltaserialascii"
            or "allenbradleydf1" or "abdf1"
            or "lscnet"
            or "fatek" or "fatekprogram" or "fatekserial"
            or "fujispb"
            or "xinjeserial"
            or "vigorserial"
            or "panasonicmewtocol"
            or "keyencenano" or "keyencenanoserial";
    }

    private static bool IsSimulatedProtocol(string? value)
    {
        return NormalizeProtocolKey(value) is "" or "simulated" or "simulation";
    }

    private static string ResolveBrandFromProtocol(string? protocol)
    {
        var normalized = NormalizeToken(protocol);
        if (normalized.Contains("inovance", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("huichuan", StringComparison.OrdinalIgnoreCase))
        {
            return "Inovance";
        }

        if (normalized.Contains("melsec", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("mitsubishi", StringComparison.OrdinalIgnoreCase))
        {
            return "Mitsubishi";
        }

        if (normalized.Contains("siemens", StringComparison.OrdinalIgnoreCase))
        {
            return "Siemens";
        }

        if (normalized.Contains("omron", StringComparison.OrdinalIgnoreCase))
        {
            return "Omron";
        }

        return normalized.Contains("modbus", StringComparison.OrdinalIgnoreCase) ? "Modbus" : "Generic";
    }

    private static int ResolveDefaultPort(string? protocol)
    {
        var normalized = NormalizeToken(protocol);
        if (normalized.Contains("siemenswebapi", StringComparison.OrdinalIgnoreCase))
        {
            return 443;
        }

        if (normalized.Contains("siemens", StringComparison.OrdinalIgnoreCase))
        {
            return 102;
        }

        if (normalized.Contains("allenbradley", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ab", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("cip", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pccc", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("slc", StringComparison.OrdinalIgnoreCase))
        {
            return 44818;
        }

        if (normalized.Contains("beckhoff", StringComparison.OrdinalIgnoreCase))
        {
            return 48898;
        }

        if (normalized.Contains("keyencenano", StringComparison.OrdinalIgnoreCase))
        {
            return 8501;
        }

        if (normalized.Contains("lsfastenet", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("lscnet", StringComparison.OrdinalIgnoreCase))
        {
            return 2004;
        }

        if (normalized.Contains("fuji", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("gesrtp", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ge", StringComparison.OrdinalIgnoreCase))
        {
            return 18245;
        }

        if (normalized.Contains("panasonicmewtocol", StringComparison.OrdinalIgnoreCase))
        {
            return 9094;
        }

        if (normalized.Contains("yokogawa", StringComparison.OrdinalIgnoreCase))
        {
            return 12289;
        }

        if (normalized.Contains("melsec", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("mitsubishi", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("fatek", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("vigor", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("panasonicmc", StringComparison.OrdinalIgnoreCase))
        {
            return 5000;
        }

        if (normalized.Contains("omron", StringComparison.OrdinalIgnoreCase))
        {
            return 9600;
        }

        return 502;
    }

    private static string NormalizeToken(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string NormalizeProtocolKey(string? value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _serialSession.StateChanged -= OnSerialSessionStateChanged;
        _serialSession.FrameReceived -= OnSerialSessionFrameReceived;
        _serialSession.Dispose();
    }
}
