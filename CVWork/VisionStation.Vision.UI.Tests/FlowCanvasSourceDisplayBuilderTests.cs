using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class FlowCanvasSourceDisplayBuilderTests
{
    [Fact]
    public void BuildInputSourceDisplayName_ShowsConnectedToolAndOutputName()
    {
        var circleTool = CreateTool("circle-1", "圆形测量", VisionToolKind.FindCircle);
        var resultTool = CreateTool(
            "result-1",
            "结果工具",
            VisionToolKind.Result,
            "resultInputCount=2; input:ResultInput1:toolId=circle-1; input:ResultInput1:portKey=ResultOutput; input:ResultInput2:toolId=circle-1; input:ResultInput2:portKey=CenterOutput");

        Assert.Equal(
            "圆形测量.OK/NG",
            FlowCanvasSourceDisplayBuilder.BuildInputSourceDisplayName(resultTool, "ResultInput1", [circleTool, resultTool]));
        Assert.Equal(
            "圆形测量.圆心",
            FlowCanvasSourceDisplayBuilder.BuildInputSourceDisplayName(resultTool, "ResultInput2", [circleTool, resultTool]));
    }

    [Fact]
    public void BuildInputSourceDisplayName_ShowsUnconnectedWhenNoSourceToolIsConfigured()
    {
        var resultTool = CreateTool("result-1", "结果工具", VisionToolKind.Result, "resultInputCount=1");

        Assert.Equal(
            "未连接",
            FlowCanvasSourceDisplayBuilder.BuildInputSourceDisplayName(resultTool, "ResultInput1", [resultTool]));
    }

    [Fact]
    public void BuildInputSourceDisplayName_ShowsInvalidSourceWhenConnectedToolIsMissing()
    {
        var resultTool = CreateTool(
            "result-1",
            "结果工具",
            VisionToolKind.Result,
            "resultInputCount=1; input:ResultInput1:toolId=missing-tool; input:ResultInput1:portKey=ResultOutput");

        Assert.Equal(
            "来源无效",
            FlowCanvasSourceDisplayBuilder.BuildInputSourceDisplayName(resultTool, "ResultInput1", [resultTool]));
    }

    private static VisionToolItem CreateTool(string id, string name, VisionToolKind kind, string parametersText = "")
    {
        return new VisionToolItem
        {
            Id = id,
            Name = name,
            Kind = kind,
            Enabled = true,
            ParametersText = parametersText
        };
    }
}
