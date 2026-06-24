using VisionStation.Application.Inspection;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class SignalSourceMapperTests
{
    [Theory]
    [InlineData("TCP", "tcp")]
    [InlineData("Serial", "serial")]
    [InlineData("串口", "serial")]
    [InlineData("轴卡 IO", "digitalIo")]
    [InlineData("IO", "digitalIo")]
    [InlineData("变量/参数", "runtimeValue")]
    [InlineData("RuntimeValues", "runtimeValue")]
    [InlineData("PLC", "device")]
    public void MapSignalSourceType_NormalizesKnownAliases(string sourceType, string expected)
    {
        Assert.Equal(expected, SignalSourceMapper.MapSignalSourceType(sourceType));
    }

    [Theory]
    [InlineData("runtimeValue")]
    [InlineData("runtimeValues")]
    [InlineData("variable")]
    [InlineData("parameter")]
    public void IsRuntimeValueSource_AcceptsRuntimeAliases(string source)
    {
        Assert.True(SignalSourceMapper.IsRuntimeValueSource(source));
    }
}
