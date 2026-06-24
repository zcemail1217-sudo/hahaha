using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace VisionStation.Communication;

public sealed record CommunicationSessionState(
    bool IsConnected,
    bool IsListening,
    string PeerText,
    string StatusText,
    string LogText = "");

public sealed record CommunicationSessionFrame(
    string Label,
    byte[] Payload,
    long TotalBytes,
    long TotalFrames);

public sealed record TcpDebugSessionSettings
{
    public string Host { get; init; } = "127.0.0.1";

    public IPAddress ListenAddress { get; init; } = IPAddress.Any;

    public int Port { get; init; } = 502;

    public bool ServerMode { get; init; }

    public CommunicationFrameOptions FrameOptions { get; init; } = new();
}

public sealed record SerialDebugSessionSettings
{
    public string PortName { get; init; } = "COM1";

    public int BaudRate { get; init; } = 9600;

    public int DataBits { get; init; } = 8;

    public Parity Parity { get; init; } = Parity.None;

    public StopBits StopBits { get; init; } = StopBits.One;

    public CommunicationFrameOptions FrameOptions { get; init; } = new();
}

public interface ITcpDebugSession : IDisposable
{
    event EventHandler<CommunicationSessionState>? StateChanged;

    event EventHandler<CommunicationSessionFrame>? FrameReceived;

    Task ConnectAsync(TcpDebugSessionSettings settings, CancellationToken cancellationToken = default);

    Task SendAsync(byte[] payload, CancellationToken cancellationToken = default);

    void Disconnect(string? statusText = null);
}

public interface ISerialDebugSession : IDisposable
{
    event EventHandler<CommunicationSessionState>? StateChanged;

    event EventHandler<CommunicationSessionFrame>? FrameReceived;

    Task OpenAsync(SerialDebugSessionSettings settings, CancellationToken cancellationToken = default);

    Task SendAsync(byte[] payload, CancellationToken cancellationToken = default);

    void Close(string? statusText = null);
}

public sealed class TcpDebugSession : ITcpDebugSession
{
    private CancellationTokenSource? _sessionCancellation;
    private TcpListener? _listener;
    private TcpClient? _client;
    private CommunicationFrameOptions _frameOptions = new();
    private bool _serverMode;
    private long _receivedBytes;
    private long _receivedFrames;

    public event EventHandler<CommunicationSessionState>? StateChanged;

    public event EventHandler<CommunicationSessionFrame>? FrameReceived;

    public async Task ConnectAsync(TcpDebugSessionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Disconnect(null);
        ResetCounters();
        _frameOptions = settings.FrameOptions;
        _serverMode = settings.ServerMode;
        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (settings.ServerMode)
        {
            _listener = new TcpListener(settings.ListenAddress, settings.Port);
            _listener.Start();
            RaiseState(
                false,
                true,
                "Waiting for client",
                $"TCP server listening: {settings.ListenAddress}:{settings.Port}",
                $"LISTEN <- {settings.ListenAddress}:{settings.Port}");
            _ = AcceptLoopAsync(_sessionCancellation.Token);
            return;
        }

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, _sessionCancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        var peer = DescribeEndpoint(client);
        RaiseState(true, false, peer, $"TCP client connected: {peer}", $"CONNECT -> {peer}");
        _ = ReceiveLoopAsync(client, _sessionCancellation.Token);
    }

