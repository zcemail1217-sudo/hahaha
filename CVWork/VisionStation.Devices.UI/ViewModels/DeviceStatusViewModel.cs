using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Text;
using System.Windows;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Communication;
using VisionStation.Communication.UI.ViewModels;
using VisionStation.Devices;
using VisionStation.Domain;
using Timer = System.Threading.Timer;

namespace VisionStation.Devices.UI.ViewModels;

public sealed class DeviceStatusViewModel : BindableBase, IDisposable
{
    private const string UnsavedChangesKey = "device-status-configuration";
    private static readonly TimeSpan AxisRefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IoRefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ManualMoveCommandHold = TimeSpan.FromMilliseconds(350);
    private const string TcpClientMode = "Client";
    private const string TcpServerMode = "Server";
    private const string TcpFrameRaw = "Raw";
    private const string TcpFrameDelimiter = "Delimiter";
    private const string TcpFrameFixedLength = "FixedLength";
    private const string TcpFrameLengthPrefix = "LengthPrefix";
    private const int TcpMaxLogCharacters = 60000;
    private const int TcpMaxQueuedLogCharacters = 24000;
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

    private readonly IAxisController _axis;
    private readonly IDigitalIoController _io;
    private readonly IDeviceRuntime _deviceRuntime;
    private readonly ITcpDebugSession _tcpSession;
    private readonly ISerialDebugSession _serialSession;
    private readonly IDeviceConfigurationRepository _configurationRepository;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IUnsavedChangesService _unsavedChanges;
    private readonly SemaphoreSlim _configurationSaveLock = new(1, 1);
    private readonly object _tcpLogGate = new();
    private readonly object _serialLogGate = new();
    private readonly StringBuilder _tcpPendingLog = new();
    private readonly StringBuilder _serialPendingLog = new();
    private readonly Timer _axisRefreshTimer;
    private readonly Timer _ioRefreshTimer;
    private DeviceConfiguration _configuration;
    private AxisPointItem? _selectedAxis;
    private string _axisKey = AxisDefaults.PrimaryAxisKey;
    private string _axisTargetPosition = "120";
    private string _axisSpeed = "80";
    private string _axisAcceleration = "120";
    private string _axisStepDistance = "1";
    private string _axisJogSpeed = "20";
    private string _interpolationSpeed = "80";
    private string _interpolationAcceleration = "120";
    private string _interpolationCoordinateSystem = "1";
    private string _interpolationFifo = "0";
    private string _nextBufferedOutputPointNo = "1";
    private string _axisControllerMode = AxisControllerKind.Simulated.ToString();
    private string _axisStatusText = "等待连接轴卡";
    private string _axisEncoderText = "-";
    private string _axisCommandText = "-";
    private string _axisServoText = "OFF";
    private string _axisReadyText = "-";
    private string _axisInPositionText = "-";
    private string _axisAlarmText = "-";
    private string _axisHomeText = "-";
    private string _axisPositiveLimitText = "-";
    private string _axisNegativeLimitText = "-";
    private string _ioStatusText = "等待连接 IO";
    private string _googolCardNo = "0";
    private string _googolConfigPath = string.Empty;
    private string _configurationStatusText = "轴卡配置已载入";
    private string _debugMode = DeviceDebugWorkbenchKind.AxisCard.ToString();
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
    private string _tcpHost = "127.0.0.1";
    private string _tcpPort = "502";
    private string _tcpPayload = string.Empty;
    private string _tcpResponse = string.Empty;
    private string _tcpStatusText = "TCP 调试台待命";
    private string _tcpMode = TcpClientMode;
    private string _tcpPeerText = "未连接";
    private string _tcpFrameMode = TcpFrameRaw;
    private string _tcpDelimiterText = @"\r\n";
    private string _tcpFixedFrameLength = "8";
    private string _tcpLengthPrefixBytes = "2";
    private string _tcpMaxFrameLength = "4096";
    private bool _tcpSendAsHex;
    private bool _tcpIsConnected;
    private bool _tcpIsListening;
    private bool _tcpLengthPrefixLittleEndian;
    private bool _tcpAppendDelimiterOnSend = true;
    private bool _tcpPrefixPayloadOnSend;
    private string _serialDataBits = "8";
    private string _serialParity = Parity.None.ToString();
    private string _serialStopBits = StopBits.One.ToString();
    private string _serialPayload = string.Empty;
    private string _serialResponse = string.Empty;
    private string _serialStatusText = "串口调试台待命";
    private string _serialPeerText = "未打开";
    private string _serialFrameMode = TcpFrameRaw;
    private string _serialDelimiterText = @"\r\n";
    private string _serialFixedFrameLength = "8";
    private string _serialLengthPrefixBytes = "2";
    private string _serialMaxFrameLength = "4096";
    private bool _serialSendAsHex;
    private bool _serialLengthPrefixLittleEndian;
    private bool _serialAppendDelimiterOnSend = true;
    private bool _serialPrefixPayloadOnSend;
    private bool _serialIsConnected;
    private bool _isDebugWorkspace = true;
    private bool _axisAutoRefreshEnabled;
    private bool _ioAutoRefreshEnabled;
    private bool _disposed;
    private bool _jogRunning;
    private bool _hasUnsavedChanges;
    private bool _loadingConfiguration;
    private bool _refreshingSelectionOptions;
    private bool _updatingCommunicationCatalog;
    private int _debugSelectionSaveVersion;
    private int _axisCommandRunning;
    private int _axisRefreshRunning;
    private int _ioRefreshRunning;
    private int _tcpLogFlushScheduled;
    private int _tcpDroppedLogLines;
    private int _serialLogFlushScheduled;
    private int _serialDroppedLogLines;
    private long _tcpReceivedBytes;
    private long _tcpReceivedFrames;
    private long _serialReceivedBytes;
    private long _serialReceivedFrames;

