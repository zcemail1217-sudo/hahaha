namespace VisionStation.Domain;

public enum AxisControllerKind
{
    Simulated,
    Googol
}

public enum AxisCardDriverKind
{
    GoogolPulse,
    GoogolBus,
    AdvantechPci1245,
    Simulated
}

public enum IoPointDirection
{
    Input,
    Output
}

public enum IoPointSource
{
    Onboard,
    AxisOnboard,
    ExtendedModule
}

public enum DeviceKind
{
    Camera,
    Motion,
    DigitalIo,
    Plc,
    Instrument,
    Other
}

public enum DeviceDebugWorkbenchKind
{
    AxisCard,
    Plc,
    Tcp,
    Modbus,
    Serial
}

public sealed record DeviceConfiguration
{
    public AxisControllerKind AxisController { get; init; } = AxisControllerKind.Simulated;

    public short GoogolCardNo { get; init; }

    public string GoogolConfigPath { get; init; } = string.Empty;

    public IReadOnlyList<AxisCardDefinition> AxisCards { get; init; } = Array.Empty<AxisCardDefinition>();

    public IReadOnlyList<GoogolCardDefinition> GoogolCards { get; init; } = Array.Empty<GoogolCardDefinition>();

    public IReadOnlyList<ExtendedIoModuleDefinition> ExtendedIoModules { get; init; } = Array.Empty<ExtendedIoModuleDefinition>();

    public IReadOnlyList<AxisPointDefinition> Axes { get; init; } = Array.Empty<AxisPointDefinition>();

    public IReadOnlyList<IoPointDefinition> IoPoints { get; init; } = Array.Empty<IoPointDefinition>();

    public IReadOnlyList<DeviceDefinition> Devices { get; init; } = Array.Empty<DeviceDefinition>();

    public DeviceDebugConfiguration Debug { get; init; } = new();

    public SystemSettingsConfiguration SystemSettings { get; init; } = new();
}

public sealed record DeviceDebugConfiguration
{
    public DeviceDebugWorkbenchKind WorkbenchKind { get; init; } = DeviceDebugWorkbenchKind.AxisCard;

    public string SelectedDeviceKey { get; init; } = "motion-main";
}

public sealed record DeviceDefinition
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public DeviceKind Kind { get; init; } = DeviceKind.Other;

    public string Driver { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public DeviceConnectionDefinition Connection { get; init; } = new();

    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Description { get; init; } = string.Empty;
}

public sealed record DeviceConnectionDefinition
{
    public string IpAddress { get; init; } = string.Empty;

    public int Port { get; init; }

    public string SerialPort { get; init; } = string.Empty;

    public int BaudRate { get; init; } = 9600;

    public string StationNo { get; init; } = string.Empty;

    public string Resource { get; init; } = string.Empty;
}

public sealed record AxisCardDefinition
{
    public string Key { get; init; } = "card1";

    public string Name { get; init; } = "Axis card";

    public AxisCardDriverKind Driver { get; init; } = AxisCardDriverKind.GoogolPulse;

    public string Vendor { get; init; } = "Googol";

    public string Model { get; init; } = string.Empty;

    public short CardNo { get; init; }

    public int AxisCount { get; init; } = 8;

    public int InputCount { get; init; } = 16;

    public int OutputCount { get; init; } = 16;

    public string ConfigPath { get; init; } = string.Empty;

    public string Connection { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Description { get; init; } = string.Empty;
}

public sealed record GoogolCardDefinition
{
    public string Key { get; init; } = "card1";

    public short CardNo { get; init; }

    public int AxisCount { get; init; } = 8;

    public int InputCount { get; init; } = 16;

    public int OutputCount { get; init; } = 16;

    public string Description { get; init; } = string.Empty;

    public string ConfigPath { get; init; } = string.Empty;
}

public sealed record ExtendedIoModuleDefinition
{
    public string Key { get; init; } = "ext1";

    public string ParentCardKey { get; init; } = string.Empty;

    public short ParentCardNo { get; init; }

    public short ModuleNo { get; init; }

    public string Model { get; init; } = Hcb2ModuleCatalog.DefaultModel;

    public int ModuleType { get; init; }

    public short StartAddress { get; init; } = -1;

    public short WorkMode { get; init; }

    public int InputCount { get; init; } = 16;

    public int OutputCount { get; init; } = 16;

    public int AdChannels { get; init; }

    public double AdMaxVoltage { get; init; }

    public double AdMinVoltage { get; init; }

    public int DaChannels { get; init; }

    public double DaMaxVoltage { get; init; }

    public double DaMinVoltage { get; init; }