    public async Task SendAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null || !client.Connected)
        {
            throw new InvalidOperationException("TCP is not connected.");
        }

        await client.GetStream().WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken).ConfigureAwait(false);
    }

    public void Disconnect(string? statusText = null)
    {
        try
        {
            _sessionCancellation?.Cancel();
            _listener?.Stop();
            _client?.Close();
        }
        finally
        {
            _listener = null;
            _client = null;
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
        }

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            RaiseState(false, false, "Disconnected", statusText);
        }
    }

    public void Dispose()
    {
        Disconnect(null);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _listener is not null)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _client?.Close();
                _client = client;
                var peer = DescribeEndpoint(client);
                RaiseState(true, true, peer, $"TCP client accepted: {peer}", $"ACCEPT <- {peer}");
                _ = ReceiveLoopAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            RaiseState(false, _serverMode, _serverMode ? "Waiting for client" : "Disconnected", ex.Message);
        }
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
                var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var incoming = buffer.AsSpan(0, count).ToArray();
                var totalBytes = Interlocked.Add(ref _receivedBytes, count);
                foreach (var frame in CommunicationFrameCodec.DecodeFrames(frameBuffer, incoming, _frameOptions))
                {
                    var totalFrames = Interlocked.Increment(ref _receivedFrames);
                    FrameReceived?.Invoke(this, new CommunicationSessionFrame(frame.Label, frame.Payload, totalBytes, totalFrames));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            RaiseState(false, _serverMode, _serverMode ? "Waiting for client" : "Disconnected", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_client, client))
            {
                _client?.Close();
                _client = null;
                RaiseState(
                    false,
                    _serverMode,
                    _serverMode ? "Waiting for client" : "Disconnected",
                    _serverMode ? "TCP client disconnected; server is still listening." : "TCP disconnected.");
            }
        }
    }

    private void ResetCounters()
    {
        Interlocked.Exchange(ref _receivedBytes, 0);
        Interlocked.Exchange(ref _receivedFrames, 0);
    }

    private void RaiseState(bool connected, bool listening, string peerText, string statusText, string logText = "")
    {
        StateChanged?.Invoke(this, new CommunicationSessionState(connected, listening, peerText, statusText, logText));
    }

    private static string DescribeEndpoint(TcpClient client)
    {
        return client.Client.RemoteEndPoint?.ToString() ?? "Unknown peer";
    }
}

public sealed class SerialDebugSession : ISerialDebugSession
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private SerialPort? _port;
    private CommunicationFrameOptions _frameOptions = new();
    private long _receivedBytes;
    private long _receivedFrames;

    public event EventHandler<CommunicationSessionState>? StateChanged;

    public event EventHandler<CommunicationSessionFrame>? FrameReceived;

    public async Task OpenAsync(SerialDebugSessionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Close(null);
        ResetCounters();
        _frameOptions = settings.FrameOptions;

        var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            ReadTimeout = 100,
            WriteTimeout = 1200,
            ReadBufferSize = 65536,
            WriteBufferSize = 65536
        };

        try
        {
            await Task.Run(port.Open, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            port.Dispose();
            throw;
        }

        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _port = port;
        var peer = DescribePort(port);
        RaiseState(true, false, peer, $"Serial port opened: {peer}", $"OPEN -> {peer}");
        _ = ReceiveLoopAsync(port, _sessionCancellation.Token);
    }

    public async Task SendAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
        {
            throw new InvalidOperationException("Serial port is not open.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => port.Write(payload, 0, payload.Length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Close(string? statusText = null)
    {
        try
        {
            _sessionCancellation?.Cancel();
            _port?.Close();
            _port?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _port = null;
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
        }

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            RaiseState(false, false, "Closed", statusText);
        }
    }

    public void Dispose()
    {
        Close(null);
        _writeLock.Dispose();
    }

    private async Task ReceiveLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var frameBuffer = new List<byte>();
        string? faultStatus = null;

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
                        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
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
                foreach (var frame in CommunicationFrameCodec.DecodeFrames(frameBuffer, incoming, _frameOptions))
                {
                    var totalFrames = Interlocked.Increment(ref _receivedFrames);
                    FrameReceived?.Invoke(this, new CommunicationSessionFrame(frame.Label, frame.Payload, totalBytes, totalFrames));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException ex) when (cancellationToken.IsCancellationRequested)
        {
            faultStatus = ex.Message;
        }
        catch (Exception ex)
        {
            faultStatus = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_port, port))
            {
                _port?.Dispose();
                _port = null;
                _sessionCancellation?.Dispose();
                _sessionCancellation = null;
                RaiseState(false, false, "Closed", string.IsNullOrWhiteSpace(faultStatus) ? "Serial port closed." : faultStatus);
            }
        }
    }

    private void ResetCounters()
    {
        Interlocked.Exchange(ref _receivedBytes, 0);
        Interlocked.Exchange(ref _receivedFrames, 0);
    }

    private void RaiseState(bool connected, bool listening, string peerText, string statusText, string logText = "")
    {
        StateChanged?.Invoke(this, new CommunicationSessionState(connected, listening, peerText, statusText, logText));
    }

    private static string DescribePort(SerialPort port)
    {
        return $"{port.PortName} / {port.BaudRate},{port.DataBits},{port.Parity},{port.StopBits}";
    }
}
