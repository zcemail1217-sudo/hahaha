using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VisionStation.Communication;
using VisionStation.Domain;
using static VisionStation.Vision.Tools.CommunicationToolParameters;

namespace VisionStation.Vision.Tools;

public sealed class TcpCommunicationTool : IVisionTool
{
    private readonly IDeviceConfigurationRepository _configurationRepository;
    private readonly ICommunicationChannelRuntime? _communicationChannels;

    public TcpCommunicationTool(
        IDeviceConfigurationRepository configurationRepository,
        ICommunicationChannelRuntime? communicationChannels = null)
    {
        _configurationRepository = configurationRepository;
        _communicationChannels = communicationChannels;
    }

    public VisionToolKind Kind => VisionToolKind.TcpCommunication;

    public async Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var configuration = await _configurationRepository.GetAsync(cancellationToken);
            var channelKey = context.ResolveTextTokens(GetParameter(definition.Parameters, "channelKey", "tcp-main"));
            var channel = configuration.SystemSettings.Communication.TcpChannels.FirstOrDefault(item =>
                string.Equals(item.Key, channelKey, StringComparison.OrdinalIgnoreCase));

            if (channel is null || !channel.Enabled)
            {
                throw new InvalidOperationException($"TCP 通道 {channelKey} 未配置或未启用。");
            }

            if (!string.Equals(channel.Mode, "Client", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(channel.Mode, "Server", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("配方 TCP 通讯工具当前使用客户端通道；服务端监听请在设备调试台中使用。");
            }

            var payloadText = context.ResolveTextTokens(GetParameter(definition.Parameters, "payload", string.Empty));
            var expected = context.ResolveTextTokens(GetParameter(definition.Parameters, "expectedContains", string.Empty));
            var payload = CommunicationToolSupport.CreatePayload(
                payloadText,
                GetParameter(definition.Parameters, "payloadMode", "Text"),
                channel.FrameMode,
                channel.Delimiter,
                channel.AppendDelimiterOnSend,
                channel.PrefixPayloadOnSend,
                channel.LengthPrefixBytes,
                channel.LengthPrefixLittleEndian);
            var timeoutMs = GetInt(definition.Parameters, "timeoutMs", channel.ReceiveTimeoutMs);
            var waitResponse = GetBool(definition.Parameters, "waitResponse", true);
            var runtimeResponse = _communicationChannels is null
                ? null
                : await _communicationChannels.TryExchangeTcpAsync(
                    channel,
                    payload,
                    timeoutMs,
                    waitResponse,
                    cancellationToken);
            if (runtimeResponse is not null)
            {
                stopwatch.Stop();
                return CreateResult(definition, context, stopwatch.Elapsed, runtimeResponse, channel.Key, payload.Length, expected);
            }

            if (string.Equals(channel.Mode, "Server", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteServerAsync(
                    definition,
                    context,
                    stopwatch,
                    channel,
                    payload,
                    expected,
                    timeoutMs,
                    waitResponse,
                    cancellationToken);
            }

            using var client = new TcpClient();
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(Math.Max(channel.ConnectTimeoutMs, 100));
            await client.ConnectAsync(channel.Host, channel.Port, connectTimeout.Token);
            await using var stream = client.GetStream();
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
            }

            var response = waitResponse
                ? await CommunicationToolSupport.ReadTcpResponseAsync(stream, channel, timeoutMs, cancellationToken)
                : [];

            stopwatch.Stop();
            return CreateResult(definition, context, stopwatch.Elapsed, response, channel.Key, payload.Length, expected);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return CreateErrorResult(definition, stopwatch.Elapsed, ex.Message);
        }
    }

