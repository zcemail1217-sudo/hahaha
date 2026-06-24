using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VisionFlowResultPreviewBuilderTests
{
    [Fact]
    public void Build_ShowsOnlyConnectedResultToolInputs()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main",
            OutputTarget = "Param4"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-template",
                    Name = "Locator",
                    Kind = VisionToolKind.TemplateLocate
                },
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["resultInputCount"] = "2",
                        ["input:ResultInput1:toolId"] = "tool-template",
                        ["input:ResultInput1:portKey"] = "XOutput",
                        ["input:ResultInput1:dataType"] = "Number",
                        ["input:ResultInput2:toolId"] = "tool-template",
                        ["input:ResultInput2:portKey"] = "YOutput",
                        ["input:ResultInput2:dataType"] = "Number"
                    }
                }
            ]
        };
        var runtimeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ResultInput1"] = "10.5",
            ["Results.ResultInput2"] = "20.5",
            ["Locator.XOutput"] = "999",
            ["OverallResult"] = "Ok",
            ["Param4"] = "should-not-display"
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues,
            resultData: null);

        Assert.Equal(["Results.ResultInput1", "Results.ResultInput2"], rows.Select(row => row.Name));
        Assert.Equal(["ResultTool", "ResultTool"], rows.Select(row => row.Source));
        Assert.Equal(["10.5", "20.5"], rows.Select(row => row.Value));
        Assert.Equal(["double", "double"], rows.Select(row => row.DataType));
    }

    [Fact]
    public void Build_ExposesStepResultBindingForConnectedResultToolInput()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main",
            ParametersText = "resultBinding:tool-result:ResultInput1=Param4"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-template",
                    Name = "Locator",
                    Kind = VisionToolKind.TemplateLocate
                },
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["resultInputCount"] = "1",
                        ["input:ResultInput1:toolId"] = "tool-template",
                        ["input:ResultInput1:portKey"] = "PositionOutput",
                        ["input:ResultInput1:dataType"] = "Point"
                    }
                }
            ]
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues: null,
            resultData: null);

        var row = Assert.Single(rows);
        Assert.Equal("Results.ResultInput1", row.Name);
        Assert.Equal("Param4", row.BoundVariableKey);
    }

    [Fact]
    public void Build_UsesBoundVariableRuntimeValueWhenResultInputHasNoCurrentValue()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main",
            ParametersText = "resultBinding:tool-result:ResultInput1=Param4"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result
                }
            ]
        };
        var runtimeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Param4"] = "(12.5,34.75)"
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues,
            resultData: null);

        var row = Assert.Single(rows);
        Assert.Equal("Param4", row.BoundVariableKey);
        Assert.Equal("(12.5,34.75)", row.Value);
        Assert.Equal("未连接", row.Source);
    }

    [Fact]
    public void Build_NormalizesResultTypeAndFiltersBindableVariablesByCompatibleType()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-template",
                    Name = "Locator",
                    Kind = VisionToolKind.TemplateLocate
                },
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["resultInputCount"] = "1",
                        ["input:ResultInput1:toolId"] = "tool-template",
                        ["input:ResultInput1:portKey"] = "ScoreOutput",
                        ["input:ResultInput1:dataType"] = "Number"
                    }
                }
            ]
        };
        TestPreviewVariable[] variables =
        [
            new("DoubleVar", "double"),
            new("PointVar", "point"),
            new("StringVar", "string")
        ];

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables,
            runtimeValues: null,
            resultData: null);

        var row = Assert.Single(rows);
        Assert.Equal("double", row.DataType);
        Assert.Equal(["DoubleVar"], row.CompatibleVariables.Select(variable => variable.Key));
    }

    [Fact]
    public void Build_ReturnsEmptyRowsWhenFlowHasNoResultTool()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main",
            OutputTarget = "Param4"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-template",
                    Name = "Locator",
                    Kind = VisionToolKind.TemplateLocate
                }
            ]
        };
        var runtimeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OverallResult"] = "Ok",
            ["ResultFrameId"] = "frame-1",
            ["Param4"] = "10,20",
            ["Locator.XOutput"] = "10"
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues,
            resultData: null);

        Assert.Empty(rows);
    }

    [Fact]
    public void Build_ShowsUnconnectedResultToolInputs()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["resultInputCount"] = "2",
                        ["input:ResultInput1:dataType"] = "Result",
                        ["input:ResultInput2:dataType"] = "Number"
                    }
                }
            ]
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues: null,
            resultData: null);

        Assert.Equal(["Results.ResultInput1", "Results.ResultInput2"], rows.Select(row => row.Name));
        Assert.Equal(["未连接", "未连接"], rows.Select(row => row.Source));
        Assert.Equal(["enum", "double"], rows.Select(row => row.DataType));
        Assert.All(rows, row => Assert.Equal("-", row.Value));
    }

    [Fact]
    public void Build_UsesDefaultResultOutputWhenResultInputHasToolIdButNoPortKey()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-template",
                    Name = "Locator",
                    Kind = VisionToolKind.TemplateLocate
                },
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["resultInputCount"] = "1",
                        ["input:ResultInput1:toolId"] = "tool-template",
                        ["input:ResultInput1:dataType"] = "Result"
                    }
                }
            ]
        };
        var runtimeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Results.ResultInput1"] = "Ok"
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues,
            resultData: null);

        var row = Assert.Single(rows);
        Assert.Equal("Results.ResultInput1", row.Name);
        Assert.Equal("enum", row.DataType);
        Assert.Equal("Ok", row.Value);
        Assert.Equal("ResultTool", row.Source);
    }

    [Fact]
    public void Build_ShowsResultInputWhenSourceToolIsMissing()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-template",
                    Name = "Locator",
                    Kind = VisionToolKind.TemplateLocate
                },
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["resultInputCount"] = "1",
                        ["input:ResultInput1:toolId"] = "deleted-tool",
                        ["input:ResultInput1:portKey"] = "XOutput",
                        ["input:ResultInput1:dataType"] = "Number"
                    }
                }
            ]
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues: null,
            resultData: null);

        var row = Assert.Single(rows);
        Assert.Equal("Results.ResultInput1", row.Name);
        Assert.Equal("来源无效", row.Source);
        Assert.Equal("double", row.DataType);
    }

    [Fact]
    public void Build_ShowsResultInputWhenSourcePortIsInvalid()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-template",
                    Name = "Locator",
                    Kind = VisionToolKind.TemplateLocate
                },
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["resultInputCount"] = "1",
                        ["input:ResultInput1:toolId"] = "tool-template",
                        ["input:ResultInput1:portKey"] = "DeletedOutput",
                        ["input:ResultInput1:dataType"] = "Number"
                    }
                }
            ]
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues: null,
            resultData: null);

        var row = Assert.Single(rows);
        Assert.Equal("Results.ResultInput1", row.Name);
        Assert.Equal("来源无效", row.Source);
        Assert.Equal("double", row.DataType);
    }

    [Fact]
    public void Build_ShowsDefaultInputForLegacyResultToolWithoutInputParameters()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["canvasX"] = "681",
                        ["canvasY"] = "81"
                    }
                }
            ]
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues: null,
            resultData: null);

        var row = Assert.Single(rows);
        Assert.Equal("Results.ResultInput1", row.Name);
        Assert.Equal("未连接", row.Source);
        Assert.Equal("enum", row.DataType);
    }

    [Fact]
    public void Build_KeepsCanonicalOverallResultDataTypeWhenConnectedInputTargetsOverallResult()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-template",
                    Name = "Locator",
                    Kind = VisionToolKind.TemplateLocate
                },
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["resultInputCount"] = "1",
                        ["input:ResultInput1:toolId"] = "tool-template",
                        ["input:ResultInput1:portKey"] = "ResultOutput",
                        ["input:ResultInput1:dataType"] = "string",
                        ["input:ResultInput1:outputTarget"] = "OverallResult"
                    }
                }
            ]
        };
        var runtimeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OverallResult"] = "Ok"
        };

        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues,
            resultData: null);

        Assert.Contains(rows, row => row.Name == "Results.ResultInput1" && row.DataType == "enum" && row.Source == "ResultTool");
    }

    [Fact]
    public void Build_ShowsSingleInputRowWhenConnectedInputRuntimeHasChildFields()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow,
            FlowId = "main"
        };
        var flow = new VisionFlowDefinition
        {
            Id = "main",
            Name = "Main",
            Tools =
            [
                new VisionToolDefinition
                {
                    Id = "tool-template",
                    Name = "Locator",
                    Kind = VisionToolKind.TemplateLocate
                },
                new VisionToolDefinition
                {
                    Id = "tool-result",
                    Name = "Results",
                    Kind = VisionToolKind.Result,
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["resultInputCount"] = "1",
                        ["input:ResultInput1:toolId"] = "tool-template",
                        ["input:ResultInput1:portKey"] = "PositionOutput",
                        ["input:ResultInput1:dataType"] = "point",
                        ["input:ResultInput1:outputTarget"] = "Param4"
                    }
                }
            ]
        };
        var runtimeValues = Enumerable.Range(1, 30)
            .ToDictionary(index => $"Param4.Child{index:00}", index => index.ToString());
        var rows = VisionFlowResultPreviewBuilder.Build(
            step,
            flow,
            variables: [],
            runtimeValues,
            resultData: null);

        var row = Assert.Single(rows);
        Assert.Equal("Results.ResultInput1", row.Name);
        Assert.Equal("point", row.DataType);
        Assert.Equal("-", row.Value);
        Assert.Equal("ResultTool", row.Source);
    }

    private sealed record TestPreviewVariable(string Key, string DataType) : IVisionFlowResultPreviewVariable;
}
