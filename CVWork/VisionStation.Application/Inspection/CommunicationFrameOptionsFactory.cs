using VisionStation.Communication;
using VisionStation.Domain;

namespace VisionStation.Application.Inspection;

internal static class CommunicationFrameOptionsFactory
{
    public static CommunicationFrameOptions Create(TcpCommunicationChannelSettings channel)
    {
        return Create(
            channel.FrameMode,
            channel.Delimiter,
            channel.FixedFrameLength,
            channel.LengthPrefixBytes,
            channel.LengthPrefixLittleEndian,
            channel.MaxFrameLength,
            channel.AppendDelimiterOnSend,
            channel.PrefixPayloadOnSend);
    }

    public static CommunicationFrameOptions Create(SerialCommunicationChannelSettings channel)
    {
        return Create(
            channel.FrameMode,
            channel.Delimiter,
            channel.FixedFrameLength,
            channel.LengthPrefixBytes,
            channel.LengthPrefixLittleEndian,
            channel.MaxFrameLength,
            channel.AppendDelimiterOnSend,
            channel.PrefixPayloadOnSend);
    }

    private static CommunicationFrameOptions Create(
        string frameMode,
        string delimiter,
        int fixedFrameLength,
        int lengthPrefixBytes,
        bool lengthPrefixLittleEndian,
        int maxFrameLength,
        bool appendDelimiterOnSend,
        bool prefixPayloadOnSend)
    {
        return new CommunicationFrameOptions
        {
            FrameMode = frameMode,
            Delimiter = delimiter,
            FixedFrameLength = fixedFrameLength,
            LengthPrefixBytes = lengthPrefixBytes,
            LengthPrefixLittleEndian = lengthPrefixLittleEndian,
            MaxFrameLength = maxFrameLength,
            AppendDelimiterOnSend = appendDelimiterOnSend,
            PrefixPayloadOnSend = prefixPayloadOnSend
        };
    }
}
