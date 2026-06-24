using VisionStation.Application.Inspection;
using VisionStation.Domain;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class VisionResultResolverTests
{
    [Fact]
    public void GetVisionResultDefinitions_CombinesGlobalAndActiveFlowDefinitions()
    {
        var recipe = new Recipe
        {
            CurrentFlowId = "flow-a",
            VisionResults =
            [
                new VisionResultDefinition { Name = "Global" },
                new VisionResultDefinition { Name = "Active", FlowId = "flow-a" },
                new VisionResultDefinition { Name = "Other", FlowId = "flow-b" }
            ]
        };

        var names = VisionResultResolver.GetVisionResultDefinitions(recipe)
            .Select(item => item.Name)
            .ToArray();

        Assert.Equal(["Global", "Active"], names);
    }

    [Fact]
    public void ResolveVisionResultAddress_MatchesAliasAndReturnsPlcAddress()
    {
        var recipe = new Recipe
        {
            VisionResults =
            [
                new VisionResultDefinition
                {
                    Name = "Width",
                    ExternalAlias = "W",
                    PlcAddress = "D100"
                }
            ]
        };

        Assert.Equal("D100", VisionResultResolver.ResolveVisionResultAddress(recipe, "W"));
    }

    [Fact]
    public void TryResolveVisionValue_ReturnsPipelineFields()
    {
        var pipelineResult = CreatePipelineResult([], InspectionOutcome.Ng, "ABC");

        var resolved = VisionResultResolver.TryResolveVisionValue(
            new Recipe(),
            [],
            pipelineResult,
            "Barcode",
            out var value);

        Assert.True(resolved);
        Assert.Equal("ABC", value);
    }

    [Fact]
    public void TryResolveVisionDefinitionValue_UsesSpecifiedToolAndExportedPort()
    {
        var definition = new VisionResultDefinition
        {
            SourceToolId = "measure-1",
            SourceKey = "port:MeasureValueOutput"
        };
        var toolResults = new[]
        {
            new ToolResult
            {
                ToolId = "measure-1",
                Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["distance"] = "12.5"
                }
            }
        };

        var resolved = VisionResultResolver.TryResolveVisionDefinitionValue(
            definition,
            toolResults,
            null,
            out var value);

        Assert.True(resolved);
        Assert.Equal("12.5", value);
    }

    private static VisionPipelineResult CreatePipelineResult(
        IReadOnlyList<ToolResult> toolResults,
        InspectionOutcome outcome,
        string barcode)
    {
        return new VisionPipelineResult(
            new ImageFrame("frame", 1, 1, 1, PixelFormatKind.Gray8, [0], DateTimeOffset.Now, "test"),
            toolResults,
            outcome,
            barcode,
            string.Empty);
    }
}
