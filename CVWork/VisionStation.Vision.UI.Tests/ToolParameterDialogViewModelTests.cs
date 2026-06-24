using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ToolParameterDialogViewModelTests
{
    [Fact]
    public void ResultTool_HidesGenericTypeSelectorAndParameterTable()
    {
        var viewModel = CreateResultDialog();

        Assert.False(viewModel.ShowGenericToolHeader);
        Assert.False(viewModel.ShowToolKindSelector);
        Assert.False(viewModel.ShowParameterTable);
        Assert.True(viewModel.ShowResultInputEditor);
    }

    [Fact]
    public void NonResultTool_UsesGenericToolHeader()
    {
        var tool = new VisionToolItem
        {
            Id = "template-1",
            Name = "模板定位",
            Kind = VisionToolKind.TemplateLocate,
            Enabled = true
        };
        var viewModel = new ToolParameterDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            [VisionToolKind.TemplateLocate],
            previewRecipe: CreatePreviewRecipe());

        Assert.True(viewModel.ShowGenericToolHeader);
    }

    [Fact]
    public void ResultToolInputType_FiltersBindableSourcesByMatchingOutputType()
    {
        var viewModel = CreateResultDialog();
        var input = Assert.Single(viewModel.InputBindings);

        input.DataType = "double";

        Assert.Contains(input.Options, option => option.ToolId == "template-1" && option.PortKey == "ScoreOutput");
        Assert.DoesNotContain(input.Options, option => option.ToolId == "template-1" && option.PortKey == "ResultOutput");
    }

    [Fact]
    public void ResultToolInputTypeChoice_UsesVariableCenterTypeNames()
    {
        var viewModel = CreateResultDialog();
        var input = Assert.Single(viewModel.InputBindings);
        var choice = Assert.Single(input.DataTypeOptions, option => option.Value == "enum");

        Assert.Equal("enum", choice.ToString());
        Assert.DoesNotContain(input.DataTypeOptions, option => option.Value == "Number");
        Assert.DoesNotContain(input.DataTypeOptions, option => option.Value == "Text");
    }

    [Fact]
    public void ResultToolInputs_CanBeAddedDeletedAndPersistTheirTypes()
    {
        var tool = CreateResultTool();
        var viewModel = CreateResultDialog(tool);

        viewModel.InputBindings[0].DataType = "double";
        viewModel.AddResultInputCommand.Execute();
        viewModel.InputBindings[1].DataType = "string";
        viewModel.DeleteResultInputCommand.Execute(viewModel.InputBindings[0]);

        viewModel.ApplyTo(tool);

        Assert.Single(viewModel.InputBindings);
        Assert.Contains("resultInputCount=1", tool.ParametersText);
        Assert.Contains("input:ResultInput1:dataType=string", tool.ParametersText);
        Assert.DoesNotContain("ResultInput2", tool.ParametersText);
    }

    [Fact]
    public void ResultToolInputs_PreserveCanvasPositionParametersWhenApplied()
    {
        var tool = CreateResultTool();
        tool.ParametersText += "; canvasX=321; canvasY=222";
        var viewModel = CreateResultDialog(tool);

        viewModel.AddResultInputCommand.Execute();

        viewModel.ApplyTo(tool);

        Assert.Contains("canvasX=321", tool.ParametersText);
        Assert.Contains("canvasY=222", tool.ParametersText);
    }

    [Fact]
    public void ResultToolInputs_RestoreCanvasPositionParametersWhenHiddenRowsAreMissing()
    {
        var tool = CreateResultTool();
        tool.ParametersText += "; canvasX=321; canvasY=222";
        var viewModel = CreateResultDialog(tool);
        foreach (var parameter in viewModel.Parameters
                     .Where(parameter => parameter.Key is "canvasX" or "canvasY")
                     .ToArray())
        {
            viewModel.Parameters.Remove(parameter);
        }

        viewModel.AddResultInputCommand.Execute();

        viewModel.ApplyTo(tool);

        Assert.Contains("canvasX=321", tool.ParametersText);
        Assert.Contains("canvasY=222", tool.ParametersText);
    }

    private static ToolParameterDialogViewModel CreateResultDialog(VisionToolItem? tool = null)
    {
        tool ??= CreateResultTool();
        return new ToolParameterDialogViewModel(
            tool,
            Array.Empty<RoiChoiceItem>(),
            [VisionToolKind.Result],
            previewRecipe: CreatePreviewRecipe());
    }

    private static VisionToolItem CreateResultTool()
    {
        return new VisionToolItem
        {
            Id = "result-1",
            Name = "结果",
            Kind = VisionToolKind.Result,
            Enabled = true,
            ParametersText = "resultInputCount=1; input:ResultInput1:dataType=Result"
        };
    }

    private static Recipe CreatePreviewRecipe()
    {
        return new Recipe
        {
            CurrentFlowId = "main",
            Flows =
            [
                new VisionFlowDefinition
                {
                    Id = "main",
                    Tools =
                    [
                        new VisionToolDefinition
                        {
                            Id = "template-1",
                            Name = "模板定位",
                            Kind = VisionToolKind.TemplateLocate
                        },
                        new VisionToolDefinition
                        {
                            Id = "result-1",
                            Name = "结果",
                            Kind = VisionToolKind.Result
                        }
                    ]
                }
            ]
        };
    }
}
