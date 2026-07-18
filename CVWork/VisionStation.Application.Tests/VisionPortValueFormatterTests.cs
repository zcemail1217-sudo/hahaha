using VisionStation.Application.Inspection;
using VisionStation.Domain;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class VisionPortValueFormatterTests
{
    [Fact]
    public void FormatPortValue_UsesPipelineOutcomeForOverallResult()
    {
        var result = new ToolResult { Outcome = InspectionOutcome.Ok };
        var pipelineResult = new VisionPipelineResult(
            new ImageFrame("frame", 1, 1, 1, PixelFormatKind.Gray8, [0], DateTimeOffset.Now, "test"),
            [result],
            InspectionOutcome.Ng,
            string.Empty,
            string.Empty);

        var value = VisionPortValueFormatter.FormatPortValue(result, "OverallResultOutput", pipelineResult);

        Assert.Equal("Ng", value);
    }

    [Theory]
    [InlineData("PositionOutput", "10,20,30")]
    [InlineData("PointOutput", "10,20")]
    [InlineData("LineOutput", "1,2;3,4")]
    [InlineData("CircleOutput", "5,6,7")]
    public void FormatPortValue_FormatsGeometryPorts(string portKey, string expected)
    {
        var result = new ToolResult
        {
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x"] = "10",
                ["y"] = "20",
                ["angle"] = "30",
                ["startX"] = "1",
                ["startY"] = "2",
                ["endX"] = "3",
                ["endY"] = "4",
                ["centerX"] = "5",
                ["centerY"] = "6",
                ["radius"] = "7"
            }
        };

        var value = VisionPortValueFormatter.FormatPortValue(result, portKey, null);

        Assert.Equal(expected, value);
    }

    [Fact]
    public void FormatPortValue_UsesFirstAvailableAlias()
    {
        var result = new ToolResult
        {
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["distance"] = "12.3",
                ["code"] = "ABC"
            }
        };

        Assert.Equal("12.3", VisionPortValueFormatter.FormatPortValue(result, "MeasureValueOutput", null));
        Assert.Equal("ABC", VisionPortValueFormatter.FormatPortValue(result, "TextOutput", null));
    }

    [Theory]
    [InlineData("ImageOutput", "processed-frame")]
    [InlineData("AngleOutput", "45.5")]
    [InlineData("MeasureValueOutput", "12.34")]
    public void FormatPortValue_FormatsTypedOutputPorts(string portKey, string expected)
    {
        var result = new ToolResult
        {
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["outputFrameId"] = "processed-frame",
                ["angle"] = "45.5",
                ["distance"] = "12.34"
            }
        };

        var value = VisionPortValueFormatter.FormatPortValue(result, portKey, null);

        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("ScaleOutput", "1.1")]
    [InlineData("ScalesOutput", "0.9,1,1.1")]
    public void FormatPortValue_UsesPublishedScaleData(string portKey, string expected)
    {
        var result = new ToolResult
        {
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scale"] = "1.1",
                ["scales"] = "0.9,1,1.1"
            }
        };

        var value = VisionPortValueFormatter.FormatPortValue(result, portKey, null);

        Assert.Equal(expected, value);
    }
}