    private static async Task<ToolResult> ExecuteServerAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        Stopwatch stopwatch,
        TcpCommunicationChannelSettings channel,
        byte[] payload,
        string expected,
        int timeoutMs,
        bool waitResponse,
        CancellationToken cancellationToken)
    {
        TcpListener? listener = null;
        try
        {
            var listenAddress = CommunicationToolSupport.ResolveListenAddress(channel.Host);
            listener = new TcpListener(listenAddress, channel.Port);
            listener.Start();

            using var acceptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acceptTimeout.CancelAfter(Math.Max(timeoutMs, 50));
            using var client = await listener.AcceptTcpClientAsync(acceptTimeout.Token);
            await using var stream = client.GetStream();

            var received = waitResponse
                ? await CommunicationToolSupport.ReadTcpResponseAsync(stream, channel, timeoutMs, cancellationToken)
                : [];

            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
            }

            stopwatch.Stop();
            return CreateResult(definition, context, stopwatch.Elapsed, received, channel.Key, payload.Length, expected);
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static ToolResult CreateResult(
        VisionToolDefinition definition,
        VisionToolContext context,
        TimeSpan duration,
        byte[] response,
        string channelKey,
        int sentBytes,
        string expected)
    {
        var responseText = Encoding.UTF8.GetString(response);
        var responseHex = Convert.ToHexString(response);
        var outcome = string.IsNullOrWhiteSpace(expected) || responseText.Contains(expected, StringComparison.OrdinalIgnoreCase)
            ? InspectionOutcome.Ok
            : InspectionOutcome.Ng;

        context.SetPortOutput(definition, "ResponseOutput", responseText);
        context.SetPortOutput(definition, "ResponseHexOutput", responseHex);
        context.SetPortOutput(definition, "ResponseBytesOutput", response.Length);

        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.TcpCommunication,
            Outcome = outcome,
            Duration = duration,
            Message = outcome == InspectionOutcome.Ok
                ? $"TCP 通讯完成：TX {sentBytes} bytes / RX {response.Length} bytes"
                : $"TCP 响应未包含期望内容：{expected}",
            Data = new Dictionary<string, string>
            {
                ["channelKey"] = channelKey,
                ["sentBytes"] = sentBytes.ToString(),
                ["responseBytes"] = response.Length.ToString(),
                ["responseText"] = responseText,
                ["responseHex"] = responseHex,
                ["expectedContains"] = expected
            }
        };
    }

    private static ToolResult CreateErrorResult(VisionToolDefinition definition, TimeSpan duration, string message)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.TcpCommunication,
            Outcome = InspectionOutcome.Error,
            Duration = duration,
            Message = message
        };
    }
}

public sealed class SerialCommunicationTool : IVisionTool
{
    private readonly IDeviceConfigurationRepository _configurationRepository;
    private readonly ICommunicationChannelRuntime? _communicationChannels;

    public SerialCommunicationTool(
        IDeviceConfigurationRepository configurationRepository,
        ICommunicationChannelRuntime? communicationChannels = null)
    {
        _configurationRepository = configurationRepository;
        _communicationChannels = communicationChannels;
    }

    public VisionToolKind Kind => VisionToolKind.SerialCommunication;

    public async Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var configuration = await _configurationRepository.GetAsync(cancellationToken);
            var channelKey = context.ResolveTextTokens(GetParameter(definition.Parameters, "channelKey", "serial-main"));
            var channel = configuration.SystemSettings.Communication.SerialChannels.FirstOrDefault(item =>
                string.Equals(item.Key, channelKey, StringComparison.OrdinalIgnoreCase));

            if (channel is null || !channel.Enabled)
            {
                throw new InvalidOperationException($"串口通道 {channelKey} 未配置或未启用。");
            }

