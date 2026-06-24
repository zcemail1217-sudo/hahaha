namespace VisionStation.Domain;

public interface IRecipeRepository
{
    Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default);

    Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default);

    Task SetCurrentRecipeAsync(string recipeId, CancellationToken cancellationToken = default);

    Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default);

    Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default);
}

public interface IDeviceConfigurationRepository
{
    event EventHandler<DeviceConfiguration>? ConfigurationSaved;

    Task<DeviceConfiguration> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DeviceConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface ICommunicationChannelRuntime : IDisposable
{
    event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived;

    Task ConnectAsync(string connectionPolicy, CancellationToken cancellationToken = default);

    Task DisconnectAsync(string connectionPolicy, CancellationToken cancellationToken = default);

    Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(
        TcpCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default);

    Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(
        SerialCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default);

    Task<CommunicationChannelRuntimeSnapshot> ReconnectTcpAsync(
        TcpCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default);

    Task<CommunicationChannelRuntimeSnapshot> ReconnectSerialAsync(
        SerialCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default);

    Task<byte[]?> TryExchangeTcpAsync(
        TcpCommunicationChannelSettings channel,
        byte[] payload,
        int timeoutMs,
        bool waitResponse,
        CancellationToken cancellationToken = default);

    Task<bool> TrySendTcpAsync(
        TcpCommunicationChannelSettings channel,
        byte[] payload,
        CancellationToken cancellationToken = default);

    Task<byte[]?> TryExchangeSerialAsync(
        SerialCommunicationChannelSettings channel,
        byte[] payload,
        int timeoutMs,
        bool waitResponse,
        CancellationToken cancellationToken = default);

    Task<bool> TrySendSerialAsync(
        SerialCommunicationChannelSettings channel,
        byte[] payload,
        CancellationToken cancellationToken = default);
}

public sealed record CommunicationChannelRuntimeSnapshot(
    string Kind,
    string Key,
    string ConnectionPolicy,
    bool IsRuntimeManaged,
    bool IsConnected,
    bool IsListening,
    string PeerText,
    string StatusText);

public sealed record CommunicationChannelRuntimeFrame(
    string Kind,
    string Key,
    string Label,
    byte[] Payload,
    long TotalBytes,
    long TotalFrames);

public interface IInspectionRecordRepository
{
    Task AddAsync(InspectionResult result, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InspectionResult>> RecentAsync(int count, CancellationToken cancellationToken = default);
}

public interface IImageTraceStore
{
    Task<ImageTracePaths> SaveAsync(
        Recipe recipe,
        ImageFrame originalFrame,
        ImageFrame resultFrame,
        InspectionResult result,
        CancellationToken cancellationToken = default);
}

public interface IAppLogService
{
    event EventHandler<AppLogEntry>? LogWritten;

    void Info(string source, string message);

    void Warning(string source, string message);

    void Error(string source, string message);

    void Critical(string source, string message);

    IReadOnlyList<AppLogEntry> Recent(int count);
}

public interface IAlarmService
{
    event EventHandler<AlarmEvent>? AlarmRaised;

    event EventHandler<AlarmEvent>? AlarmChanged;

    AlarmEvent Raise(
        AlarmSeverity severity,
        string source,
        string message,
        string details = "",
        string? alarmId = null);

    void Acknowledge(string alarmId);

    void Clear(string alarmId);

    IReadOnlyList<AlarmEvent> Active();

    IReadOnlyList<AlarmEvent> Recent(int count);
}

public interface IAlarmEventRepository
{
    Task UpsertAsync(AlarmEvent alarm, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlarmEvent>> RecentAsync(int count, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlarmEvent>> ActiveAsync(CancellationToken cancellationToken = default);
}
