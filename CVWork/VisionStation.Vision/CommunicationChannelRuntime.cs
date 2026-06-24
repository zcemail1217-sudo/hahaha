using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using VisionStation.Communication;
using VisionStation.Domain;
using VisionStation.Vision.Tools;

namespace VisionStation.Vision;

public sealed class CommunicationChannelRuntime : ICommunicationChannelRuntime
{
    private readonly IDeviceConfigurationRepository _configurationRepository;
    private readonly IAppLogService? _log;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, TcpChannelSession> _tcpSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SerialChannelSession> _serialSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tcpLastStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _serialLastStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activePolicies = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived;

    public CommunicationChannelRuntime(
        IDeviceConfigurationRepository configurationRepository,
        IAppLogService? log = null)
    {
        _configurationRepository = configurationRepository;
        _log = log;
        _configurationRepository.ConfigurationSaved += OnConfigurationSaved;
    }

    public async Task ConnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var policy = CommunicationChannelConnectionPolicies.Normalize(connectionPolicy);
        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return;
        }

        var configuration = await _configurationRepository.GetAsync(cancellationToken);
        await _sync.WaitAsync(cancellationToken);
        try
        {
            _activePolicies.Add(policy);
            await RefreshPolicyLockedAsync(policy, configuration, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task DisconnectAsync(string connectionPolicy, CancellationToken cancellationToken = default)
    {
        var policy = CommunicationChannelConnectionPolicies.Normalize(connectionPolicy);
        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return;
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            _activePolicies.Remove(policy);
            DisconnectPolicyLocked(policy);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<byte[]?> TryExchangeTcpAsync(
        TcpCommunicationChannelSettings channel,
        byte[] payload,
        int timeoutMs,
        bool waitResponse,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return null;
        }

        TcpChannelSession? session;
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!_activePolicies.Contains(policy) ||
                !_tcpSessions.TryGetValue(channel.Key, out session) ||
                !string.Equals(session.Policy, policy, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        finally
        {
            _sync.Release();
        }

        return await session.ExchangeAsync(payload, timeoutMs, waitResponse, cancellationToken);
    }

    public async Task<bool> TrySendTcpAsync(
        TcpCommunicationChannelSettings channel,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return false;
        }

        TcpChannelSession? session;
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!_activePolicies.Contains(policy) ||
                !_tcpSessions.TryGetValue(channel.Key, out session) ||
                !string.Equals(session.Policy, policy, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        finally
        {
            _sync.Release();
        }

        await session.SendAsync(payload, Math.Max(channel.ReceiveTimeoutMs, 100), cancellationToken);
        return true;
    }

    public async Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(
        TcpCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(channel);
        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (!channel.Enabled)
        {
            return new CommunicationChannelRuntimeSnapshot(
                "TCP",
                channel.Key,
                policy,
                false,
                false,
                false,
                "未启用",
                $"TCP 通道 '{channel.Key}' 未启用。");
        }

        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return new CommunicationChannelRuntimeSnapshot(
                "TCP",
                channel.Key,
                policy,
                false,
                false,
                false,
                "按需连接",
                $"TCP 通道 '{channel.Key}' 使用按需调试连接。");
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            var policyActive = _activePolicies.Contains(policy);
            if (_tcpSessions.TryGetValue(channel.Key, out var session) &&
                string.Equals(session.Policy, policy, StringComparison.OrdinalIgnoreCase))
            {
                return session.CreateSnapshot(policyActive);
            }

            return new CommunicationChannelRuntimeSnapshot(
                "TCP",
                channel.Key,
                policy,
                true,
                false,
                false,
                $"{channel.Host}:{channel.Port}",
                policyActive
                    ? GetLastStatus(_tcpLastStatuses, policy, channel.Key)
                        ?? $"TCP 运行时策略 '{policy}' 已启动，但通道 '{channel.Key}' 尚未连接：{channel.Host}:{channel.Port}。"
                    : $"TCP 运行时策略 '{policy}' 尚未启动。");
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<CommunicationChannelRuntimeSnapshot> ReconnectTcpAsync(
        TcpCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(channel);
        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return await GetTcpSnapshotAsync(channel, cancellationToken);
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            _activePolicies.Add(policy);
            if (_tcpSessions.TryGetValue(channel.Key, out var existing))
            {
                existing.Dispose();
                _tcpSessions.Remove(channel.Key);
            }

            if (!channel.Enabled)
            {
                return new CommunicationChannelRuntimeSnapshot(
                    "TCP",
                    channel.Key,
                    policy,
                    false,
                    false,
                    false,
                    "未启用",
                    $"TCP 通道 '{channel.Key}' 未启用。");
            }

            var session = new TcpChannelSession(channel, policy, RaiseFrameReceived);
            try
            {
                await session.ConnectAsync(cancellationToken);
                _tcpSessions[channel.Key] = session;
                var status = session.CreateSnapshot(policyActive: true).StatusText;
                SetLastStatus(_tcpLastStatuses, policy, channel.Key, status);
                _log?.Info("Communication", $"{DescribeTcpChannel(channel)} 手动重连成功：{status}");
                return session.CreateSnapshot(policyActive: true);
            }
            catch (Exception ex)
            {
                session.Dispose();
                var status = $"{DescribeTcpChannel(channel)} 手动重连失败：{ex.Message}";
                SetLastStatus(_tcpLastStatuses, policy, channel.Key, status);
                _log?.Warning("Communication", status);
                return new CommunicationChannelRuntimeSnapshot(
                    "TCP",
                    channel.Key,
                    policy,
                    true,
                    false,
                    false,
                    channel.Host,
                    status);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<byte[]?> TryExchangeSerialAsync(
        SerialCommunicationChannelSettings channel,
        byte[] payload,
        int timeoutMs,
        bool waitResponse,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return null;
        }

        SerialChannelSession? session;
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!_activePolicies.Contains(policy) ||
                !_serialSessions.TryGetValue(channel.Key, out session) ||
                !string.Equals(session.Policy, policy, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        finally
        {
            _sync.Release();
        }

        return await session.ExchangeAsync(payload, timeoutMs, waitResponse, cancellationToken);
    }

    public async Task<bool> TrySendSerialAsync(
        SerialCommunicationChannelSettings channel,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return false;
        }

        SerialChannelSession? session;
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!_activePolicies.Contains(policy) ||
                !_serialSessions.TryGetValue(channel.Key, out session) ||
                !string.Equals(session.Policy, policy, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        finally
        {
            _sync.Release();
        }

        await session.SendAsync(payload, cancellationToken);
        return true;
    }

    public async Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(
        SerialCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(channel);
        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (!channel.Enabled)
        {
            return new CommunicationChannelRuntimeSnapshot(
                "Serial",
                channel.Key,
                policy,
                false,
                false,
                false,
                "未启用",
                $"串口通道 '{channel.Key}' 未启用。");
        }

        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return new CommunicationChannelRuntimeSnapshot(
                "Serial",
                channel.Key,
                policy,
                false,
                false,
                false,
                "按需连接",
                $"串口通道 '{channel.Key}' 使用按需调试连接。");
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            var policyActive = _activePolicies.Contains(policy);
            if (_serialSessions.TryGetValue(channel.Key, out var session) &&
                string.Equals(session.Policy, policy, StringComparison.OrdinalIgnoreCase))
            {
                return session.CreateSnapshot(policyActive);
            }

            return new CommunicationChannelRuntimeSnapshot(
                "Serial",
                channel.Key,
                policy,
                true,
                false,
                false,
                "未连接",
                policyActive
                    ? GetLastStatus(_serialLastStatuses, policy, channel.Key)
                        ?? $"串口运行时策略 '{policy}' 已启动，但通道 '{channel.Key}' 尚未连接。"
                    : $"串口运行时策略 '{policy}' 尚未启动。");
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<CommunicationChannelRuntimeSnapshot> ReconnectSerialAsync(
        SerialCommunicationChannelSettings channel,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(channel);
        var policy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (policy == CommunicationChannelConnectionPolicies.OnDemand)
        {
            return await GetSerialSnapshotAsync(channel, cancellationToken);
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            _activePolicies.Add(policy);
            if (_serialSessions.TryGetValue(channel.Key, out var existing))
            {
                existing.Dispose();
                _serialSessions.Remove(channel.Key);
            }

            if (!channel.Enabled)
            {
                return new CommunicationChannelRuntimeSnapshot(
                    "Serial",
                    channel.Key,
                    policy,
                    false,
                    false,
                    false,
                    "未启用",
                    $"串口通道 '{channel.Key}' 未启用。");
            }

            var session = new SerialChannelSession(channel, policy, RaiseFrameReceived);
            try
            {
                await session.ConnectAsync(cancellationToken);
                _serialSessions[channel.Key] = session;
                var status = session.CreateSnapshot(policyActive: true).StatusText;
                SetLastStatus(_serialLastStatuses, policy, channel.Key, status);
                _log?.Info("Communication", $"{DescribeSerialChannel(channel)} 手动重连成功：{status}");
                return session.CreateSnapshot(policyActive: true);
            }
            catch (Exception ex)
            {
                session.Dispose();
                var status = $"{DescribeSerialChannel(channel)} 手动重连失败：{ex.Message}";
                SetLastStatus(_serialLastStatuses, policy, channel.Key, status);
                _log?.Warning("Communication", status);
                return new CommunicationChannelRuntimeSnapshot(
                    "Serial",
                    channel.Key,
                    policy,
                    true,
                    false,
                    false,
                    channel.PortName,
                    status);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _configurationRepository.ConfigurationSaved -= OnConfigurationSaved;
        _sync.Wait();
        try
        {
            DisposeAllLocked();
        }
        finally
        {
            _sync.Release();
            _sync.Dispose();
        }
    }

    private async Task RefreshPolicyLockedAsync(
        string policy,
        DeviceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var tcpChannels = configuration.SystemSettings.Communication.TcpChannels
            .Where(channel => channel.Enabled &&
                              CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy) == policy)
            .ToArray();
        var serialChannels = configuration.SystemSettings.Communication.SerialChannels
            .Where(channel => channel.Enabled &&
                              CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy) == policy)
            .ToArray();

        RemoveStaleSessionsLocked(_tcpSessions, policy, tcpChannels.Select(channel => channel.Key));
        RemoveStaleSessionsLocked(_serialSessions, policy, serialChannels.Select(channel => channel.Key));

        foreach (var channel in tcpChannels)
        {
            if (_tcpSessions.TryGetValue(channel.Key, out var existing) &&
                existing.Matches(channel, policy) &&
                existing.IsOperational)
            {
                continue;
            }

            if (existing is not null)
            {
                existing.Dispose();
                _tcpSessions.Remove(channel.Key);
            }

            var session = new TcpChannelSession(channel, policy, RaiseFrameReceived);
            try
            {
                await session.ConnectAsync(cancellationToken);
                _tcpSessions[channel.Key] = session;
                var status = session.CreateSnapshot(policyActive: true).StatusText;
                SetLastStatus(_tcpLastStatuses, policy, channel.Key, status);
                _log?.Info("Communication", $"{DescribeTcpChannel(channel)} 连接成功：{status}");
            }
            catch (Exception ex)
            {
                session.Dispose();
                var status = $"{DescribeTcpChannel(channel)} 连接失败：{ex.Message}";
                SetLastStatus(_tcpLastStatuses, policy, channel.Key, status);
                _log?.Warning("Communication", status);
            }
        }

        foreach (var channel in serialChannels)
        {
            if (_serialSessions.TryGetValue(channel.Key, out var existing) &&
                existing.Matches(channel, policy) &&
                existing.IsOperational)
            {
                continue;
            }

            if (existing is not null)
            {
                existing.Dispose();
                _serialSessions.Remove(channel.Key);
            }

            var session = new SerialChannelSession(channel, policy, RaiseFrameReceived);
            try
            {
                await session.ConnectAsync(cancellationToken);
                _serialSessions[channel.Key] = session;
                var status = session.CreateSnapshot(policyActive: true).StatusText;
                SetLastStatus(_serialLastStatuses, policy, channel.Key, status);
                _log?.Info("Communication", $"{DescribeSerialChannel(channel)} 连接成功：{status}");
            }
            catch (Exception ex)
            {
                session.Dispose();
                var status = $"{DescribeSerialChannel(channel)} 连接失败：{ex.Message}";
                SetLastStatus(_serialLastStatuses, policy, channel.Key, status);
                _log?.Warning("Communication", status);
            }
        }
    }

    private void DisconnectPolicyLocked(string policy)
    {
        DisconnectPolicyLocked(_tcpSessions, policy);
        DisconnectPolicyLocked(_serialSessions, policy);
    }

    private void DisposeAllLocked()
    {
        foreach (var session in _tcpSessions.Values)
        {
            session.Dispose();
        }

        foreach (var session in _serialSessions.Values)
        {
            session.Dispose();
        }

        _tcpSessions.Clear();
        _serialSessions.Clear();
        _tcpLastStatuses.Clear();
        _serialLastStatuses.Clear();
        _activePolicies.Clear();
    }

    private void OnConfigurationSaved(object? sender, DeviceConfiguration configuration)
    {
        _ = RefreshActivePoliciesAsync(configuration);
    }

    private async Task RefreshActivePoliciesAsync(DeviceConfiguration configuration)
    {
        if (_disposed)
        {
            return;
        }

        await _sync.WaitAsync();
        try
        {
            foreach (var policy in _activePolicies.ToArray())
            {
                await RefreshPolicyLockedAsync(policy, configuration, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _log?.Warning("Communication", $"Communication channel refresh failed: {ex.Message}");
        }
        finally
        {
            _sync.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void RaiseFrameReceived(CommunicationChannelRuntimeFrame frame)
    {
        FrameReceived?.Invoke(this, frame);
    }

    private static void RemoveStaleSessionsLocked<TSession>(
        Dictionary<string, TSession> sessions,
        string policy,
        IEnumerable<string> activeKeys)
        where TSession : IPolicySession
    {
        var active = activeKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sessions.ToArray())
        {
            if (!string.Equals(item.Value.Policy, policy, StringComparison.OrdinalIgnoreCase) ||
                active.Contains(item.Key))
            {
                continue;
            }

            item.Value.Dispose();
            sessions.Remove(item.Key);
        }
    }

    private static void DisconnectPolicyLocked<TSession>(
        Dictionary<string, TSession> sessions,
        string policy)
        where TSession : IPolicySession
    {
        foreach (var item in sessions.ToArray())
        {
            if (!string.Equals(item.Value.Policy, policy, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            item.Value.Dispose();
            sessions.Remove(item.Key);
        }
    }

    private static string CreateStatusKey(string policy, string key)
    {
        return $"{policy}|{key}";
    }

    private static void SetLastStatus(
        Dictionary<string, string> statuses,
        string policy,
        string key,
        string status)
    {
        statuses[CreateStatusKey(policy, key)] = status;
    }

    private static string? GetLastStatus(
        Dictionary<string, string> statuses,
        string policy,
        string key)
    {
        return statuses.TryGetValue(CreateStatusKey(policy, key), out var status)
            ? status
            : null;
    }

    private static string DescribeTcpChannel(TcpCommunicationChannelSettings channel)
    {
        return $"TCP 通道 '{channel.Name}'({channel.Key}, {channel.Mode}, {channel.Host}:{channel.Port}, 策略 {CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy)})";
    }

    private static string DescribeSerialChannel(SerialCommunicationChannelSettings channel)
    {
        return $"串口通道 '{channel.Name}'({channel.Key}, {channel.PortName}, {channel.BaudRate},{channel.DataBits},{channel.Parity},{channel.StopBits}, 策略 {CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy)})";
    }

    private interface IPolicySession : IDisposable
    {
        string Policy { get; }
    }

    private sealed class TcpChannelSession : IPolicySession
    {
        private const int MaxQueuedFrames = 200;

        private readonly SemaphoreSlim _io = new(1, 1);
        private readonly SemaphoreSlim _receivedSignal = new(0);
        private readonly object _clientLock = new();
        private readonly object _receiveLock = new();
        private readonly Queue<byte[]> _receivedFrames = new();
        private readonly Action<CommunicationChannelRuntimeFrame> _frameReceived;
        private readonly string _signature;
        private TcpCommunicationChannelSettings _channel;
        private TcpClient? _client;
        private TcpListener? _listener;
        private CancellationTokenSource? _acceptCancellation;
        private Task? _acceptTask;
        private TcpClient? _acceptedClient;
        private bool _disposed;
        private long _receivedBytes;
        private long _receivedFrameCount;

        public TcpChannelSession(
            TcpCommunicationChannelSettings channel,
            string policy,
            Action<CommunicationChannelRuntimeFrame> frameReceived)
        {
            _channel = channel;
            _frameReceived = frameReceived;
            Policy = policy;
            _signature = CreateSignature(channel, policy);
        }

        public string Policy { get; }

        public bool IsOperational => IsServer
            ? _listener is not null
            : IsClientConnected(_client);

        public bool Matches(TcpCommunicationChannelSettings channel, string policy)
        {
            return string.Equals(_signature, CreateSignature(channel, policy), StringComparison.Ordinal);
        }

        public CommunicationChannelRuntimeSnapshot CreateSnapshot(bool policyActive)
        {
            if (IsServer)
            {
                TcpClient? acceptedClient;
                lock (_clientLock)
                {
                    acceptedClient = _acceptedClient;
                }

                var listening = _listener is not null;
                var connected = IsClientConnected(acceptedClient);
                var peer = connected
                    ? DescribeEndpoint(acceptedClient)
                    : listening
                        ? $"{_channel.Host}:{_channel.Port}"
                        : "未监听";
                var status = connected
                    ? $"TCP 运行时 '{Policy}' 服务端监听中，客户端已接入：{peer}"
                    : listening
                        ? $"TCP 运行时 '{Policy}' 服务端监听中，等待客户端：{peer}"
                        : policyActive
                            ? $"TCP 运行时 '{Policy}' 服务端会话已启动，但当前未监听。"
                            : $"TCP 运行时 '{Policy}' 未启动。";

                return new CommunicationChannelRuntimeSnapshot(
                    "TCP",
                    _channel.Key,
                    Policy,
                    true,
                    connected,
                    listening,
                    peer,
                    status);
            }

            var client = _client;
            var isConnected = IsClientConnected(client);
            var clientPeer = isConnected
                ? DescribeEndpoint(client)
                : $"{_channel.Host}:{_channel.Port}";
            var clientStatus = isConnected
                ? $"TCP 运行时 '{Policy}' 客户端已连接：{clientPeer}"
                : policyActive
                    ? $"TCP 运行时 '{Policy}' 客户端已启动，但当前未连接：{clientPeer}"
                    : $"TCP 运行时 '{Policy}' 未启动。";

            return new CommunicationChannelRuntimeSnapshot(
                "TCP",
                _channel.Key,
                Policy,
                true,
                isConnected,
                false,
                clientPeer,
                clientStatus);
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            _acceptCancellation = new CancellationTokenSource();
            if (IsServer)
            {
                _listener = new TcpListener(ResolveListenAddress(_channel.Host), _channel.Port);
                _listener.Start();
                _acceptTask = Task.Run(() => AcceptLoopAsync(_acceptCancellation.Token), CancellationToken.None);
                return;
            }

            _client = await CreateClientAsync(_channel, cancellationToken);
            StartReceiveLoop(_client, _acceptCancellation.Token);
        }

        public async Task<byte[]> ExchangeAsync(
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _io.WaitAsync(cancellationToken);
            try
            {
                return IsServer
                    ? await ExchangeServerAsync(payload, timeoutMs, waitResponse, cancellationToken)
                    : await ExchangeClientAsync(payload, timeoutMs, waitResponse, cancellationToken);
            }
            finally
            {
                _io.Release();
            }
        }

        public async Task SendAsync(byte[] payload, int timeoutMs, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _io.WaitAsync(cancellationToken);
            try
            {
                var client = IsServer
                    ? await WaitAcceptedClientAsync(timeoutMs, cancellationToken)
                    : await EnsureClientAsync(cancellationToken);
                await WriteAndFlushAsync(client.GetStream(), payload, cancellationToken);
            }
            catch
            {
                if (IsServer)
                {
                    CloseAcceptedClient();
                }
                else
                {
                    CloseClient();
                }

                throw;
            }
            finally
            {
                _io.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _acceptCancellation?.Cancel();
            _listener?.Stop();
            CloseClient();
            CloseAcceptedClient();
            _acceptCancellation?.Dispose();
            _io.Dispose();
            _receivedSignal.Dispose();
        }

        private bool IsServer => string.Equals(_channel.Mode, "Server", StringComparison.OrdinalIgnoreCase);

        private async Task<byte[]> ExchangeClientAsync(
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken)
        {
            var client = await EnsureClientAsync(cancellationToken);
            try
            {
                await WriteAndFlushAsync(client.GetStream(), payload, cancellationToken);
                return waitResponse
                    ? await WaitReceivedFrameAsync(timeoutMs, cancellationToken)
                    : [];
            }
            catch
            {
                CloseClient();
                throw;
            }
        }

        private async Task<byte[]> ExchangeServerAsync(
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken)
        {
            var client = await WaitAcceptedClientAsync(timeoutMs, cancellationToken);
            try
            {
                var response = waitResponse
                    ? await WaitReceivedFrameAsync(timeoutMs, cancellationToken)
                    : [];
                await WriteAndFlushAsync(client.GetStream(), payload, cancellationToken);
                return response;
            }
            catch
            {
                CloseAcceptedClient(client);
                throw;
            }
        }

        private async Task<TcpClient> EnsureClientAsync(CancellationToken cancellationToken)
        {
            if (_client is { Connected: true })
            {
                return _client;
            }

            CloseClient();
            _client = await CreateClientAsync(_channel, cancellationToken);
            StartReceiveLoop(_client, EnsureSessionCancellationToken());
            return _client;
        }

        private async Task<TcpClient> WaitAcceptedClientAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var timeout = Math.Max(timeoutMs, 50);
            while (stopwatch.ElapsedMilliseconds <= timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_clientLock)
                {
                    if (_acceptedClient is { Connected: true })
                    {
                        return _acceptedClient;
                    }
                }

                await Task.Delay(25, cancellationToken);
            }

            throw new TimeoutException($"TCP server channel '{_channel.Key}' has no connected client.");
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var listener = _listener;
                    if (listener is null)
                    {
                        return;
                    }

                    var client = await listener.AcceptTcpClientAsync(cancellationToken);
                    client.NoDelay = true;
                    ReplaceAcceptedClient(client);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(100, CancellationToken.None);
                }
            }
        }

        private void ReplaceAcceptedClient(TcpClient client)
        {
            lock (_clientLock)
            {
                _acceptedClient?.Dispose();
                _acceptedClient = client;
            }

            StartReceiveLoop(client, EnsureSessionCancellationToken());
        }

        private void CloseClient()
        {
            _client?.Dispose();
            _client = null;
        }

        private void CloseAcceptedClient(TcpClient? client = null)
        {
            lock (_clientLock)
            {
                if (client is not null && !ReferenceEquals(client, _acceptedClient))
                {
                    client.Dispose();
                    return;
                }

                _acceptedClient?.Dispose();
                _acceptedClient = null;
            }
        }

        private CancellationToken EnsureSessionCancellationToken()
        {
            if (_acceptCancellation is null || _acceptCancellation.IsCancellationRequested)
            {
                _acceptCancellation?.Dispose();
                _acceptCancellation = new CancellationTokenSource();
            }

            return _acceptCancellation.Token;
        }

        private void StartReceiveLoop(TcpClient client, CancellationToken cancellationToken)
        {
            _ = Task.Run(() => ReceiveLoopAsync(client, cancellationToken), CancellationToken.None);
        }

        private async Task ReceiveLoopAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var frameBuffer = new List<byte>();
            try
            {
                var stream = client.GetStream();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (count == 0)
                    {
                        break;
                    }

                    var incoming = buffer.AsSpan(0, count).ToArray();
                    var totalBytes = Interlocked.Add(ref _receivedBytes, count);
                    foreach (var frame in CommunicationFrameCodec.DecodeFrames(frameBuffer, incoming, CreateFrameOptions(_channel)))
                    {
                        var totalFrames = Interlocked.Increment(ref _receivedFrameCount);
                        EnqueueReceivedFrame(frame.Label, frame.Payload, totalBytes, totalFrames);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch
            {
            }
            finally
            {
                if (IsServer)
                {
                    CloseAcceptedClient(client);
                }
                else if (ReferenceEquals(_client, client))
                {
                    CloseClient();
                }
            }
        }

        private void EnqueueReceivedFrame(string label, byte[] payload, long totalBytes, long totalFrames)
        {
            lock (_receiveLock)
            {
                while (_receivedFrames.Count >= MaxQueuedFrames)
                {
                    _receivedFrames.Dequeue();
                }

                _receivedFrames.Enqueue(payload);
            }

            if (!_disposed)
            {
                try
                {
                    _receivedSignal.Release();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _frameReceived(new CommunicationChannelRuntimeFrame("TCP", _channel.Key, label, payload, totalBytes, totalFrames));
            }
        }

        private bool TryDequeueReceivedFrame(out byte[] payload)
        {
            lock (_receiveLock)
            {
                if (_receivedFrames.Count > 0)
                {
                    payload = _receivedFrames.Dequeue();
                    return true;
                }
            }

            payload = [];
            return false;
        }

        private async Task<byte[]> WaitReceivedFrameAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            if (TryDequeueReceivedFrame(out var existing))
            {
                return existing;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Max(timeoutMs, 50));
            try
            {
                while (true)
                {
                    await _receivedSignal.WaitAsync(timeout.Token);
                    if (TryDequeueReceivedFrame(out var payload))
                    {
                        return payload;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"TCP channel '{_channel.Key}' response timed out.");
            }

            throw new TimeoutException($"TCP channel '{_channel.Key}' response timed out.");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private static bool IsClientConnected(TcpClient? client)
        {
            return client is { Connected: true };
        }

        private static string DescribeEndpoint(TcpClient? client)
        {
            return client?.Client.RemoteEndPoint?.ToString() ?? "未知对端";
        }

        private static async Task<TcpClient> CreateClientAsync(
            TcpCommunicationChannelSettings channel,
            CancellationToken cancellationToken)
        {
            var client = new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = Math.Max(channel.ReceiveTimeoutMs, 100),
                SendTimeout = Math.Max(channel.ConnectTimeoutMs, 100)
            };

            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(Math.Max(channel.ConnectTimeoutMs, 100));
                await client.ConnectAsync(channel.Host, channel.Port, connectTimeout.Token);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static async Task WriteAndFlushAsync(
            NetworkStream stream,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            if (payload.Length == 0)
            {
                return;
            }

            await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);
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

        private static string CreateSignature(TcpCommunicationChannelSettings channel, string policy)
        {
            return string.Join(
                "|",
                policy,
                channel.Key,
                channel.Mode,
                channel.Host,
                channel.Port,
                channel.ConnectTimeoutMs,
                channel.ReceiveTimeoutMs,
                channel.FrameMode,
                channel.Delimiter,
                channel.FixedFrameLength,
                channel.LengthPrefixBytes,
                channel.LengthPrefixLittleEndian,
                channel.MaxFrameLength,
                channel.AppendDelimiterOnSend,
                channel.PrefixPayloadOnSend);
        }
    }

    private sealed class SerialChannelSession : IPolicySession
    {
        private const int MaxQueuedFrames = 200;

        private readonly SemaphoreSlim _io = new(1, 1);
        private readonly SemaphoreSlim _receivedSignal = new(0);
        private readonly object _receiveLock = new();
        private readonly Queue<byte[]> _receivedFrames = new();
        private readonly Action<CommunicationChannelRuntimeFrame> _frameReceived;
        private readonly string _signature;
        private readonly SerialCommunicationChannelSettings _channel;
        private SerialPort? _port;
        private CancellationTokenSource? _receiveCancellation;
        private bool _disposed;
        private long _receivedBytes;
        private long _receivedFrameCount;

        public SerialChannelSession(
            SerialCommunicationChannelSettings channel,
            string policy,
            Action<CommunicationChannelRuntimeFrame> frameReceived)
        {
            _channel = channel;
            _frameReceived = frameReceived;
            Policy = policy;
            _signature = CreateSignature(channel, policy);
        }

        public string Policy { get; }

        public bool IsOperational => _port is { IsOpen: true };

        public bool Matches(SerialCommunicationChannelSettings channel, string policy)
        {
            return string.Equals(_signature, CreateSignature(channel, policy), StringComparison.Ordinal);
        }

        public CommunicationChannelRuntimeSnapshot CreateSnapshot(bool policyActive)
        {
            var connected = _port is { IsOpen: true };
            var peer = connected
                ? $"{_channel.PortName} / {_channel.BaudRate},{_channel.DataBits},{_channel.Parity},{_channel.StopBits}"
                : _channel.PortName;
            var status = connected
                ? $"串口运行时 '{Policy}' 端口已打开：{peer}"
                : policyActive
                    ? $"串口运行时 '{Policy}' 已启动，但端口未打开：{peer}"
                    : $"串口运行时 '{Policy}' 未启动。";

            return new CommunicationChannelRuntimeSnapshot(
                "Serial",
                _channel.Key,
                Policy,
                true,
                connected,
                false,
                peer,
                status);
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return Task.Run(() => EnsurePortOpen(), cancellationToken);
        }

        public async Task<byte[]> ExchangeAsync(
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _io.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var port = EnsurePortOpen();
                    port.WriteTimeout = Math.Max(timeoutMs, 100);

                    if (payload.Length > 0)
                    {
                        port.Write(payload, 0, payload.Length);
                    }
                }, cancellationToken);
            }
            catch
            {
                ClosePort();
                throw;
            }
            finally
            {
                _io.Release();
            }

            return waitResponse
                ? await WaitReceivedFrameAsync(timeoutMs, cancellationToken)
                : [];
        }

        public async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _io.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var port = EnsurePortOpen();
                    if (payload.Length > 0)
                    {
                        port.Write(payload, 0, payload.Length);
                    }
                }, cancellationToken);
            }
            catch
            {
                ClosePort();
                throw;
            }
            finally
            {
                _io.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClosePort();
            _io.Dispose();
            _receivedSignal.Dispose();
        }

        private SerialPort EnsurePortOpen()
        {
            if (_port is { IsOpen: true })
            {
                return _port;
            }

            ClosePort();
            _port = new SerialPort(
                _channel.PortName,
                _channel.BaudRate,
                ParseEnum(_channel.Parity, Parity.None),
                _channel.DataBits,
                ParseEnum(_channel.StopBits, StopBits.One))
            {
                ReadTimeout = 50,
                WriteTimeout = Math.Max(_channel.ReceiveTimeoutMs, 100)
            };
            _port.Open();
            StartReceiveLoop(_port);
            return _port;
        }

        private void ClosePort()
        {
            _receiveCancellation?.Cancel();
            _port?.Dispose();
            _port = null;
            _receiveCancellation?.Dispose();
            _receiveCancellation = null;
        }

        private void StartReceiveLoop(SerialPort port)
        {
            _receiveCancellation?.Cancel();
            _receiveCancellation?.Dispose();
            _receiveCancellation = new CancellationTokenSource();
            var cancellationToken = _receiveCancellation.Token;
            _ = Task.Run(() => ReceiveLoopAsync(port, cancellationToken), CancellationToken.None);
        }

        private async Task ReceiveLoopAsync(SerialPort port, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var frameBuffer = new List<byte>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int count;
                    try
                    {
                        if (!port.IsOpen)
                        {
                            break;
                        }

                        var available = port.BytesToRead;
                        if (available <= 0)
                        {
                            await Task.Delay(10, cancellationToken);
                            continue;
                        }

                        count = port.Read(buffer, 0, Math.Min(buffer.Length, available));
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }

                    if (count <= 0)
                    {
                        continue;
                    }

                    var incoming = buffer.AsSpan(0, count).ToArray();
                    var totalBytes = Interlocked.Add(ref _receivedBytes, count);
                    foreach (var frame in CommunicationFrameCodec.DecodeFrames(frameBuffer, incoming, CreateFrameOptions(_channel)))
                    {
                        var totalFrames = Interlocked.Increment(ref _receivedFrameCount);
                        EnqueueReceivedFrame(frame.Label, frame.Payload, totalBytes, totalFrames);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch
            {
            }
            finally
            {
                if (ReferenceEquals(_port, port))
                {
                    _port?.Dispose();
                    _port = null;
                }
            }
        }

        private void EnqueueReceivedFrame(string label, byte[] payload, long totalBytes, long totalFrames)
        {
            lock (_receiveLock)
            {
                while (_receivedFrames.Count >= MaxQueuedFrames)
                {
                    _receivedFrames.Dequeue();
                }

                _receivedFrames.Enqueue(payload);
            }

            if (!_disposed)
            {
                try
                {
                    _receivedSignal.Release();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _frameReceived(new CommunicationChannelRuntimeFrame("Serial", _channel.Key, label, payload, totalBytes, totalFrames));
            }
        }

        private bool TryDequeueReceivedFrame(out byte[] payload)
        {
            lock (_receiveLock)
            {
                if (_receivedFrames.Count > 0)
                {
                    payload = _receivedFrames.Dequeue();
                    return true;
                }
            }

            payload = [];
            return false;
        }

        private async Task<byte[]> WaitReceivedFrameAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            if (TryDequeueReceivedFrame(out var existing))
            {
                return existing;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Max(timeoutMs, 50));
            try
            {
                while (true)
                {
                    await _receivedSignal.WaitAsync(timeout.Token);
                    if (TryDequeueReceivedFrame(out var payload))
                    {
                        return payload;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Serial channel '{_channel.Key}' response timed out.");
            }

            throw new TimeoutException($"Serial channel '{_channel.Key}' response timed out.");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
            where TEnum : struct, Enum
        {
            return Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
        }

        private static string CreateSignature(SerialCommunicationChannelSettings channel, string policy)
        {
            return string.Join(
                "|",
                policy,
                channel.Key,
                channel.PortName,
                channel.BaudRate,
                channel.DataBits,
                channel.Parity,
                channel.StopBits,
                channel.ReceiveTimeoutMs,
                channel.FrameMode,
                channel.Delimiter,
                channel.FixedFrameLength,
                channel.LengthPrefixBytes,
                channel.LengthPrefixLittleEndian,
                channel.MaxFrameLength,
                channel.AppendDelimiterOnSend,
                channel.PrefixPayloadOnSend);
        }
    }
}
