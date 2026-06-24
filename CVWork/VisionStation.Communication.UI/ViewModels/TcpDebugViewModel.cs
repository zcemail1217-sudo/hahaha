using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Windows;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Application;
using VisionStation.Communication;
using VisionStation.Domain;

namespace VisionStation.Communication.UI.ViewModels;

public sealed class TcpDebugViewModel : BindableBase, IDisposable
{
    private const string TcpClientMode = "Client";
    private const string TcpServerMode = "Server";
    private const string TcpFrameRaw = "Raw";
    private const string TcpFrameDelimiter = "Delimiter";
    private const string TcpFrameFixedLength = "FixedLength";
    private const string TcpFrameLengthPrefix = "LengthPrefix";
    private const int TcpMaxLogCharacters = 60000;
    private const int TcpMaxQueuedLogCharacters = 24000;

    private readonly ITcpDebugSession _tcpSession;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly object _tcpLogGate = new();
    private readonly StringBuilder _tcpPendingLog = new();
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
    private bool _disposed;
    private int _tcpLogFlushScheduled;
    private int _tcpDroppedLogLines;
    private long _tcpReceivedBytes;
    private long _tcpReceivedFrames;

    public TcpDebugViewModel(ITcpDebugSession tcpSession, IUiDispatcher uiDispatcher)
    {
        _tcpSession = tcpSession;
        _uiDispatcher = uiDispatcher;

        TcpConnectCommand = new DelegateCommand(async () => await ConnectAsync());
        TcpDisconnectCommand = new DelegateCommand(async () => await DisconnectAsync());
        TcpSendCommand = new DelegateCommand(async () => await SendAsync());
        TcpClearCommand = new DelegateCommand(ClearLog);

        _tcpSession.StateChanged += OnTcpSessionStateChanged;
        _tcpSession.FrameReceived += OnTcpSessionFrameReceived;
    }

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

    public ObservableCollection<TextSelectionOption> TcpLengthPrefixByteOptions { get; } = new(
    [
        new TextSelectionOption("1", "1 byte"),
        new TextSelectionOption("2", "2 bytes"),
        new TextSelectionOption("4", "4 bytes")
    ]);

    public DelegateCommand TcpConnectCommand { get; }

    public DelegateCommand TcpDisconnectCommand { get; }

    public DelegateCommand TcpSendCommand { get; }

    public DelegateCommand TcpClearCommand { get; }

    public bool IsServerMode => IsTcpServerMode(TcpMode);

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
                RaisePropertyChanged(nameof(IsServerMode));
                RaiseTcpModeProperties();
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
                AppendLog($"FRAME MODE -> {GetTcpFrameModeText(resolved)}");
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

    public async Task ConnectAsync()
    {
        try
        {
            if (IsTcpServerMode(TcpMode))
            {
                await StartServerAsync();
                return;
            }

            await ConnectClientAsync();
        }
        catch (Exception ex)
        {
            TcpStatusText = ex.Message;
        }
    }

    public Task DisconnectAsync()
    {
        _tcpSession.Disconnect(IsTcpServerMode(TcpMode) ? "TCP 服务端已停止监听。" : "TCP 客户端已断开。");
        return Task.CompletedTask;
    }

    public async Task SendAsync()
    {
        try
        {
            if (!TcpIsConnected)
            {
                throw new InvalidOperationException(IsTcpServerMode(TcpMode)
                    ? "请先开始监听，并等待客户端连接。"
                    : "请先连接 TCP 服务端。");
            }

            var payload = CreateSendPayload();
            if (payload.Length == 0)
            {
                throw new InvalidOperationException("发送内容不能为空。");
            }

            await _tcpSession.SendAsync(payload);
            AppendLog("TX", payload);
            TcpStatusText = $"TCP 已发送：{payload.Length} bytes";
        }
        catch (Exception ex)
        {
            TcpStatusText = ex.Message;
        }
    }

    public void ApplyRuntimeSnapshot(CommunicationChannelRuntimeSnapshot snapshot)
    {
        TcpIsConnected = snapshot.IsConnected;
        TcpIsListening = snapshot.IsListening;
        TcpPeerText = snapshot.PeerText;
        TcpStatusText = snapshot.StatusText;
    }

    public void SetExternalStatus(string statusText)
    {
        TcpStatusText = statusText;
    }

    public void AppendExternalLog(string direction, byte[] payload)
    {
        AppendLog(direction, payload);
    }

    public void ClearExternalLog()
    {
        ClearLog();
    }

    private async Task ConnectClientAsync()
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
            FrameOptions = CreateFrameOptions()
        });
    }

    private async Task StartServerAsync()
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
            FrameOptions = CreateFrameOptions()
        });
    }

    private byte[] CreateSendPayload()
    {
        return CommunicationFrameCodec.CreatePayload(TcpPayload, TcpSendAsHex, CreateFrameOptions());
    }

    private CommunicationFrameOptions CreateFrameOptions()
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

    private void ClearLog()
    {
        TcpResponse = string.Empty;
        TcpStatusText = "TCP 日志已清空。";
    }

    private void AppendLog(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        AppendLogBlock(line);
    }

    private void AppendLogBlock(string block)
    {
        TcpResponse = string.IsNullOrWhiteSpace(TcpResponse)
            ? block
            : $"{TcpResponse}{Environment.NewLine}{block}";

        if (TcpResponse.Length > TcpMaxLogCharacters)
        {
            TcpResponse = TcpResponse[^TcpMaxLogCharacters..];
        }
    }

    private void AppendLog(string direction, byte[] payload)
    {
        AppendLog($"{direction} {payload.Length} bytes  {FormatPayload(payload)}");
    }

    private void QueueLog(string direction, byte[] payload)
    {
        QueueLog($"{direction} {payload.Length} bytes  {FormatPayload(payload)}");
    }

    private void QueueLog(string text)
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

        ScheduleLogFlush();
    }

    private void ScheduleLogFlush()
    {
        if (Interlocked.Exchange(ref _tcpLogFlushScheduled, 1) == 0)
        {
            _ = FlushLogSoonAsync();
        }
    }

    private async Task FlushLogSoonAsync()
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
                    AppendLogBlock($"{DateTime.Now:HH:mm:ss.fff}  LOG DROP {dropped} lines，接收过快，已保护界面刷新");
                }

                if (!string.IsNullOrWhiteSpace(block))
                {
                    AppendLogBlock(block);
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
                    ScheduleLogFlush();
                }
            }
        }
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
                AppendLog(state.LogText);
            }
        });
    }

    private void OnTcpSessionFrameReceived(object? sender, CommunicationSessionFrame frame)
    {
        Interlocked.Exchange(ref _tcpReceivedBytes, frame.TotalBytes);
        Interlocked.Exchange(ref _tcpReceivedFrames, frame.TotalFrames);
        QueueLog(frame.Label, frame.Payload);
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

    private void RaiseTcpFrameProperties()
    {
        RaisePropertyChanged(nameof(TcpDelimiterOptionsVisibility));
        RaisePropertyChanged(nameof(TcpFixedLengthOptionsVisibility));
        RaisePropertyChanged(nameof(TcpLengthPrefixOptionsVisibility));
    }

    private string FormatPayload(byte[] payload)
    {
        return CommunicationFrameCodec.FormatPayload(payload, TcpSendAsHex);
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

    private static int ParseInt(string? text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               int.TryParse(text, out value)
            ? value
            : fallback;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tcpSession.StateChanged -= OnTcpSessionStateChanged;
        _tcpSession.FrameReceived -= OnTcpSessionFrameReceived;
        _tcpSession.Dispose();
    }
}
