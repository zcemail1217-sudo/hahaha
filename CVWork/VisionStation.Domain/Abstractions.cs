namespace VisionStation.Domain;

public interface IRecipeRepository
{
    Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default);

    Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default);

    Task SetCurrentRecipeAsync(string recipeId, CancellationToken cancellationToken = default);

    Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a recipe and returns its repository-issued storage revision.
    /// Replace the caller's old recipe instance with the returned value.
    /// </summary>
    Task<Recipe> CreateAsync(Recipe recipe, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a recipe and returns its new repository-issued storage revision.
    /// Replace the caller's old recipe instance with the returned value.
    /// </summary>
    Task<Recipe> SaveAsync(Recipe recipe, CancellationToken cancellationToken = default);

    Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires the catalog-wide recipe mutation lease.
    /// </summary>
    /// <remarks>
    /// Always use the returned session with <c>await using</c>, and never nest recipe mutation sessions.
    /// </remarks>
    Task<RecipeMutationSession> BeginMutationAsync(
        string recipeId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Holds a catalog-wide mutation lease for one recipe until asynchronously disposed.
/// </summary>
public abstract class RecipeMutationSession : IAsyncDisposable
{
    public abstract string RecipeId { get; }

    public abstract Task<Recipe?> GetAsync(CancellationToken cancellationToken = default);

    public abstract Task<Recipe> CreateAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the session recipe when <paramref name="recipe"/> carries the current
    /// repository-issued storage revision, and returns the newly issued revision.
    /// </summary>
    public abstract Task<Recipe> UpdateAsync(
        Recipe recipe,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the session recipe only when <paramref name="expected"/> carries the current
    /// repository-issued storage revision.
    /// </summary>
    public abstract Task DeleteAsync(
        Recipe expected,
        CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();
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