    public string ConfigPath { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
}

public sealed record Hcb2ModuleProfile(
    string Model,
    int ModuleType,
    int InputCount,
    int OutputCount,
    int AddressSpan,
    int AdChannels = 0,
    double AdMaxVoltage = 0,
    double AdMinVoltage = 0,
    int DaChannels = 0,
    double DaMaxVoltage = 0,
    double DaMinVoltage = 0);

public static class Hcb2ModuleCatalog
{
    public const string DefaultModel = "HCB2-1616-DTD01";

    public static IReadOnlyList<Hcb2ModuleProfile> Profiles { get; } =
    [
        new Hcb2ModuleProfile("HCB2-1616-DTD01", 3, 16, 16, 1),
        new Hcb2ModuleProfile("HCB2-1616-DTS01", 3, 16, 16, 1),
        new Hcb2ModuleProfile("HCB2-3200-DXX01", 3, 32, 0, 2),
        new Hcb2ModuleProfile("HCB2-0604-A1201", 6, 6, 4, 8, 6, 10, -10, 4, 10, -10),
        new Hcb2ModuleProfile("HCB2-0606-A1201", 6, 6, 6, 8, 6, 10, -10, 6, 10, -10),
        new Hcb2ModuleProfile("HCB2-0604-A1601", 6, 6, 4, 8, 6, 10, -10, 4, 10, -10),
        new Hcb2ModuleProfile("HCB2-0606-A1601", 6, 6, 6, 8, 6, 10, -10, 6, 10, -10)
    ];

