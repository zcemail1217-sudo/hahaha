using VisionStation.Domain;

namespace VisionStation.Devices;

public interface ICameraDevice
{
    event EventHandler<DeviceSnapshot>? StateChanged;

    string DeviceId { get; }

    DeviceSnapshot Snapshot { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<ImageFrame> GrabAsync(CancellationToken cancellationToken = default);
}

public sealed record CameraDeviceInfo
{
    public string DeviceId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Vendor { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string SerialNumber { get; init; } = string.Empty;

    public string UserDefinedName { get; init; } = string.Empty;

    public string TransportLayer { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public string SubnetMask { get; init; } = string.Empty;

    public string Gateway { get; init; } = string.Empty;

    public bool IsAccessible { get; init; } = true;

    public string AccessStatus { get; init; } = string.Empty;
}

public sealed record CameraAcquisitionSettings
{
    public string DeviceId { get; init; } = string.Empty;

    public double ExposureTimeMs { get; init; }

    public string TriggerSource { get; init; } = string.Empty;

    public int HeartbeatTimeoutMs { get; init; } = 3000;

    public bool ClearBufferBeforeTrigger { get; init; } = true;
}

public sealed record CameraDiagnostics
{
    public string DeviceId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public DeviceConnectionState ConnectionState { get; init; } = DeviceConnectionState.Disconnected;

    public string TransportLayer { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public bool IsGrabbing { get; init; }

    public int HeartbeatTimeoutMs { get; init; }

    public bool ClearBufferBeforeTrigger { get; init; }

    public uint PacketSize { get; init; }

    public uint PayloadSize { get; init; }

    public int ImageNodeCount { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public uint FrameNumber { get; init; }

    public uint FrameLength { get; init; }

    public string PixelFormat { get; init; } = string.Empty;

    public uint LostPacketCount { get; init; }

    public int ReconnectAttempts { get; init; }

    public DateTimeOffset? LastFrameAt { get; init; }

    public DateTimeOffset? LastReconnectAt { get; init; }

    public string LastErrorCode { get; init; } = string.Empty;

    public string LastError { get; init; } = string.Empty;

    public string LastMessage { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

public interface ICameraDeviceDiscovery
{
    Task<IReadOnlyList<CameraDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface ISelectableCameraDevice
{
    string SelectedDeviceId { get; }

    Task SelectDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
}

public interface IConfigurableCameraDevice : ISelectableCameraDevice
{
    Task ApplyAcquisitionSettingsAsync(CameraAcquisitionSettings settings, CancellationToken cancellationToken = default);
}

public interface ICameraDiagnosticsProvider
{
    CameraDiagnostics GetDiagnostics();
}

public interface IPlcClient
{
    event EventHandler<DeviceSnapshot>? StateChanged;

    DeviceSnapshot Snapshot { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SetInspectionBusyAsync(bool busy, CancellationToken cancellationToken = default);

    Task<string> ReadAddressAsync(string address, CancellationToken cancellationToken = default);

    Task WriteAddressAsync(string address, string value, CancellationToken cancellationToken = default);

    Task WriteInspectionResultAsync(InspectionResult result, CancellationToken cancellationToken = default);

    Task ResetAlarmAsync(CancellationToken cancellationToken = default);
}

public interface IAxisController
{
    event EventHandler<DeviceSnapshot>? StateChanged;

    DeviceSnapshot Snapshot { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task ServoOnAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default);

    Task ServoOffAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default);

    Task ClearAlarmAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default);

    Task ZeroPositionAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default);

    Task HomeAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default);

    Task HomeAsync(AxisHomeCommand command, CancellationToken cancellationToken = default);

    Task MoveAbsoluteAsync(AxisMoveCommand command, CancellationToken cancellationToken = default);

    Task MoveLinearInterpolationAsync(AxisLinearInterpolationCommand command, CancellationToken cancellationToken = default);

    Task StartJogAsync(AxisJogCommand command, CancellationToken cancellationToken = default);

    Task StopJogAsync(string axisKey = AxisDefaults.PrimaryAxisKey, CancellationToken cancellationToken = default);

    Task StopAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        AxisStopMode stopMode = AxisStopMode.Smooth,
        CancellationToken cancellationToken = default);

    Task EmergencyStopAsync(CancellationToken cancellationToken = default);

    Task<AxisStatus> GetAxisStatusAsync(
        string axisKey = AxisDefaults.PrimaryAxisKey,
        CancellationToken cancellationToken = default);

    Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface IAxisDriver : IAxisController
{
    string DriverId { get; }

    AxisCardDriverKind DriverKind { get; }

    IReadOnlyCollection<string> AxisKeys { get; }

    bool ContainsAxis(string axisKey);
}

public interface IDigitalIoController
{
    event EventHandler<DeviceSnapshot>? StateChanged;

    DeviceSnapshot Snapshot { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IoPointStatus>> GetAllPointStatusAsync(CancellationToken cancellationToken = default);

    Task<IoPointStatus> GetPointStatusAsync(string pointKey, CancellationToken cancellationToken = default);

    Task WritePointAsync(string pointKey, bool value, CancellationToken cancellationToken = default);

    Task ApplyConfigurationAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface IDigitalIoDriver : IDigitalIoController
{
    string DriverId { get; }

    AxisCardDriverKind DriverKind { get; }

    IReadOnlyCollection<string> PointKeys { get; }

    bool ContainsPoint(string pointKey);
}

public static class AxisDefaults
{
    public const string PrimaryAxisKey = "AxisX";
}

public enum AxisStopMode
{
    Smooth,
    Immediate
}

public enum AxisHomeMode
{
    Home,
    HomeIndex,
    Index,
    Limit,
    LimitHome,
    LimitIndex,
    LimitHomeIndex
}

public enum AxisJogDirection
{
    Negative = -1,
    Positive = 1
}

public enum AxisInterpolationBufferedActionTiming
{
    BeforeMotion,
    AfterMotion
}

public static class AxisInterpolationDefaults
{
    public const short CoordinateSystem = 1;

    public const short Fifo = 0;

    public const short GeneralOutput = 12;
}

public sealed record AxisMoveCommand
{
    public string AxisKey { get; init; } = AxisDefaults.PrimaryAxisKey;

    public double Position { get; init; }

    public double Speed { get; init; } = 100;

    public double Acceleration { get; init; } = 100;

    public double Deceleration { get; init; } = 100;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool WaitForCompletion { get; init; } = true;
}

public sealed record AxisJogCommand
{
    public string AxisKey { get; init; } = AxisDefaults.PrimaryAxisKey;

    public AxisJogDirection Direction { get; init; } = AxisJogDirection.Positive;

    public double Speed { get; init; } = 20;

    public double Acceleration { get; init; } = 100;

    public double Deceleration { get; init; } = 100;
}

public sealed record AxisHomeCommand
{
    public string AxisKey { get; init; } = AxisDefaults.PrimaryAxisKey;

    public AxisHomeMode HomeMode { get; init; } = AxisHomeMode.LimitHomeIndex;

    public bool HomePositive { get; init; }

    public double HomeOffset { get; init; }

    public double EscapeDistance { get; init; } = 10;

    public double HighSpeed { get; init; } = 20;

    public double LowSpeed { get; init; } = 5;

    public double Acceleration { get; init; } = 100;

    public double Deceleration { get; init; } = 100;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record AxisInterpolationTarget
{
    public string AxisKey { get; init; } = AxisDefaults.PrimaryAxisKey;

    public double Position { get; init; }
}

public sealed record AxisBufferedOutputAction
{
    public AxisInterpolationBufferedActionTiming Timing { get; init; } = AxisInterpolationBufferedActionTiming.BeforeMotion;

    public short DoType { get; init; } = AxisInterpolationDefaults.GeneralOutput;

    public short PointNo { get; init; } = 1;

    public bool Value { get; init; }

    public ushort DelayMilliseconds { get; init; }
}

public sealed record AxisLinearInterpolationCommand
{
    public IReadOnlyList<AxisInterpolationTarget> Targets { get; init; } = [];

    public double Speed { get; init; } = 100;

    public double Acceleration { get; init; } = 100;

    public double EndVelocity { get; init; }

    public short CoordinateSystem { get; init; } = AxisInterpolationDefaults.CoordinateSystem;

    public short Fifo { get; init; } = AxisInterpolationDefaults.Fifo;

    public IReadOnlyList<AxisBufferedOutputAction> BufferedOutputActions { get; init; } = [];

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool WaitForCompletion { get; init; } = true;
}

public sealed record AxisStatus
{
    public string AxisKey { get; init; } = AxisDefaults.PrimaryAxisKey;

    public double CommandPosition { get; init; }

    public double EncoderPosition { get; init; }

    public bool ServoOn { get; init; }

    public bool Alarm { get; init; }

    public bool PositiveLimit { get; init; }

    public bool NegativeLimit { get; init; }

    public bool Home { get; init; }

    public bool EmergencyStop { get; init; }

    public bool Ready { get; init; } = true;

    public bool InPosition { get; init; } = true;

    public bool Homed { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

public sealed record IoPointStatus
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IoPointDirection Direction { get; init; }

    public string Address { get; init; } = string.Empty;

    public short CardNo { get; init; }

    public short PointNo { get; init; }

    public bool ActiveLow { get; init; }

    public bool Enabled { get; init; } = true;

    public bool Value { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
