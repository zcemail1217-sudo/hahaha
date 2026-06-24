using System.Text;

namespace VisionStation.Communication;

public static class CommunicationFrameModes
{
    public const string Raw = "Raw";
    public const string Delimiter = "Delimiter";
    public const string FixedLength = "FixedLength";
    public const string LengthPrefix = "LengthPrefix";
}

public sealed record CommunicationFrameOptions
{
    public string FrameMode { get; init; } = CommunicationFrameModes.Raw;

    public string Delimiter { get; init; } = "\\r\\n";

    public int FixedFrameLength { get; init; } = 8;

    public int LengthPrefixBytes { get; init; } = 2;

    public bool LengthPrefixLittleEndian { get; init; }

    public int MaxFrameLength { get; init; } = 4096;

    public bool AppendDelimiterOnSend { get; init; } = true;

    public bool PrefixPayloadOnSend { get; init; }
}

public sealed record CommunicationDecodedFrame(string Label, byte[] Payload);

public static class CommunicationFrameCodec
{
    private const int DefaultPreviewBytes = 512;

    public static byte[] CreatePayload(string? payloadText, bool payloadIsHex, CommunicationFrameOptions options)
    {
        var payload = payloadIsHex
            ? ParseHexBytes(payloadText ?? string.Empty)
            : Encoding.UTF8.GetBytes(payloadText ?? string.Empty);

        if (payload.Length == 0)
        {
            return payload;
        }

        if (IsMode(options.FrameMode, CommunicationFrameModes.Delimiter) && options.AppendDelimiterOnSend)
        {
            var delimiter = ResolveDelimiterBytes(options.Delimiter);
            if (!EndsWith(payload, delimiter))
            {
                return [.. payload, .. delimiter];
            }
        }

        if (IsMode(options.FrameMode, CommunicationFrameModes.LengthPrefix) && options.PrefixPayloadOnSend)
        {
            var prefix = CreateLengthPrefix(
                payload.Length,
                NormalizeLengthPrefixBytes(options.LengthPrefixBytes),
                options.LengthPrefixLittleEndian);
            return [.. prefix, .. payload];
        }

        return payload;
    }

    public static IReadOnlyList<CommunicationDecodedFrame> DecodeFrames(
        List<byte> frameBuffer,
        byte[] incoming,
        CommunicationFrameOptions options)
    {
        if (IsMode(options.FrameMode, CommunicationFrameModes.Raw))
        {
            return [new CommunicationDecodedFrame("RX-CHUNK", incoming)];
        }

        frameBuffer.AddRange(incoming);
        var frames = new List<CommunicationDecodedFrame>();
        var maxFrameLength = Math.Clamp(options.MaxFrameLength, 1, 1024 * 1024);

        if (IsMode(options.FrameMode, CommunicationFrameModes.Delimiter))
        {
            DecodeDelimiterFrames(frameBuffer, frames, maxFrameLength, options.Delimiter);
        }
        else if (IsMode(options.FrameMode, CommunicationFrameModes.FixedLength))
        {
            DecodeFixedLengthFrames(frameBuffer, frames, maxFrameLength, options.FixedFrameLength);
        }
        else if (IsMode(options.FrameMode, CommunicationFrameModes.LengthPrefix))
        {
            DecodeLengthPrefixFrames(frameBuffer, frames, maxFrameLength, options);
        }
        else
        {
            frames.Add(new CommunicationDecodedFrame("RX-CHUNK", incoming));
            frameBuffer.Clear();
        }

        return frames;
    }