    public DeviceStatusViewModel(
        IAxisController axis,
        IDigitalIoController io,
        IDeviceRuntime deviceRuntime,
        ITcpDebugSession tcpSession,
        ISerialDebugSession serialSession,
        DeviceConfiguration configuration,
        IDeviceConfigurationRepository configurationRepository,
        IUiDispatcher uiDispatcher,
        IUnsavedChangesService unsavedChanges)
    {
        _axis = axis;
        _io = io;
        _deviceRuntime = deviceRuntime;
        _tcpSession = tcpSession;
        _serialSession = serialSession;
        _configuration = configuration;
        _configurationRepository = configurationRepository;
        _uiDispatcher = uiDispatcher;
        _unsavedChanges = unsavedChanges;
        TcpDebug = new TcpDebugViewModel(tcpSession, uiDispatcher);
        FieldbusDebug = new FieldbusCommunicationDebugViewModel(
            deviceRuntime,
            serialSession,
            configuration,
            SaveCommunicationDebugConfigurationAsync,
            Upsert,
            uiDispatcher);
        TcpDebug.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(TcpDebugViewModel.IsServerMode) or nameof(TcpDebugViewModel.TcpMode))
            {
                RaisePropertyChanged(nameof(ConnectButtonText));
            }
        };
        FieldbusDebug.SelectionChanged += (_, _) => QueueDebugSelectionSave();

        ConnectAllCommand = new DelegateCommand(async () => await ConnectAllAsync());
        ServoOnCommand = new DelegateCommand(async () => await RunAxisCommandAsync(() => _axis.ServoOnAsync(AxisKey), "伺服使能中..."));
        ServoOffCommand = new DelegateCommand(async () => await RunAxisCommandAsync(() => _axis.ServoOffAsync(AxisKey), "伺服关闭中..."));
        ClearAxisAlarmCommand = new DelegateCommand(async () => await RunAxisCommandAsync(() => _axis.ClearAlarmAsync(AxisKey), "轴报警清除中..."));
        ZeroAxisPositionCommand = new DelegateCommand(async () => await RunAxisCommandAsync(() => _axis.ZeroPositionAsync(AxisKey), "位置清零中..."));
        HomeAxisCommand = new DelegateCommand(async () => await HomeAxisAsync());
        MoveAxisCommand = new DelegateCommand(async () => await MoveAxisAsync());
        MovePositiveStepCommand = new DelegateCommand(async () => await MoveStepAsync(1));
        MoveNegativeStepCommand = new DelegateCommand(async () => await MoveStepAsync(-1));
        PrecheckLinearInterpolationCommand = new DelegateCommand(async () => await PrecheckLinearInterpolationAsync());
        RunLinearInterpolationCommand = new DelegateCommand(async () => await RunLinearInterpolationAsync());
        AddBeforeMotionOutputCommand = new DelegateCommand(() => AddBufferedOutputAction(AxisInterpolationBufferedActionTiming.BeforeMotion));
        AddAfterMotionOutputCommand = new DelegateCommand(() => AddBufferedOutputAction(AxisInterpolationBufferedActionTiming.AfterMotion));
        SelectAxisCommand = new DelegateCommand<object>(SelectAxis);
        StopAxisCommand = new DelegateCommand(async () => await RunAxisCommandAsync(() => _axis.StopAsync(AxisKey), "轴减速停止中...", allowWhileBusy: true));
        EmergencyStopCommand = new DelegateCommand(async () => await RunAxisCommandAsync(() => _axis.EmergencyStopAsync(), "全部轴急停中...", allowWhileBusy: true));
        RefreshAxisStatusCommand = new DelegateCommand(async () => await RefreshAxisStatusAsync());
        RefreshIoPointsCommand = new DelegateCommand(async () => await RefreshIoPointsAsync());
        ToggleIoPointCommand = new DelegateCommand<object>(async point => await ToggleIoPointAsync(point));
        ConnectCommunicationDeviceCommand = new DelegateCommand(async () => await FieldbusDebug.ConnectCommunicationDeviceAsync());
        ReadCommunicationAddressCommand = new DelegateCommand(async () => await FieldbusDebug.ReadCommunicationAddressAsync());
        WriteCommunicationAddressCommand = new DelegateCommand(async () => await FieldbusDebug.WriteCommunicationAddressAsync());
        SaveCommunicationDeviceCommand = new DelegateCommand(async () => await FieldbusDebug.SaveCommunicationDeviceAsync());
        TcpConnectCommand = new DelegateCommand(async () => await TcpDebug.ConnectAsync());
        TcpDisconnectCommand = new DelegateCommand(async () => await TcpDebug.DisconnectAsync());
        TcpSendCommand = new DelegateCommand(async () => await TcpDebug.SendAsync());
        TcpClearCommand = new DelegateCommand(() => TcpDebug.TcpClearCommand.Execute());
        SerialConnectCommand = new DelegateCommand(async () => await FieldbusDebug.ConnectSerialAsync());
        SerialDisconnectCommand = new DelegateCommand(async () => await FieldbusDebug.DisconnectSerialAsync());
        SerialSendCommand = new DelegateCommand(async () => await FieldbusDebug.SendSerialAsync());
        SerialClearCommand = new DelegateCommand(() => FieldbusDebug.SerialClearCommand.Execute());
        AddCardCommand = new DelegateCommand(AddCard);
        AddExtendedModuleCommand = new DelegateCommand(AddExtendedModule);
        AddAxisCommand = new DelegateCommand(AddAxis);
        AddInputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Input, IoPointSource.Onboard));
        AddOutputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Output, IoPointSource.Onboard));
        AddAxisInputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Input, IoPointSource.AxisOnboard));
        AddAxisOutputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Output, IoPointSource.AxisOnboard));
        AddExtendedInputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Input, IoPointSource.ExtendedModule));
        AddExtendedOutputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Output, IoPointSource.ExtendedModule));
        RemoveCardCommand = new DelegateCommand<object>(RemoveCard);
        RemoveExtendedModuleCommand = new DelegateCommand<object>(RemoveExtendedModule);
        RemoveAxisCommand = new DelegateCommand<object>(RemoveAxis);
        RemoveIoPointCommand = new DelegateCommand<object>(RemoveIoPoint);
        SaveConfigurationCommand = new DelegateCommand(async () => await SaveConfigurationAsync());
        ReloadConfigurationCommand = new DelegateCommand(async () => await ReloadConfigurationAsync());
        ShowDebugWorkspaceCommand = new DelegateCommand(() => IsDebugWorkspace = true);
        ShowConfigurationWorkspaceCommand = new DelegateCommand(() => IsDebugWorkspace = false);

        _axisRefreshTimer = new Timer(_ => _ = RefreshAxisStatusFromTimerAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _ioRefreshTimer = new Timer(_ => _ = RefreshIoPointsFromTimerAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        InitializeCommunicationCatalog();
        LoadConfiguration(configuration);

        _axis.StateChanged += (_, snapshot) => _uiDispatcher.Invoke(() =>
        {
            Upsert(snapshot);
            if (snapshot.State == DeviceConnectionState.Connected)
            {
                StartAxisAutoRefresh();
            }
            else if (snapshot.State is DeviceConnectionState.Disconnected or DeviceConnectionState.Faulted)
            {
                StopAxisAutoRefresh();
            }
        });
        _io.StateChanged += (_, snapshot) => _uiDispatcher.Invoke(() =>
        {
            Upsert(snapshot);
            if (snapshot.State == DeviceConnectionState.Connected)
            {
                StartIoAutoRefresh();
            }
            else if (snapshot.State is DeviceConnectionState.Disconnected or DeviceConnectionState.Faulted)
            {
                StopIoAutoRefresh();
            }
        });

        Upsert(_axis.Snapshot);
        Upsert(_io.Snapshot);

        if (_axis.Snapshot.State == DeviceConnectionState.Connected)
        {
            StartAxisAutoRefresh();
        }

        if (_io.Snapshot.State == DeviceConnectionState.Connected)
        {
            StartIoAutoRefresh();
        }
    }

    public ObservableCollection<DeviceSnapshotItem> Devices { get; } = new();

    public TcpDebugViewModel TcpDebug { get; }

    public FieldbusCommunicationDebugViewModel FieldbusDebug { get; }

    public ObservableCollection<TextSelectionOption> DebugModeOptions { get; } = new(
    [
        new TextSelectionOption(DeviceDebugWorkbenchKind.AxisCard.ToString(), "轴卡"),
        new TextSelectionOption(DeviceDebugWorkbenchKind.Plc.ToString(), "PLC")
    ]);

    public ObservableCollection<TextSelectionOption> CommunicationDeviceOptions { get; } = new();

    public ObservableCollection<TextSelectionOption> TcpModeOptions { get; } = new(
    [
        new TextSelectionOption(TcpClientMode, "客户端"),
        new TextSelectionOption(TcpServerMode, "服务端")
    ]);

    public ObservableCollection<TextSelectionOption> TcpFrameModeOptions { get; } = new(
    [
        new TextSelectionOption(TcpFrameRaw, "原始流"),
        new TextSelectionOption(TcpFrameDelimiter, "分隔符"),
        new TextSelectionOption(TcpFrameFixedLength, "固定长度"),
        new TextSelectionOption(TcpFrameLengthPrefix, "长度前缀")
    ]);

    public ObservableCollection<TextSelectionOption> SerialFrameModeOptions { get; } = new(
    [
        new TextSelectionOption(TcpFrameRaw, "原始流"),
        new TextSelectionOption(TcpFrameDelimiter, "分隔符"),
        new TextSelectionOption(TcpFrameFixedLength, "固定长度"),
        new TextSelectionOption(TcpFrameLengthPrefix, "长度前缀")
    ]);

    public ObservableCollection<TextSelectionOption> TcpLengthPrefixByteOptions { get; } = new(
    [
        new TextSelectionOption("1", "1 byte"),
        new TextSelectionOption("2", "2 bytes"),
        new TextSelectionOption("4", "4 bytes")
    ]);

    public ObservableCollection<TextSelectionOption> CommunicationProtocolOptions { get; } = new(
    [
        new TextSelectionOption("Simulated", "仿真"),
        new TextSelectionOption("ModbusTcp", "Modbus TCP"),
        new TextSelectionOption("ModbusUdp", "Modbus UDP"),
        new TextSelectionOption("ModbusRtu", "Modbus RTU"),
        new TextSelectionOption("ModbusAscii", "Modbus ASCII"),
        new TextSelectionOption("ModbusRtuOverTcp", "Modbus RTU over TCP"),
        new TextSelectionOption("ModbusAsciiOverTcp", "Modbus ASCII over TCP"),
        new TextSelectionOption("InovanceSerial", "Inovance Serial"),
        new TextSelectionOption("InovanceSerialOverTcp", "Inovance Serial over TCP"),
        new TextSelectionOption("MelsecMcAscii", "Mitsubishi MC ASCII"),
        new TextSelectionOption("MelsecMcUdp", "Mitsubishi MC UDP"),
        new TextSelectionOption("MelsecMcR", "Mitsubishi MC R"),
        new TextSelectionOption("MelsecFxSerial", "Mitsubishi FX Serial"),
        new TextSelectionOption("MelsecFxSerialOverTcp", "Mitsubishi FX Serial over TCP"),
        new TextSelectionOption("MelsecFxLinks", "Mitsubishi FX Links"),
        new TextSelectionOption("MelsecFxLinksOverTcp", "Mitsubishi FX Links over TCP"),
        new TextSelectionOption("SiemensPPI", "Siemens PPI"),
        new TextSelectionOption("SiemensPPIOverTcp", "Siemens PPI over TCP"),
        new TextSelectionOption("SiemensFetchWrite", "Siemens Fetch/Write"),
        new TextSelectionOption("OmronFinsUdp", "Omron FINS UDP"),
        new TextSelectionOption("OmronHostLink", "Omron HostLink"),
        new TextSelectionOption("OmronHostLinkOverTcp", "Omron HostLink over TCP"),
        new TextSelectionOption("OmronHostLinkCMode", "Omron HostLink C-Mode"),
        new TextSelectionOption("OmronHostLinkCModeOverTcp", "Omron HostLink C-Mode over TCP"),
        new TextSelectionOption("OmronCip", "Omron CIP"),
        new TextSelectionOption("KeyenceMc", "Keyence MC"),
        new TextSelectionOption("KeyenceMcAscii", "Keyence MC ASCII"),
        new TextSelectionOption("KeyenceNanoSerial", "Keyence Nano Serial"),
        new TextSelectionOption("KeyenceNanoSerialOverTcp", "Keyence Nano over TCP"),
        new TextSelectionOption("PanasonicMc", "Panasonic MC"),
        new TextSelectionOption("PanasonicMewtocol", "Panasonic Mewtocol"),
        new TextSelectionOption("PanasonicMewtocolOverTcp", "Panasonic Mewtocol over TCP"),
        new TextSelectionOption("DeltaTcp", "Delta TCP"),
        new TextSelectionOption("DeltaSerial", "Delta Serial"),
        new TextSelectionOption("DeltaSerialAscii", "Delta Serial ASCII"),
        new TextSelectionOption("DeltaSerialOverTcp", "Delta Serial over TCP"),
        new TextSelectionOption("AllenBradley", "Allen-Bradley CIP"),
        new TextSelectionOption("AllenBradleyConnectedCip", "Allen-Bradley Connected CIP"),
        new TextSelectionOption("AllenBradleyPccc", "Allen-Bradley PCCC"),
        new TextSelectionOption("AllenBradleySlc", "Allen-Bradley SLC"),
        new TextSelectionOption("AllenBradleyDf1", "Allen-Bradley DF1 Serial"),
        new TextSelectionOption("BeckhoffAds", "Beckhoff ADS"),
        new TextSelectionOption("LsFastEnet", "LS Fast Enet"),
        new TextSelectionOption("LsCnet", "LS Cnet Serial"),
        new TextSelectionOption("LsCnetOverTcp", "LS Cnet over TCP"),
        new TextSelectionOption("FatekProgram", "FATEK Program Serial"),
        new TextSelectionOption("FatekProgramOverTcp", "FATEK Program over TCP"),
        new TextSelectionOption("FujiSph", "Fuji SPH"),
        new TextSelectionOption("FujiSpb", "Fuji SPB Serial"),
        new TextSelectionOption("FujiSpbOverTcp", "Fuji SPB over TCP"),
        new TextSelectionOption("FujiCommandSettingType", "Fuji Command Setting"),
        new TextSelectionOption("GeSrtp", "GE SRTP"),
        new TextSelectionOption("XinJeTcp", "XinJE TCP"),
        new TextSelectionOption("XinJeSerial", "XinJE Serial"),
        new TextSelectionOption("XinJeSerialOverTcp", "XinJE Serial over TCP"),
        new TextSelectionOption("XinJeInternal", "XinJE Internal TCP"),
        new TextSelectionOption("VigorSerial", "Vigor Serial"),
        new TextSelectionOption("VigorSerialOverTcp", "Vigor Serial over TCP"),
        new TextSelectionOption("MemobusTcp", "Yaskawa Memobus TCP"),
        new TextSelectionOption("YokogawaLink", "Yokogawa Link"),
        new TextSelectionOption("InovanceTcp", "汇川 TCP"),
        new TextSelectionOption("MelsecMc", "三菱 MC"),
        new TextSelectionOption("SiemensS7", "西门子 S7"),
        new TextSelectionOption("OmronFins", "欧姆龙 FINS")
    ]);

    public ObservableCollection<TextSelectionOption> PlcBrandOptions { get; } = new(
    [
        new TextSelectionOption("Modbus", "Modbus"),
        new TextSelectionOption("Keyence", "Keyence"),
        new TextSelectionOption("Panasonic", "Panasonic"),
        new TextSelectionOption("Delta", "Delta"),
        new TextSelectionOption("AllenBradley", "Allen-Bradley"),
        new TextSelectionOption("Beckhoff", "Beckhoff"),
        new TextSelectionOption("LS", "LS"),
        new TextSelectionOption("FATEK", "FATEK"),
        new TextSelectionOption("Fuji", "Fuji"),
        new TextSelectionOption("GE", "GE"),
        new TextSelectionOption("XinJE", "XinJE"),
        new TextSelectionOption("Yaskawa", "Yaskawa"),
        new TextSelectionOption("Yokogawa", "Yokogawa"),
        new TextSelectionOption("Inovance", "汇川"),
        new TextSelectionOption("Mitsubishi", "三菱"),
        new TextSelectionOption("Siemens", "西门子"),
        new TextSelectionOption("Omron", "欧姆龙"),
        new TextSelectionOption("Generic", "通用设备")
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

    public ObservableCollection<GoogolCardItem> Cards { get; } = new();

    public ObservableCollection<NumericSelectionOption> CardNoOptions { get; } = new();

    public ObservableCollection<AxisCardSelectionOption> AxisCardOptions { get; } = new();

    public ObservableCollection<TextSelectionOption> AxisCardDriverOptions { get; } = new(
        GoogolCardItem.CreateDriverOptions());

    public ObservableCollection<TextSelectionOption> AxisControllerModes { get; } = new(
    [
        new TextSelectionOption(AxisControllerKind.Simulated.ToString(), "仿真"),
        new TextSelectionOption(AxisControllerKind.Googol.ToString(), "真实硬件")
    ]);

    public ObservableCollection<ExtendedIoModuleItem> ExtendedModules { get; } = new();

    public ObservableCollection<AxisPointItem> Axes { get; } = new();

    public ObservableCollection<IoPointItem> IoPoints { get; } = new();

    public ObservableCollection<IoPointItem> OnboardInputPoints { get; } = new();

    public ObservableCollection<IoPointItem> OnboardOutputPoints { get; } = new();

    public ObservableCollection<IoPointItem> AxisInputPoints { get; } = new();

    public ObservableCollection<IoPointItem> AxisOutputPoints { get; } = new();

    public ObservableCollection<IoPointItem> ExtendedInputPoints { get; } = new();

    public ObservableCollection<IoPointItem> ExtendedOutputPoints { get; } = new();

    public ObservableCollection<IoPointItem> InputPoints { get; } = new();

    public ObservableCollection<IoPointItem> OutputPoints { get; } = new();

    public ObservableCollection<InterpolationAxisTargetItem> InterpolationTargets { get; } = new();

    public ObservableCollection<InterpolationBufferedOutputItem> InterpolationBufferedOutputs { get; } = new();

    public ObservableCollection<string> HomeModeOptions { get; } = new(
    [
        AxisHomeMode.LimitHomeIndex.ToString(),
        AxisHomeMode.LimitHome.ToString(),
        AxisHomeMode.LimitIndex.ToString(),
        AxisHomeMode.Limit.ToString(),
        AxisHomeMode.Home.ToString(),
        AxisHomeMode.HomeIndex.ToString(),
        AxisHomeMode.Index.ToString()
    ]);

    public DelegateCommand ConnectAllCommand { get; }

    public DelegateCommand ServoOnCommand { get; }

    public DelegateCommand ServoOffCommand { get; }

    public DelegateCommand ClearAxisAlarmCommand { get; }

    public DelegateCommand ZeroAxisPositionCommand { get; }

    public DelegateCommand HomeAxisCommand { get; }

    public DelegateCommand MoveAxisCommand { get; }

    public DelegateCommand MovePositiveStepCommand { get; }

    public DelegateCommand MoveNegativeStepCommand { get; }

    public DelegateCommand PrecheckLinearInterpolationCommand { get; }

    public DelegateCommand RunLinearInterpolationCommand { get; }

    public DelegateCommand AddBeforeMotionOutputCommand { get; }

    public DelegateCommand AddAfterMotionOutputCommand { get; }

    public DelegateCommand<object> SelectAxisCommand { get; }

    public DelegateCommand StopAxisCommand { get; }

    public DelegateCommand EmergencyStopCommand { get; }

    public DelegateCommand RefreshAxisStatusCommand { get; }

    public DelegateCommand RefreshIoPointsCommand { get; }

    public DelegateCommand ConnectCommunicationDeviceCommand { get; }

    public DelegateCommand ReadCommunicationAddressCommand { get; }

    public DelegateCommand WriteCommunicationAddressCommand { get; }

    public DelegateCommand SaveCommunicationDeviceCommand { get; }

    public DelegateCommand TcpConnectCommand { get; }

    public DelegateCommand TcpDisconnectCommand { get; }

    public DelegateCommand TcpSendCommand { get; }

    public DelegateCommand TcpClearCommand { get; }

    public DelegateCommand SerialConnectCommand { get; }

    public DelegateCommand SerialDisconnectCommand { get; }

    public DelegateCommand SerialSendCommand { get; }

    public DelegateCommand SerialClearCommand { get; }

    public DelegateCommand<object> ToggleIoPointCommand { get; }

    public DelegateCommand AddCardCommand { get; }

    public DelegateCommand AddExtendedModuleCommand { get; }

    public DelegateCommand AddAxisCommand { get; }

    public DelegateCommand AddInputPointCommand { get; }

    public DelegateCommand AddOutputPointCommand { get; }

    public DelegateCommand AddAxisInputPointCommand { get; }

    public DelegateCommand AddAxisOutputPointCommand { get; }

    public DelegateCommand AddExtendedInputPointCommand { get; }

    public DelegateCommand AddExtendedOutputPointCommand { get; }

    public DelegateCommand<object> RemoveCardCommand { get; }

    public DelegateCommand<object> RemoveExtendedModuleCommand { get; }

    public DelegateCommand<object> RemoveAxisCommand { get; }

    public DelegateCommand<object> RemoveIoPointCommand { get; }

    public DelegateCommand SaveConfigurationCommand { get; }

    public DelegateCommand ReloadConfigurationCommand { get; }

    public DelegateCommand ShowDebugWorkspaceCommand { get; }

    public DelegateCommand ShowConfigurationWorkspaceCommand { get; }

    public string DebugMode
    {
        get => _debugMode;
        set
        {
            var resolved = ResolveDebugWorkbenchKind(value).ToString();
            if (SetProperty(ref _debugMode, resolved))
            {
                RefreshDebugModeState();
                QueueDebugSelectionSave();
            }
        }
    }

    public string SelectedCommunicationDeviceKey
    {
        get => _selectedCommunicationDeviceKey;
        set
        {
            if (SetProperty(ref _selectedCommunicationDeviceKey, value?.Trim() ?? string.Empty))
            {
                ApplySelectedCommunicationDevice();
                QueueDebugSelectionSave();
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
                RaisePropertyChanged(nameof(TcpConnectionFieldsVisibility));
                RaisePropertyChanged(nameof(SerialConnectionFieldsVisibility));
                RaisePropertyChanged(nameof(ModbusTcpConnectionFieldsVisibility));
                RaisePropertyChanged(nameof(ModbusSerialConnectionFieldsVisibility));
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

    public string TcpHost
    {
        get => _tcpHost;
        set => SetProperty(ref _tcpHost, value?.Trim() ?? string.Empty);
    }

    public string TcpPort
    {
        get => _tcpPort;
        set => SetProperty(ref _tcpPort, value?.Trim() ?? string.Empty);
    }

    public string TcpPayload
    {
        get => _tcpPayload;
        set => SetProperty(ref _tcpPayload, value ?? string.Empty);
    }

    public string TcpResponse
    {
        get => _tcpResponse;
        private set => SetProperty(ref _tcpResponse, value ?? string.Empty);
    }

    public string TcpStatusText
    {
        get => _tcpStatusText;
        private set => SetProperty(ref _tcpStatusText, value ?? string.Empty);
    }

    public string TcpMode
    {
        get => _tcpMode;
        set
        {
            var resolved = IsTcpServerMode(value) ? TcpServerMode : TcpClientMode;
            if (SetProperty(ref _tcpMode, resolved))
            {
                _tcpSession.Disconnect("TCP 模式已切换，当前连接已断开。");
                RaiseTcpModeProperties();
                RaisePropertyChanged(nameof(ConnectButtonText));
            }
        }
    }

    public string TcpHostLabel => IsTcpServerMode(TcpMode) ? "监听地址" : "目标 Host";

    public string TcpPortLabel => IsTcpServerMode(TcpMode) ? "监听端口" : "目标端口";

    public string TcpConnectButtonText => IsTcpServerMode(TcpMode)
        ? (_tcpIsListening ? "监听中" : "开始监听")
        : (_tcpIsConnected ? "已连接" : "连接");

    public string TcpDisconnectButtonText => IsTcpServerMode(TcpMode) ? "停止监听" : "断开";

    public string TcpSendButtonText => IsTcpServerMode(TcpMode) ? "发送给客户端" : "发送";

    public string TcpSessionText => IsTcpServerMode(TcpMode)
        ? (_tcpIsListening ? $"服务端监听中，对端：{TcpPeerText}" : "服务端未监听")
        : (_tcpIsConnected ? $"客户端已连接：{TcpPeerText}" : "客户端未连接");

    public string TcpPeerText
    {
        get => _tcpPeerText;
        private set
        {
            if (SetProperty(ref _tcpPeerText, value ?? string.Empty))
            {
                RaisePropertyChanged(nameof(TcpSessionText));
            }
        }
    }

    public bool TcpIsConnected
    {
        get => _tcpIsConnected;
        private set
        {
            if (SetProperty(ref _tcpIsConnected, value))
            {
                RaiseTcpSessionProperties();
            }
        }
    }

    public bool TcpIsListening
    {
        get => _tcpIsListening;
        private set
        {
            if (SetProperty(ref _tcpIsListening, value))
            {
                RaiseTcpSessionProperties();
            }
        }
    }

    public string TcpFrameMode
    {
        get => _tcpFrameMode;
        set
        {
            var resolved = value?.Trim() switch
            {
                TcpFrameDelimiter => TcpFrameDelimiter,
                TcpFrameFixedLength => TcpFrameFixedLength,
                TcpFrameLengthPrefix => TcpFrameLengthPrefix,
                _ => TcpFrameRaw
            };

            if (SetProperty(ref _tcpFrameMode, resolved))
            {
                RaiseTcpFrameProperties();
                AppendTcpLog($"FRAME MODE -> {GetTcpFrameModeText(resolved)}");
            }
        }
    }

    public string TcpDelimiterText
    {
        get => _tcpDelimiterText;
        set => SetProperty(ref _tcpDelimiterText, string.IsNullOrWhiteSpace(value) ? @"\r\n" : value.Trim());
    }

    public string TcpFixedFrameLength
    {
        get => _tcpFixedFrameLength;
        set => SetProperty(ref _tcpFixedFrameLength, string.IsNullOrWhiteSpace(value) ? "1" : value.Trim());
    }

    public string TcpLengthPrefixBytes
    {
        get => _tcpLengthPrefixBytes;
        set => SetProperty(ref _tcpLengthPrefixBytes, value?.Trim() is "1" or "2" or "4" ? value.Trim() : "2");
    }

    public bool TcpLengthPrefixLittleEndian
    {
        get => _tcpLengthPrefixLittleEndian;
        set => SetProperty(ref _tcpLengthPrefixLittleEndian, value);
    }

    public string TcpMaxFrameLength
    {
        get => _tcpMaxFrameLength;
        set => SetProperty(ref _tcpMaxFrameLength, string.IsNullOrWhiteSpace(value) ? "4096" : value.Trim());
    }

    public bool TcpAppendDelimiterOnSend
    {
        get => _tcpAppendDelimiterOnSend;
        set => SetProperty(ref _tcpAppendDelimiterOnSend, value);
    }

    public bool TcpPrefixPayloadOnSend
    {
        get => _tcpPrefixPayloadOnSend;
        set => SetProperty(ref _tcpPrefixPayloadOnSend, value);
    }

    public Visibility TcpDelimiterOptionsVisibility => TcpFrameMode == TcpFrameDelimiter
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TcpFixedLengthOptionsVisibility => TcpFrameMode == TcpFrameFixedLength
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TcpLengthPrefixOptionsVisibility => TcpFrameMode == TcpFrameLengthPrefix
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool TcpSendAsHex
    {
        get => _tcpSendAsHex;
        set => SetProperty(ref _tcpSendAsHex, value);
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
                TcpFrameDelimiter => TcpFrameDelimiter,
                TcpFrameFixedLength => TcpFrameFixedLength,
                TcpFrameLengthPrefix => TcpFrameLengthPrefix,
                _ => TcpFrameRaw
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

    public Visibility SerialDelimiterOptionsVisibility => SerialFrameMode == TcpFrameDelimiter
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SerialFixedLengthOptionsVisibility => SerialFrameMode == TcpFrameFixedLength
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SerialLengthPrefixOptionsVisibility => SerialFrameMode == TcpFrameLengthPrefix
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool SerialSendAsHex
    {
        get => _serialSendAsHex;
        set => SetProperty(ref _serialSendAsHex, value);
    }

    public AxisPointItem? SelectedAxis
    {
        get => _selectedAxis;
        set
        {
            if (!SetProperty(ref _selectedAxis, value) || value is null)
            {
                return;
            }

            AxisKey = value.Key;
            AxisSpeed = value.DefaultSpeed.ToString("0.###", CultureInfo.InvariantCulture);
            AxisAcceleration = value.DefaultAcceleration.ToString("0.###", CultureInfo.InvariantCulture);
            AxisJogSpeed = Math.Max(value.DefaultSpeed / 4, 1).ToString("0.###", CultureInfo.InvariantCulture);
            foreach (var axis in Axes)
            {
                axis.IsSelected = ReferenceEquals(axis, value);
            }

            RaisePropertyChanged(nameof(SelectedAxisTitle));
            RaisePropertyChanged(nameof(AxisSoftLimitText));
            _ = RefreshAxisStatusAsync();
        }
    }

    public string AxisKey
    {
        get => _axisKey;
        set => SetProperty(ref _axisKey, string.IsNullOrWhiteSpace(value) ? AxisDefaults.PrimaryAxisKey : value.Trim());
    }

    public string AxisTargetPosition
    {
        get => _axisTargetPosition;
        set => SetProperty(ref _axisTargetPosition, value);
    }

    public string AxisSpeed
    {
        get => _axisSpeed;
        set => SetProperty(ref _axisSpeed, value);
    }

    public string AxisAcceleration
    {
        get => _axisAcceleration;
        set => SetProperty(ref _axisAcceleration, value);
    }

    public string AxisStepDistance
    {
        get => _axisStepDistance;
        set => SetProperty(ref _axisStepDistance, value);
    }

    public string AxisJogSpeed
    {
        get => _axisJogSpeed;
        set => SetProperty(ref _axisJogSpeed, value);
    }

    public string InterpolationSpeed
    {
        get => _interpolationSpeed;
        set => SetProperty(ref _interpolationSpeed, value);
    }

    public string InterpolationAcceleration
    {
        get => _interpolationAcceleration;
        set => SetProperty(ref _interpolationAcceleration, value);
    }

    public string InterpolationCoordinateSystem
    {
        get => _interpolationCoordinateSystem;
        set => SetProperty(ref _interpolationCoordinateSystem, value);
    }

    public string InterpolationFifo
    {
        get => _interpolationFifo;
        set => SetProperty(ref _interpolationFifo, value);
    }

    public string AxisStatusText
    {
        get => _axisStatusText;
        private set => SetProperty(ref _axisStatusText, value);
    }

    public string AxisEncoderText
    {
        get => _axisEncoderText;
        private set => SetProperty(ref _axisEncoderText, value);
    }

    public string AxisCommandText
    {
        get => _axisCommandText;
        private set => SetProperty(ref _axisCommandText, value);
    }

    public string AxisServoText
    {
        get => _axisServoText;
        private set => SetProperty(ref _axisServoText, value);
    }

    public string AxisReadyText
    {
        get => _axisReadyText;
        private set => SetProperty(ref _axisReadyText, value);
    }

    public string AxisInPositionText
    {
        get => _axisInPositionText;
        private set => SetProperty(ref _axisInPositionText, value);
    }

    public string AxisAlarmText
    {
        get => _axisAlarmText;
        private set => SetProperty(ref _axisAlarmText, value);
    }

    public string AxisHomeText
    {
        get => _axisHomeText;
        private set => SetProperty(ref _axisHomeText, value);
    }

    public string AxisPositiveLimitText
    {
        get => _axisPositiveLimitText;
        private set => SetProperty(ref _axisPositiveLimitText, value);
    }

    public string AxisNegativeLimitText
    {
        get => _axisNegativeLimitText;
        private set => SetProperty(ref _axisNegativeLimitText, value);
    }

    public string SelectedAxisTitle => SelectedAxis is null
        ? "未选择轴"
        : $"{SelectedAxis.ChannelText}  {SelectedAxis.Key} / {SelectedAxis.Name}";

    public string AxisSoftLimitText => SelectedAxis is null
        ? "软限位 -"
        : $"软限位 {SelectedAxis.SoftLimitNegative:0.###} ~ {SelectedAxis.SoftLimitPositive:0.###}";

    public string IoStatusText
    {
        get => _ioStatusText;
        private set => SetProperty(ref _ioStatusText, value);
    }

    public string GoogolCardNo
    {
        get => _googolCardNo;
        set
        {
            if (SetProperty(ref _googolCardNo, value))
            {
                RefreshSelectionOptions();
                MarkConfigurationDirty("轴卡设置已修改，保存后生效。");
            }
        }
    }

    public string GoogolConfigPath
    {
        get => _googolConfigPath;
        set
        {
            if (SetProperty(ref _googolConfigPath, value))
            {
                MarkConfigurationDirty("轴卡设置已修改，保存后生效。");
            }
        }
    }

    public string ConfigurationStatusText
    {
        get => _configurationStatusText;
        private set => SetProperty(ref _configurationStatusText, value);
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
                    "设备调试-轴卡设置",
                    value,
                    _ => SaveConfigurationAsync(throwOnError: true),
                    "轴卡、轴、扩展 IO 或点位配置");
            }
        }
    }

    public string AxisControllerMode
    {
        get => _axisControllerMode;
        set
        {
            if (SetProperty(ref _axisControllerMode, ResolveAxisControllerKind(value).ToString()))
            {
                RaisePropertyChanged(nameof(ControllerModeText));
                RaisePropertyChanged(nameof(AxisDriverSummaryText));
                MarkConfigurationDirty("轴卡设置已修改，保存后生效。");
            }
        }
    }

    public bool IsDebugWorkspace
    {
        get => _isDebugWorkspace;
        set
        {
            if (SetProperty(ref _isDebugWorkspace, value))
            {
                RaisePropertyChanged(nameof(DebugWorkspaceVisibility));
                RaisePropertyChanged(nameof(AxisDebugWorkspaceVisibility));
                RaisePropertyChanged(nameof(PlcDebugWorkspaceVisibility));
                RaisePropertyChanged(nameof(TcpDebugWorkspaceVisibility));
                RaisePropertyChanged(nameof(ModbusDebugWorkspaceVisibility));
                RaisePropertyChanged(nameof(SerialDebugWorkspaceVisibility));
                RaisePropertyChanged(nameof(CommunicationDebugWorkspaceVisibility));
                RaisePropertyChanged(nameof(ConfigurationWorkspaceVisibility));
                RaisePropertyChanged(nameof(DebugTabBackground));
                RaisePropertyChanged(nameof(ConfigurationTabBackground));
                RaisePropertyChanged(nameof(DebugTabBorderBrush));
                RaisePropertyChanged(nameof(ConfigurationTabBorderBrush));
                ApplyAxisAutoRefreshState();
                ApplyIoAutoRefreshState();
            }
        }
    }

    public Visibility DebugWorkspaceVisibility => IsDebugWorkspace ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AxisDebugWorkspaceVisibility => IsDebugWorkspace && CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.AxisCard
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PlcDebugWorkspaceVisibility => IsDebugWorkspace && CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Plc
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TcpDebugWorkspaceVisibility => IsDebugWorkspace && CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Tcp
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ModbusDebugWorkspaceVisibility => IsDebugWorkspace && CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Modbus
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SerialDebugWorkspaceVisibility => IsDebugWorkspace && CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Serial
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CommunicationDebugWorkspaceVisibility => Visibility.Collapsed;

    public Visibility TcpDebugPanelVisibility => Visibility.Collapsed;

    public Visibility AddressableDebugPanelVisibility => Visibility.Collapsed;

    public Visibility TcpConnectionFieldsVisibility => CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Plc &&
                                                       !IsSimulatedProtocol(CommunicationProtocol) &&
                                                       !IsSerialProtocol(CommunicationProtocol)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SerialConnectionFieldsVisibility => CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Plc &&
                                                          IsSerialProtocol(CommunicationProtocol)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ModbusTcpConnectionFieldsVisibility => CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Modbus &&
                                                             !IsSerialProtocol(CommunicationProtocol)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ModbusSerialConnectionFieldsVisibility => CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Modbus &&
                                                                IsSerialProtocol(CommunicationProtocol)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ConfigurationWorkspaceVisibility => IsDebugWorkspace ? Visibility.Collapsed : Visibility.Visible;

    public string DebugTabBackground => IsDebugWorkspace ? "#FF12352F" : "#FF111B23";

    public string ConfigurationTabBackground => IsDebugWorkspace ? "#FF111B23" : "#FF12352F";

    public string DebugTabBorderBrush => IsDebugWorkspace ? "#FF33D6A6" : "#FF2C3B44";

    public string ConfigurationTabBorderBrush => IsDebugWorkspace ? "#FF2C3B44" : "#FF33D6A6";

    public string WorkbenchTitle => CurrentDebugWorkbenchKind switch
    {
        DeviceDebugWorkbenchKind.Plc => "PLC 调试台",
        DeviceDebugWorkbenchKind.Tcp => "TCP 通讯调试台",
        DeviceDebugWorkbenchKind.Modbus => "Modbus 调试台",
        DeviceDebugWorkbenchKind.Serial => "串口调试台",
        _ => "运动控制工程台"
    };

    public string WorkbenchSubtitle => CurrentDebugWorkbenchKind switch
    {
        DeviceDebugWorkbenchKind.Plc => "选择 PLC 品牌、协议和连接参数，使用 CVCommunication 执行地址读写",
        DeviceDebugWorkbenchKind.Tcp => "直接连接 TCP 服务端并发送文本或十六进制报文",
        DeviceDebugWorkbenchKind.Modbus => "按 Modbus TCP/RTU/ASCII 协议调试寄存器与线圈地址",
        DeviceDebugWorkbenchKind.Serial => "按串口号、波特率、校验位等参数调试串口仪器",
        _ => "按轴卡驱动配置固高、研华等运动控制卡和 IO 点表"
    };

    public string ConnectButtonText => CurrentDebugWorkbenchKind switch
    {
        DeviceDebugWorkbenchKind.Plc => "连接 PLC",
        DeviceDebugWorkbenchKind.Tcp => TcpDebug.IsServerMode ? "开始监听 TCP" : "连接 TCP",
        DeviceDebugWorkbenchKind.Modbus => "连接 Modbus",
        DeviceDebugWorkbenchKind.Serial => "发送串口",
        _ => "连接轴卡 / IO"
    };

    private DeviceDebugWorkbenchKind CurrentDebugWorkbenchKind => ResolveDebugWorkbenchKind(DebugMode);

    public string ControllerModeText => ResolveAxisControllerKind(AxisControllerMode) == AxisControllerKind.Simulated
        ? "仿真"
        : "真实硬件";

    public string AxisDriverSummaryText
    {
        get
        {
            var drivers = Cards
                .Select(card => card.DriverDisplayText)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return drivers.Length == 0
                ? $"{ControllerModeText} / 未配置轴卡"
                : $"{ControllerModeText} / {string.Join(" / ", drivers)}";
        }
    }

    public string AxisServoBrush => AxisServoText == "ON" ? "#FF33D6A6" : "#FFFFC95A";

    public string AxisReadyBrush => AxisReadyText == "READY" ? "#FF33D6A6" : "#FFFFC95A";

    public string AxisInPositionBrush => AxisInPositionText == "IN-POS" ? "#FF33D6A6" : "#FFFFC95A";

    public string AxisAlarmBrush => AxisAlarmText == "NORMAL" ? "#FF33D6A6" : "#FFFF667A";

    public string AxisHomeBrush => AxisHomeText is "HOME" or "HOMED" or "已回原" ? "#FF33D6A6" : "#FF7AD7FF";

    public string AxisPositiveLimitBrush => AxisPositiveLimitText == "CLEAR" ? "#FF33D6A6" : "#FFFF667A";

    public string AxisNegativeLimitBrush => AxisNegativeLimitText == "CLEAR" ? "#FF33D6A6" : "#FFFF667A";

    private void MarkConfigurationDirty(string statusText)
    {
        if (_loadingConfiguration || _refreshingSelectionOptions)
        {
            return;
        }

        HasUnsavedChanges = true;
        ConfigurationStatusText = statusText;
    }

    private void ConfigureCardItem(GoogolCardItem card)
    {
        card.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(GoogolCardItem.Key)
                or nameof(GoogolCardItem.Name)
                or nameof(GoogolCardItem.Driver)
                or nameof(GoogolCardItem.CardNo)
                or nameof(GoogolCardItem.AxisCount)
                or nameof(GoogolCardItem.InputCount)
                or nameof(GoogolCardItem.OutputCount))
            {
                if (args.PropertyName == nameof(GoogolCardItem.Driver)
                    && card.DriverKind != AxisCardDriverKind.Simulated)
                {
                    AxisControllerMode = AxisControllerKind.Googol.ToString();
                }

                RaisePropertyChanged(nameof(AxisDriverSummaryText));
                RefreshSelectionOptions();
                MarkConfigurationDirty("轴卡设置已修改，保存后生效。");
            }
        };
    }

    private void ConfigureExtendedModuleItem(ExtendedIoModuleItem module)
    {
        module.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ExtendedIoModuleItem.ParentCardKey)
                or nameof(ExtendedIoModuleItem.ParentCardNo)
                or nameof(ExtendedIoModuleItem.ModuleNo)
                or nameof(ExtendedIoModuleItem.Model)
                or nameof(ExtendedIoModuleItem.StartAddress)
                or nameof(ExtendedIoModuleItem.InputCount)
                or nameof(ExtendedIoModuleItem.OutputCount)
                or nameof(ExtendedIoModuleItem.ConfigPath))
            {
                RefreshSelectionOptions();
                MarkConfigurationDirty("扩展 IO 模块配置已修改，保存后生效。");
            }
        };
    }

    private void ConfigureAxisItem(AxisPointItem axis)
    {
        axis.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AxisPointItem.CardKey)
                or nameof(AxisPointItem.CardNo))
            {
                RefreshSelectionOptions();
                MarkConfigurationDirty("轴配置已修改，保存后生效。");
            }
        };
    }

    private void ConfigureIoPointItem(IoPointItem point)
    {
        point.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IoPointItem.CardKey)
                or nameof(IoPointItem.CardNo)
                or nameof(IoPointItem.ParentCardKey)
                or nameof(IoPointItem.ParentCardNo)
                or nameof(IoPointItem.AxisNo)
                or nameof(IoPointItem.ModuleNo)
                or nameof(IoPointItem.PointNo)
                or nameof(IoPointItem.Direction)
                or nameof(IoPointItem.Source))
            {
                RefreshSelectionOptions();
                MarkConfigurationDirty("IO 点位配置已修改，保存后生效。");
            }
        };
    }

    private void RefreshSelectionOptions()
    {
        if (_refreshingSelectionOptions)
        {
            return;
        }

        _refreshingSelectionOptions = true;
        try
        {
            var cards = Cards.OrderBy(card => card.CardNo).ToArray();
            NumericSelectionOption.Replace(
                CardNoOptions,
                cards.Length == 0
                    ? new[] { new NumericSelectionOption(ParseShort(GoogolCardNo, 0), $"C{ParseShort(GoogolCardNo, 0)}") }
                    : cards.Select(card => new NumericSelectionOption(card.CardNo, $"C{card.CardNo}")));
            AxisCardSelectionOption.Replace(
                AxisCardOptions,
                cards.Length == 0
                    ? new[] { new AxisCardSelectionOption("card1", ParseShort(GoogolCardNo, 0), $"card1 / C{ParseShort(GoogolCardNo, 0)}") }
                    : cards.Select(card => new AxisCardSelectionOption(ResolveCardKey(card), card.CardNo, FormatAxisCardOption(card))));

            foreach (var module in ExtendedModules)
            {
                RefreshExtendedModuleOptions(module);
            }

            foreach (var axis in Axes)
            {
                RefreshAxisOptions(axis);
            }

            foreach (var point in IoPoints)
            {
                RefreshIoPointOptions(point);
            }
        }
        finally
        {
            _refreshingSelectionOptions = false;
        }
    }

    private void RefreshExtendedModuleOptions(ExtendedIoModuleItem module)
    {
        var card = ResolveModuleParentCard(module);
        if (card is not null)
        {
            var cardKey = ResolveCardKey(card);
            if (!string.Equals(module.ParentCardKey, cardKey, StringComparison.OrdinalIgnoreCase))
            {
                module.ParentCardKey = cardKey;
            }

            if (module.ParentCardNo != card.CardNo)
            {
                module.ParentCardNo = card.CardNo;
            }
        }
        else if (!ContainsOption(CardNoOptions, module.ParentCardNo))
        {
            module.ParentCardNo = FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        }

        module.SetModuleNoOptions(RangeWithCurrent(0, 15, module.ModuleNo, value => $"M{value}"));
        module.SetStartAddressOptions(RangeWithCurrent(0, 15, module.StartAddress, value => value.ToString(CultureInfo.InvariantCulture)));
    }

    private void RefreshAxisOptions(AxisPointItem axis)
    {
        var card = ResolveAxisCard(axis);
        if (card is not null)
        {
            var cardKey = ResolveCardKey(card);
            if (!string.Equals(axis.CardKey, cardKey, StringComparison.OrdinalIgnoreCase))
            {
                axis.CardKey = cardKey;
            }

            if (axis.CardNo != card.CardNo)
            {
                axis.CardNo = card.CardNo;
            }
        }
        else if (!ContainsOption(CardNoOptions, axis.CardNo))
        {
            axis.CardNo = FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        }

        if (axis.AxisNo <= 0)
        {
            axis.AxisNo = 1;
        }

        var axisCount = Math.Max(card?.AxisCount ?? 8, 1);
        axis.SetAxisNoOptions(RangeWithCurrent(1, ToPositiveShort(axisCount), axis.AxisNo, value => $"CH-{value:00}"));
    }

    private void RefreshIoPointOptions(IoPointItem point)
    {
        if (point.PointNo <= 0)
        {
            point.PointNo = 1;
        }

        if (point.Source == IoPointSource.ExtendedModule)
        {
            var parentCard = ResolvePointParentCard(point);
            if (parentCard is not null)
            {
                var parentCardKey = ResolveCardKey(parentCard);
                if (!string.Equals(point.ParentCardKey, parentCardKey, StringComparison.OrdinalIgnoreCase))
                {
                    point.ParentCardKey = parentCardKey;
                }

                if (!string.Equals(point.CardKey, parentCardKey, StringComparison.OrdinalIgnoreCase))
                {
                    point.CardKey = parentCardKey;
                }

                if (point.ParentCardNo != parentCard.CardNo)
                {
                    point.ParentCardNo = parentCard.CardNo;
                }
            }
            else if (!ContainsOption(CardNoOptions, point.ParentCardNo))
            {
                point.ParentCardNo = FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
            }

            point.CardNo = point.ParentCardNo;
            point.AxisNo = -1;
            point.SetAxisNoOptions(Array.Empty<NumericSelectionOption>());
            var modules = ExtendedModules
                .Where(module => IsModuleOnCard(module, point.ParentCardKey, point.ParentCardNo))
                .OrderBy(module => module.ModuleNo)
                .ToArray();

            var moduleOptions = modules
                .Select(module => new NumericSelectionOption(module.ModuleNo, $"M{module.ModuleNo}"))
                .ToList();
            if (moduleOptions.Count == 0)
            {
                var moduleNo = point.ModuleNo >= 0 ? point.ModuleNo : (short)0;
                moduleOptions.Add(new NumericSelectionOption(moduleNo, $"M{moduleNo}"));
            }
            else if (!moduleOptions.Any(option => option.Value == point.ModuleNo) && point.ModuleNo >= 0)
            {
                moduleOptions.Add(new NumericSelectionOption(point.ModuleNo, $"M{point.ModuleNo}"));
            }

            point.SetModuleNoOptions(moduleOptions.OrderBy(option => option.Value));
            if (point.ModuleNo < 0)
            {
                point.ModuleNo = FirstOptionValue(point.ModuleNoOptions, 0);
            }

            var selectedModule = modules.FirstOrDefault(module => module.ModuleNo == point.ModuleNo);
            if (!string.IsNullOrWhiteSpace(selectedModule?.ConfigPath))
            {
                point.ModuleConfigPath = selectedModule.ConfigPath;
            }

            var pointCount = point.Direction == IoPointDirection.Input
                ? selectedModule?.InputCount ?? 16
                : selectedModule?.OutputCount ?? 16;
            point.SetPointNoOptions(RangeWithCurrent(1, ToPositiveShort(pointCount), point.PointNo, value => $"{point.DirectionCode}{value}"));
        }
        else
        {
            var card = ResolvePointCard(point);
            if (card is not null)
            {
                var cardKey = ResolveCardKey(card);
                if (!string.Equals(point.CardKey, cardKey, StringComparison.OrdinalIgnoreCase))
                {
                    point.CardKey = cardKey;
                }

                if (point.CardNo != card.CardNo)
                {
                    point.CardNo = card.CardNo;
                }
            }
            else if (!ContainsOption(CardNoOptions, point.CardNo))
            {
                point.CardNo = FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
            }

            point.ParentCardKey = string.Empty;
            point.ParentCardNo = -1;
            point.ModuleNo = -1;
            point.ModuleConfigPath = string.Empty;
            point.SetModuleNoOptions(Array.Empty<NumericSelectionOption>());

            if (point.Source == IoPointSource.AxisOnboard)
            {
                if (point.AxisNo <= 0)
                {
                    point.AxisNo = 1;
                }

                var axisCount = Math.Max(card?.AxisCount ?? 4, 1);
                point.SetAxisNoOptions(RangeWithCurrent(1, ToPositiveShort(axisCount), point.AxisNo, value => $"CH-{value:00}"));
                if (point.Direction == IoPointDirection.Output && point.PointNo < 4)
                {
                    point.PointNo = 4;
                }

                point.SetPointNoOptions(point.Direction == IoPointDirection.Input
                    ? RangeWithCurrent(1, 4, point.PointNo, value => $"AXDI{value}")
                    : RangeWithCurrent(4, 7, point.PointNo, value => $"AXDO{value}"));
            }
            else
            {
                point.AxisNo = -1;
                point.SetAxisNoOptions(Array.Empty<NumericSelectionOption>());
                var pointCount = point.Direction == IoPointDirection.Input
                    ? card?.InputCount ?? 16
                    : card?.OutputCount ?? 16;
                point.SetPointNoOptions(RangeWithCurrent(1, ToPositiveShort(pointCount), point.PointNo, value => $"{point.DirectionCode}{value}"));
            }
        }

        point.RefreshAddress();
    }

    private short ResolveNextAxisNo(string cardKey, short cardNo)
    {
        var card = ResolveCardByKey(cardKey) ?? ResolveCard(cardNo);
        var axisCount = Math.Max(card?.AxisCount ?? 8, 1);
        var used = Axes
            .Where(axis => IsAxisOnCard(axis, cardKey, cardNo))
            .Select(axis => axis.AxisNo)
            .ToHashSet();

        for (short axisNo = 1; axisNo <= axisCount && axisNo < short.MaxValue; axisNo++)
        {
            if (!used.Contains(axisNo))
            {
                return axisNo;
            }
        }

        return ToPositiveShort(used.Select(value => (int)value).DefaultIfEmpty(0).Max() + 1);
    }

    private short ResolveFirstAxisNo(string cardKey, short cardNo)
    {
        return Axes
            .Where(axis => axis.Enabled)
            .Where(axis => IsAxisOnCard(axis, cardKey, cardNo))
            .OrderBy(axis => axis.AxisNo)
            .Select(axis => axis.AxisNo)
            .FirstOrDefault((short)1);
    }

    private short ResolveNextIoPointNo(
        IoPointDirection direction,
        IoPointSource source,
        string cardKey,
        short cardNo,
        string parentCardKey,
        short parentCardNo,
        short moduleNo,
        short axisNo)
    {
        var card = ResolveCardByKey(cardKey) ?? ResolveCard(cardNo);
        var module = ExtendedModules.FirstOrDefault(item => IsModuleOnCard(item, parentCardKey, parentCardNo) && item.ModuleNo == moduleNo);
        var firstPointNo = source == IoPointSource.AxisOnboard && direction == IoPointDirection.Output
            ? (short)4
            : (short)1;
        var lastPointNo = source switch
        {
            IoPointSource.ExtendedModule => direction == IoPointDirection.Input
                ? ToPositiveShort(module?.InputCount ?? 16)
                : ToPositiveShort(module?.OutputCount ?? 16),
            IoPointSource.AxisOnboard => direction == IoPointDirection.Input ? (short)4 : (short)7,
            _ => direction == IoPointDirection.Input
                ? ToPositiveShort(card?.InputCount ?? 16)
                : ToPositiveShort(card?.OutputCount ?? 16)
        };
        var used = IoPoints
            .Where(point => point.Direction == direction)
            .Where(point => point.Source == source)
            .Where(point => source == IoPointSource.ExtendedModule
                ? IsPointOnCard(point, parentCardKey, parentCardNo) && point.ModuleNo == moduleNo
                : source == IoPointSource.AxisOnboard
                    ? IsPointOnCard(point, cardKey, cardNo) && point.AxisNo == axisNo
                    : IsPointOnCard(point, cardKey, cardNo))
            .Select(point => point.PointNo)
            .ToHashSet();

        for (var pointNo = firstPointNo; pointNo <= lastPointNo && pointNo < short.MaxValue; pointNo++)
        {
            if (!used.Contains(pointNo))
            {
                return pointNo;
            }
        }

        return ToPositiveShort(used.Select(value => (int)value).DefaultIfEmpty(0).Max() + 1);
    }

    private GoogolCardItem? ResolveCard(short cardNo)
    {
        return Cards.FirstOrDefault(card => card.CardNo == cardNo) ?? Cards.FirstOrDefault();
    }

    private GoogolCardItem? ResolveCardByKey(string? cardKey)
    {
        if (string.IsNullOrWhiteSpace(cardKey))
        {
            return null;
        }

        return Cards.FirstOrDefault(
            card => string.Equals(ResolveCardKey(card), cardKey.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private GoogolCardItem? ResolveAxisCard(AxisPointItem axis)
    {
        return ResolveCardByKey(axis.CardKey) ?? ResolveCard(axis.CardNo);
    }

    private GoogolCardItem? ResolveModuleParentCard(ExtendedIoModuleItem module)
    {
        return ResolveCardByKey(module.ParentCardKey) ?? ResolveCard(module.ParentCardNo);
    }

    private GoogolCardItem? ResolvePointCard(IoPointItem point)
    {
        return ResolveCardByKey(point.CardKey) ?? ResolveCard(point.CardNo);
    }

    private GoogolCardItem? ResolvePointParentCard(IoPointItem point)
    {
        return ResolveCardByKey(point.ParentCardKey)
            ?? ResolveCardByKey(point.CardKey)
            ?? ResolveCard(point.ParentCardNo >= 0 ? point.ParentCardNo : point.CardNo);
    }

    private static bool IsAxisOnCard(AxisPointItem axis, string cardKey, short cardNo)
    {
        if (!string.IsNullOrWhiteSpace(axis.CardKey) && !string.IsNullOrWhiteSpace(cardKey))
        {
            return string.Equals(axis.CardKey, cardKey, StringComparison.OrdinalIgnoreCase);
        }

        return axis.CardNo == cardNo;
    }

    private static bool IsModuleOnCard(ExtendedIoModuleItem module, string cardKey, short cardNo)
    {
        if (!string.IsNullOrWhiteSpace(module.ParentCardKey) && !string.IsNullOrWhiteSpace(cardKey))
        {
            return string.Equals(module.ParentCardKey, cardKey, StringComparison.OrdinalIgnoreCase);
        }

        return module.ParentCardNo == cardNo;
    }

    private static bool IsPointOnCard(IoPointItem point, string cardKey, short cardNo)
    {
        var pointCardKey = point.Source == IoPointSource.ExtendedModule ? point.ParentCardKey : point.CardKey;
        var pointCardNo = point.Source == IoPointSource.ExtendedModule ? point.ParentCardNo : point.CardNo;
        if (!string.IsNullOrWhiteSpace(pointCardKey) && !string.IsNullOrWhiteSpace(cardKey))
        {
            return string.Equals(pointCardKey, cardKey, StringComparison.OrdinalIgnoreCase);
        }

        return pointCardNo == cardNo;
    }

    private static string ResolveCardKey(GoogolCardItem card)
    {
        return string.IsNullOrWhiteSpace(card.Key) ? $"card{card.CardNo}" : card.Key.Trim();
    }

    private static string FormatAxisCardOption(GoogolCardItem card)
    {
        return $"{ResolveCardKey(card)} / C{card.CardNo} / {card.DriverDisplayText}";
    }

    private static IEnumerable<NumericSelectionOption> RangeWithCurrent(short first, short last, short current, Func<short, string> format)
    {
        var options = NumericSelectionOption.Range(first, last, format).ToList();
        if (!options.Any(option => option.Value == current) && current >= 0)
        {
            options.Add(new NumericSelectionOption(current, format(current)));
        }

        return options.OrderBy(option => option.Value);
    }

    private static bool ContainsOption(IEnumerable<NumericSelectionOption> options, short value)
    {
        return options.Any(option => option.Value == value);
    }

    private static short FirstOptionValue(IEnumerable<NumericSelectionOption> options, short fallback)
    {
        return options.FirstOrDefault()?.Value ?? fallback;
    }

    private static short ToPositiveShort(int value)
    {
        return (short)Math.Clamp(Math.Max(value, 1), 1, short.MaxValue);
    }

    private static int RemoveMatching<T>(ICollection<T> collection, Func<T, bool> predicate)
    {
        var items = collection.Where(predicate).ToArray();
        foreach (var item in items)
        {
            collection.Remove(item);
        }

        return items.Length;
    }

    private void LoadConfiguration(DeviceConfiguration configuration)
    {
        _loadingConfiguration = true;
        try
        {
            _configuration = configuration;
            AxisControllerMode = configuration.AxisController.ToString();
            GoogolCardNo = configuration.GoogolCardNo.ToString(CultureInfo.InvariantCulture);
            GoogolConfigPath = configuration.GoogolConfigPath;
            RaisePropertyChanged(nameof(ControllerModeText));

        Cards.Clear();
        var cardDefinitions = configuration.AxisCards.Count == 0
            ? configuration.GoogolCards.Select(GoogolCardItem.FromDefinition)
            : configuration.AxisCards.Select(GoogolCardItem.FromDefinition);
        foreach (var item in cardDefinitions)
        {
            ConfigureCardItem(item);
            Cards.Add(item);
        }

        if (Cards.Count == 0)
        {
            var item = CreateDefaultCardItem(configuration);
            ConfigureCardItem(item);
            Cards.Add(item);
        }

        ExtendedModules.Clear();
        foreach (var module in configuration.ExtendedIoModules)
        {
            var item = ExtendedIoModuleItem.FromDefinition(module);
            ConfigureExtendedModuleItem(item);
            ExtendedModules.Add(item);
        }

        Axes.Clear();
        foreach (var axis in configuration.Axes)
        {
            var item = AxisPointItem.FromDefinition(axis);
            ConfigureAxisItem(item);
            Axes.Add(item);
        }

        if (Axes.Count == 0)
        {
            var axis = new AxisPointItem
            {
                Key = AxisDefaults.PrimaryAxisKey,
                Name = "X轴",
                CardKey = Cards.Count == 0 ? "card1" : ResolveCardKey(Cards[0]),
                CardNo = ParseShort(GoogolCardNo, 0),
                AxisNo = 1,
                Enabled = true,
                PulsesPerUnit = 1000,
                PositionBand = 0.01,
                DefaultSpeed = 80,
                DefaultAcceleration = 120
            };
            ConfigureAxisItem(axis);
            Axes.Add(axis);
        }

        SelectedAxis = Axes.FirstOrDefault(axis => axis.Enabled) ?? Axes.FirstOrDefault();
        RebuildInterpolationTargets();

        IoPoints.Clear();
        foreach (var point in configuration.IoPoints)
        {
            var item = IoPointItem.FromDefinition(point);
            ConfigureIoPointItem(item);
            IoPoints.Add(item);
        }

        RefreshSelectionOptions();
        RebuildConfigurationIoGroups();
        RebuildIoDebugPoints();
        DebugMode = configuration.Debug.WorkbenchKind.ToString();
        RebuildCommunicationDeviceOptions();
        SelectedCommunicationDeviceKey = string.IsNullOrWhiteSpace(configuration.Debug.SelectedDeviceKey)
            ? CommunicationDeviceOptions.FirstOrDefault()?.Value ?? "plc-main"
            : configuration.Debug.SelectedDeviceKey;
        ApplySelectedCommunicationDevice();
        FieldbusDebug.LoadConfiguration(configuration, CurrentDebugWorkbenchKind);
            RaisePropertyChanged(nameof(AxisDriverSummaryText));
        }
        finally
        {
            _loadingConfiguration = false;
            HasUnsavedChanges = false;
        }
    }

    private void RefreshDebugModeState()
    {
        FieldbusDebug.SetWorkbenchKind(CurrentDebugWorkbenchKind);
        RebuildCommunicationDeviceOptions();
        if (CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.AxisCard)
        {
            SelectedCommunicationDeviceKey = "motion-main";
        }
        else if (CurrentDebugWorkbenchKind != DeviceDebugWorkbenchKind.Tcp &&
                 CommunicationDeviceOptions.All(option => !string.Equals(option.Value, SelectedCommunicationDeviceKey, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedCommunicationDeviceKey = CommunicationDeviceOptions.FirstOrDefault()?.Value ?? string.Empty;
        }

        ApplyDebugModeCommunicationCatalog();
        RaisePropertyChanged(nameof(WorkbenchTitle));
        RaisePropertyChanged(nameof(WorkbenchSubtitle));
        RaisePropertyChanged(nameof(ConnectButtonText));
        RaisePropertyChanged(nameof(DebugWorkspaceVisibility));
        RaisePropertyChanged(nameof(AxisDebugWorkspaceVisibility));
        RaisePropertyChanged(nameof(PlcDebugWorkspaceVisibility));
        RaisePropertyChanged(nameof(TcpDebugWorkspaceVisibility));
        RaisePropertyChanged(nameof(ModbusDebugWorkspaceVisibility));
        RaisePropertyChanged(nameof(SerialDebugWorkspaceVisibility));
        RaisePropertyChanged(nameof(CommunicationDebugWorkspaceVisibility));
        RaisePropertyChanged(nameof(TcpDebugPanelVisibility));
        RaisePropertyChanged(nameof(AddressableDebugPanelVisibility));
        RaisePropertyChanged(nameof(TcpConnectionFieldsVisibility));
        RaisePropertyChanged(nameof(SerialConnectionFieldsVisibility));
        RaisePropertyChanged(nameof(ModbusTcpConnectionFieldsVisibility));
        RaisePropertyChanged(nameof(ModbusSerialConnectionFieldsVisibility));
        RaiseTcpModeProperties();
        ApplyAxisAutoRefreshState();
        ApplyIoAutoRefreshState();
    }

    private void ApplyDebugModeCommunicationCatalog()
    {
        if (CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Modbus)
        {
            CommunicationBrand = "Modbus";
        }
        else if (CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Plc &&
                 string.Equals(CommunicationBrand, "Modbus", StringComparison.OrdinalIgnoreCase))
        {
            CommunicationBrand = "Mitsubishi";
        }
    }

    private void RaiseTcpModeProperties()
    {
        RaisePropertyChanged(nameof(TcpHostLabel));
        RaisePropertyChanged(nameof(TcpPortLabel));
        RaiseTcpSessionProperties();
    }

    private void RaiseTcpSessionProperties()
    {
        RaisePropertyChanged(nameof(TcpConnectButtonText));
        RaisePropertyChanged(nameof(TcpDisconnectButtonText));
        RaisePropertyChanged(nameof(TcpSendButtonText));
        RaisePropertyChanged(nameof(TcpSessionText));
    }

    private void RaiseSerialSessionProperties()
    {
        RaisePropertyChanged(nameof(SerialConnectButtonText));
        RaisePropertyChanged(nameof(SerialDisconnectButtonText));
        RaisePropertyChanged(nameof(SerialSendButtonText));
        RaisePropertyChanged(nameof(SerialSessionText));
    }

    private void RaiseTcpFrameProperties()
    {
        RaisePropertyChanged(nameof(TcpDelimiterOptionsVisibility));
        RaisePropertyChanged(nameof(TcpFixedLengthOptionsVisibility));
        RaisePropertyChanged(nameof(TcpLengthPrefixOptionsVisibility));
    }

    private void RaiseSerialFrameProperties()
    {
        RaisePropertyChanged(nameof(SerialDelimiterOptionsVisibility));
        RaisePropertyChanged(nameof(SerialFixedLengthOptionsVisibility));
        RaisePropertyChanged(nameof(SerialLengthPrefixOptionsVisibility));
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

    private string ResolveSupportedCommunicationBrand(string? brand)
    {
        var candidate = string.IsNullOrWhiteSpace(brand)
            ? (CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Modbus ? "Modbus" : "Mitsubishi")
            : brand.Trim();

        if (TargetCommunicationBrands.Any(option => string.Equals(option.Value, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return TargetCommunicationBrands.First(option => string.Equals(option.Value, candidate, StringComparison.OrdinalIgnoreCase)).Value;
        }

        return CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Modbus ? "Modbus" : "Mitsubishi";
    }

    private void QueueDebugSelectionSave()
    {
        if (_loadingConfiguration || _disposed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _debugSelectionSaveVersion);
        _ = SaveDebugSelectionAsync(version);
    }

    private async Task SaveDebugSelectionAsync(int version)
    {
        try
        {
            await Task.Delay(150);
            if (_disposed || version != Volatile.Read(ref _debugSelectionSaveVersion))
            {
                return;
            }

            await SaveDebugConfigurationFileAsync(CreateCurrentDebugConfiguration());
        }
        catch (Exception ex)
        {
            ConfigurationStatusText = ex.Message;
        }
    }

    private DeviceDebugConfiguration CreateCurrentDebugConfiguration()
    {
        return new DeviceDebugConfiguration
        {
            WorkbenchKind = CurrentDebugWorkbenchKind,
            SelectedDeviceKey = CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.AxisCard
                ? "motion-main"
                : FieldbusDebug.ResolveSelectedDebugDeviceKey(_configuration.Debug.SelectedDeviceKey)
        };
    }

    private async Task SaveConfigurationFileAsync(DeviceConfiguration configuration)
    {
        await _configurationSaveLock.WaitAsync();
        try
        {
            await _configurationRepository.SaveAsync(configuration);
        }
        finally
        {
            _configurationSaveLock.Release();
        }
    }

    private async Task<DeviceConfiguration> SaveCommunicationDebugConfigurationAsync(DeviceConfiguration configuration)
    {
        await SaveConfigurationFileAsync(configuration);
        _configuration = await _configurationRepository.GetAsync();
        LoadConfiguration(_configuration);
        return _configuration;
    }

    private async Task SaveDebugConfigurationFileAsync(DeviceDebugConfiguration debug)
    {
        await _configurationSaveLock.WaitAsync();
        try
        {
            var latest = await _configurationRepository.GetAsync();
            var updated = latest with
            {
                Debug = debug
            };

            await _configurationRepository.SaveAsync(updated);
            _configuration = await _configurationRepository.GetAsync();
        }
        finally
        {
            _configurationSaveLock.Release();
        }
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
        return CurrentDebugWorkbenchKind switch
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
        CommunicationProtocol = FirstNonEmpty(GetOption(options, "protocol"), device.Driver, CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Serial ? "ModbusRtu" : "ModbusTcp");
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
        SerialFrameMode = FirstNonEmpty(GetOption(options, "frameMode"), TcpFrameRaw);
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

    private async Task ConnectCommunicationDeviceAsync()
    {
        try
        {
            var device = ResolveSelectedAddressableDevice();
            await EnsureDeviceConnectedAsync(device);
            Upsert(device.Snapshot);
            CommunicationStatusText = $"{device.Key} 已连接：{device.Snapshot.Message}";
        }
        catch (Exception ex)
        {
            CommunicationStatusText = ex.Message;
        }
    }

    private async Task ReadCommunicationAddressAsync()
    {
        try
        {
            var device = ResolveSelectedAddressableDevice();
            await EnsureDeviceConnectedAsync(device);
            var value = await device.ReadAsync(CommunicationAddress);
            CommunicationReadValue = value;
            CommunicationStatusText = $"{device.Key} 读取 {CommunicationAddress} = {value}";
            Upsert(device.Snapshot);
        }
        catch (Exception ex)
        {
            CommunicationStatusText = ex.Message;
        }
    }

    private async Task WriteCommunicationAddressAsync()
    {
        try
        {
            var device = ResolveSelectedAddressableDevice();
            await EnsureDeviceConnectedAsync(device);
            await device.WriteAsync(CommunicationAddress, CommunicationWriteValue);
            CommunicationStatusText = $"{device.Key} 写入 {CommunicationAddress} <= {CommunicationWriteValue}";
            Upsert(device.Snapshot);
        }
        catch (Exception ex)
        {
            CommunicationStatusText = ex.Message;
        }
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

    private static async Task EnsureDeviceConnectedAsync(IDeviceClient device)
    {
        if (device.Snapshot.State != DeviceConnectionState.Connected)
        {
            await device.ConnectAsync();
        }
    }

    private async Task SaveCommunicationDeviceAsync()
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
                WorkbenchKind = CurrentDebugWorkbenchKind,
                SelectedDeviceKey = CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Tcp
                    ? selectedKey
                    : FirstNonEmpty(selectedKey, CommunicationDeviceOptions.FirstOrDefault()?.Value)
            };

            var updated = _configuration with
            {
                Devices = updatedDevices,
                Debug = debug,
                SystemSettings = string.Equals(selectedKey, "plc-main", StringComparison.OrdinalIgnoreCase)
                    ? _configuration.SystemSettings with { Plc = CreatePlcSettingsFromCommunicationFields() }
                    : _configuration.SystemSettings
            };

            await SaveConfigurationFileAsync(updated);
            _configuration = await _configurationRepository.GetAsync();
            LoadConfiguration(_configuration);
            CommunicationStatusText = "通讯调试参数已保存；已注册设备的连接参数需要重启软件后完全生效。";
        }
        catch (Exception ex)
        {
            CommunicationStatusText = ex.Message;
        }
    }

    private DeviceDefinition UpdateCommunicationDeviceDefinition(DeviceDefinition device)
    {
        var options = new Dictionary<string, string>(device.Options, StringComparer.OrdinalIgnoreCase);
        var driver = device.Driver;

        if (CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Plc)
        {
            options["protocol"] = CommunicationProtocol;
            options["brand"] = CommunicationBrand;
            driver = string.IsNullOrWhiteSpace(CommunicationProtocol) ? device.Driver : CommunicationProtocol.Trim();

            if (!string.IsNullOrWhiteSpace(CommunicationModel))
            {
                options["model"] = CommunicationModel;
            }
        }
        else if (CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Modbus)
        {
            options["protocol"] = CommunicationProtocol;
            options["brand"] = "Modbus";
            driver = string.IsNullOrWhiteSpace(CommunicationProtocol) ? device.Driver : CommunicationProtocol.Trim();
        }
        else if (CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Serial)
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

    private void OnTcpSessionStateChanged(object? sender, CommunicationSessionState state)
    {
        _uiDispatcher.Invoke(() =>
        {
            TcpIsConnected = state.IsConnected;
            TcpIsListening = state.IsListening;
            TcpPeerText = state.PeerText;
            TcpStatusText = state.StatusText;
            if (!string.IsNullOrWhiteSpace(state.LogText))
            {
                AppendTcpLog(state.LogText);
            }
        });
    }

    private void OnTcpSessionFrameReceived(object? sender, CommunicationSessionFrame frame)
    {
        Interlocked.Exchange(ref _tcpReceivedBytes, frame.TotalBytes);
        Interlocked.Exchange(ref _tcpReceivedFrames, frame.TotalFrames);
        QueueTcpLog(frame.Label, frame.Payload);
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

    private async Task ConnectTcpAsync()
    {
        try
        {
            if (IsTcpServerMode(TcpMode))
            {
                await StartTcpServerAsync();
                return;
            }

            await ConnectTcpClientAsync();
        }
        catch (Exception ex)
        {
            TcpStatusText = ex.Message;
        }
    }

    private async Task ConnectTcpClientAsync()
    {
        if (TcpIsConnected)
        {
            TcpStatusText = "TCP 客户端已连接。";
            return;
        }

        var port = ParseInt(TcpPort, 0);
        if (string.IsNullOrWhiteSpace(TcpHost) || port <= 0)
        {
            throw new InvalidOperationException("TCP 目标 Host 和端口不能为空。");
        }

        await _tcpSession.ConnectAsync(new TcpDebugSessionSettings
        {
            Host = TcpHost.Trim(),
            Port = port,
            ServerMode = false,
            FrameOptions = CreateTcpFrameOptions()
        });
    }

    private async Task StartTcpServerAsync()
    {
        if (TcpIsListening)
        {
            TcpStatusText = $"TCP 服务端已在监听：{TcpHost}:{TcpPort}";
            return;
        }

        var port = ParseInt(TcpPort, 0);
        if (port <= 0)
        {
            throw new InvalidOperationException("TCP 监听端口必须是大于 0 的数字。");
        }

        var address = ResolveListenAddress(TcpHost);
        await _tcpSession.ConnectAsync(new TcpDebugSessionSettings
        {
            ListenAddress = address,
            Port = port,
            ServerMode = true,
            FrameOptions = CreateTcpFrameOptions()
        });
    }

    private Task DisconnectTcpAsync()
    {
        _tcpSession.Disconnect(IsTcpServerMode(TcpMode) ? "TCP 服务端已停止监听。" : "TCP 客户端已断开。");
        return Task.CompletedTask;
    }

    private async Task SendTcpAsync()
    {
        try
        {
            if (!TcpIsConnected)
            {
                throw new InvalidOperationException(IsTcpServerMode(TcpMode)
                    ? "请先开始监听，并等待客户端连接。"
                    : "请先连接 TCP 服务端。");
            }

            var payload = CreateTcpSendPayload();
            if (payload.Length == 0)
            {
                throw new InvalidOperationException("发送内容不能为空。");
            }

            await _tcpSession.SendAsync(payload);
            AppendTcpLog("TX", payload);
            TcpStatusText = $"TCP 已发送：{payload.Length} bytes";
        }
        catch (Exception ex)
        {
            TcpStatusText = ex.Message;
        }
    }

    private byte[] CreateTcpSendPayload()
    {
        return CommunicationFrameCodec.CreatePayload(TcpPayload, TcpSendAsHex, CreateTcpFrameOptions());
    }

    private CommunicationFrameOptions CreateTcpFrameOptions()
    {
        return new CommunicationFrameOptions
        {
            FrameMode = TcpFrameMode,
            Delimiter = TcpDelimiterText,
            FixedFrameLength = ParseInt(TcpFixedFrameLength, 1),
            LengthPrefixBytes = ParseInt(TcpLengthPrefixBytes, 2),
            LengthPrefixLittleEndian = TcpLengthPrefixLittleEndian,
            MaxFrameLength = ParseInt(TcpMaxFrameLength, 4096),
            AppendDelimiterOnSend = TcpAppendDelimiterOnSend,
            PrefixPayloadOnSend = TcpPrefixPayloadOnSend
        };
    }

    private void ClearTcpLog()
    {
        TcpResponse = string.Empty;
        TcpStatusText = "TCP 日志已清空。";
    }

    private void AppendTcpLog(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        AppendTcpLogBlock(line);
    }

    private void AppendTcpLogBlock(string block)
    {
        TcpResponse = string.IsNullOrWhiteSpace(TcpResponse)
            ? block
            : $"{TcpResponse}{Environment.NewLine}{block}";

        if (TcpResponse.Length > TcpMaxLogCharacters)
        {
            TcpResponse = TcpResponse[^TcpMaxLogCharacters..];
        }
    }

    private void AppendTcpLog(string direction, byte[] payload)
    {
        AppendTcpLog($"{direction} {payload.Length} bytes  {FormatTcpPayload(payload)}");
    }

    private void QueueTcpLog(string direction, byte[] payload)
    {
        QueueTcpLog($"{direction} {payload.Length} bytes  {FormatTcpPayload(payload)}");
    }

    private void QueueTcpLog(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        lock (_tcpLogGate)
        {
            if (_tcpPendingLog.Length > TcpMaxQueuedLogCharacters)
            {
                _tcpDroppedLogLines++;
                return;
            }

            if (_tcpPendingLog.Length > 0)
            {
                _tcpPendingLog.AppendLine();
            }

            _tcpPendingLog.Append(line);
        }

        ScheduleTcpLogFlush();
    }

    private void ScheduleTcpLogFlush()
    {
        if (Interlocked.Exchange(ref _tcpLogFlushScheduled, 1) == 0)
        {
            _ = FlushTcpLogSoonAsync();
        }
    }

    private async Task FlushTcpLogSoonAsync()
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
            lock (_tcpLogGate)
            {
                block = _tcpPendingLog.ToString();
                _tcpPendingLog.Clear();
                dropped = _tcpDroppedLogLines;
                _tcpDroppedLogLines = 0;
            }

            if (string.IsNullOrWhiteSpace(block) && dropped == 0)
            {
                return;
            }

            _uiDispatcher.Invoke(() =>
            {
                if (dropped > 0)
                {
                    AppendTcpLogBlock($"{DateTime.Now:HH:mm:ss.fff}  LOG DROP {dropped} lines，接收过快，已保护界面刷新");
                }

                if (!string.IsNullOrWhiteSpace(block))
                {
                    AppendTcpLogBlock(block);
                }

                var bytes = Interlocked.Read(ref _tcpReceivedBytes);
                var frames = Interlocked.Read(ref _tcpReceivedFrames);
                TcpStatusText = $"TCP 已接收：{bytes} bytes / {frames} 帧";
            });
        }
        finally
        {
            Interlocked.Exchange(ref _tcpLogFlushScheduled, 0);
            lock (_tcpLogGate)
            {
                if (_tcpPendingLog.Length > 0 && !_disposed)
                {
                    ScheduleTcpLogFlush();
                }
            }
        }
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

    private string FormatSerialPayload(byte[] payload)
    {
        return CommunicationFrameCodec.FormatPayload(payload, SerialSendAsHex);
    }

    private async Task ConnectSerialAsync()
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

    private Task DisconnectSerialAsync()
    {
        _serialSession.Close("串口已关闭。");
        return Task.CompletedTask;
    }

    private async Task SendSerialAsync()
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

        if (SerialResponse.Length > TcpMaxLogCharacters)
        {
            SerialResponse = SerialResponse[^TcpMaxLogCharacters..];
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
            if (_serialPendingLog.Length > TcpMaxQueuedLogCharacters)
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

    private void RebuildConfigurationIoGroups()
    {
        OnboardInputPoints.Clear();
        OnboardOutputPoints.Clear();
        AxisInputPoints.Clear();
        AxisOutputPoints.Clear();
        ExtendedInputPoints.Clear();
        ExtendedOutputPoints.Clear();

        foreach (var item in IoPoints)
        {
            AddToConfigurationIoGroup(item);
        }
    }

    private void AddToConfigurationIoGroup(IoPointItem item)
    {
        if (item.Source == IoPointSource.ExtendedModule)
        {
            if (item.Direction == IoPointDirection.Output)
            {
                ExtendedOutputPoints.Add(item);
                return;
            }

            ExtendedInputPoints.Add(item);
            return;
        }

        if (item.Source == IoPointSource.AxisOnboard)
        {
            if (item.Direction == IoPointDirection.Output)
            {
                AxisOutputPoints.Add(item);
                return;
            }

            AxisInputPoints.Add(item);
            return;
        }

        if (item.Direction == IoPointDirection.Output)
        {
            OnboardOutputPoints.Add(item);
            return;
        }

        OnboardInputPoints.Add(item);
    }

    private void RebuildIoDebugPoints()
    {
        InputPoints.Clear();
        OutputPoints.Clear();
        foreach (var item in IoPoints.Where(point => point.Enabled))
        {
            if (item.Direction == IoPointDirection.Output)
            {
                OutputPoints.Add(item);
            }
            else
            {
                InputPoints.Add(item);
            }
        }

        _nextBufferedOutputPointNo = OutputPoints.FirstOrDefault()?.PointNo.ToString(CultureInfo.InvariantCulture) ?? "1";
    }

    private void RebuildInterpolationTargets()
    {
        InterpolationTargets.Clear();
        var enabledAxes = Axes.Where(axis => axis.Enabled).OrderBy(axis => axis.AxisNo).ToArray();
        for (var i = 0; i < enabledAxes.Length; i++)
        {
            InterpolationTargets.Add(InterpolationAxisTargetItem.FromAxis(enabledAxes[i], i < 2));
        }
    }

    private void AddBufferedOutputAction(AxisInterpolationBufferedActionTiming timing)
    {
        InterpolationBufferedOutputs.Add(new InterpolationBufferedOutputItem
        {
            Enabled = true,
            Timing = timing,
            PointNo = _nextBufferedOutputPointNo,
            Value = true,
            DelayMilliseconds = "0"
        });
    }

    private void SelectAxis(object? parameter)
    {
        if (parameter is AxisPointItem axis)
        {
            SelectedAxis = axis;
        }
    }

    private void AddCard()
    {
        var nextCardNo = Cards
            .Select(card => (int)card.CardNo)
            .DefaultIfEmpty(ParseShort(GoogolCardNo, 0) - 1)
            .Max() + 1;
        var card = new GoogolCardItem
        {
            Key = CreateUniqueKey("card", Cards.Count + 1, Cards.Select(item => item.Key)),
            Name = $"固高脉冲轴卡 C{nextCardNo}",
            Driver = AxisCardDriverKind.GoogolPulse.ToString(),
            Vendor = "Googol",
            CardNo = (short)nextCardNo,
            AxisCount = 8,
            InputCount = 16,
            OutputCount = 16,
            ConfigPath = GoogolConfigPath?.Trim() ?? string.Empty,
            Description = $"固高卡 {nextCardNo}"
        };
        ConfigureCardItem(card);
        Cards.Add(card);
        RefreshSelectionOptions();
        RaisePropertyChanged(nameof(AxisDriverSummaryText));
        MarkConfigurationDirty("已新增轴卡配置，保存后生效。");
        ConfigurationStatusText = "已新增轴卡配置，保存后写入配置。";
    }

    private void AddExtendedModule()
    {
        var parentCard = Cards.FirstOrDefault();
        var parentCardKey = parentCard is null ? "card1" : ResolveCardKey(parentCard);
        var parentCardNo = parentCard?.CardNo ?? FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        var nextModuleNo = ExtendedModules
            .Where(module => IsModuleOnCard(module, parentCardKey, parentCardNo))
            .Select(module => (int)module.ModuleNo)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        var nextStartAddress = ExtendedModules
            .Where(module => IsModuleOnCard(module, parentCardKey, parentCardNo))
            .Select(module => (int)module.StartAddress + Hcb2ModuleCatalog.Resolve(module.Model).AddressSpan)
            .DefaultIfEmpty(0)
            .Max();
        var profile = Hcb2ModuleCatalog.Resolve(Hcb2ModuleCatalog.DefaultModel);
        var module = new ExtendedIoModuleItem
        {
            Key = CreateUniqueKey("ext", ExtendedModules.Count + 1, ExtendedModules.Select(item => item.Key)),
            ParentCardKey = parentCardKey,
            ParentCardNo = parentCardNo,
            ModuleNo = (short)nextModuleNo,
            Model = profile.Model,
            ModuleType = profile.ModuleType,
            StartAddress = (short)Math.Clamp(nextStartAddress, 0, 15),
            InputCount = profile.InputCount,
            OutputCount = profile.OutputCount,
            ConfigPath = string.Empty,
            Description = $"扩展IO模块 {nextModuleNo}"
        };
        ConfigureExtendedModuleItem(module);
        ExtendedModules.Add(module);
        RefreshSelectionOptions();
        MarkConfigurationDirty("已新增扩展 IO 模块，保存后生效。");
        ConfigurationStatusText = "已新增扩展IO模块，保存后写入配置。";
    }

    private void AddAxis()
    {
        var card = Cards.FirstOrDefault();
        var cardKey = card is null ? "card1" : ResolveCardKey(card);
        var cardNo = card?.CardNo ?? FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        var nextNo = ResolveNextAxisNo(cardKey, cardNo);
        var axis = new AxisPointItem
        {
            Key = CreateUniqueKey("Axis", nextNo, Axes.Select(item => item.Key)),
            Name = $"新增轴{nextNo}",
            CardKey = cardKey,
            CardNo = cardNo,
            AxisNo = nextNo,
            Enabled = true,
            PulsesPerUnit = 1000,
            PositionBand = 0.01,
            SoftLimitNegative = -500,
            SoftLimitPositive = 500,
            DefaultSpeed = 80,
            DefaultAcceleration = 120,
            HomeMode = "LimitHomeIndex"
        };

        ConfigureAxisItem(axis);
        Axes.Add(axis);
        SelectedAxis = axis;
        RefreshSelectionOptions();
        RebuildInterpolationTargets();
        MarkConfigurationDirty("已新增轴，保存后生效。");
        ConfigurationStatusText = "已新增轴，保存后写入配置。";
    }

    private void AddIoPoint(IoPointDirection direction, IoPointSource source)
    {
        var prefix = source switch
        {
            IoPointSource.ExtendedModule => direction == IoPointDirection.Input ? "EDI" : "EDO",
            IoPointSource.AxisOnboard => direction == IoPointDirection.Input ? "AXDI" : "AXDO",
            _ => direction == IoPointDirection.Input ? "DI" : "DO"
        };
        var card = Cards.FirstOrDefault();
        var cardKey = card is null ? "card1" : ResolveCardKey(card);
        var cardNo = card?.CardNo ?? FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        var module = source == IoPointSource.ExtendedModule
            ? ExtendedModules.FirstOrDefault()
            : null;
        var parentCardKey = module?.ParentCardKey ?? cardKey;
        var parentCardNo = module?.ParentCardNo ?? cardNo;
        var moduleNo = module?.ModuleNo ?? (short)0;
        var moduleConfigPath = module?.ConfigPath ?? GoogolConfigPath?.Trim() ?? string.Empty;
        var axisNo = source == IoPointSource.AxisOnboard ? ResolveFirstAxisNo(cardKey, cardNo) : (short)-1;
        var nextNo = ResolveNextIoPointNo(direction, source, cardKey, cardNo, parentCardKey, parentCardNo, moduleNo, axisNo);

        var item = new IoPointItem
        {
            Key = CreateUniqueKey(prefix, nextNo, IoPoints.Select(point => point.Key)),
            Name = source == IoPointSource.ExtendedModule
                ? direction == IoPointDirection.Input ? $"扩展输入点{nextNo}" : $"扩展输出点{nextNo}"
                : source == IoPointSource.AxisOnboard
                    ? direction == IoPointDirection.Input ? $"轴输入点{nextNo}" : $"轴输出点{nextNo}"
                : direction == IoPointDirection.Input ? $"输入点{nextNo}" : $"输出点{nextNo}",
            Direction = direction,
            Source = source,
            Address = string.Empty,
            CardKey = source == IoPointSource.ExtendedModule ? parentCardKey : cardKey,
            CardNo = source == IoPointSource.ExtendedModule ? parentCardNo : cardNo,
            ParentCardKey = source == IoPointSource.ExtendedModule ? parentCardKey : string.Empty,
            ParentCardNo = source == IoPointSource.ExtendedModule ? parentCardNo : (short)-1,
            ModuleNo = source == IoPointSource.ExtendedModule ? moduleNo : (short)-1,
            AxisNo = source == IoPointSource.AxisOnboard ? axisNo : (short)-1,
            ModuleConfigPath = source == IoPointSource.ExtendedModule ? moduleConfigPath : string.Empty,
            PointNo = nextNo,
            Enabled = true,
            ActiveLow = true
        };

        ConfigureIoPointItem(item);
        IoPoints.Add(item);
        RefreshSelectionOptions();
        AddToConfigurationIoGroup(item);
        RebuildIoDebugPoints();
        MarkConfigurationDirty("已新增 IO 点，保存后生效。");
        ConfigurationStatusText = source == IoPointSource.ExtendedModule
            ? "已新增扩展IO点，请填写父卡、模块号和模块配置文件。"
            : "已新增IO点，保存后写入配置。";
    }

    private void RemoveCard(object? parameter)
    {
        if (parameter is not GoogolCardItem card || !Cards.Contains(card))
        {
            return;
        }

        var cardNo = card.CardNo;
        var cardKey = ResolveCardKey(card);
        Cards.Remove(card);
        RaisePropertyChanged(nameof(AxisDriverSummaryText));
        var removedAxes = RemoveMatching(Axes, axis => IsAxisOnCard(axis, cardKey, cardNo));
        var removedModules = RemoveMatching(ExtendedModules, module => IsModuleOnCard(module, cardKey, cardNo));
        var removedIoPoints = RemoveMatching(
            IoPoints,
            point => IsPointOnCard(point, cardKey, cardNo));

        RefreshConfigurationCollections();
        MarkConfigurationDirty("已删除轴卡配置，保存后生效。");
        ConfigurationStatusText =
            $"已删除轴卡 C{cardNo}，同时移除 {removedAxes} 个轴、{removedModules} 个扩展模块、{removedIoPoints} 个IO点；保存后写入配置。";
    }

    private void RemoveExtendedModule(object? parameter)
    {
        if (parameter is not ExtendedIoModuleItem module || !ExtendedModules.Contains(module))
        {
            return;
        }

        ExtendedModules.Remove(module);
        var removedIoPoints = RemoveMatching(
            IoPoints,
            point => point.Source == IoPointSource.ExtendedModule
                && IsPointOnCard(point, module.ParentCardKey, module.ParentCardNo)
                && point.ModuleNo == module.ModuleNo);

        RefreshConfigurationCollections();
        MarkConfigurationDirty("已删除扩展 IO 模块，保存后生效。");
        ConfigurationStatusText =
            $"已删除扩展模块 C{module.ParentCardNo}-M{module.ModuleNo}，同时移除 {removedIoPoints} 个扩展IO点；保存后写入配置。";
    }

    private void RemoveAxis(object? parameter)
    {
        if (parameter is not AxisPointItem axis || !Axes.Remove(axis))
        {
            return;
        }

        var removedIoPoints = RemoveMatching(
            IoPoints,
            point => point.Source == IoPointSource.AxisOnboard
                && IsPointOnCard(point, axis.CardKey, axis.CardNo)
                && point.AxisNo == axis.AxisNo);
        RefreshConfigurationCollections();
        MarkConfigurationDirty("已删除轴，保存后生效。");
        ConfigurationStatusText = $"已删除轴 {axis.Key} / {axis.Name}，同时移除 {removedIoPoints} 个轴上IO点；保存后写入配置。";
    }

    private void RemoveIoPoint(object? parameter)
    {
        if (parameter is not IoPointItem point || !IoPoints.Remove(point))
        {
            return;
        }

        RefreshConfigurationCollections();
        MarkConfigurationDirty("已删除 IO 点，保存后生效。");
        ConfigurationStatusText = $"已删除IO点 {point.Key} / {point.Name}，保存后写入配置。";
    }

    private void RefreshConfigurationCollections()
    {
        RefreshSelectionOptions();
        if (SelectedAxis is null || !Axes.Contains(SelectedAxis))
        {
            SelectedAxis = Axes.FirstOrDefault(axis => axis.Enabled) ?? Axes.FirstOrDefault();
        }

        RebuildInterpolationTargets();
        RebuildConfigurationIoGroups();
        RebuildIoDebugPoints();
    }

    private async Task SaveConfigurationAsync(bool throwOnError = false)
    {
        try
        {
            StopAxisAutoRefresh();
            StopIoAutoRefresh();
            var updated = _configuration with
            {
                AxisController = ResolveAxisControllerKind(AxisControllerMode),
                GoogolCardNo = ParseShort(GoogolCardNo, 0),
                GoogolConfigPath = GoogolConfigPath?.Trim() ?? string.Empty,
                AxisCards = Cards.Select(card => card.ToAxisCardDefinition()).ToArray(),
                GoogolCards = Cards
                    .Where(card => card.DriverKind == AxisCardDriverKind.GoogolPulse)
                    .Select(card => card.ToDefinition())
                    .ToArray(),
                ExtendedIoModules = ExtendedModules.Select(module => module.ToDefinition()).ToArray(),
                Axes = Axes.Select(axis => axis.ToDefinition()).ToArray(),
                IoPoints = IoPoints.Select(point => point.ToDefinition()).ToArray(),
                Debug = new DeviceDebugConfiguration
                {
                    WorkbenchKind = CurrentDebugWorkbenchKind,
                    SelectedDeviceKey = CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.AxisCard
                        ? "motion-main"
                        : FieldbusDebug.ResolveSelectedDebugDeviceKey(_configuration.Debug.SelectedDeviceKey)
                }
            };

            await SaveConfigurationFileAsync(updated);
            _configuration = await _configurationRepository.GetAsync();
            await _axis.ApplyConfigurationAsync(_configuration);
            await _io.ApplyConfigurationAsync(_configuration);
            LoadConfiguration(_configuration);
            ConfigurationStatusText = "配置已保存，轴卡已重新载入配置，请重新连接后调试。";
        }
        catch (Exception ex)
        {
            ConfigurationStatusText = ex.Message;
            if (throwOnError)
            {
                throw;
            }
        }
    }

    private async Task ReloadConfigurationAsync()
    {
        try
        {
            StopAxisAutoRefresh();
            StopIoAutoRefresh();
            _configuration = await _configurationRepository.GetAsync();
            LoadConfiguration(_configuration);
            await _axis.ApplyConfigurationAsync(_configuration);
            await _io.ApplyConfigurationAsync(_configuration);
            ConfigurationStatusText = "配置已重新载入。";
        }
        catch (Exception ex)
        {
            ConfigurationStatusText = ex.Message;
        }
    }

    private async Task ConnectAllAsync()
    {
        if (CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Tcp)
        {
            await TcpDebug.ConnectAsync();
            return;
        }

        if (CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.Serial)
        {
            await FieldbusDebug.SendSerialAsync();
            return;
        }

        if (CurrentDebugWorkbenchKind != DeviceDebugWorkbenchKind.AxisCard)
        {
            await FieldbusDebug.ConnectCommunicationDeviceAsync();
            return;
        }

        try
        {
            AxisStatusText = "正在连接轴卡...";
            await _axis.ConnectAsync();

            IoStatusText = "正在连接 IO...";
            await _io.ConnectAsync();

            await RefreshAxisStatusAsync();
            await RefreshIoPointsAsync();
            StartAxisAutoRefresh();
            StartIoAutoRefresh();
        }
        catch (Exception ex)
        {
            StopAxisAutoRefresh();
            StopIoAutoRefresh();
            AxisStatusText = ex.Message;
            IoStatusText = ex.Message;
        }
    }

    private async Task MoveAxisAsync()
    {
        var target = ParseDouble(AxisTargetPosition, 0);
        if (!ValidateTargetPosition(target))
        {
            return;
        }

        await RunAxisCommandAsync(
            () => _axis.MoveAbsoluteAsync(CreateMoveCommand(target)),
            $"绝对移动到 {target:0.###}...",
            holdAfterCompletion: ManualMoveCommandHold);
    }

    private async Task MoveStepAsync(int direction)
    {
        await RunAxisCommandAsync(
            async () =>
            {
                var status = await _axis.GetAxisStatusAsync(AxisKey);
                var distance = Math.Abs(ParseDouble(AxisStepDistance, 1));
                var target = status.CommandPosition + direction * distance;
                if (!ValidateTargetPosition(target))
                {
                    return;
                }

                AxisTargetPosition = target.ToString("0.###", CultureInfo.InvariantCulture);
                await _axis.MoveAbsoluteAsync(CreateMoveCommand(target));
            },
            direction > 0 ? "+STEP 指令下发中..." : "-STEP 指令下发中...",
            holdAfterCompletion: ManualMoveCommandHold);
    }

    private async Task PrecheckLinearInterpolationAsync()
    {
        try
        {
            var targets = CreateInterpolationTargets();
            await ValidateInterpolationTargetsAsync(targets, true);
            AxisStatusText = $"插补预检通过：{targets.Length}轴，坐标系 {InterpolationCoordinateSystem}，FIFO {InterpolationFifo}。";
        }
        catch (Exception ex)
        {
            AxisStatusText = ex.Message;
        }
    }

    private async Task RunLinearInterpolationAsync()
    {
        AxisInterpolationTarget[] targets;
        try
        {
            targets = CreateInterpolationTargets();
            await ValidateInterpolationTargetsAsync(targets, true);
        }
        catch (Exception ex)
        {
            AxisStatusText = ex.Message;
            return;
        }

        await RunAxisCommandAsync(
            () => _axis.MoveLinearInterpolationAsync(new AxisLinearInterpolationCommand
            {
                Targets = targets,
                Speed = ParseDouble(InterpolationSpeed, 80),
                Acceleration = ParseDouble(InterpolationAcceleration, 120),
                CoordinateSystem = ParseShort(InterpolationCoordinateSystem, AxisInterpolationDefaults.CoordinateSystem),
                Fifo = ParseShort(InterpolationFifo, AxisInterpolationDefaults.Fifo),
                BufferedOutputActions = CreateBufferedOutputActions(),
                Timeout = TimeSpan.FromSeconds(30),
                WaitForCompletion = true
            }),
            $"{targets.Length}轴硬件直线插补中...");
    }

    private AxisInterpolationTarget[] CreateInterpolationTargets()
    {
        var targets = InterpolationTargets
            .Where(target => target.Enabled)
            .Select(target => new AxisInterpolationTarget
            {
                AxisKey = target.AxisKey,
                Position = ParseDouble(target.TargetPosition, double.NaN)
            })
            .ToArray();

        if (targets.Length is < 2 or > 4)
        {
            throw new InvalidOperationException("硬件直线插补需要勾选 2~4 根轴。");
        }

        if (targets.Any(target => double.IsNaN(target.Position)))
        {
            throw new InvalidOperationException("插补目标位置存在无效数值。");
        }

        return targets;
    }

    private async Task ValidateInterpolationTargetsAsync(
        IReadOnlyList<AxisInterpolationTarget> targets,
        bool readHardwareStatus)
    {
        var speed = ParseDouble(InterpolationSpeed, 0);
        var acceleration = ParseDouble(InterpolationAcceleration, 0);
        var coordinateSystem = ParseShort(InterpolationCoordinateSystem, AxisInterpolationDefaults.CoordinateSystem);
        var fifo = ParseShort(InterpolationFifo, AxisInterpolationDefaults.Fifo);

        if (speed <= 0 || acceleration <= 0)
        {
            throw new InvalidOperationException("插补速度和加速度必须大于 0。");
        }

        if (coordinateSystem is < 1 or > 2)
        {
            throw new InvalidOperationException("固高坐标系号必须为 1 或 2。");
        }

        if (fifo is < 0 or > 1)
        {
            throw new InvalidOperationException("固高插补 FIFO 必须为 0 或 1。");
        }

        foreach (var target in targets)
        {
            var axis = Axes.FirstOrDefault(item => string.Equals(item.Key, target.AxisKey, StringComparison.OrdinalIgnoreCase));
            if (axis is null)
            {
                throw new InvalidOperationException($"未找到插补轴配置：{target.AxisKey}");
            }

            if (IsSoftLimitEnabled(axis) && (target.Position < axis.SoftLimitNegative || target.Position > axis.SoftLimitPositive))
            {
                throw new InvalidOperationException($"{axis.Key} 插补目标 {target.Position:0.###} 超出软限位 {axis.SoftLimitNegative:0.###} ~ {axis.SoftLimitPositive:0.###}。");
            }

            if (!readHardwareStatus)
            {
                continue;
            }

            var status = await _axis.GetAxisStatusAsync(target.AxisKey);
            if (status.Alarm || status.EmergencyStop || status.PositiveLimit || status.NegativeLimit)
            {
                var reason = status.Alarm
                    ? "报警"
                    : status.EmergencyStop
                        ? "急停"
                        : status.PositiveLimit
                            ? "正极限"
                            : "负极限";
                throw new InvalidOperationException($"{target.AxisKey} 当前存在{reason}，不能插补。");
            }
        }
    }

    private AxisBufferedOutputAction[] CreateBufferedOutputActions()
    {
        return InterpolationBufferedOutputs
            .Where(action => action.Enabled)
            .Select(action => new AxisBufferedOutputAction
            {
                Timing = action.Timing,
                PointNo = ParseShort(action.PointNo, 1),
                Value = action.Value,
                DelayMilliseconds = (ushort)Math.Clamp(ParseShort(action.DelayMilliseconds, 0), 0, ushort.MaxValue)
            })
            .ToArray();
    }

    public async Task StartJogAsync(bool positive)
    {
        if (_jogRunning)
        {
            return;
        }

        if (!EnsureAxisConnectedForManualCommand())
        {
            return;
        }

        _jogRunning = true;
        AxisStatusText = positive ? $"{AxisKey} Jog+ 运行中..." : $"{AxisKey} Jog- 运行中...";
        try
        {
            await _axis.StartJogAsync(new AxisJogCommand
            {
                AxisKey = AxisKey,
                Direction = positive ? AxisJogDirection.Positive : AxisJogDirection.Negative,
                Speed = ParseDouble(AxisJogSpeed, Math.Max(SelectedAxis?.DefaultSpeed / 4 ?? 20, 1)),
                Acceleration = ParseDouble(AxisAcceleration, SelectedAxis?.DefaultAcceleration ?? 120),
                Deceleration = ParseDouble(AxisAcceleration, SelectedAxis?.DefaultAcceleration ?? 120)
            });
        }
        catch (Exception ex)
        {
            _jogRunning = false;
            AxisStatusText = ex.Message;
        }
    }

    public async Task StopJogAsync()
    {
        if (!_jogRunning)
        {
            return;
        }

        try
        {
            await _axis.StopJogAsync(AxisKey);
            await RefreshAxisStatusAsync();
        }
        catch (Exception ex)
        {
            AxisStatusText = ex.Message;
        }
        finally
        {
            _jogRunning = false;
        }
    }

    private async Task HomeAxisAsync()
    {
        await RunAxisCommandAsync(
            () => _axis.HomeAsync(CreateHomeCommand()),
            $"{AxisKey} {SelectedAxis?.HomeMode ?? AxisHomeMode.LimitHomeIndex.ToString()} 回原中...");
    }

    private AxisMoveCommand CreateMoveCommand(double position)
    {
        return new AxisMoveCommand
        {
            AxisKey = AxisKey,
            Position = position,
            Speed = ParseDouble(AxisSpeed, SelectedAxis?.DefaultSpeed ?? 80),
            Acceleration = ParseDouble(AxisAcceleration, SelectedAxis?.DefaultAcceleration ?? 120),
            Deceleration = ParseDouble(AxisAcceleration, SelectedAxis?.DefaultAcceleration ?? 120),
            Timeout = TimeSpan.FromSeconds(10),
            WaitForCompletion = false
        };
    }

    private AxisHomeCommand CreateHomeCommand()
    {
        var axis = SelectedAxis;
        var speed = ParseDouble(AxisSpeed, axis?.DefaultSpeed ?? 80);
        var acceleration = ParseDouble(AxisAcceleration, axis?.DefaultAcceleration ?? 120);
        return new AxisHomeCommand
        {
            AxisKey = AxisKey,
            HomeMode = ResolveHomeMode(axis?.HomeMode),
            HomePositive = axis?.HomePositive ?? false,
            HomeOffset = axis?.HomeOffset ?? 0,
            EscapeDistance = axis?.EscapeDistance ?? 10,
            HighSpeed = Math.Max(speed, 1),
            LowSpeed = Math.Max(speed / 4, 1),
            Acceleration = Math.Max(acceleration, 1),
            Deceleration = Math.Max(acceleration, 1)
        };
    }

    private bool ValidateTargetPosition(double position)
    {
        if (SelectedAxis is null)
        {
            return true;
        }

        if (IsSoftLimitEnabled(SelectedAxis) && (position < SelectedAxis.SoftLimitNegative || position > SelectedAxis.SoftLimitPositive))
        {
            AxisStatusText = $"{SelectedAxis.Key} 目标位置超出软件限位。";
            return false;
        }

        return true;
    }

    private async Task RunAxisCommandAsync(
        Func<Task> action,
        string pendingText,
        bool allowWhileBusy = false,
        TimeSpan? holdAfterCompletion = null)
    {
        if (!EnsureAxisConnectedForManualCommand())
        {
            return;
        }

        var ownsCommandSlot = false;
        if (!allowWhileBusy)
        {
            if (Interlocked.CompareExchange(ref _axisCommandRunning, 1, 0) != 0)
            {
                AxisStatusText = "上一条轴运动还未结束，已忽略重复操作；需要中断请点“停止”。";
                return;
            }

            ownsCommandSlot = true;
        }

        AxisStatusText = pendingText;
        var shouldHoldCommandSlot = false;
        try
        {
            await action();
            shouldHoldCommandSlot = holdAfterCompletion.GetValueOrDefault() > TimeSpan.Zero;
            await RefreshAxisStatusAsync();
        }
        catch (Exception ex)
        {
            AxisStatusText = ex.Message;
        }
        finally
        {
            if (ownsCommandSlot)
            {
                if (shouldHoldCommandSlot)
                {
                    await Task.Delay(holdAfterCompletion!.Value);
                }

                Interlocked.Exchange(ref _axisCommandRunning, 0);
            }
        }
    }

    private bool EnsureAxisConnectedForManualCommand()
    {
        if (_axis.Snapshot.State is not DeviceConnectionState.Disconnected and not DeviceConnectionState.Connecting)
        {
            return true;
        }

        AxisStatusText = "轴卡未连接，请先点击“连接轴卡 / IO”。";
        return false;
    }

    private async Task RefreshAxisStatusAsync()
    {
        await RefreshAxisStatusCoreAsync(false, false);
    }

    private async Task RefreshAxisStatusLegacyAsync()
    {
        try
        {
            var status = await _axis.GetAxisStatusAsync(AxisKey);
            AxisEncoderText = status.EncoderPosition.ToString("0.###", CultureInfo.InvariantCulture);
            AxisCommandText = status.CommandPosition.ToString("0.###", CultureInfo.InvariantCulture);
            AxisServoText = status.ServoOn ? "ON" : "OFF";
            AxisReadyText = status.Ready ? "READY" : "BUSY";
            AxisInPositionText = status.InPosition ? "IN-POS" : "MOVING";
            AxisAlarmText = status.Alarm ? "ALARM" : "NORMAL";
            AxisHomeText = status.Home || status.Homed ? "HOME" : "NO-HOME";
            AxisPositiveLimitText = status.PositiveLimit ? "+LIMIT" : "CLEAR";
            AxisNegativeLimitText = status.NegativeLimit ? "-LIMIT" : "CLEAR";
            RaiseAxisStatusBrushesChanged();
            AxisStatusText =
                $"{status.AxisKey} | 编码器 {AxisEncoderText} | 指令 {AxisCommandText} | {AxisServoText} | {AxisReadyText} | {AxisAlarmText}";
        }
        catch (Exception ex)
        {
            AxisStatusText = ex.Message;
        }
    }

    private async Task RefreshAxisStatusFromTimerAsync()
    {
        if (!_axisAutoRefreshEnabled || !IsDebugWorkspace || CurrentDebugWorkbenchKind != DeviceDebugWorkbenchKind.AxisCard || _disposed)
        {
            return;
        }

        await RefreshAxisStatusCoreAsync(true, true);
    }

    private async Task RefreshAxisStatusCoreAsync(bool automatic, bool marshalToUi)
    {
        if (Interlocked.CompareExchange(ref _axisRefreshRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var status = await _axis.GetAxisStatusAsync(AxisKey);
            void ApplyStatus() => ApplyAxisStatus(status, automatic);
            if (marshalToUi)
            {
                _uiDispatcher.Invoke(ApplyStatus);
            }
            else
            {
                ApplyStatus();
            }
        }
        catch (Exception ex)
        {
            void ApplyError()
            {
                AxisStatusText = ex.Message;
                if (automatic && _axis.Snapshot.State is DeviceConnectionState.Disconnected or DeviceConnectionState.Faulted)
                {
                    StopAxisAutoRefresh();
                }
            }

            if (marshalToUi)
            {
                _uiDispatcher.Invoke(ApplyError);
            }
            else
            {
                ApplyError();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _axisRefreshRunning, 0);
        }
    }

    private void ApplyAxisStatus(AxisStatus status, bool automatic)
    {
        AxisEncoderText = status.EncoderPosition.ToString("0.###", CultureInfo.InvariantCulture);
        AxisCommandText = status.CommandPosition.ToString("0.###", CultureInfo.InvariantCulture);
        AxisServoText = status.ServoOn ? "ON" : "OFF";
        AxisReadyText = status.Ready ? "READY" : "BUSY";
        AxisInPositionText = status.InPosition ? "IN-POS" : "MOVING";
        AxisAlarmText = status.Alarm ? "ALARM" : "NORMAL";
        AxisHomeText = status.Home || status.Homed ? "已回原" : "未回原";
        AxisPositiveLimitText = status.PositiveLimit ? "+LIMIT" : "CLEAR";
        AxisNegativeLimitText = status.NegativeLimit ? "-LIMIT" : "CLEAR";
        RaiseAxisStatusBrushesChanged();
        if (!automatic)
        {
            AxisStatusText = $"{status.AxisKey} | {AxisServoText} | {AxisReadyText} | {AxisInPositionText} | {AxisAlarmText}";
        }
    }

    private void RaiseAxisStatusBrushesChanged()
    {
        RaisePropertyChanged(nameof(AxisServoBrush));
        RaisePropertyChanged(nameof(AxisReadyBrush));
        RaisePropertyChanged(nameof(AxisInPositionBrush));
        RaisePropertyChanged(nameof(AxisAlarmBrush));
        RaisePropertyChanged(nameof(AxisHomeBrush));
        RaisePropertyChanged(nameof(AxisPositiveLimitBrush));
        RaisePropertyChanged(nameof(AxisNegativeLimitBrush));
    }

    private async Task RefreshIoPointsAsync()
    {
        await RefreshIoPointsCoreAsync(false, false);
    }

    private async Task RefreshIoPointsFromTimerAsync()
    {
        if (!_ioAutoRefreshEnabled || !IsDebugWorkspace || CurrentDebugWorkbenchKind != DeviceDebugWorkbenchKind.AxisCard || _disposed)
        {
            return;
        }

        await RefreshIoPointsCoreAsync(true, true);
    }

    private async Task RefreshIoPointsCoreAsync(bool automatic, bool marshalToUi)
    {
        if (Interlocked.CompareExchange(ref _ioRefreshRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var statuses = await _io.GetAllPointStatusAsync();
            if (marshalToUi)
            {
                _uiDispatcher.Invoke(() => ApplyIoStatuses(statuses, automatic));
            }
            else
            {
                ApplyIoStatuses(statuses, automatic);
            }
        }
        catch (Exception ex)
        {
            if (automatic)
            {
                StopIoAutoRefresh();
            }

            if (marshalToUi)
            {
                _uiDispatcher.Invoke(() => IoStatusText = ex.Message);
            }
            else
            {
                IoStatusText = ex.Message;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _ioRefreshRunning, 0);
        }
    }

    private void ApplyIoStatuses(IReadOnlyList<IoPointStatus> statuses, bool automatic)
    {
        var byKey = statuses.ToDictionary(status => status.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var item in InputPoints.Concat(OutputPoints))
        {
            if (byKey.TryGetValue(item.Key, out var status))
            {
                item.ApplyStatus(status);
            }
        }

        IoStatusText = automatic
            ? $"IO自动刷新中，周期 100ms，输入 {InputPoints.Count} 点，输出 {OutputPoints.Count} 点。"
            : $"IO状态已刷新，输入 {InputPoints.Count} 点，输出 {OutputPoints.Count} 点。";
    }

    private void StartIoAutoRefresh()
    {
        _ioAutoRefreshEnabled = true;
        ApplyIoAutoRefreshState();
    }

    private void StartAxisAutoRefresh()
    {
        _axisAutoRefreshEnabled = true;
        ApplyAxisAutoRefreshState();
    }

    private void StopAxisAutoRefresh()
    {
        _axisAutoRefreshEnabled = false;
        _axisRefreshTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void StopIoAutoRefresh()
    {
        _ioAutoRefreshEnabled = false;
        _ioRefreshTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void ApplyAxisAutoRefreshState()
    {
        if (_disposed)
        {
            return;
        }

        if (_axisAutoRefreshEnabled && IsDebugWorkspace && CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.AxisCard)
        {
            _axisRefreshTimer.Change(AxisRefreshInterval, AxisRefreshInterval);
            return;
        }

        _axisRefreshTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void ApplyIoAutoRefreshState()
    {
        if (_disposed)
        {
            return;
        }

        if (_ioAutoRefreshEnabled && IsDebugWorkspace && CurrentDebugWorkbenchKind == DeviceDebugWorkbenchKind.AxisCard)
        {
            _ioRefreshTimer.Change(IoRefreshInterval, IoRefreshInterval);
            return;
        }

        _ioRefreshTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private async Task ToggleIoPointAsync(object? parameter)
    {
        if (parameter is not IoPointItem point)
        {
            return;
        }

        if (!point.CanWrite)
        {
            IoStatusText = $"{point.Key} 不是可写输出点。";
            return;
        }

        try
        {
            await _io.WritePointAsync(point.Key, !point.Value);
            await RefreshIoPointsAsync();
        }
        catch (Exception ex)
        {
            IoStatusText = ex.Message;
        }
    }

    private void Upsert(DeviceSnapshot snapshot)
    {
        var item = new DeviceSnapshotItem(
            OperatorMessageLocalizer.LocalizeSource(snapshot.Name),
            OperatorMessageLocalizer.LocalizeDeviceState(snapshot.State),
            OperatorMessageLocalizer.LocalizeMessage(snapshot.Message),
            snapshot.Timestamp.ToString("HH:mm:ss"));
        var existing = Devices.FirstOrDefault(device => device.Name == snapshot.Name);
        if (existing is not null)
        {
            var index = Devices.IndexOf(existing);
            Devices[index] = item;
            return;
        }

        Devices.Add(item);
    }

    private static DeviceDebugWorkbenchKind ResolveDebugWorkbenchKind(string? value)
    {
        if (!Enum.TryParse<DeviceDebugWorkbenchKind>(value, true, out var kind))
        {
            return DeviceDebugWorkbenchKind.AxisCard;
        }

        return kind is DeviceDebugWorkbenchKind.AxisCard or DeviceDebugWorkbenchKind.Plc
            ? kind
            : DeviceDebugWorkbenchKind.Plc;
    }

    private static int ParseInt(string? text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               int.TryParse(text, out value)
            ? value
            : fallback;
    }

    private static bool IsTcpServerMode(string? value)
    {
        return string.Equals(value?.Trim(), TcpServerMode, StringComparison.OrdinalIgnoreCase);
    }

    private static IPAddress ResolveListenAddress(string? value)
    {
        var host = value?.Trim();
        if (string.IsNullOrWhiteSpace(host) ||
            string.Equals(host, "*", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Any;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(host, out var address)
            ? address
            : IPAddress.Any;
    }

    private static string GetTcpFrameModeText(string mode)
    {
        return mode switch
        {
            TcpFrameDelimiter => "分隔符",
            TcpFrameFixedLength => "固定长度",
            TcpFrameLengthPrefix => "长度前缀",
            _ => "原始流"
        };
    }

    private string FormatTcpPayload(byte[] payload)
    {
        return CommunicationFrameCodec.FormatPayload(payload, TcpSendAsHex);
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

        if (normalized.Contains("keyence", StringComparison.OrdinalIgnoreCase))
        {
            return "Keyence";
        }

        if (normalized.Contains("panasonic", StringComparison.OrdinalIgnoreCase))
        {
            return "Panasonic";
        }

        if (normalized.Contains("delta", StringComparison.OrdinalIgnoreCase))
        {
            return "Delta";
        }

        if (normalized.Contains("allenbradley", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ab", StringComparison.OrdinalIgnoreCase))
        {
            return "AllenBradley";
        }

        if (normalized.Contains("beckhoff", StringComparison.OrdinalIgnoreCase))
        {
            return "Beckhoff";
        }

        if (normalized.Contains("ls", StringComparison.OrdinalIgnoreCase))
        {
            return "LS";
        }

        if (normalized.Contains("fatek", StringComparison.OrdinalIgnoreCase))
        {
            return "FATEK";
        }

        if (normalized.Contains("fuji", StringComparison.OrdinalIgnoreCase))
        {
            return "Fuji";
        }

        if (normalized.Contains("gesrtp", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ge", StringComparison.OrdinalIgnoreCase))
        {
            return "GE";
        }

        if (normalized.Contains("xinje", StringComparison.OrdinalIgnoreCase))
        {
            return "XinJE";
        }

        if (normalized.Contains("yaskawa", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("memobus", StringComparison.OrdinalIgnoreCase))
        {
            return "Yaskawa";
        }

        if (normalized.Contains("yokogawa", StringComparison.OrdinalIgnoreCase))
        {
            return "Yokogawa";
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

    private static double ParseDouble(string? text, double fallback)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
               double.TryParse(text, out value)
            ? value
            : fallback;
    }

    private static short ParseShort(string? text, short fallback)
    {
        return short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               short.TryParse(text, out value)
            ? value
            : fallback;
    }

    private static AxisHomeMode ResolveHomeMode(string? value)
    {
        return Enum.TryParse<AxisHomeMode>(value, true, out var mode)
            ? mode
            : AxisHomeMode.LimitHomeIndex;
    }

    private static AxisControllerKind ResolveAxisControllerKind(string? value)
    {
        return Enum.TryParse<AxisControllerKind>(value, true, out var kind)
            ? kind
            : AxisControllerKind.Simulated;
    }

    private static bool IsSoftLimitEnabled(AxisPointItem axis)
    {
        return axis.SoftLimitPositive > axis.SoftLimitNegative;
    }

    private static string CreateUniqueKey(string prefix, int start, IEnumerable<string> existingKeys)
    {
        var existing = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    private static GoogolCardItem CreateDefaultCardItem(DeviceConfiguration configuration)
    {
        return new GoogolCardItem
        {
            Key = "card1",
            Name = $"固高脉冲轴卡 C{configuration.GoogolCardNo}",
            Driver = AxisCardDriverKind.GoogolPulse.ToString(),
            Vendor = "Googol",
            CardNo = configuration.GoogolCardNo,
            AxisCount = Math.Max(8, configuration.Axes.Select(axis => (int)axis.AxisNo).DefaultIfEmpty(1).Max()),
            InputCount = configuration.IoPoints.Count(point => point.Direction == IoPointDirection.Input),
            OutputCount = configuration.IoPoints.Count(point => point.Direction == IoPointDirection.Output),
            ConfigPath = configuration.GoogolConfigPath?.Trim() ?? string.Empty,
            Description = "Default Googol card"
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TcpDebug.Dispose();
        FieldbusDebug.Dispose();
        _axisRefreshTimer.Dispose();
        _ioRefreshTimer.Dispose();
    }
}

public sealed class InterpolationAxisTargetItem : BindableBase
{
    private bool _enabled;
    private string _targetPosition = "0";

    public string AxisKey { get; init; } = AxisDefaults.PrimaryAxisKey;

    public string Name { get; init; } = "轴";

    public string CardKey { get; init; } = string.Empty;

    public short CardNo { get; init; }

    public short AxisNo { get; init; } = 1;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string TargetPosition
    {
        get => _targetPosition;
        set => SetProperty(ref _targetPosition, value);
    }

    public string DisplayName => $"C{CardNo} CH-{AxisNo:00}  {AxisKey} / {Name}";

    public static InterpolationAxisTargetItem FromAxis(AxisPointItem axis, bool enabled)
    {
        return new InterpolationAxisTargetItem
        {
            AxisKey = axis.Key,
            Name = axis.Name,
            CardKey = axis.CardKey,
            CardNo = axis.CardNo,
            AxisNo = axis.AxisNo,
            Enabled = enabled,
            TargetPosition = "0"
        };
    }
}

public sealed class InterpolationBufferedOutputItem : BindableBase
{
    private bool _enabled = true;
    private AxisInterpolationBufferedActionTiming _timing = AxisInterpolationBufferedActionTiming.BeforeMotion;
    private string _pointNo = "1";
    private bool _value = true;
    private string _delayMilliseconds = "0";

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public AxisInterpolationBufferedActionTiming Timing
    {
        get => _timing;
        set
        {
            if (SetProperty(ref _timing, value))
            {
                RaisePropertyChanged(nameof(TimingText));
            }
        }
    }

    public string PointNo
    {
        get => _pointNo;
        set => SetProperty(ref _pointNo, value);
    }

    public bool Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                RaisePropertyChanged(nameof(ValueText));
            }
        }
    }

    public string DelayMilliseconds
    {
        get => _delayMilliseconds;
        set => SetProperty(ref _delayMilliseconds, value);
    }

    public string TimingText => Timing == AxisInterpolationBufferedActionTiming.BeforeMotion ? "运动前" : "运动后";

    public string ValueText => Value ? "ON" : "OFF";
}

public sealed record NumericSelectionOption(short Value, string Text)
{
    public override string ToString()
    {
        return Text;
    }

    public static NumericSelectionOption[] Range(short first, short last, Func<short, string> format)
    {
        if (last < first)
        {
            return [];
        }

        return Enumerable
            .Range(first, last - first + 1)
            .Select(value => (short)value)
            .Select(value => new NumericSelectionOption(value, format(value)))
            .ToArray();
    }

    public static void Replace(
        ObservableCollection<NumericSelectionOption> target,
        IEnumerable<NumericSelectionOption> options)
    {
        var updated = options.ToArray();
        if (target.Count == updated.Length && target.Zip(updated).All(pair => pair.First.Equals(pair.Second)))
        {
            return;
        }

        target.Clear();
        foreach (var option in updated)
        {
            target.Add(option);
        }
    }
}

public sealed record AxisCardSelectionOption(string Key, short CardNo, string Text)
{
    public override string ToString()
    {
        return Text;
    }

    public static void Replace(
        ObservableCollection<AxisCardSelectionOption> target,
        IEnumerable<AxisCardSelectionOption> options)
    {
        var updated = options.ToArray();
        if (target.Count == updated.Length && target.Zip(updated).All(pair => pair.First.Equals(pair.Second)))
        {
            return;
        }

        target.Clear();
        foreach (var option in updated)
        {
            target.Add(option);
        }
    }
}

public sealed record TextSelectionOption(string Value, string Text)
{
    public override string ToString()
    {
        return Text;
    }
}

public sealed class GoogolCardItem : BindableBase
{
    private string _key = "card1";
    private string _name = "固高脉冲轴卡";
    private string _driver = AxisCardDriverKind.GoogolPulse.ToString();
    private string _vendor = "Googol";
    private string _model = string.Empty;
    private short _cardNo;
    private int _axisCount = 8;
    private int _inputCount = 16;
    private int _outputCount = 16;
    private string _configPath = string.Empty;
    private string _connection = string.Empty;
    private string _description = string.Empty;

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

    public string Driver
    {
        get => _driver;
        set
        {
            if (SetProperty(ref _driver, string.IsNullOrWhiteSpace(value) ? AxisCardDriverKind.GoogolPulse.ToString() : value.Trim()))
            {
                RaisePropertyChanged(nameof(DriverKind));
                RaisePropertyChanged(nameof(DriverDisplayText));
                ApplyDriverDefaults(DriverKind);
            }
        }
    }

    public AxisCardDriverKind DriverKind => Enum.TryParse<AxisCardDriverKind>(Driver, true, out var kind)
        ? kind
        : AxisCardDriverKind.GoogolPulse;

    public string DriverDisplayText => FormatDriver(DriverKind);

    public string Vendor
    {
        get => _vendor;
        set => SetProperty(ref _vendor, value);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    public short CardNo
    {
        get => _cardNo;
        set
        {
            if (SetProperty(ref _cardNo, value))
            {
                RefreshDriverConfigPathDefault();
            }
        }
    }

    public int AxisCount
    {
        get => _axisCount;
        set => SetProperty(ref _axisCount, value);
    }

    public int InputCount
    {
        get => _inputCount;
        set => SetProperty(ref _inputCount, value);
    }

    public int OutputCount
    {
        get => _outputCount;
        set => SetProperty(ref _outputCount, value);
    }

    public string ConfigPath
    {
        get => _configPath;
        set => SetProperty(ref _configPath, value);
    }

    public string Connection
    {
        get => _connection;
        set => SetProperty(ref _connection, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public static GoogolCardItem FromDefinition(GoogolCardDefinition definition)
    {
        return new GoogolCardItem
        {
            Key = definition.Key,
            Name = string.IsNullOrWhiteSpace(definition.Description)
                ? $"固高脉冲轴卡 C{definition.CardNo}"
                : definition.Description,
            Driver = AxisCardDriverKind.GoogolPulse.ToString(),
            Vendor = "Googol",
            CardNo = definition.CardNo,
            AxisCount = definition.AxisCount,
            InputCount = definition.InputCount,
            OutputCount = definition.OutputCount,
            ConfigPath = definition.ConfigPath,
            Description = definition.Description
        };
    }

    public static GoogolCardItem FromDefinition(AxisCardDefinition definition)
    {
        return new GoogolCardItem
        {
            Key = definition.Key,
            Name = definition.Name,
            Driver = definition.Driver.ToString(),
            Vendor = definition.Vendor,
            Model = definition.Model,
            CardNo = definition.CardNo,
            AxisCount = definition.AxisCount,
            InputCount = definition.InputCount,
            OutputCount = definition.OutputCount,
            ConfigPath = definition.ConfigPath,
            Connection = definition.Connection,
            Description = definition.Description
        };
    }

    public AxisCardDefinition ToAxisCardDefinition()
    {
        var driver = DriverKind;
        return new AxisCardDefinition
        {
            Key = string.IsNullOrWhiteSpace(Key) ? $"card{CardNo}" : Key.Trim(),
            Name = string.IsNullOrWhiteSpace(Name) ? $"Axis card {CardNo}" : Name.Trim(),
            Driver = driver,
            Vendor = string.IsNullOrWhiteSpace(Vendor) ? ResolveDefaultVendor(driver) : Vendor.Trim(),
            Model = Model?.Trim() ?? string.Empty,
            CardNo = CardNo < 0 ? (short)0 : CardNo,
            AxisCount = AxisCount <= 0 ? ResolveDefaultAxisCount(driver) : AxisCount,
            InputCount = InputCount < 0 ? 0 : InputCount,
            OutputCount = OutputCount < 0 ? 0 : OutputCount,
            ConfigPath = ConfigPath?.Trim() ?? string.Empty,
            Connection = Connection?.Trim() ?? string.Empty,
            Description = Description?.Trim() ?? string.Empty
        };
    }

    public GoogolCardDefinition ToDefinition()
    {
        return new GoogolCardDefinition
        {
            Key = string.IsNullOrWhiteSpace(Key) ? $"card{CardNo}" : Key.Trim(),
            CardNo = CardNo < 0 ? (short)0 : CardNo,
            AxisCount = AxisCount <= 0 ? 8 : AxisCount,
            InputCount = InputCount < 0 ? 0 : InputCount,
            OutputCount = OutputCount < 0 ? 0 : OutputCount,
            ConfigPath = ConfigPath?.Trim() ?? string.Empty,
            Description = Description?.Trim() ?? string.Empty
        };
    }

    private static string ResolveDefaultVendor(AxisCardDriverKind driver)
    {
        return driver switch
        {
            AxisCardDriverKind.GoogolPulse or AxisCardDriverKind.GoogolBus => "Googol",
            AxisCardDriverKind.AdvantechPci1245 => "Advantech",
            AxisCardDriverKind.Simulated => "Simulated",
            _ => string.Empty
        };
    }

    public static TextSelectionOption[] CreateDriverOptions()
    {
        return Enum.GetValues<AxisCardDriverKind>()
            .Select(kind => new TextSelectionOption(kind.ToString(), FormatDriver(kind)))
            .ToArray();
    }

    public static string FormatDriver(AxisCardDriverKind driver)
    {
        return driver switch
        {
            AxisCardDriverKind.GoogolPulse => "固高脉冲",
            AxisCardDriverKind.GoogolBus => "固高总线",
            AxisCardDriverKind.AdvantechPci1245 => "研华 PCI-1245",
            AxisCardDriverKind.Simulated => "仿真",
            _ => driver.ToString()
        };
    }

    private void ApplyDriverDefaults(AxisCardDriverKind driver)
    {
        Vendor = ResolveDefaultVendor(driver);
        switch (driver)
        {
            case AxisCardDriverKind.AdvantechPci1245:
                if (string.IsNullOrWhiteSpace(Model) || IsKnownDefaultModel(Model))
                {
                    Model = "PCI-1245";
                }

                if (AxisCount <= 0 || AxisCount == 8)
                {
                    AxisCount = 4;
                }

                if (InputCount <= 0)
                {
                    InputCount = 16;
                }

                if (OutputCount <= 0)
                {
                    OutputCount = 16;
                }

                RefreshDriverConfigPathDefault();

                if (string.IsNullOrWhiteSpace(Name) || Name.Contains("固高", StringComparison.OrdinalIgnoreCase))
                {
                    Name = $"研华 PCI-1245 轴卡 C{CardNo}";
                }

                if (string.IsNullOrWhiteSpace(Description) || Description.Contains("固高", StringComparison.OrdinalIgnoreCase) || Description == "Default Googol card")
                {
                    Description = $"研华 PCI-1245 C{CardNo}";
                }

                break;

            case AxisCardDriverKind.GoogolPulse:
            case AxisCardDriverKind.GoogolBus:
                if (string.IsNullOrWhiteSpace(Model) || IsKnownDefaultModel(Model))
                {
                    Model = driver == AxisCardDriverKind.GoogolBus ? "Googol Bus" : "GTS";
                }

                if (AxisCount <= 0 || AxisCount == 4)
                {
                    AxisCount = 8;
                }

                if (InputCount <= 0)
                {
                    InputCount = 16;
                }

                if (OutputCount <= 0)
                {
                    OutputCount = 16;
                }

                if (string.IsNullOrWhiteSpace(Name) || Name.Contains("研华", StringComparison.OrdinalIgnoreCase))
                {
                    Name = $"{FormatDriver(driver)}轴卡 C{CardNo}";
                }

                break;

            case AxisCardDriverKind.Simulated:
                if (string.IsNullOrWhiteSpace(Model) || IsKnownDefaultModel(Model))
                {
                    Model = "Simulated";
                }

                if (AxisCount <= 0)
                {
                    AxisCount = 8;
                }

                break;
        }
    }

    private static int ResolveDefaultAxisCount(AxisCardDriverKind driver)
    {
        return driver == AxisCardDriverKind.AdvantechPci1245 ? 4 : 8;
    }

    private void RefreshDriverConfigPathDefault()
    {
        if (DriverKind != AxisCardDriverKind.AdvantechPci1245)
        {
            return;
        }

        var defaultPath = ResolveDefaultAdvantechConfigPath(CardNo);
        if (string.IsNullOrWhiteSpace(ConfigPath) || IsKnownAdvantechConfigPath(ConfigPath))
        {
            ConfigPath = defaultPath;
        }
    }

    private static string ResolveDefaultAdvantechConfigPath(short cardNo)
    {
        var cardIndex = Math.Max(0, (int)cardNo) + 1;
        return $@"D:\Script\Config\PCI1245card{cardIndex}.cfg";
    }

    private static bool IsKnownAdvantechConfigPath(string configPath)
    {
        var fileName = System.IO.Path.GetFileName(configPath.Trim());
        return fileName.StartsWith("PCI1245card", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownDefaultModel(string model)
    {
        return model.Equals("GTS", StringComparison.OrdinalIgnoreCase)
            || model.Equals("Googol Bus", StringComparison.OrdinalIgnoreCase)
            || model.Equals("PCI-1245", StringComparison.OrdinalIgnoreCase)
            || model.Equals("Simulated", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ExtendedIoModuleItem : BindableBase
{
    private string _key = "ext1";
    private string _parentCardKey = string.Empty;
    private short _parentCardNo;
    private short _moduleNo;
    private string _model = Hcb2ModuleCatalog.DefaultModel;
    private int _moduleType = 3;
    private short _startAddress;
    private short _workMode;
    private int _inputCount = 16;
    private int _outputCount = 16;
    private int _adChannels;
    private double _adMaxVoltage;
    private double _adMinVoltage;
    private int _daChannels;
    private double _daMaxVoltage;
    private double _daMinVoltage;
    private string _configPath = string.Empty;
    private string _description = string.Empty;

    public ObservableCollection<NumericSelectionOption> ModuleNoOptions { get; } = new();

    public ObservableCollection<NumericSelectionOption> StartAddressOptions { get; } = new();

    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    public string ParentCardKey
    {
        get => _parentCardKey;
        set => SetProperty(ref _parentCardKey, value?.Trim() ?? string.Empty);
    }

    public short ParentCardNo
    {
        get => _parentCardNo;
        set => SetProperty(ref _parentCardNo, value);
    }

    public short ModuleNo
    {
        get => _moduleNo;
        set => SetProperty(ref _moduleNo, value);
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, string.IsNullOrWhiteSpace(value) ? Hcb2ModuleCatalog.DefaultModel : value.Trim()))
            {
                ApplyProfile(Hcb2ModuleCatalog.Resolve(_model));
            }
        }
    }

    public int ModuleType
    {
        get => _moduleType;
        set => SetProperty(ref _moduleType, value);
    }

    public short StartAddress
    {
        get => _startAddress;
        set => SetProperty(ref _startAddress, value);
    }

    public short WorkMode
    {
        get => _workMode;
        set => SetProperty(ref _workMode, value);
    }

    public int InputCount
    {
        get => _inputCount;
        set => SetProperty(ref _inputCount, value);
    }

    public int OutputCount
    {
        get => _outputCount;
        set => SetProperty(ref _outputCount, value);
    }

    public int AdChannels
    {
        get => _adChannels;
        set => SetProperty(ref _adChannels, value);
    }

    public double AdMaxVoltage
    {
        get => _adMaxVoltage;
        set => SetProperty(ref _adMaxVoltage, value);
    }

    public double AdMinVoltage
    {
        get => _adMinVoltage;
        set => SetProperty(ref _adMinVoltage, value);
    }

    public int DaChannels
    {
        get => _daChannels;
        set => SetProperty(ref _daChannels, value);
    }

    public double DaMaxVoltage
    {
        get => _daMaxVoltage;
        set => SetProperty(ref _daMaxVoltage, value);
    }

    public double DaMinVoltage
    {
        get => _daMinVoltage;
        set => SetProperty(ref _daMinVoltage, value);
    }

    public string ConfigPath
    {
        get => _configPath;
        set => SetProperty(ref _configPath, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public void SetModuleNoOptions(IEnumerable<NumericSelectionOption> options)
    {
        NumericSelectionOption.Replace(ModuleNoOptions, options);
    }

    public void SetStartAddressOptions(IEnumerable<NumericSelectionOption> options)
    {
        NumericSelectionOption.Replace(StartAddressOptions, options);
    }

    public static ExtendedIoModuleItem FromDefinition(ExtendedIoModuleDefinition definition)
    {
        return new ExtendedIoModuleItem
        {
            Key = definition.Key,
            ParentCardKey = definition.ParentCardKey,
            ParentCardNo = definition.ParentCardNo,
            ModuleNo = definition.ModuleNo,
            Model = definition.Model,
            ModuleType = definition.ModuleType,
            StartAddress = definition.StartAddress,
            WorkMode = definition.WorkMode,
            InputCount = definition.InputCount,
            OutputCount = definition.OutputCount,
            AdChannels = definition.AdChannels,
            AdMaxVoltage = definition.AdMaxVoltage,
            AdMinVoltage = definition.AdMinVoltage,
            DaChannels = definition.DaChannels,
            DaMaxVoltage = definition.DaMaxVoltage,
            DaMinVoltage = definition.DaMinVoltage,
            ConfigPath = definition.ConfigPath,
            Description = definition.Description
        };
    }

    public ExtendedIoModuleDefinition ToDefinition()
    {
        return new ExtendedIoModuleDefinition
        {
            Key = string.IsNullOrWhiteSpace(Key) ? $"ext{ModuleNo}" : Key.Trim(),
            ParentCardKey = ParentCardKey?.Trim() ?? string.Empty,
            ParentCardNo = ParentCardNo < 0 ? (short)0 : ParentCardNo,
            ModuleNo = ModuleNo < 0 ? (short)0 : ModuleNo,
            Model = string.IsNullOrWhiteSpace(Model) ? Hcb2ModuleCatalog.DefaultModel : Model.Trim(),
            ModuleType = ModuleType <= 0 ? Hcb2ModuleCatalog.Resolve(Model).ModuleType : ModuleType,
            StartAddress = StartAddress < 0 ? (short)0 : StartAddress,
            WorkMode = WorkMode <= 0 ? (short)0 : (short)1,
            InputCount = InputCount < 0 ? 0 : InputCount,
            OutputCount = OutputCount < 0 ? 0 : OutputCount,
            AdChannels = AdChannels < 0 ? 0 : AdChannels,
            AdMaxVoltage = AdMaxVoltage,
            AdMinVoltage = AdMinVoltage,
            DaChannels = DaChannels < 0 ? 0 : DaChannels,
            DaMaxVoltage = DaMaxVoltage,
            DaMinVoltage = DaMinVoltage,
            ConfigPath = ConfigPath?.Trim() ?? string.Empty,
            Description = Description?.Trim() ?? string.Empty
        };
    }

    private void ApplyProfile(Hcb2ModuleProfile profile)
    {
        ModuleType = profile.ModuleType;
        InputCount = profile.InputCount;
        OutputCount = profile.OutputCount;
        AdChannels = profile.AdChannels;
        AdMaxVoltage = profile.AdMaxVoltage;
        AdMinVoltage = profile.AdMinVoltage;
        DaChannels = profile.DaChannels;
        DaMaxVoltage = profile.DaMaxVoltage;
        DaMinVoltage = profile.DaMinVoltage;
    }
}

public sealed class AxisPointItem : BindableBase
{
    private string _key = AxisDefaults.PrimaryAxisKey;
    private string _name = "X轴";
    private string _cardKey = string.Empty;
    private short _cardNo;
    private short _axisNo = 1;
    private bool _enabled = true;
    private double _pulsesPerUnit = 1000;
    private double _positionBand = 0.01;
    private double _softLimitNegative = -500;
    private double _softLimitPositive = 500;
    private double _defaultSpeed = 80;
    private double _defaultAcceleration = 120;
    private string _homeMode = "LimitHomeIndex";
    private bool _homePositive;
    private double _homeOffset;
    private double _escapeDistance = 10;
    private string _description = string.Empty;
    private bool _isSelected;

    public ObservableCollection<NumericSelectionOption> AxisNoOptions { get; } = new();

    public string Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value))
            {
                RaisePropertyChanged(nameof(DisplayName));
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
                RaisePropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string CardKey
    {
        get => _cardKey;
        set
        {
            if (SetProperty(ref _cardKey, value?.Trim() ?? string.Empty))
            {
                RaisePropertyChanged(nameof(ChannelText));
                RaisePropertyChanged(nameof(ProfileText));
            }
        }
    }

    public short CardNo
    {
        get => _cardNo;
        set
        {
            if (SetProperty(ref _cardNo, value))
            {
                RaisePropertyChanged(nameof(ChannelText));
                RaisePropertyChanged(nameof(ProfileText));
            }
        }
    }

    public short AxisNo
    {
        get => _axisNo;
        set
        {
            if (SetProperty(ref _axisNo, value))
            {
                RaisePropertyChanged(nameof(ChannelText));
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public double PulsesPerUnit
    {
        get => _pulsesPerUnit;
        set
        {
            if (SetProperty(ref _pulsesPerUnit, value))
            {
                RaisePropertyChanged(nameof(ProfileText));
            }
        }
    }

    public double PositionBand
    {
        get => _positionBand;
        set
        {
            if (SetProperty(ref _positionBand, value))
            {
                RaisePropertyChanged(nameof(ProfileText));
            }
        }
    }

    public double SoftLimitNegative
    {
        get => _softLimitNegative;
        set
        {
            if (SetProperty(ref _softLimitNegative, value))
            {
                RaisePropertyChanged(nameof(SoftLimitText));
            }
        }
    }

    public double SoftLimitPositive
    {
        get => _softLimitPositive;
        set
        {
            if (SetProperty(ref _softLimitPositive, value))
            {
                RaisePropertyChanged(nameof(SoftLimitText));
            }
        }
    }

    public double DefaultSpeed
    {
        get => _defaultSpeed;
        set
        {
            if (SetProperty(ref _defaultSpeed, value))
            {
                RaisePropertyChanged(nameof(ProfileText));
            }
        }
    }

    public double DefaultAcceleration
    {
        get => _defaultAcceleration;
        set
        {
            if (SetProperty(ref _defaultAcceleration, value))
            {
                RaisePropertyChanged(nameof(ProfileText));
            }
        }
    }

    public string HomeMode
    {
        get => _homeMode;
        set => SetProperty(ref _homeMode, value);
    }

    public bool HomePositive
    {
        get => _homePositive;
        set => SetProperty(ref _homePositive, value);
    }

    public double HomeOffset
    {
        get => _homeOffset;
        set => SetProperty(ref _homeOffset, value);
    }

    public double EscapeDistance
    {
        get => _escapeDistance;
        set => SetProperty(ref _escapeDistance, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void SetAxisNoOptions(IEnumerable<NumericSelectionOption> options)
    {
        NumericSelectionOption.Replace(AxisNoOptions, options);
    }

    public string DisplayName => $"{Key} | {Name}";

    public string ChannelText => $"C{CardNo} / CH-{AxisNo:00}";

    public string SoftLimitText => $"{SoftLimitNegative:0.###} / {SoftLimitPositive:0.###}";

    public string ProfileText => $"C{CardNo}  P/U {PulsesPerUnit:0.###}  Band {PositionBand:0.###}  V {DefaultSpeed:0.###}  A {DefaultAcceleration:0.###}";

    public override string ToString()
    {
        return DisplayName;
    }

    public static AxisPointItem FromDefinition(AxisPointDefinition definition)
    {
        return new AxisPointItem
        {
            Key = definition.Key,
            Name = definition.Name,
            CardKey = definition.CardKey,
            CardNo = definition.CardNo < 0 ? (short)0 : definition.CardNo,
            AxisNo = definition.AxisNo,
            Enabled = definition.Enabled,
            PulsesPerUnit = definition.PulsesPerUnit,
            PositionBand = definition.PositionBand <= 0 ? 0.01 : definition.PositionBand,
            SoftLimitNegative = definition.SoftLimitNegative,
            SoftLimitPositive = definition.SoftLimitPositive,
            DefaultSpeed = definition.DefaultSpeed,
            DefaultAcceleration = definition.DefaultAcceleration,
            HomeMode = definition.HomeMode,
            HomePositive = definition.HomePositive,
            HomeOffset = definition.HomeOffset,
            EscapeDistance = definition.EscapeDistance,
            Description = definition.Description
        };
    }

    public AxisPointDefinition ToDefinition()
    {
        return new AxisPointDefinition
        {
            Key = string.IsNullOrWhiteSpace(Key) ? AxisDefaults.PrimaryAxisKey : Key.Trim(),
            Name = string.IsNullOrWhiteSpace(Name) ? Key : Name.Trim(),
            CardKey = CardKey?.Trim() ?? string.Empty,
            CardNo = CardNo < 0 ? (short)0 : CardNo,
            AxisNo = AxisNo <= 0 ? (short)1 : AxisNo,
            Enabled = Enabled,
            PulsesPerUnit = PulsesPerUnit <= 0 ? 1 : PulsesPerUnit,
            PositionBand = PositionBand <= 0 ? 0.01 : PositionBand,
            SoftLimitNegative = SoftLimitNegative,
            SoftLimitPositive = SoftLimitPositive,
            DefaultSpeed = DefaultSpeed <= 0 ? 80 : DefaultSpeed,
            DefaultAcceleration = DefaultAcceleration <= 0 ? 120 : DefaultAcceleration,
            HomeMode = string.IsNullOrWhiteSpace(HomeMode) ? "LimitHomeIndex" : HomeMode.Trim(),
            HomePositive = HomePositive,
            HomeOffset = HomeOffset,
            EscapeDistance = EscapeDistance,
            Description = Description
        };
    }
}

public sealed class IoPointItem : BindableBase
{
    private string _key = string.Empty;
    private string _name = string.Empty;
    private IoPointDirection _direction;
    private IoPointSource _source;
    private string _address = string.Empty;
    private string _cardKey = string.Empty;
    private short _cardNo;
    private string _parentCardKey = string.Empty;
    private short _parentCardNo = -1;
    private short _moduleNo = -1;
    private short _axisNo = -1;
    private string _moduleConfigPath = string.Empty;
    private short _pointNo = 1;
    private bool _activeLow;
    private bool _enabled = true;
    private bool _value;
    private string _message = string.Empty;

    public ObservableCollection<NumericSelectionOption> ModuleNoOptions { get; } = new();

    public ObservableCollection<NumericSelectionOption> AxisNoOptions { get; } = new();

    public ObservableCollection<NumericSelectionOption> PointNoOptions { get; } = new();

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

    public IoPointDirection Direction
    {
        get => _direction;
        set
        {
            if (SetProperty(ref _direction, value))
            {
                RaisePropertyChanged(nameof(DirectionText));
                RaisePropertyChanged(nameof(DirectionCode));
                RaisePropertyChanged(nameof(CanWrite));
            }
        }
    }

    public IoPointSource Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string CardKey
    {
        get => _cardKey;
        set => SetProperty(ref _cardKey, value?.Trim() ?? string.Empty);
    }

    public short CardNo
    {
        get => _cardNo;
        set => SetProperty(ref _cardNo, value);
    }

    public string ParentCardKey
    {
        get => _parentCardKey;
        set => SetProperty(ref _parentCardKey, value?.Trim() ?? string.Empty);
    }

    public short ParentCardNo
    {
        get => _parentCardNo;
        set => SetProperty(ref _parentCardNo, value);
    }

    public short ModuleNo
    {
        get => _moduleNo;
        set => SetProperty(ref _moduleNo, value);
    }

    public short AxisNo
    {
        get => _axisNo;
        set => SetProperty(ref _axisNo, value);
    }

    public string ModuleConfigPath
    {
        get => _moduleConfigPath;
        set => SetProperty(ref _moduleConfigPath, value);
    }

    public short PointNo
    {
        get => _pointNo;
        set => SetProperty(ref _pointNo, value);
    }

    public bool ActiveLow
    {
        get => _activeLow;
        set
        {
            if (SetProperty(ref _activeLow, value))
            {
                RaisePropertyChanged(nameof(PolarityText));
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                RaisePropertyChanged(nameof(CanWrite));
            }
        }
    }

    public bool Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                RaisePropertyChanged(nameof(ValueText));
                RaisePropertyChanged(nameof(ToggleText));
                RaisePropertyChanged(nameof(StateBrush));
            }
        }
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public string DirectionText => Direction == IoPointDirection.Input ? "输入" : "输出";

    public string DirectionCode => Direction == IoPointDirection.Input ? "DI" : "DO";

    public string PolarityText => ActiveLow ? "LOW" : "HIGH";

    public bool CanWrite => Enabled && Direction == IoPointDirection.Output;

    public string ValueText => Value ? "ON" : "OFF";

    public string ToggleText => Value ? "关闭" : "打开";

    public string StateBrush => Value ? "#FF33D6A6" : "#FF6F7B84";

    public void SetModuleNoOptions(IEnumerable<NumericSelectionOption> options)
    {
        NumericSelectionOption.Replace(ModuleNoOptions, options);
    }

    public void SetAxisNoOptions(IEnumerable<NumericSelectionOption> options)
    {
        NumericSelectionOption.Replace(AxisNoOptions, options);
    }

    public void SetPointNoOptions(IEnumerable<NumericSelectionOption> options)
    {
        NumericSelectionOption.Replace(PointNoOptions, options);
    }

    public void RefreshAddress()
    {
        var code = Direction == IoPointDirection.Input ? "DI" : "DO";
        Address = Source == IoPointSource.ExtendedModule
            ? $"C{ParentCardNo}-M{ModuleNo}-{code}{PointNo}"
            : Source == IoPointSource.AxisOnboard
                ? $"C{CardNo}-CH{AxisNo:00}-AX{code}{PointNo}"
            : $"C{CardNo}-{code}{PointNo}";
    }

    public static IoPointItem FromDefinition(IoPointDefinition definition)
    {
        return new IoPointItem
        {
            Key = definition.Key,
            Name = definition.Name,
            Direction = definition.Direction,
            Source = definition.Source,
            Address = definition.Address,
            CardKey = definition.CardKey,
            CardNo = definition.CardNo,
            ParentCardKey = definition.ParentCardKey,
            ParentCardNo = definition.ParentCardNo,
            ModuleNo = definition.ModuleNo,
            AxisNo = definition.AxisNo,
            ModuleConfigPath = definition.ModuleConfigPath,
            PointNo = definition.PointNo,
            ActiveLow = definition.ActiveLow,
            Enabled = definition.Enabled,
            Value = definition.InitialValue,
            Message = definition.Description
        };
    }

    public void ApplyStatus(IoPointStatus status)
    {
        Value = status.Value;
        Message = status.Message;
    }

    public IoPointDefinition ToDefinition()
    {
        RefreshAddress();

        return new IoPointDefinition
        {
            Key = string.IsNullOrWhiteSpace(Key) ? Address : Key.Trim(),
            Name = string.IsNullOrWhiteSpace(Name) ? Key : Name.Trim(),
            Direction = Direction,
            Source = Source,
            Address = Address,
            CardKey = CardKey?.Trim() ?? string.Empty,
            CardNo = CardNo,
            ParentCardKey = Source == IoPointSource.ExtendedModule ? (ParentCardKey?.Trim() ?? string.Empty) : string.Empty,
            ParentCardNo = ParentCardNo,
            ModuleNo = ModuleNo,
            AxisNo = Source == IoPointSource.AxisOnboard ? AxisNo <= 0 ? (short)1 : AxisNo : (short)-1,
            ModuleConfigPath = ModuleConfigPath?.Trim() ?? string.Empty,
            PointNo = PointNo <= 0 ? (short)1 : PointNo,
            ActiveLow = ActiveLow,
            Enabled = Enabled,
            InitialValue = Value,
            Description = Message
        };
    }
}