            var payloadText = context.ResolveTextTokens(GetParameter(definition.Parameters, "payload", string.Empty));
            var expected = context.ResolveTextTokens(GetParameter(definition.Parameters, "expectedContains", string.Empty));
            var payload = CommunicationToolSupport.CreatePayload(
                payloadText,
                GetParameter(definition.Parameters, "payloadMode", "Text"),
                channel.FrameMode,
                channel.Delimiter,
                channel.AppendDelimiterOnSend,
                channel.PrefixPayloadOnSend,
                channel.LengthPrefixBytes,
                channel.LengthPrefixLittleEndian);
            var timeoutMs = GetInt(definition.Parameters, "timeoutMs", channel.ReceiveTimeoutMs);
            var waitResponse = GetBool(definition.Parameters, "waitResponse", true);
            var runtimeResponse = _communicationChannels is null
                ? null
                : await _communicationChannels.TryExchangeSerialAsync(
                    channel,
                    payload,
                    timeoutMs,
                    waitResponse,
                    cancellationToken);
            if (runtimeResponse is not null)
            {
                stopwatch.Stop();
                return CreateResult(definition, context, stopwatch.Elapsed, runtimeResponse, channel.Key, payload.Length, expected);
            }

            var response = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var port = new SerialPort(
                    channel.PortName,
                    channel.BaudRate,
                    ParseEnum(channel.Parity, Parity.None),
                    channel.DataBits,
                    ParseEnum(channel.StopBits, StopBits.One))
                {
                    ReadTimeout = 50,
                    WriteTimeout = Math.Max(timeoutMs, 100)
                };

                port.Open();
                if (payload.Length > 0)
                {
                    port.Write(payload, 0, payload.Length);
                }

                return waitResponse
                    ? CommunicationToolSupport.ReadSerialResponse(port, channel, timeoutMs, cancellationToken)
                    : [];
            }, cancellationToken);

            stopwatch.Stop();
            return CreateResult(definition, context, stopwatch.Elapsed, response, channel.Key, payload.Length, expected);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return CreateErrorResult(definition, stopwatch.Elapsed, ex.Message);
        }
    }

    private static ToolResult CreateResult(
        VisionToolDefinition definition,
        VisionToolContext context,
        TimeSpan duration,
        byte[] response,
        string channelKey,
        int sentBytes,
        string expected)
    {
        var responseText = Encoding.UTF8.GetString(response);
        var responseHex = Convert.ToHexString(response);
        var outcome = string.IsNullOrWhiteSpace(expected) || responseText.Contains(expected, StringComparison.OrdinalIgnoreCase)
            ? InspectionOutcome.Ok
            : InspectionOutcome.Ng;

        context.SetPortOutput(definition, "ResponseOutput", responseText);
        context.SetPortOutput(definition, "ResponseHexOutput", responseHex);
        context.SetPortOutput(definition, "ResponseBytesOutput", response.Length);

        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.SerialCommunication,
            Outcome = outcome,
            Duration = duration,
            Message = outcome == InspectionOutcome.Ok
                ? $"串口通讯完成：TX {sentBytes} bytes / RX {response.Length} bytes"
                : $"串口响应未包含期望内容：{expected}",
            Data = new Dictionary<string, string>
            {
                ["channelKey"] = channelKey,
                ["sentBytes"] = sentBytes.ToString(),
                ["responseBytes"] = response.Length.ToString(),
                ["responseText"] = responseText,
                ["responseHex"] = responseHex,
                ["expectedContains"] = expected
            }
        };
    }

    private static ToolResult CreateErrorResult(VisionToolDefinition definition, TimeSpan duration, string message)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.SerialCommunication,
            Outcome = InspectionOutcome.Error,
            Duration = duration,
            Message = message
        };
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
    }
}

internal static class CommunicationToolSupport
{
    public static IPAddress ResolveListenAddress(string? value)
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

    public static byte[] CreatePayload(
        string payloadText,
        string payloadMode,
        string frameMode,
        string delimiterText,
        bool appendDelimiterOnSend,
        bool prefixPayloadOnSend,
        int lengthPrefixBytes,
        bool lengthPrefixLittleEndian)
    {
        return CommunicationFrameCodec.CreatePayload(
            payloadText,
            string.Equals(payloadMode, "Hex", StringComparison.OrdinalIgnoreCase),
            new CommunicationFrameOptions
            {
                FrameMode = frameMode,
                Delimiter = delimiterText,
                AppendDelimiterOnSend = appendDelimiterOnSend,
                PrefixPayloadOnSend = prefixPayloadOnSend,
                LengthPrefixBytes = lengthPrefixBytes,
                LengthPrefixLittleEndian = lengthPrefixLittleEndian
            });
    }