    public static byte[]? DecodeFirstFrame(
        List<byte> frameBuffer,
        byte[] incoming,
        CommunicationFrameOptions options)
    {
        var frames = DecodeFrames(frameBuffer, incoming, options);
        var frame = frames.FirstOrDefault(frame => frame.Label.StartsWith("RX-FRAME", StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(frame.Label, "RX-CHUNK", StringComparison.OrdinalIgnoreCase));
        return frame?.Payload;
    }

    public static byte[] ParseHexBytes(string? value)
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

    public static string FormatPayload(byte[] payload, bool asHex, int previewBytes = DefaultPreviewBytes)
    {
        var preview = payload.Length > previewBytes
            ? payload.AsSpan(0, previewBytes).ToArray()
            : payload;
        var formatted = asHex
            ? Convert.ToHexString(preview)
            : Encoding.UTF8.GetString(preview);

        return payload.Length > previewBytes
            ? $"{formatted} ...(+{payload.Length - previewBytes} bytes)"
            : formatted;
    }

    public static int NormalizeLengthPrefixBytes(int prefixBytes)
    {
        return prefixBytes is 1 or 2 or 4 ? prefixBytes : 2;
    }

    public static byte[] ResolveDelimiterBytes(string? text)
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
                case 'x' when index + 2 < value.Length &&
                              IsHexDigit(value[index + 1]) &&
                              IsHexDigit(value[index + 2]):
                    bytes.Add(Convert.ToByte(value.Substring(index + 1, 2), 16));
                    index += 2;
                    break;
                default:
                    bytes.AddRange(Encoding.UTF8.GetBytes(next.ToString()));
                    break;
            }
        }

        return bytes.Count == 0 ? [0x0A] : bytes.ToArray();
    }

    private static void DecodeDelimiterFrames(
        List<byte> frameBuffer,
        List<CommunicationDecodedFrame> frames,
        int maxFrameLength,
        string delimiterText)
    {
        var delimiter = ResolveDelimiterBytes(delimiterText);
        while (TryFindSequence(frameBuffer, delimiter, out var delimiterIndex))
        {
            var payload = frameBuffer.GetRange(0, delimiterIndex).ToArray();
            frameBuffer.RemoveRange(0, delimiterIndex + delimiter.Length);
            frames.Add(new CommunicationDecodedFrame("RX-FRAME", payload));
        }

        DrainOversizedFrameBuffer(frameBuffer, frames, maxFrameLength, "RX-OVERFLOW(no delimiter)");
    }

    private static void DecodeFixedLengthFrames(
        List<byte> frameBuffer,
        List<CommunicationDecodedFrame> frames,
        int maxFrameLength,
        int fixedFrameLength)
    {
        var length = Math.Clamp(fixedFrameLength, 1, maxFrameLength);
        while (frameBuffer.Count >= length)
        {
            var payload = frameBuffer.GetRange(0, length).ToArray();
            frameBuffer.RemoveRange(0, length);
            frames.Add(new CommunicationDecodedFrame("RX-FRAME", payload));
        }

        DrainOversizedFrameBuffer(frameBuffer, frames, maxFrameLength, "RX-OVERFLOW(fixed)");
    }

    private static void DecodeLengthPrefixFrames(
        List<byte> frameBuffer,
        List<CommunicationDecodedFrame> frames,
        int maxFrameLength,
        CommunicationFrameOptions options)
    {
        var prefixBytes = NormalizeLengthPrefixBytes(options.LengthPrefixBytes);
        while (frameBuffer.Count >= prefixBytes)
        {
            var payloadLength = ReadLengthPrefix(frameBuffer, prefixBytes, options.LengthPrefixLittleEndian);
            if (payloadLength < 0 || payloadLength > maxFrameLength)
            {
                var dropped = frameBuffer[0];
                frameBuffer.RemoveAt(0);
                frames.Add(new CommunicationDecodedFrame($"RX-BAD-LEN({payloadLength}, drop {dropped:X2})", []));
                continue;
            }

            var totalLength = prefixBytes + (int)payloadLength;
            if (frameBuffer.Count < totalLength)
            {
                break;
            }

            var payload = frameBuffer.GetRange(prefixBytes, (int)payloadLength).ToArray();
            frameBuffer.RemoveRange(0, totalLength);
            frames.Add(new CommunicationDecodedFrame($"RX-FRAME(len={payloadLength})", payload));
        }

        DrainOversizedFrameBuffer(frameBuffer, frames, maxFrameLength + prefixBytes, "RX-OVERFLOW(length)");
    }

    private static void DrainOversizedFrameBuffer(
        List<byte> frameBuffer,
        List<CommunicationDecodedFrame> frames,
        int maxFrameLength,
        string label)
    {
        while (frameBuffer.Count > maxFrameLength)
        {
            var payload = frameBuffer.GetRange(0, maxFrameLength).ToArray();
            frameBuffer.RemoveRange(0, maxFrameLength);
            frames.Add(new CommunicationDecodedFrame(label, payload));
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
        prefixBytes = NormalizeLengthPrefixBytes(prefixBytes);
        long maxValue = prefixBytes switch
        {
            1 => byte.MaxValue,
            2 => ushort.MaxValue,
            _ => uint.MaxValue
        };
        if (payloadLength > maxValue)
        {
            throw new InvalidOperationException($"Payload length exceeds {prefixBytes}-byte length prefix capacity.");
        }

        var prefix = new byte[prefixBytes];
        for (var i = 0; i < prefixBytes; i++)
        {
            var shift = littleEndian ? i * 8 : (prefixBytes - 1 - i) * 8;
            prefix[i] = (byte)((payloadLength >> shift) & 0xFF);
        }

        return prefix;
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

    private static bool IsHexDigit(char value)
    {
        return value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    private static bool IsMode(string? value, string expected)
    {
        return string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }
}
