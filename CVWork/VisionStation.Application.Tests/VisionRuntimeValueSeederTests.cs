using VisionStation.Application.Inspection;
using VisionStation.Domain;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class VisionRuntimeValueSeederTests
{
    [Fact]
    public void SeedPipelineOutputs_WritesPipelineAndToolRuntimeValues()
    {
        var toolResult = new ToolResult
        {
            ToolId = "measure-1",
            ToolName = "Measure",
            Kind = VisionToolKind.MeasureDistance,
            Outcome = InspectionOutcome.Ok,
            Duration = TimeSpan.FromMilliseconds(12.345),
            Message = "done",
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["distance"] = "12.5"
            }
        };
        var context = new ProcessExecutionContext("flow");
        context.ToolResults.Add(toolResult);

        VisionRuntimeValueSeeder.SeedPipelineOutputs(
            new Recipe(),
            context,
            CreatePipelineResult([toolResult], InspectionOutcome.Ng, "ABC", "pipeline done"));

        Assert.Equal("Ng", context.RuntimeValues["Vision.Outcome"]);
        Assert.Equal("ABC", context.RuntimeValues["Vision.Barcode"]);
        Assert.Equal("pipeline done", context.RuntimeValues["Vision.Message"]);
        Assert.Equal("Ng", context.RuntimeValues["OverallResult"]);
        Assert.Equal("ABC", context.RuntimeValues["Barcode"]);
        Assert.Equal("12.345", context.RuntimeValues["measure-1.DurationMs"]);
        Assert.Equal("done", context.RuntimeValues["Measure.Message"]);
        Assert.Equal("MeasureDistance", context.RuntimeValues["measure-1.Kind"]);
        Assert.Equal("12.5", context.RuntimeValues["measure-1.distance"]);
        Assert.Equal("12.5", context.RuntimeValues["measure-1:distance"]);
        Assert.Equal("12.5", context.RuntimeValues["Measure.distance"]);
        Assert.Equal("12.5", context.RuntimeValues["MeasureDistance.distance"]);
        Assert.Equal("12.5", context.RuntimeValues["distance"]);
    }

    [Fact]
    public void SeedPipelineOutputs_WritesVisionResultDefinitionsToRuntimeAndResultTable()
    {
        var toolResult = new ToolResult
        {
            ToolId = "measure-1",
            Kind = VisionToolKind.MeasureDistance,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["distance"] = "12.5"
            }
        };
        var recipe = new Recipe
        {
            VisionResults =
            [
                new VisionResultDefinition
                {
                    Name = "Width",
                    ExternalAlias = "W",
                    SourceToolId = "measure-1",
                    SourceKey = "distance"
                }
            ]
        };
        var context = new ProcessExecutionContext("flow");
        context.ToolResults.Add(toolResult);

        VisionRuntimeValueSeeder.SeedPipelineOutputs(
            recipe,
            context,
            CreatePipelineResult([toolResult], InspectionOutcome.Ok, string.Empty, string.Empty));

        Assert.Equal("12.5", context.RuntimeValues["Width"]);
        Assert.Equal("12.5", context.RuntimeValues["W"]);
        Assert.Equal("12.5", context.ResultTable["Width"]);
        Assert.Equal("12.5", context.ResultTable["W"]);
    }

    [Fact]
    public void SeedPipelineOutputs_WritesFinalResultFrameMapping()
    {
        var recipe = new Recipe
        {
            VisionResults =
            [
                new VisionResultDefinition
                {
                    Name = "ResultImage",
                    SourceKey = "ResultFrameId",
                    DataType = "Image"
                }
            ]
        };
        var context = new ProcessExecutionContext("flow");

        VisionRuntimeValueSeeder.SeedPipelineOutputs(
            recipe,
            context,
            CreatePipelineResult([], InspectionOutcome.Ok, string.Empty, string.Empty));

        Assert.Equal("frame", context.RuntimeValues["Vision.ResultFrameId"]);
        Assert.Equal("frame", context.RuntimeValues["ResultFrameId"]);
        Assert.Equal("frame", context.RuntimeValues["ResultImage"]);
        Assert.Equal("frame", context.ResultTable["ResultImage"]);
    }

    [Fact]
    public void SeedPipelineOutputs_WritesSelectedVisionResultVariable()
    {
        var toolResult = new ToolResult
        {
            ToolId = "measure-1",
            ToolName = "Measure",
            Kind = VisionToolKind.MeasureDistance,
            Outcome = InspectionOutcome.Ok,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["distance"] = "12.5"
            }
        };
        var context = new ProcessExecutionContext("flow");
        var pipelineResult = CreatePipelineResult([toolResult], InspectionOutcome.Ok, "ABC", "done");

        VisionRuntimeValueSeeder.SeedPipelineOutputs(
            new Recipe(),
            context,
            pipelineResult,
            "VisionResult");

        var variable = Assert.IsType<VisionRuntimeResultValue>(context.RuntimeObjects["VisionResult"]);
        Assert.Same(pipelineResult.ResultFrame, variable.ResultFrame);
        Assert.Equal("Ok", context.RuntimeValues["VisionResult.OverallResult"]);
        Assert.Equal("ABC", context.RuntimeValues["VisionResult.Barcode"]);
        Assert.Equal("frame", context.RuntimeValues["VisionResult.ResultFrameId"]);
        Assert.Equal("12.5", context.RuntimeValues["VisionResult.measure-1.distance"]);
        Assert.Equal("12.5", context.RuntimeValues["VisionResult.Measure.distance"]);
    }

    [Fact]
    public void SeedResultToolBindings_WritesBoundResultToolInputToRuntimeVariable()
    {
        var resultTool = new ToolResult
        {
            ToolId = "result-1",
            ToolName = "Results",
            Kind = VisionToolKind.Result,
            Outcome = InspectionOutcome.Ok,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ResultInput1"] = "12.5",
                ["ResultInput1.X"] = "12.5",
                ["ResultInput1.Y"] = "34.75"
            }
        };
        var context = new ProcessExecutionContext("flow");
        var pipelineResult = CreatePipelineResult([resultTool], InspectionOutcome.Ok, string.Empty, "done");
        var stepParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["resultBinding:result-1:ResultInput1"] = "TargetPoint"
        };

        VisionRuntimeValueSeeder.SeedResultToolBindings(
            new Recipe(),
            context,
            pipelineResult,
            stepParameters);

        Assert.Equal("12.5", context.RuntimeValues["TargetPoint"]);
        Assert.Equal("12.5", context.ResultTable["TargetPoint"]);
        Assert.Equal("12.5", context.RuntimeValues["TargetPoint.X"]);
        Assert.Equal("34.75", context.RuntimeValues["TargetPoint.Y"]);
    }

    [Fact]
    public void SeedPipelineOutputs_WritesPointVariableFromToolResult()
    {
        var toolResult = new ToolResult
        {
            ToolId = "locate-1",
            ToolName = "Template",
            Kind = VisionToolKind.TemplateLocate,
            Outcome = InspectionOutcome.Ok,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x"] = "12.5",
                ["y"] = "34.75",
                ["angle"] = "5"
            }
        };
        var recipe = new Recipe
        {
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Key = "TargetPoint",
                    DataType = "point"
                }
            ]
        };
        var context = new ProcessExecutionContext("flow");
        var pipelineResult = CreatePipelineResult([toolResult], InspectionOutcome.Ok, string.Empty, "done");

        VisionRuntimeValueSeeder.SeedPipelineOutputs(
            recipe,
            context,
            pipelineResult,
            "TargetPoint");

        var point = Assert.IsType<Point2D>(context.RuntimeObjects["TargetPoint"]);
        Assert.Equal(12.5, point.X);
        Assert.Equal(34.75, point.Y);
        Assert.Equal("12.5,34.75", context.RuntimeValues["TargetPoint"]);
        Assert.Equal("12.5", context.RuntimeValues["TargetPoint.X"]);
        Assert.Equal("34.75", context.RuntimeValues["TargetPoint.Y"]);
        Assert.Equal("12.5,34.75", context.ResultTable["TargetPoint"]);
    }

    private static VisionPipelineResult CreatePipelineResult(
        IReadOnlyList<ToolResult> toolResults,
        InspectionOutcome outcome,
        string barcode,
        string message)
    {
        return new VisionPipelineResult(
            new ImageFrame("frame", 1, 1, 1, PixelFormatKind.Gray8, [0], DateTimeOffset.Now, "test"),
            toolResults,
            outcome,
            barcode,
            message);
    }
}
