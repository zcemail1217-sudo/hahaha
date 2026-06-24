using System.Text;
using VisionStation.Application.Inspection;
using VisionStation.Communication;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class CommunicationSignalMatcherTests
{
    [Fact]
    public void MatchResponse_ReturnsMatchedValue()
    {
        var result = CommunicationSignalMatcher.MatchResponse(
            Encoding.UTF8.GetBytes("READY"),
            "READY",
            "Equals");

        Assert.Equal("READY", result.MatchedValue);
        Assert.Empty(result.IgnoredValues);
    }

    [Fact]
    public void MatchResponse_ReturnsIgnoredValueWhenNotMatched()
    {
        var result = CommunicationSignalMatcher.MatchResponse(
            Encoding.UTF8.GetBytes("BUSY"),
            "READY",
            "Equals");

        Assert.Null(result.MatchedValue);
        Assert.Equal(["BUSY"], result.IgnoredValues);
    }

    [Fact]
    public void MatchFrames_WaitsForCompleteDelimitedFrame()
    {
        var frameBuffer = new List<byte>();
        var options = new CommunicationFrameOptions
        {
            FrameMode = CommunicationFrameModes.Delimiter,
            Delimiter = "\\r\\n"
        };

        var first = CommunicationSignalMatcher.MatchFrames(
            frameBuffer,
            Encoding.UTF8.GetBytes("REA"),
            options,
            "READY",
            "Equals");
        var second = CommunicationSignalMatcher.MatchFrames(
            frameBuffer,
            Encoding.UTF8.GetBytes("DY\r\n"),
            options,
            "READY",
            "Equals");

        Assert.Null(first.MatchedValue);
        Assert.Empty(first.IgnoredValues);
        Assert.Equal("READY", second.MatchedValue);
        Assert.Empty(second.IgnoredValues);
    }
}
