using VisionStation.Application.Inspection;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class VisionFlowFramePolicyTests
{
    [Fact]
    public void RequiresExternalFrame_ReturnsFalse_ForFileAcquireTool()
    {
        var recipe = CreateRecipe("File");

        Assert.False(VisionFlowFramePolicy.RequiresExternalFrame(recipe));
    }

    [Fact]
    public void RequiresExternalFrame_ReturnsFalse_ForDirectoryAcquireTool()
    {
        var recipe = CreateRecipe("Directory");

        Assert.False(VisionFlowFramePolicy.RequiresExternalFrame(recipe));
    }

    [Fact]
    public void RequiresExternalFrame_ReturnsTrue_ForCameraAcquireTool()
    {
        var recipe = CreateRecipe("Camera");

        Assert.True(VisionFlowFramePolicy.RequiresExternalFrame(recipe));
    }

    [Fact]
    public void RequiresExternalFrame_ReturnsTrue_WhenNoAcquireToolExists()
    {
        var recipe = new Recipe
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
                            Id = "judge",
                            Kind = VisionToolKind.Judge
                        }
                    ]
                }
            ]
        };

        Assert.True(VisionFlowFramePolicy.RequiresExternalFrame(recipe));
    }

    private static Recipe CreateRecipe(string source)
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
                            Id = "acquire",
                            Kind = VisionToolKind.AcquireImage,
                            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["source"] = source
                            }
                        }
                    ]
                }
            ]
        };
    }
}
