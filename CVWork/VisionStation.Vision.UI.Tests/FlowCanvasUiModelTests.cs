using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class FlowCanvasUiModelTests
{
    [Fact]
    public void ResultFlowNode_ShowsSourceSummariesInsteadOfOutputPorts()
    {
        var node = CreateNode(VisionToolKind.Result);

        Assert.True(node.ShowResultSourceSummaries);
        Assert.False(node.ShowOutputPorts);
    }

    [Fact]
    public void NonResultFlowNode_ShowsOutputPortsInsteadOfSourceSummaries()
    {
        var node = CreateNode(VisionToolKind.FindCircle);

        Assert.False(node.ShowResultSourceSummaries);
        Assert.True(node.ShowOutputPorts);
    }

    [Fact]
    public void FlowPortSourceDisplayName_ReportsWhetherSourceTextExists()
    {
        var port = new FlowPortItem
        {
            OwnerTool = new VisionToolItem { Kind = VisionToolKind.Result },
            Key = "ResultInput1",
            Name = "结果1",
            DataType = "enum",
            IsInput = true,
            IsOutput = false,
            IsConnected = true,
            X = 0,
            Y = 0,
            SourceDisplayName = "圆形测量.OK/NG"
        };

        Assert.True(port.HasSourceDisplayName);
    }

    private static FlowNodeItem CreateNode(VisionToolKind kind)
    {
        return new FlowNodeItem
        {
            Tool = new VisionToolItem { Kind = kind },
            Title = "01. Tool",
            Icon = string.Empty,
            X = 0,
            Y = 0,
            Width = 190,
            Height = 80,
            IsSelected = false
        };
    }
}
