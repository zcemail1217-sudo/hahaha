using VisionStation.Application.Inspection;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class CommunicationFrameOptionsFactoryTests
{
    [Fact]
    public void Create_MapsTcpChannelFrameOptions()
    {
        var options = CommunicationFrameOptionsFactory.Create(new TcpCommunicationChannelSettings
        {
            FrameMode = "LengthPrefix",
            Delimiter = "|",
            FixedFrameLength = 12,
            LengthPrefixBytes = 4,
            LengthPrefixLittleEndian = true,
            MaxFrameLength = 2048,
            AppendDelimiterOnSend = false,
            PrefixPayloadOnSend = true
        });

        Assert.Equal("LengthPrefix", options.FrameMode);
        Assert.Equal("|", options.Delimiter);
        Assert.Equal(12, options.FixedFrameLength);
        Assert.Equal(4, options.LengthPrefixBytes);
        Assert.True(options.LengthPrefixLittleEndian);
        Assert.Equal(2048, options.MaxFrameLength);
        Assert.False(options.AppendDelimiterOnSend);
        Assert.True(options.PrefixPayloadOnSend);
    }

    [Fact]
    public void Create_MapsSerialChannelFrameOptions()
    {
        var options = CommunicationFrameOptionsFactory.Create(new SerialCommunicationChannelSettings
        {
            FrameMode = "Delimiter",
            Delimiter = "\\n",
            FixedFrameLength = 16,
            LengthPrefixBytes = 1,
            LengthPrefixLittleEndian = true,
            MaxFrameLength = 1024,
            AppendDelimiterOnSend = false,
            PrefixPayloadOnSend = true
        });

        Assert.Equal("Delimiter", options.FrameMode);
        Assert.Equal("\\n", options.Delimiter);
        Assert.Equal(16, options.FixedFrameLength);
        Assert.Equal(1, options.LengthPrefixBytes);
        Assert.True(options.LengthPrefixLittleEndian);
        Assert.Equal(1024, options.MaxFrameLength);
        Assert.False(options.AppendDelimiterOnSend);
        Assert.True(options.PrefixPayloadOnSend);
    }
}
