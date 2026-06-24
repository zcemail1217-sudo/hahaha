using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VisionToolCatalogTests
{
    [Fact]
    public void ResultTool_DefaultsToSingleTypedInputAndNoOutputPorts()
    {
        var inputs = VisionToolCatalog.GetInputPorts(VisionToolKind.Result);
        var outputs = VisionToolCatalog.GetOutputPorts(VisionToolKind.Result);

        Assert.Equal(["ResultInput1"], inputs.Select(input => input.Key));
        Assert.Equal("enum", inputs[0].DataType);
        Assert.Empty(outputs);
    }

    [Fact]
    public void ResultToolInputPorts_UseConfiguredCountAndPerInputDataTypes()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["resultInputCount"] = "3",
            ["input:ResultInput1:dataType"] = "Result",
            ["input:ResultInput2:dataType"] = "Number",
            ["input:ResultInput3:dataType"] = "Text"
        };

        var inputs = VisionToolCatalog.GetResultInputPorts(parameters);

        Assert.Equal(["enum", "double", "string"], inputs.Select(input => input.DataType));
    }

    [Fact]
    public void Toolbox_IncludesResultToolAndExcludesCommunicationTools()
    {
        var tools = Flatten(VisionToolCatalog.GetToolboxCategories()).ToArray();

        Assert.Contains(tools, item => item.Kind == VisionToolKind.Result && item.Name.Contains("结果"));
        Assert.DoesNotContain(tools, item => item.Kind == VisionToolKind.TcpCommunication);
        Assert.DoesNotContain(tools, item => item.Kind == VisionToolKind.SerialCommunication);
    }

    private static IEnumerable<ToolboxCatalogItem> Flatten(IEnumerable<ToolboxCatalogItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children ?? Array.Empty<ToolboxCatalogItem>()))
            {
                yield return child;
            }
        }
    }
}
