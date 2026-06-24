using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class RecipeProcessStepItemTests
{
    [Fact]
    public void RunVisionFlowStep_ShowsVisionResultPanelInsteadOfAdvancedParameters()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.RunVisionFlow
        };

        Assert.True(step.ShowVisionFlowResultPanel);
        Assert.False(step.ShowAdvancedParametersText);
    }

    [Fact]
    public void NonVisionFlowStep_KeepsAdvancedParametersText()
    {
        var step = new RecipeProcessStepItem
        {
            StepType = ProcessStepType.StringProcess
        };

        Assert.False(step.ShowVisionFlowResultPanel);
        Assert.True(step.ShowAdvancedParametersText);
    }
}