    public static Hcb2ModuleProfile Resolve(string? model)
    {
        return Profiles.FirstOrDefault(
            profile => string.Equals(profile.Model, model?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? Profiles[0];
    }
}

public sealed record SystemSettingsConfiguration
{
    public MesUploadSettings Mes { get; init; } = new();

    public PlcCommunicationSettings Plc { get; init; } = new();

    public ProductionSettingsConfiguration Production { get; init; } = new();

    public AppLoggingSettingsConfiguration Logging { get; init; } = new();

    public CommunicationChannelSettings Communication { get; init; } = new();

    public RuntimeParameterSettings Parameters { get; init; } = new();

    public AccessControlSettings AccessControl { get; init; } = new();
}

public sealed record ProductionSettingsConfiguration
{
    public int CycleDelayMs { get; init; } = 900;

    public int MaxConsecutiveFailures { get; init; } = 1;

    public bool AutoStopOnAlarm { get; init; } = true;

    public int CleanupTimeoutMs { get; init; } = 2000;
}

public sealed record AppLoggingSettingsConfiguration
{
    public int RetentionDays { get; init; } = 30;

    public int MaxRecentEntries { get; init; } = 300;

    public bool IncludeThreadId { get; init; } = true;
}

public sealed record CommunicationChannelSettings
{
    public IReadOnlyList<TcpCommunicationChannelSettings> TcpChannels { get; init; } = Array.Empty<TcpCommunicationChannelSettings>();

    public IReadOnlyList<SerialCommunicationChannelSettings> SerialChannels { get; init; } = Array.Empty<SerialCommunicationChannelSettings>();
}

public static class CommunicationChannelConnectionPolicies
{
    public const string OnDemand = "OnDemand";

    public const string Startup = "Startup";

    public const string Production = "Production";

    public static string Normalize(string? value)
    {
        return value?.Trim() switch
        {
            Startup => Startup,
            Production => Production,
            _ => OnDemand
        };
    }
}

public sealed record TcpCommunicationChannelSettings
{
    public string Key { get; init; } = "tcp-main";

    public string Name { get; init; } = "TCP 通讯";

    public bool Enabled { get; init; } = true;

    public string ConnectionPolicy { get; init; } = CommunicationChannelConnectionPolicies.OnDemand;

    public string Mode { get; init; } = "Client";

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 502;

    public int ConnectTimeoutMs { get; init; } = 3000;

    public int ReceiveTimeoutMs { get; init; } = 1000;

    public string FrameMode { get; init; } = "Raw";

    public string Delimiter { get; init; } = "\\r\\n";

    public int FixedFrameLength { get; init; } = 8;

    public int LengthPrefixBytes { get; init; } = 2;

    public bool LengthPrefixLittleEndian { get; init; }

    public int MaxFrameLength { get; init; } = 4096;

    public bool AppendDelimiterOnSend { get; init; } = true;

    public bool PrefixPayloadOnSend { get; init; }

    public string Description { get; init; } = string.Empty;
}

public sealed record SerialCommunicationChannelSettings
{
    public string Key { get; init; } = "serial-main";

    public string Name { get; init; } = "串口通讯";

    public bool Enabled { get; init; } = true;

    public string ConnectionPolicy { get; init; } = CommunicationChannelConnectionPolicies.OnDemand;

    public string PortName { get; init; } = "COM3";

    public int BaudRate { get; init; } = 9600;

    public int DataBits { get; init; } = 8;

    public string Parity { get; init; } = "None";

    public string StopBits { get; init; } = "One";

    public int ReceiveTimeoutMs { get; init; } = 1000;

    public string FrameMode { get; init; } = "Raw";

    public string Delimiter { get; init; } = "\\r\\n";

    public int FixedFrameLength { get; init; } = 8;

    public int LengthPrefixBytes { get; init; } = 2;

    public bool LengthPrefixLittleEndian { get; init; }

    public int MaxFrameLength { get; init; } = 4096;

    public bool AppendDelimiterOnSend { get; init; } = true;

    public bool PrefixPayloadOnSend { get; init; }

    public string Description { get; init; } = string.Empty;
}

public sealed record MesUploadSettings
{
    public bool UploadEnabled { get; init; } = true;

    public string Endpoint { get; init; } = "http://127.0.0.1:8080/api/mes/upload";

    public string LineCode { get; init; } = "LINE-01";

    public string StationCode { get; init; } = "CV-01";

    public string EquipmentCode { get; init; } = "VISION-01";

    public string ProcessCode { get; init; } = "INSPECTION";

    public string ProductCode { get; init; } = string.Empty;

    public string UploadMode { get; init; } = "OnInspectionComplete";

    public string ApiToken { get; init; } = string.Empty;
}

public sealed record PlcCommunicationSettings
{
    public string Protocol { get; init; } = "Simulated";

    public string IpAddress { get; init; } = "192.168.1.10";

    public int Port { get; init; } = 502;

    public int StationNo { get; init; } = 1;

    public string Model { get; init; } = string.Empty;

    public int ConnectTimeoutMs { get; init; } = 3000;

    public int HeartbeatIntervalMs { get; init; } = 1000;

    public string HeartbeatAddress { get; init; } = "D100";

    public string ResultAddress { get; init; } = "D200";

    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record RuntimeParameterSettings
{
    public string MachineName { get; init; } = "VisionStation-01";

    public int InspectionTimeoutMs { get; init; } = 5000;

    public int ImageRetentionDays { get; init; } = 30;

    public bool SaveOriginalImage { get; init; } = true;

    public bool SaveResultImage { get; init; } = true;

    public IReadOnlyList<SystemParameterDefinition> Items { get; init; } = Array.Empty<SystemParameterDefinition>();
}

public sealed record SystemParameterDefinition
{
    public string Key { get; init; } = "ReservedParameter";

    public string Name { get; init; } = "预留参数";

    public string Value { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;
}

public sealed record AccessControlSettings
{
    public bool LoginRequired { get; init; } = true;

    public int SessionTimeoutMinutes { get; init; } = 30;

    public string DefaultRole { get; init; } = "Operator";

    public IReadOnlyList<AccessRoleDefinition> Roles { get; init; } = Array.Empty<AccessRoleDefinition>();
}

public sealed record AccessRoleDefinition
{
    public string Key { get; init; } = "Operator";

    public string Name { get; init; } = "操作员";

    public string Description { get; init; } = "基础运行权限";
}

public sealed record AxisPointDefinition
{
    public string Key { get; init; } = "AxisX";

    public string Name { get; init; } = "X Axis";

    public string CardKey { get; init; } = string.Empty;

    public short CardNo { get; init; } = -1;

    public short AxisNo { get; init; } = 1;

    public bool Enabled { get; init; } = true;

    public double PulsesPerUnit { get; init; } = 1000;

    public double PositionBand { get; init; } = 0.01;

    public double SoftLimitNegative { get; init; } = -500;

    public double SoftLimitPositive { get; init; } = 500;

    public double DefaultSpeed { get; init; } = 80;

    public double DefaultAcceleration { get; init; } = 120;

    public string HomeMode { get; init; } = "LimitHomeIndex";

    public bool HomePositive { get; init; }

    public double HomeOffset { get; init; }

    public double EscapeDistance { get; init; } = 10;

    public string Description { get; init; } = string.Empty;
}

public sealed record IoPointDefinition
{
    public string Key { get; init; } = "StartButton";

    public string Name { get; init; } = "Start Button";

    public IoPointDirection Direction { get; init; } = IoPointDirection.Input;

    public IoPointSource Source { get; init; } = IoPointSource.Onboard;

    public string Address { get; init; } = string.Empty;

    public string CardKey { get; init; } = string.Empty;

    public short CardNo { get; init; }

    public string ParentCardKey { get; init; } = string.Empty;

    public short ParentCardNo { get; init; } = -1;

    public short ModuleNo { get; init; } = -1;

    public short AxisNo { get; init; } = -1;

    public string ModuleConfigPath { get; init; } = string.Empty;

    public short PointNo { get; init; } = 1;

    public bool ActiveLow { get; init; }

    public bool Enabled { get; init; } = true;

    public bool InitialValue { get; init; }

    public string Description { get; init; } = string.Empty;
}
