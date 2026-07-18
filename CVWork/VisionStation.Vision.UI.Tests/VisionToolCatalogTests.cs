using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VisionToolCatalogTests
{
    [Fact]
    public void NewSingleAndMultiToolsUseExplicitHalconStrictDefaults()
    {
        var singleParameters = VisionToolItem.Create(VisionToolKind.TemplateLocate, 1)
            .ToDefinition()
            .Parameters;
        var multiParameters = VisionToolItem.Create(VisionToolKind.MultiTargetMatch, 1)
            .ToDefinition()
            .Parameters;

        AssertCatalogDefaults(
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single),
            singleParameters);
        AssertCatalogDefaults(
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount),
            multiParameters);
        Assert.Equal("Halcon", singleParameters[TemplateMatchingParameterCatalog.Engine]);
        Assert.Equal("Halcon", multiParameters[TemplateMatchingParameterCatalog.Engine]);
        Assert.Equal("1", multiParameters[TemplateMatchingParameterCatalog.ExpectedCount]);
        Assert.Equal("128", multiParameters[TemplateMatchingParameterCatalog.LegacyMatchCount]);
        Assert.Equal("2", singleParameters["shapeScoreVersion"]);
        Assert.Equal("3", singleParameters["shapeCoverageDistance"]);
        Assert.False(multiParameters.ContainsKey("shapeScoreVersion"));
        Assert.False(multiParameters.ContainsKey("shapeCoverageDistance"));
    }

    [Fact]
    public void DefaultRecipeAndCatalogUseTheSameSingleTargetStrictDefaults()
    {
        var strict = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        var catalog = VisionToolCatalog.GetDefaultParameters(VisionToolKind.TemplateLocate);
        var recipe = DefaultRecipeFactory.Create()
            .Tools
            .Single(tool => tool.Kind == VisionToolKind.TemplateLocate)
            .Parameters;

        AssertCatalogDefaults(strict, catalog);
        AssertCatalogDefaults(strict, recipe);
        Assert.Equal(catalog["shapeScoreVersion"], recipe["shapeScoreVersion"]);
        Assert.Equal(catalog["shapeCoverageDistance"], recipe["shapeCoverageDistance"]);
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

    [Fact]
    public void TemplateMatchingCatalogPublishesSingleAndMultiScalePorts()
    {
        var single = VisionToolCatalog.GetOutputPorts(VisionToolKind.TemplateLocate);
        var multi = VisionToolCatalog.GetOutputPorts(VisionToolKind.MultiTargetMatch);

        Assert.Contains(single, port =>
            port.Key == "ScaleOutput" &&
            port.DataType == "Number");
        Assert.Contains(multi, port =>
            port.Key == "ScalesOutput" &&
            port.DataType == "Number[]");
        Assert.Contains("ScaleOutput", VisionToolCatalog.GetDefaultOutputKeys(VisionToolKind.TemplateLocate));
        Assert.Contains("ScalesOutput", VisionToolCatalog.GetDefaultOutputKeys(VisionToolKind.MultiTargetMatch));
        Assert.Contains(
            "ScalesOutput",
            VisionToolCatalog.GetDefaultParameters(VisionToolKind.MultiTargetMatch)["enabledOutputs"]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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

    private static void AssertCatalogDefaults(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        foreach (var parameter in expected)
        {
            Assert.True(actual.TryGetValue(parameter.Key, out var value), $"Missing parameter: {parameter.Key}");
            Assert.Equal(parameter.Value, value);
        }
    }
}