    public static async Task<byte[]> ReadTcpResponseAsync(
        NetworkStream stream,
        TcpCommunicationChannelSettings channel,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(timeoutMs, 50));
        var buffer = new byte[4096];
        var frameBuffer = new List<byte>();

        while (!timeout.IsCancellationRequested)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token);
            if (count == 0)
            {
                break;
            }

            var frame = CommunicationFrameCodec.DecodeFirstFrame(
                frameBuffer,
                buffer.AsSpan(0, count).ToArray(),
                CreateFrameOptions(channel));
            if (frame is not null)
            {
                return frame;
            }
        }

        return frameBuffer.ToArray();
    }

    public static byte[] ReadSerialResponse(
        SerialPort port,
        SerialCommunicationChannelSettings channel,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var frameBuffer = new List<byte>();
        var buffer = new byte[4096];
        var deadline = DateTimeOffset.Now.AddMilliseconds(Math.Max(timeoutMs, 50));

        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = port.BytesToRead;
            if (available <= 0)
            {
                Thread.Sleep(10);
                continue;
            }

            var count = port.Read(buffer, 0, Math.Min(buffer.Length, available));
            var frame = CommunicationFrameCodec.DecodeFirstFrame(
                frameBuffer,
                buffer.AsSpan(0, count).ToArray(),
                CreateFrameOptions(channel));
            if (frame is not null)
            {
                return frame;
            }
        }

        return frameBuffer.ToArray();
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

    private static byte[]? DecodeFirstFrame(
        List<byte> frameBuffer,
        byte[] incoming,
        string frameMode,
        string delimiterText,
        int fixedFrameLength,
        int lengthPrefixBytes,
        bool lengthPrefixLittleEndian,
        int maxFrameLength)
    {
        if (string.Equals(frameMode, "Raw", StringComparison.OrdinalIgnoreCase))
        {
            return incoming;
        }

        frameBuffer.AddRange(incoming);
        maxFrameLength = Math.Clamp(maxFrameLength, 1, 1024 * 1024);

        if (string.Equals(frameMode, "Delimiter", StringComparison.OrdinalIgnoreCase))
        {
            var delimiter = ResolveDelimiterBytes(delimiterText);
            if (TryFindSequence(frameBuffer, delimiter, out var index))
            {
                var payload = frameBuffer.GetRange(0, index).ToArray();
                frameBuffer.RemoveRange(0, index + delimiter.Length);
                return payload;
            }

            DrainOversized(frameBuffer, maxFrameLength);
            return null;
        }

        if (string.Equals(frameMode, "FixedLength", StringComparison.OrdinalIgnoreCase))
        {
            var length = Math.Clamp(fixedFrameLength, 1, maxFrameLength);
            if (frameBuffer.Count >= length)
            {
                var payload = frameBuffer.GetRange(0, length).ToArray();
                frameBuffer.RemoveRange(0, length);
                return payload;
            }

            DrainOversized(frameBuffer, maxFrameLength);
            return null;
        }

        if (string.Equals(frameMode, "LengthPrefix", StringComparison.OrdinalIgnoreCase))
        {
            var prefixBytes = lengthPrefixBytes is 1 or 2 or 4 ? lengthPrefixBytes : 2;
            while (frameBuffer.Count >= prefixBytes)
            {
                var payloadLength = ReadLengthPrefix(frameBuffer, prefixBytes, lengthPrefixLittleEndian);
                if (payloadLength < 0 || payloadLength > maxFrameLength)
                {
                    frameBuffer.RemoveAt(0);
                    continue;
                }

                var totalLength = prefixBytes + (int)payloadLength;
                if (frameBuffer.Count < totalLength)
                {
                    return null;
                }

                var payload = frameBuffer.GetRange(prefixBytes, (int)payloadLength).ToArray();
                frameBuffer.RemoveRange(0, totalLength);
                return payload;
            }
        }

        return null;
    }

    private static void DrainOversized(List<byte> frameBuffer, int maxFrameLength)
    {
        if (frameBuffer.Count > maxFrameLength)
        {
            frameBuffer.RemoveRange(0, frameBuffer.Count - maxFrameLength);
        }
    }

    private static long ReadLengthPrefix(IReadOnlyList<byte> buffer, int prefixBytes, bool littleEndian)
    {
        long value = 0;
        if (littleEndian)
        {
            for (var i = 0; i < prefixBytes; i++)
            {
                value |= (long)buffer[i] << (i * 8);
            }

            return value;
        }

        for (var i = 0; i < prefixBytes; i++)
        {
            value = (value << 8) | buffer[i];
        }

        return value;
    }

    private static byte[] CreateLengthPrefix(int payloadLength, int prefixBytes, bool littleEndian)
    {
        prefixBytes = prefixBytes is 1 or 2 or 4 ? prefixBytes : 2;
        long maxValue = prefixBytes switch
        {
            1 => byte.MaxValue,
            2 => ushort.MaxValue,
            _ => uint.MaxValue
        };
        if (payloadLength > maxValue)
        {
            throw new InvalidOperationException($"发送内容超过 {prefixBytes} 字节长度头可表示的最大长度。");
        }

        var prefix = new byte[prefixBytes];
        for (var i = 0; i < prefixBytes; i++)
        {
            var shift = littleEndian ? i * 8 : (prefixBytes - 1 - i) * 8;
            prefix[i] = (byte)((payloadLength >> shift) & 0xFF);
        }

        return prefix;
    }

    private static byte[] ResolveDelimiterBytes(string? text)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "\\r\\n" : text.Trim();
        if (value.StartsWith("HEX:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseHexBytes(value[4..]);
        }

        var bytes = new List<byte>();
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (c != '\\' || index == value.Length - 1)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            var next = value[++index];
            switch (next)
            {
                case 'r':
                    bytes.Add(0x0D);
                    break;
                case 'n':
                    bytes.Add(0x0A);
                    break;
                case 't':
                    bytes.Add(0x09);
                    break;
                case '\\':
                    bytes.Add((byte)'\\');
                    break;
                default:
                    bytes.AddRange(Encoding.UTF8.GetBytes(next.ToString()));
                    break;
            }
        }

        return bytes.Count == 0 ? [0x0A] : bytes.ToArray();
    }

    private static bool TryFindSequence(IReadOnlyList<byte> buffer, byte[] sequence, out int index)
    {
        index = -1;
        if (sequence.Length == 0 || buffer.Count < sequence.Length)
        {
            return false;
        }

        for (var i = 0; i <= buffer.Count - sequence.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < sequence.Length; j++)
            {
                if (buffer[i + j] != sequence[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private static byte[] ParseHexBytes(string value)
    {
        var text = new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-' && c != ',').ToArray());
        if (text.Length == 0)
        {
            return [];
        }

        if (text.Length % 2 != 0)
        {
            text = $"0{text}";
        }

        return Convert.FromHexString(text);
    }

    private static bool EndsWith(byte[] value, byte[] suffix)
    {
        if (suffix.Length == 0 || value.Length < suffix.Length)
        {
            return false;
        }

        for (var i = 0; i < suffix.Length; i++)
        {
            if (value[value.Length - suffix.Length + i] != suffix[i])
            {
                return false;
            }
        }

        return true;
    }
}

internal static class CommunicationToolParameters
{
    public static string GetParameter(IReadOnlyDictionary<string, string> parameters, string key, string fallback)
    {
        return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
    }

    public static int GetInt(IReadOnlyDictionary<string, string> parameters, string key, int fallback)
    {
        return parameters.TryGetValue(key, out var value) &&
               (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                int.TryParse(value, out parsed))
            ? parsed
            : fallback;
    }

    public static bool GetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        return parameters.TryGetValue(key, out var value) &&
               bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;
    }
}
