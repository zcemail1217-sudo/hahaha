using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VisionToolCatalogTests
{
    [Fact]
    public void TemplateLocateDefaults_EnableShapeV2WithoutChangingMultiTargetDefaults()
    {
        var catalogParameters = VisionToolItem.Create(VisionToolKind.TemplateLocate, 1)
            .ToDefinition()
            .Parameters;
        var recipeParameters = DefaultRecipeFactory.Create()
            .Tools
            .Single(tool => tool.Kind == VisionToolKind.TemplateLocate)
            .Parameters;
        var multiTargetParameters = VisionToolItem.Create(VisionToolKind.MultiTargetMatch, 1)
            .ToDefinition()
            .Parameters;

        Assert.Equal("2", catalogParameters["shapeScoreVersion"]);
        Assert.Equal("3", catalogParameters["shapeCoverageDistance"]);
        Assert.Equal("2", recipeParameters["shapeScoreVersion"]);
        Assert.Equal("3", recipeParameters["shapeCoverageDistance"]);
        Assert.False(multiTargetParameters.ContainsKey("shapeScoreVersion"));
        Assert.False(multiTargetParameters.ContainsKey("shapeCoverageDistance"));
    }

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
