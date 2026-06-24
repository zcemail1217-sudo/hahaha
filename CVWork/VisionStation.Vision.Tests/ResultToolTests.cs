using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class ResultToolTests
{
    [Fact]
    public async Task ExecuteAsync_CollectsMultipleConnectedResultInputs()
    {
        var frame = CreateFrame();
        using var context = new VisionToolContext(new Recipe(), frame);
        var lineTool = new VisionToolDefinition
        {
            Id = "line-1",
            Name = "Line",
            Kind = VisionToolKind.FindLine
        };
        var circleTool = new VisionToolDefinition
        {
            Id = "circle-1",
            Name = "Circle",
            Kind = VisionToolKind.FindCircle
        };
        var lineResult = new ToolResult
        {
            ToolId = "line-1",
            ToolName = "Line",
            Kind = VisionToolKind.FindLine,
            Outcome = InspectionOutcome.Ng,
            Message = "Line missing",
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["score"] = "0.12"
            }
        };
        var circleResult = new ToolResult
        {
            ToolId = "circle-1",
            ToolName = "Circle",
            Kind = VisionToolKind.FindCircle,
            Outcome = InspectionOutcome.Ok,
            Message = "Circle found",
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["radius"] = "8.5"
            }
        };
        context.ToolResults.Add(lineResult);
        context.ToolResults.Add(circleResult);
        context.SetPortOutput(lineTool, "ResultOutput", lineResult);
        context.SetPortOutput(circleTool, "ResultOutput", circleResult);

        var definition = new VisionToolDefinition
        {
            Id = "result-1",
            Name = "Result",
            Kind = VisionToolKind.Result,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:ResultInput1:toolId"] = "line-1",
                ["input:ResultInput1:portKey"] = "ResultOutput",
                ["input:ResultInput2:toolId"] = "circle-1",
                ["input:ResultInput2:portKey"] = "ResultOutput"
            }
        };

        var result = await new ResultTool().ExecuteAsync(definition, context);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("2", result.Data["inputCount"]);
        Assert.Equal("Ng", result.Data["ResultInput1"]);
        Assert.Equal("Ok", result.Data["ResultInput2"]);
        Assert.Equal("line-1", result.Data["ResultInput1.sourceToolId"]);
        Assert.Equal("circle-1", result.Data["ResultInput2.sourceToolId"]);
        Assert.Equal("0.12", result.Data["ResultInput1.score"]);
        Assert.Equal("8.5", result.Data["ResultInput2.radius"]);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsOkWhenResultInputIsMissing()
    {
        var frame = CreateFrame();
        using var context = new VisionToolContext(new Recipe(), frame);
        var definition = new VisionToolDefinition
        {
            Id = "result-1",
            Name = "Result",
            Kind = VisionToolKind.Result
        };

        var result = await new ResultTool().ExecuteAsync(definition, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Equal("0", result.Data["inputCount"]);
    }

    private static ImageFrame CreateFrame()
    {
        return new ImageFrame(
            "frame-1",
            1,
            1,
            1,
            PixelFormatKind.Gray8,
            [0],
            DateTimeOffset.UtcNow,
            "test");
    }
}
